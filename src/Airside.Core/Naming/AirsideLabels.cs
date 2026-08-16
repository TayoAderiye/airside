namespace Airside.Core.Naming;

/// <summary>
/// Docker label keys and well-known host paths for Airside-managed objects.
/// </summary>
/// <remarks>
/// <para>
/// Every managed container, volume, and network carries these labels, without
/// exception — reconciliation can only see what is labelled, and an unlabelled
/// object is indistinguishable from something a human created by hand.
/// </para>
/// <para>
/// This file is compiled into <c>Airside.Cli</c> as a linked source file rather
/// than shared by project reference. The CLI must stay dependency-free so it
/// works on the day the API does not, but the label vocabulary has to be
/// identical in both — so it is one definition, compiled twice.
/// See ARCHITECTURE.md §1.
/// </para>
/// </remarks>
public static class AirsideLabels
{
    public const string Managed = "airside.managed";
    public const string WorkloadId = "airside.workload-id";
    public const string Kind = "airside.kind";
    public const string Slug = "airside.slug";
    public const string Engine = "airside.engine";
    public const string DeploymentId = "airside.deployment-id";
    public const string System = "airside.system";

    public const string KindDatabase = "database";
    public const string KindApplication = "application";
    public const string KindSystem = "system";

    public const string True = "true";

    /// <summary>The three system containers, protected at the service layer even from Super Admin.</summary>
    public static class SystemContainers
    {
        public const string Api = "airside-api";
        public const string Database = "airside-db";
        public const string Proxy = "airside-proxy";

        /// <summary>The dashboard. Serves the UI only; every call it makes goes to <see cref="Api"/>.</summary>
        public const string Ui = "airside-ui";

        public static IReadOnlyList<string> All { get; } = [Api, Database, Proxy, Ui];
    }

    /// <summary>Host paths that survive container replacement. Written by the installer.</summary>
    public static class HostPaths
    {
        public const string Root = "/var/lib/airside";
        public const string KeyRing = Root + "/keys";
        public const string Data = Root + "/data";
        public const string Volumes = Root + "/volumes";
        public const string Backups = Root + "/backups";

        /// <summary>
        /// Updater and rollback state. Written before each step so the CLI can finish
        /// an update that died with no API and no updater running (ARCHITECTURE.md §7).
        /// </summary>
        public const string State = Root + "/state.json";

        /// <summary>
        /// Written by <c>airside domain reset</c>; consumed and deleted at startup.
        /// </summary>
        /// <remarks>
        /// A file rather than a database write, because the CLI is deliberately
        /// dependency-free — it has to work on the day the API does not, which is
        /// exactly when this command is needed. Presence of the file is the whole
        /// signal; its contents are ignored.
        /// </remarks>
        public const string DomainReset = Root + "/domain-reset";
    }
}
