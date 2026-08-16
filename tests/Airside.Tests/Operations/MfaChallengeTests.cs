using Airside.Api.Features.Operations;
using Airside.Core.Common;
using Airside.Core.Operations;
using Airside.Core.Security;
using Airside.Data.Entities;
using Airside.Runtime.Operations;

namespace Airside.Tests.Operations;

/// <summary>
/// The second factor as login actually applies it.
/// </summary>
/// <remarks>
/// <para>
/// Until 0.1.7 the login endpoint accepted a <c>totpCode</c> field and never
/// looked at it. Enrolment worked, the dashboard reported two-factor
/// authentication as active, and the password alone still signed you in. That is
/// worse than having no second factor at all, because the operator stops
/// worrying about the password.
/// </para>
/// <para>
/// Nothing in a single successful login distinguishes a working implementation
/// from that one, which is why these tests are mostly about the second attempt:
/// a code offered twice, a recovery code spent twice, an enrolment that was
/// never confirmed.
/// </para>
/// </remarks>
public class MfaChallengeTests
{
    /// <summary>The RFC 6238 seed, "12345678901234567890", in base32.</summary>
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private const long Instant = 1111111109L;
    private const string CodeAtInstant = "081804";

    [Fact]
    public void AValidCodeIsAccepted()
    {
        var mfa = Enrolled(out _);

        Assert.Equal(
            MfaOutcome.Accepted,
            MfaChallenge.Evaluate(mfa, CodeAtInstant, Totp(Instant), new PassThroughProtector()));
    }

    [Fact]
    public void AWrongCodeIsRejected()
    {
        var mfa = Enrolled(out _);

        Assert.Equal(
            MfaOutcome.Invalid,
            MfaChallenge.Evaluate(mfa, "000000", Totp(Instant), new PassThroughProtector()));
    }

    [Fact]
    public void NoCodeAtAllIsDistinguishedFromAWrongOne()
    {
        // The login form has to know the difference: one means "ask for a code",
        // the other means "that code was wrong". Collapsing them gives a form
        // that either never shows the field or always shows an error in it.
        var mfa = Enrolled(out _);

        Assert.Equal(
            MfaOutcome.Missing,
            MfaChallenge.Evaluate(mfa, null, Totp(Instant), new PassThroughProtector()));

        Assert.Equal(
            MfaOutcome.Missing,
            MfaChallenge.Evaluate(mfa, "   ", Totp(Instant), new PassThroughProtector()));
    }

    [Fact]
    public void TheSameCodeCannotBeUsedTwice()
    {
        // A TOTP code stays valid for its whole window, so one captured in
        // transit works again until the window closes — unless accepting it
        // moves the floor, which is what this asserts.
        var mfa = Enrolled(out _);
        var totp = Totp(Instant);

        Assert.Equal(MfaOutcome.Accepted, MfaChallenge.Evaluate(mfa, CodeAtInstant, totp, new PassThroughProtector()));
        Assert.Equal(MfaOutcome.Invalid, MfaChallenge.Evaluate(mfa, CodeAtInstant, totp, new PassThroughProtector()));
    }

    [Fact]
    public void AcceptingACodeAdvancesTheRecordedStep()
    {
        // The mechanism behind the test above, asserted directly: the caller has
        // to persist this, and a caller that forgets would still pass a
        // single-login test.
        var mfa = Enrolled(out _);

        MfaChallenge.Evaluate(mfa, CodeAtInstant, Totp(Instant), new PassThroughProtector());

        Assert.Equal(Instant / 30, mfa.LastUsedTimeStep);
    }

    [Fact]
    public void ARecoveryCodeIsAccepted()
    {
        var mfa = Enrolled(out var codes);

        Assert.Equal(
            MfaOutcome.AcceptedWithRecoveryCode,
            MfaChallenge.Evaluate(mfa, codes[3], Totp(Instant), new PassThroughProtector()));
    }

    [Fact]
    public void ARecoveryCodeIsBurnedOnUse()
    {
        var mfa = Enrolled(out var codes);
        var totp = Totp(Instant);

        Assert.Equal(
            MfaOutcome.AcceptedWithRecoveryCode,
            MfaChallenge.Evaluate(mfa, codes[0], totp, new PassThroughProtector()));

        Assert.Equal(RecoveryCodes.Count - 1, RecoveryCodes.Remaining(mfa));

        // The point of a recovery code: it is a password on a piece of paper, and
        // one that still works after being used is a password left on the floor.
        Assert.Equal(
            MfaOutcome.Invalid,
            MfaChallenge.Evaluate(mfa, codes[0], totp, new PassThroughProtector()));
    }

