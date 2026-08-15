using Airside.Data.Entities;

namespace Airside.Api.Contracts;

/// <summary>
/// A secret-bearing field as it appears in every response.
/// </summary>
/// <remarks>
/// The value is always the mask. Revealing is a separate <c>POST</c>, never a
/// <c>GET</c>: a secret in a <c>GET</c> response lands in browser history, proxy
/// logs, and referrer headers.
/// </remarks>
public sealed record SecretFieldDto(
    bool IsSecret,
    string Value,
    string RevealUrl,
    DateTimeOffset LastChangedAt);

public sealed record RevealedSecretDto(string Value);

public sealed record CredentialDto(
    Guid Id,
    string? Username,
    SecretFieldDto Password,
    bool IsPrimary,
    string State,
    DateTimeOffset CreatedAt);

public sealed record BackupDto(
    Guid Id,
    Guid DatabaseId,
    string Kind,
    string TriggerKind,
    string Status,
    long? SizeBytes,
    string? Sha256,
    string EngineSnapshot,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ExpiresAt,
    bool IsRetained,
    string? ErrorMessage,
    IReadOnlyList<WarningDto> Warnings)
{
    public static BackupDto From(Backup b)
    {
        ArgumentNullException.ThrowIfNull(b);

        return new BackupDto(
            b.Id,
            b.DatabaseInstanceId,
            b.Kind.ToString().ToLowerInvariant(),
            char.ToLowerInvariant(b.TriggerKind.ToString()[0]) + b.TriggerKind.ToString()[1..],
            b.Status.ToString().ToLowerInvariant(),
            b.SizeBytes,
            b.Sha256,
            b.EngineSnapshot,
            new DateTimeOffset(b.StartedAt, TimeSpan.Zero),
            b.CompletedAt is null ? null : new DateTimeOffset(b.CompletedAt.Value, TimeSpan.Zero),
            b.ExpiresAt is null ? null : new DateTimeOffset(b.ExpiresAt.Value, TimeSpan.Zero),
            b.IsRetained,
            b.ErrorMessage,
            [
                // Stated on every backup rather than buried in documentation: a
                // backup on the same instance as its database does not survive the
                // failure people are actually afraid of.
                new WarningDto(
                    "backup.local_only",
                    "This backup is stored on the same host as the database. It will not survive the loss "
                    + "of this instance."),
            ]);
    }
}

public sealed record RestoreRequest(string ConfirmSlug);

public sealed record RestorePreviewDto(
    bool RequiresStop,
    int? EstimatedDowntimeSeconds,
    bool EngineVersionMatches,
    string BackupEngine,
    string TargetEngine,
    bool PreRestoreBackupWillBeTaken);

public sealed record QueryRequestDto(string Statement, int? MaxRows, int? TimeoutSeconds);

public sealed record QueryResponseDto(
    Guid ExecutionId,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowsAffected,
    bool Truncated,
    int DurationMs);

public sealed record QueryHistoryDto(
    Guid Id,
    string Body,
    DateTimeOffset ExecutedAt,
    int DurationMs,
    int RowsAffected,
    bool Success,
    string? ErrorMessage);
