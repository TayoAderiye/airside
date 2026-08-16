using Airside.Core.Common;
using Airside.Core.Naming;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Security;
using Microsoft.EntityFrameworkCore;
using Serilog.Core;
using Serilog.Events;

namespace Airside.Api.Infrastructure;

public sealed class AirsideStoreOptions
{
    public const string Section = "Airside:Store";

    public StoreProvider Provider { get; set; } = StoreProvider.Postgres;

    public string ConnectionString { get; set; } =
        $"Data Source={AirsideLabels.HostPaths.Data}/airside.db";

    /// <summary>
    /// Data Protection key ring location. A host bind mount, not a container
    /// path: if this is lost, every stored secret becomes unrecoverable — which
    /// would happen on the first self-update if it lived inside the container.
    /// </summary>
    public string KeyRingPath { get; set; } = AirsideLabels.HostPaths.KeyRing;

    /// <summary>
    /// Where backup artefacts are written. Configurable for the same reason the
    /// key ring is: an operator with a separate data disk will want backups on it,
    /// and a compiled-in constant makes that impossible.
    /// </summary>
    public string BackupRoot { get; set; } = AirsideLabels.HostPaths.Backups;
}

/// <summary>
/// Redacts secret-bearing properties before Serilog writes them.
/// </summary>
/// <remarks>
/// The third layer of defence, after <see cref="Secret"/> masking on
/// <c>ToString</c> and the rule that env values are never logged. It exists
/// because the first two only protect types Airside controls — this catches an
/// anonymous object or a third-party model that happens to carry a password
/// property.
/// </remarks>
public sealed class SecretRedactionPolicy : IDestructuringPolicy
{
    private static readonly string[] SensitiveNames =
    [
        "password", "secret", "token", "apikey", "connectionstring",
        "requirepass", "credential", "privatekey", "passphrase",
    ];

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(propertyValueFactory);

        if (value is Secret)
        {
            result = new ScalarValue(Secret.Mask);
            return true;
        }

        var type = value.GetType();

        if (type.IsPrimitive || value is string || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
        {
            result = null;
            return false;
        }

        var properties = type.GetProperties()
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToList();

        if (properties.Count == 0)
        {
            result = null;
            return false;
        }

        var redacted = new List<LogEventProperty>();

        foreach (var property in properties)
        {
            if (IsSensitive(property.Name))
            {
                redacted.Add(new LogEventProperty(property.Name, new ScalarValue(Secret.Mask)));
                continue;
            }

            object? propertyValue;

            try
            {
                propertyValue = property.GetValue(value);
            }
#pragma warning disable CA1031 // A property that throws must not break logging.
            catch (Exception)
            {
                continue;
            }
#pragma warning restore CA1031

            redacted.Add(new LogEventProperty(property.Name, propertyValueFactory.CreatePropertyValue(propertyValue, true)));
        }

        result = new StructureValue(redacted, type.Name);
        return true;
    }

    private static bool IsSensitive(string name) =>
        Array.Exists(SensitiveNames, s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Issues and prints the one-time setup token on first boot.
/// </summary>
/// <remarks>
/// Printed to the console rather than written to a file or an env var, because
/// the console is what the installer shows the admin at the moment they need it,
/// and it leaves no artefact to find later. Only the hash is stored.
/// </remarks>
internal static class SetupTokenPrinter
{
    public static async Task EnsureAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<AirsideDbContext>();
        var generator = services.GetRequiredService<ISecretGenerator>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILogger<AirsideDbContext>>();

        var settings = await db.InstanceSettings.FirstAsync(ct).ConfigureAwait(false);

        if (settings.SetupCompletedAt is not null)
        {
            return;
        }

        var token = generator.GenerateToken();
        settings.SetupTokenHash = SecretGenerator.HashToken(token);
        settings.SetupTokenExpiresAt = timeProvider.GetUtcNow().UtcDateTime.AddHours(24);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Written straight to stdout rather than through the logger: a structured
        // log line would be shipped, indexed, and retained, and this value is a
        // credential for the next 24 hours.
        // Every row is padded to the same interior width rather than hand-spaced.
        // Hand-spacing had one line a character wide, which is invisible while
        // writing and obvious in a terminal.
        Console.WriteLine();
        Console.WriteLine($"  ┌{new string('─', BoxWidth)}┐");
        BoxLine("Airside is not set up yet.");
        BoxLine("Open the dashboard and use this one-time setup token:");
        BoxLine(string.Empty);
        BoxLine(token.Reveal());
        BoxLine(string.Empty);
        BoxLine("It expires in 24 hours and is consumed on first use.");
        Console.WriteLine($"  └{new string('─', BoxWidth)}┘");
        Console.WriteLine();

        logger.LogInformation("A setup token was issued and printed to the console; it expires in 24 hours");
    }

    private const int BoxWidth = 61;

    private static void BoxLine(string text) =>
        Console.WriteLine($"  │  {text.PadRight(BoxWidth - 2)}│");
}
