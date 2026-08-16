using Airside.Runtime.Operations;

namespace Airside.Tests.Operations;

/// <summary>
/// TOTP, checked against RFC 6238's published test vectors.
/// </summary>
/// <remarks>
/// The vectors matter more than usual here. A TOTP implementation that is subtly
/// wrong still produces six plausible digits, and the only symptom is that users
/// cannot log in with a code their phone says is correct — which reads as their
/// mistake, not the server's.
/// </remarks>
public class TotpTests
{
    /// <summary>The RFC 6238 seed, "12345678901234567890", in base32.</summary>
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private static Totp At(long unixSeconds) => new(new FixedClock(unixSeconds));

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void MatchesTheRfcTestVectors(long unixSeconds, string expected)
    {
        // Validation is the only public way in, so the vector is checked by
        // asserting that the published code is the one accepted at that instant.
        Assert.True(At(unixSeconds).TryValidate(RfcSecret, expected, notBeforeStep: 0, out var step));
        Assert.Equal(unixSeconds / 30, step);
    }

    [Fact]
    public void AWrongCodeIsRejected() =>
        Assert.False(At(59L).TryValidate(RfcSecret, "000000", notBeforeStep: 0, out _));

    [Fact]
    public void ACodeCannotBeUsedTwice()
    {
        // A TOTP code stays valid for its whole window, so one captured in transit
        // works again until the window closes. Remembering the last accepted step
        // is what closes that.
        var totp = At(1111111109L);

        Assert.True(totp.TryValidate(RfcSecret, "081804", notBeforeStep: 0, out var step));
        Assert.False(totp.TryValidate(RfcSecret, "081804", notBeforeStep: step, out _));
    }

    [Fact]
    public void ACodeFromTheAdjacentWindowIsAccepted()
    {
        // Phones drift. Refusing a code that was correct thirty seconds ago locks
        // out anyone whose clock is slightly off, for no security gain.
        var justAfter = At(1111111109L + 30);

        Assert.True(justAfter.TryValidate(RfcSecret, "081804", notBeforeStep: 0, out _));
    }

    [Fact]
    public void ACodeFromTooFarAwayIsRejected() =>
        Assert.False(At(1111111109L + 120).TryValidate(RfcSecret, "081804", notBeforeStep: 0, out _));

    [Fact]
    public void GeneratedSecretsRoundTripAndDiffer()
    {
        var totp = At(59L);

        var first = totp.GenerateSecret();
        var second = totp.GenerateSecret();

        Assert.NotEqual(first, second);
        Assert.Equal(32, first.Length);
        Assert.Matches("^[A-Z2-7]+$", first);
    }

    [Fact]
    public void TheProvisioningUriNamesTheIssuerTwice()
    {
        // Once in the label and once as a parameter, because authenticator apps
        // disagree about which they read, and an entry missing the issuer shows up
        // unlabelled next to every other six-digit code on the phone.
        var uri = At(59L).BuildProvisioningUri(RfcSecret, "Airside", "admin@example.com");

        Assert.StartsWith("otpauth://totp/Airside:admin%40example.com", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Airside", uri, StringComparison.Ordinal);
        Assert.Contains("algorithm=SHA1", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedSecretIsRejectedRatherThanThrowing() =>
        Assert.False(At(59L).TryValidate("not base32 !!", "123456", notBeforeStep: 0, out _));

    private sealed class FixedClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}