    [Fact]
    public void SpendingOneRecoveryCodeLeavesTheRestUsable()
    {
        var mfa = Enrolled(out var codes);
        var totp = Totp(Instant);

        MfaChallenge.Evaluate(mfa, codes[0], totp, new PassThroughProtector());

        Assert.Equal(
            MfaOutcome.AcceptedWithRecoveryCode,
            MfaChallenge.Evaluate(mfa, codes[1], totp, new PassThroughProtector()));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ARecoveryCodeIsReadTheWayItWasWrittenDown(bool lowercase, bool spaced)
    {
        // These are transcribed from paper, so they come back without the hyphen,
        // in the wrong case, or with a space picked up on the way. Refusing those
        // sends someone to the recovery path who is already on it.
        var mfa = Enrolled(out var codes);

        var typed = codes[2].Replace("-", string.Empty, StringComparison.Ordinal);
        typed = lowercase ? typed.ToLowerInvariant() : typed;
        typed = spaced ? $"  {typed} " : typed;

        Assert.Equal(
            MfaOutcome.AcceptedWithRecoveryCode,
            MfaChallenge.Evaluate(mfa, typed, Totp(Instant), new PassThroughProtector()));
    }

    [Fact]
    public void AnUnreadableSecretIsReportedRatherThanWavedThrough()
    {
        // The key ring was replaced. The tempting behaviour is to fall back to
        // the password, which silently removes the second factor for whoever is
        // at the login form — including whoever replaced the key ring.
        var mfa = Enrolled(out _);

        Assert.Equal(
            MfaOutcome.SecretUnreadable,
            MfaChallenge.Evaluate(mfa, CodeAtInstant, Totp(Instant), new BrokenProtector()));
    }

    [Fact]
    public void AnUnreadableSecretDoesNotAcceptARecoveryCodeEither()
    {
        // Recovery codes are hashed separately and would still match, but a
        // record whose secret cannot be decrypted is one this server can no
        // longer reason about. Failing closed keeps the two consistent.
        var mfa = Enrolled(out var codes);

        Assert.Equal(
            MfaOutcome.SecretUnreadable,
            MfaChallenge.Evaluate(mfa, codes[0], Totp(Instant), new BrokenProtector()));
    }

    [Fact]
    public void ACodeFromTheAdjacentWindowIsStillAccepted()
    {
        // Phones drift. This is delegated to ITotp, but it is asserted here too
        // because tightening the drift window is the kind of change that looks
        // like a security improvement and reads as "MFA stopped working".
        var mfa = Enrolled(out _);

        Assert.Equal(
            MfaOutcome.Accepted,
            MfaChallenge.Evaluate(mfa, CodeAtInstant, Totp(Instant + 30), new PassThroughProtector()));
    }

    private static Totp Totp(long unixSeconds) => new(new FixedClock(unixSeconds));

    private sealed class FixedClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    private static UserMfa Enrolled(out List<string> recoveryCodes)
    {
        recoveryCodes = RecoveryCodes.Generate();

        return new UserMfa
        {
            Id = Guid.CreateVersion7(),
            UserId = Guid.CreateVersion7(),
            EncryptedSecret = Secret,
            RecoveryCodeHashes = RecoveryCodes.HashAll(recoveryCodes),
            ConfirmedAt = DateTime.UnixEpoch,
        };
    }

    /// <summary>Encryption is not what these tests are about.</summary>
    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(Secret value) => value.Reveal();

        public Result<Secret> Unprotect(string protectedValue) =>
            Result.Ok(new Secret(protectedValue));
    }

    /// <summary>A key ring that no longer holds the key the secret was sealed with.</summary>
    private sealed class BrokenProtector : ISecretProtector
    {
        public string Protect(Secret value) => value.Reveal();

        public Result<Secret> Unprotect(string protectedValue) =>
            Result.Fail<Secret>(new Error(ErrorCodes.ValidationFailed, "Key ring replaced."));
    }
}
