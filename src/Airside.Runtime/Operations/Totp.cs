using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Airside.Core.Operations;

namespace Airside.Runtime.Operations;

/// <inheritdoc />
public sealed class Totp(TimeProvider timeProvider) : ITotp
{
    private const int Digits = 6;
    private const int PeriodSeconds = 30;

    /// <summary>
    /// How many steps either side of now are accepted.
    /// </summary>
    /// <remarks>
    /// One, which tolerates about thirty seconds of clock drift between the server
    /// and the phone. Zero rejects people whose phone is slightly fast; more than
    /// one widens the window an intercepted code stays usable in.
    /// </remarks>
    private const int DriftSteps = 1;

    public string GenerateSecret()
    {
        // 160 bits, matching the HMAC-SHA1 block that RFC 4226 specifies and that
        // every authenticator app expects.
        var bytes = RandomNumberGenerator.GetBytes(20);

        return Base32.Encode(bytes);
    }

    public string BuildProvisioningUri(string secret, string issuer, string account)
    {
        var escapedIssuer = Uri.EscapeDataString(issuer);
        var escapedAccount = Uri.EscapeDataString(account);

        // The issuer appears twice on purpose: in the label for apps that read it
        // there, and as a parameter for apps that do not. Omitting either makes
        // the entry show up unlabelled in one authenticator or another.
        return $"otpauth://totp/{escapedIssuer}:{escapedAccount}"
            + $"?secret={secret}&issuer={escapedIssuer}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    public bool TryValidate(string secret, string code, long notBeforeStep, out long matchedStep)
    {
        matchedStep = 0;

        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits)
        {
            return false;
        }

        var trimmed = code.Trim();

        if (!Base32.TryDecode(secret, out var key))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds() / PeriodSeconds;

        for (var offset = -DriftSteps; offset <= DriftSteps; offset++)
        {
            var step = now + offset;

            // A step at or below the last accepted one is a replay, even when the
            // code itself is correct.
            if (step <= notBeforeStep)
            {
                continue;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Compute(key, step)),
                Encoding.ASCII.GetBytes(trimmed)))
            {
                continue;
            }

            matchedStep = step;
            return true;
        }

        return false;
    }

    /// <summary>The HOTP truncation from RFC 4226, over a time counter.</summary>
    private static string Compute(byte[] key, long step)
    {
        var counter = BitConverter.GetBytes(step);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        // SHA-1 is not a choice here. RFC 6238 specifies HMAC-SHA1, and while the
        // spec permits SHA-256, Google Authenticator and several others silently
        // ignore the algorithm parameter and compute SHA-1 regardless — so a
        // "stronger" implementation produces codes that never match.
        //
        // The weakness the rule warns about does not apply to this construction:
        // HMAC does not depend on collision resistance, the key is 160 random
        // bits, and each code is valid for thirty seconds. There is no known
        // attack on HMAC-SHA1 that this exposes.
#pragma warning disable CA5350
        var hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350

        // The low nibble of the last byte selects where to read four bytes from,
        // so an attacker cannot predict which part of the hash is exposed.
        var offset = hash[^1] & 0x0F;

        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        return (binary % (int)Math.Pow(10, Digits))
            .ToString(CultureInfo.InvariantCulture)
            .PadLeft(Digits, '0');
    }
}

/// <summary>
/// Base32 as RFC 4648 defines it, which is what authenticator apps read.
/// </summary>
/// <remarks>
/// Written here rather than pulled in, because the whole implementation is forty
/// lines and the alternative is a dependency on the authentication path.
/// </remarks>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 / 5) + 1);
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }

    public static bool TryDecode(string? encoded, out byte[] data)
    {
        data = [];

        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        var bytes = new List<byte>(encoded.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var c in encoded.TrimEnd('=').ToUpperInvariant())
        {
            var index = Alphabet.IndexOf(c, StringComparison.Ordinal);

            if (index < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        data = [.. bytes];
        return true;
    }
}
