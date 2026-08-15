namespace Airside.Data.Entities;

public enum StoreProvider
{
    Postgres,
    Sqlite,
}

/// <summary>Singleton row holding instance-wide configuration and first-run state.</summary>
public class InstanceSettings : Entity
{
    /// <summary>Fixed id — there is exactly one of these, and a known id makes that enforceable.</summary>
    public static readonly Guid SingletonId = new("0195aaaa-0000-7000-8000-000000000001");

    public string InstanceName { get; set; } = "Airside";

    public string? DashboardDomain { get; set; }

    public StoreProvider StoreProvider { get; set; }

    public string? CurrentImageTag { get; set; }

    public string? PreviousImageTag { get; set; }

    public string UpdateChannel { get; set; } = "stable";

    public DateTime? SetupCompletedAt { get; set; }

    /// <summary>
    /// Hash of the one-time token the installer prints on the console. Only the
    /// hash is stored, so a stolen database dump does not hand over first-run
    /// access to an instance that has not been set up yet.
    /// </summary>
    public string? SetupTokenHash { get; set; }

    public DateTime? SetupTokenExpiresAt { get; set; }

    public bool TelemetryEnabled { get; set; }

    /// <summary>
    /// True until a domain is attached. While true the dashboard has no publicly
    /// trusted certificate — Let's Encrypt does not issue for bare IP addresses —
    /// so the UI must warn that credentials are crossing the wire unprotected.
    /// </summary>
    public bool AwaitingDomain => string.IsNullOrEmpty(DashboardDomain);
}
