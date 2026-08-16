using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Airside.Core.Proxy;

namespace Airside.Runtime.Proxy;

/// <summary>
/// Reads the certificate a hostname is actually presenting.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately an observation rather than a query. Caddy owns issuance and
/// renewal and exposes no stable API for listing certificates, and reading its
/// storage would tie Airside to an internal layout that is free to change. What
/// an operator needs to know is what clients receive, and the only way to know
/// that is to be a client.
/// </para>
/// <para>
/// It follows that a certificate Caddy believes it has issued but is not serving
/// shows up here as missing — which is the failure worth catching, not one to
/// paper over.
/// </para>
/// </remarks>
public static class CertificateInspector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static async Task<CertificateStatus?> InspectAsync(string hostname, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(hostname, 443, timeout.Token).ConfigureAwait(false);

            X509Certificate2? captured = null;

            // CA5359 is right to fire, and the suppression is deliberately as
            // narrow as it can be. Accepting any certificate is correct here and
            // only here: this connection exists to read what is being served, not
            // to exchange anything with it. Nothing is sent, nothing is trusted,
            // and no security decision is made from the result — the issuer and
            // expiry are reported so a human can judge. Reusing this callback
            // anywhere that carries data would be a genuine vulnerability.
#pragma warning disable CA5359
            await using var tls = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, certificate, _, _) =>
                {
                    if (certificate is not null)
                    {
                        captured = new X509Certificate2(certificate);
                    }

                    return true;
                });
#pragma warning restore CA5359

            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = hostname },
                timeout.Token).ConfigureAwait(false);

            if (captured is null)
            {
                return null;
            }

            using (captured)
            {
                return new CertificateStatus(
                    hostname,
                    captured.Issuer,
                    captured.NotBefore.ToUniversalTime(),
                    captured.NotAfter.ToUniversalTime(),

                    // Everything Caddy issues renews automatically. A certificate
                    // Airside did not obtain would not be here at all.
                    AutoRenew: true);
            }
        }
        catch (SocketException)
        {
            // DNS does not resolve, or nothing is listening. The commonest case by
            // far is a domain whose A record has not been pointed at this host.
            return null;
        }
        catch (AuthenticationException)
        {
            // Reachable, but no usable TLS — usually the ACME challenge has not
            // completed yet.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
