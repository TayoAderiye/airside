using System.Security.Cryptography;
using System.Text;
using Airside.Data.Entities;

namespace Airside.Api.Features.Operations;

/// <summary>
/// Generation, hashing, and single-use redemption of MFA recovery codes.
/// </summary>
/// <remarks>
/// Shared by enrolment and by login, which is the point of it being here.
/// Redemption has to burn the code, and two implementations of "burn" would
/// eventually disagree about whether it happened — leaving a code that keeps
/// working after it has been used, which is a password written on paper.
/// </remarks>
internal static class RecoveryCodes
{
    public const int Count = 10;

    /// <summary>
    /// Omits characters that are misread from paper: no O against 0, no I or 1.
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static List<string> Generate()
    {
        var codes = new List<string>(Count);

        for (var i = 0; i < Count; i++)
        {
            var chars = new char[10];

            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            // Two blocks with a hyphen, because these get written down by hand.
            codes.Add($"{new string(chars[..5])}-{new string(chars[5..])}");
        }

        return codes;
    }

    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(code))));

    public static string HashAll(IEnumerable<string> codes) =>
        string.Join('\n', codes.Select(Hash));

    public static int Remaining(UserMfa record) =>
        record.RecoveryCodeHashes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Redeems <paramref name="candidate"/>, removing it from the record on a
    /// match. The caller must persist the record for the redemption to stick.
    /// </summary>
    public static bool TryRedeem(UserMfa record, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var hash = Hash(candidate);
        var remaining = record.RecoveryCodeHashes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var index = remaining.FindIndex(h => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(h),
            Encoding.ASCII.GetBytes(hash)));

        if (index < 0)
        {
            return false;
        }

        remaining.RemoveAt(index);
        record.RecoveryCodeHashes = string.Join('\n', remaining);

        return true;
    }

    /// <summary>
    /// Codes are written down, so they come back with the hyphen dropped, in
    /// the wrong case, or with a stray space around them.
    /// </summary>
    private static string Normalise(string code) =>
        code.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
}
