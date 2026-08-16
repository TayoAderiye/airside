using System.Net.Sockets;

namespace Airside.Tests.Domains;

/// <summary>
/// A fact needing outbound DNS, which skips when there is none.
/// </summary>
/// <remarks>
/// A contributor on a train should not see red for a network they do not have.
/// Set <c>AIRSIDE_REQUIRE_NETWORK=1</c> to make absence a failure instead, which
/// is what CI does so these cannot quietly stop running.
/// </remarks>
public sealed class NetworkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 2000;
            udp.Connect("1.1.1.1", 53);

            // A DNS query for the root, which any reachable resolver answers.
            udp.Send([0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01]);

            var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
            return udp.Receive(ref endpoint).Length > 0;
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    });

    public NetworkFactAttribute()
    {
        if (Available.Value)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("AIRSIDE_REQUIRE_NETWORK") == "1")
        {
            throw new InvalidOperationException(
                "AIRSIDE_REQUIRE_NETWORK=1 but no DNS resolver answered. These tests must not be skipped in CI.");
        }

        Skip = "No outbound DNS is available.";
    }
}
