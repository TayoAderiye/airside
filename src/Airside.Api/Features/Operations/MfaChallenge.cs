using Airside.Core.Operations;
using Airside.Core.Security;
using Airside.Data.Entities;

namespace Airside.Api.Features.Operations;

/// <summary>What checking a submitted second factor concluded.</summary>
internal enum MfaOutcome
{
    /// <summary>No code was submitted, and this account requires one.</summary>
    Missing,

    /// <summary>A current code from the authenticator.</summary>
    Accepted,

    /// <summary>A recovery code, now burned.</summary>
    AcceptedWithRecoveryCode,

    /// <summary>The stored secret could not be decrypted.</summary>
    SecretUnreadable,

    /// <summary>Neither a valid code nor an unused recovery code.</summary>
    Invalid,
}

/// <summary>
/// Decides whether a submitted second factor is acceptable.
/// </summary>
/// <remarks>
/// Separated from the login endpoint so the decision can be tested without an
/// HTTP context, a user store, or an audit sink. What is worth testing here is
/// not the arithmetic — <see cref="ITotp"/> has its own RFC vectors — but the
/// state changes: that a code cannot be replayed, and that a recovery code stops
/// working once it has been spent. Both are invisible in a single successful
/// login and only fail on the second attempt.
/// </remarks>
internal static class MfaChallenge
{
    /// <summary>
    /// Evaluates <paramref name="submitted"/> against <paramref name="mfa"/>,
    /// mutating the record when it is accepted.
    /// </summary>
    /// <remarks>
    /// The mutation is the security property, so the caller must persist the
    /// record whenever this returns an accepting outcome. Nothing here writes to
    /// the database, because the caller has other things to save in the same
    /// transaction.
    /// </remarks>
    public static MfaOutcome Evaluate(
        UserMfa mfa,
        string? submitted,
        ITotp totp,
        ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(mfa);
        ArgumentNullException.ThrowIfNull(totp);
        ArgumentNullException.ThrowIfNull(protector);

        if (string.IsNullOrWhiteSpace(submitted))
        {
            return MfaOutcome.Missing;
        }

        var secret = protector.Unprotect(mfa.EncryptedSecret);

        if (secret.IsFailure)
        {
            return MfaOutcome.SecretUnreadable;
        }

        if (totp.TryValidate(secret.Value.Reveal(), submitted, mfa.LastUsedTimeStep, out var step))
        {
            // Recording the step is what stops a code being replayed inside its
            // own window, which for a code read over someone's shoulder or out of
            // a proxy log is the whole exposure.
            mfa.LastUsedTimeStep = step;
            return MfaOutcome.Accepted;
        }

        return RecoveryCodes.TryRedeem(mfa, submitted)
            ? MfaOutcome.AcceptedWithRecoveryCode
            : MfaOutcome.Invalid;
    }
}
