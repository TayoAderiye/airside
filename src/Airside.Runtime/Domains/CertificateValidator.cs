using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Airside.Core.Domains;

namespace Airside.Runtime.Domains;

/// <inheritdoc />
/// <remarks>
/// Every check here corresponds to a mistake that otherwise surfaces as a TLS
/// handshake failure in a browser — which reports none of the causes and looks
/// identical for all of them.
/// </remarks>
public sealed class CertificateValidator(TimeProvider timeProvider) : ICertificateValidator
{
    private const int MinimumRsaKeySize = 2048;
    private const int ExpiryWarningDays = 30;

    public CertificateValidation Validate(CertificateUpload upload, string hostname)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var findings = new List<CertificateFinding>();

        var chain = ParseChain(upload.CertificateChainPem, findings);

        if (chain.Count == 0)
        {
            return Fail(findings);
        }

        var keyPem = upload.PrivateKeyPem.Reveal();

        if (LooksEncrypted(keyPem))
        {
            // Caddy cannot use a key it cannot open, and Airside will not prompt
            // for a passphrase on every restart.
            findings.Add(new CertificateFinding(
                CertificateFindings.KeyEncrypted, PreflightSeverity.Blocking,
                "The private key is protected by a passphrase.",
                "Decrypt it before uploading: openssl rsa -in key.pem -out key-decrypted.pem. "
                + "Airside encrypts it at rest with its own key ring."));

            return Fail(findings);
        }

        // Reordering happens before the leaf is identified, because "which one is
        // the leaf" is exactly what an out-of-order upload gets wrong.
        var ordered = Reorder(chain, findings);
        var leaf = ordered[0];

        if (!KeyMatches(leaf, keyPem))
        {
            // The commonest upload error by a distance, and invisible until a
            // browser refuses the handshake with nothing useful in the message.
            findings.Add(new CertificateFinding(
                CertificateFindings.KeyMismatch, PreflightSeverity.Blocking,
                "The private key does not match the certificate.",
                "These are from different key pairs. Check that you uploaded the key generated with this "
                + "certificate's signing request, not one from an earlier request."));

            return Fail(findings);
        }

        var now = timeProvider.GetUtcNow();

        findings.AddRange(CheckValidity(leaf, now));
        findings.AddRange(CheckChain(ordered, now));
        findings.AddRange(CheckKeyStrength(leaf));
        findings.AddRange(CheckHostname(leaf, hostname));

        var details = Describe(leaf, ordered.Count);
        var blocking = findings.Exists(f => f.Severity == PreflightSeverity.Blocking);

        return new CertificateValidation(
            IsAcceptable: !blocking,
            findings,
            details,
            blocking ? null : Encode(ordered));
    }

    private static List<X509Certificate2> ParseChain(string pem, List<CertificateFinding> findings)
    {
        var certificates = new List<X509Certificate2>();

        try
        {
            // Reads every block rather than just the first: a chain is almost
            // always supplied as one concatenated file, leaf then intermediates.
            certificates.AddRange(ReadAll(pem));

            if (certificates.Count == 0)
            {
                findings.Add(new CertificateFinding(
                    CertificateFindings.Unparseable, PreflightSeverity.Blocking,
                    "No certificate was found in the uploaded text.",
                    "Check that the whole file was pasted, including the BEGIN and END lines."));
            }
        }
        catch (CryptographicException)
        {
            findings.Add(new CertificateFinding(
                CertificateFindings.Unparseable, PreflightSeverity.Blocking,
                "The certificate could not be read.",
                "It must be PEM encoded, beginning with -----BEGIN CERTIFICATE-----. A DER or PFX file "
                + "has to be converted first."));
        }
        catch (ArgumentException)
        {
            findings.Add(new CertificateFinding(
                CertificateFindings.Unparseable, PreflightSeverity.Blocking,
                "No certificate was found in the uploaded text.",
                "Check that the whole file was pasted, including the BEGIN and END lines."));
        }

        return certificates;
    }

    private static List<X509Certificate2> ReadAll(string pem)
    {
        var result = new List<X509Certificate2>();
        var remaining = pem.AsMemory();

        while (PemEncoding.TryFind(remaining.Span, out var fields))
        {
            var slice = remaining[fields.Location.Start..fields.Location.End];

            if (remaining.Span[fields.Label].SequenceEqual("CERTIFICATE"))
            {
                result.Add(X509Certificate2.CreateFromPem(slice.Span));
            }

            remaining = remaining[fields.Location.End..];
        }

        return result;
    }

    /// <summary>
    /// Puts the leaf first and each issuer after its subject.
    /// </summary>
    /// <remarks>
    /// Uploads arrive in every order. Some servers tolerate it and some do not, so
    /// the order is fixed here rather than left to chance — and the user is told,
    /// because an upload that had to be corrected is worth knowing about when the
    /// same file is used elsewhere.
    /// </remarks>
    private static List<X509Certificate2> Reorder(
        List<X509Certificate2> chain, List<CertificateFinding> findings)
    {
        if (chain.Count == 1)
        {
            return chain;
        }

        // The leaf is the one that issues nothing else in the bundle.
        var leaf = chain.Find(c => !chain.Exists(other =>
            !ReferenceEquals(other, c) && other.IssuerName.Name == c.SubjectName.Name));

        if (leaf is null)
        {
            return chain;
        }

        var ordered = new List<X509Certificate2> { leaf };
        var pool = chain.Where(c => !ReferenceEquals(c, leaf)).ToList();

        while (pool.Count > 0)
        {
            var issuer = pool.Find(c => c.SubjectName.Name == ordered[^1].IssuerName.Name);

            if (issuer is null)
            {
                // A gap in the chain, or an unrelated certificate in the bundle.
                // Reported by the completeness check rather than here.
                ordered.AddRange(pool);
                break;
            }

            ordered.Add(issuer);
            pool.Remove(issuer);
        }

        if (!ordered.SequenceEqual(chain))
        {
            findings.Add(new CertificateFinding(
                CertificateFindings.ChainReordered, PreflightSeverity.Warning,
                "The certificates were not in order and have been reordered.",
                "A chain must run leaf first, then each issuer. Some servers accept any order, so the "
                + "same file may work elsewhere and fail here — it is worth fixing at the source."));
        }

        return ordered;
    }

    private static IEnumerable<CertificateFinding> CheckValidity(X509Certificate2 leaf, DateTimeOffset now)
    {
        if (now < leaf.NotBefore)
        {
            yield return new CertificateFinding(
                CertificateFindings.NotYetValid, PreflightSeverity.Blocking,
                $"The certificate is not valid until {leaf.NotBefore:u}.",
                "Check that this server's clock is correct — a clock that is behind makes a valid "
                + "certificate look premature.");

            yield break;
        }

        if (now >= leaf.NotAfter)
        {
            yield return new CertificateFinding(
                CertificateFindings.Expired, PreflightSeverity.Blocking,
                $"The certificate expired on {leaf.NotAfter:u}.",
                "Upload a current certificate. Browsers refuse an expired one outright.");

            yield break;
        }

        var days = (int)Math.Floor((leaf.NotAfter - now).TotalDays);

        if (days <= ExpiryWarningDays)
        {
            yield return new CertificateFinding(
                CertificateFindings.ExpiringSoon, PreflightSeverity.Warning,
                $"The certificate expires in {days} days, on {leaf.NotAfter:u}.",
                "Nothing renews an uploaded certificate. Airside will remind you as the date approaches, "
                + "but replacing it is up to you.");
        }
    }

    private static IEnumerable<CertificateFinding> CheckChain(
        List<X509Certificate2> ordered, DateTimeOffset now)
    {
        var leaf = ordered[0];
        var selfSigned = leaf.SubjectName.Name == leaf.IssuerName.Name;

        if (selfSigned)
        {
            yield return new CertificateFinding(
                CertificateFindings.SelfSigned, PreflightSeverity.Warning,
                "This is a self-signed certificate.",
                "Browsers will show a security warning. If this is deliberate — behind Cloudflare's Full "
                + "mode, for instance — Internal mode does the same thing without an upload to maintain.");

            yield break;
        }

        if (ordered.Count == 1)
        {
            // Browsers often paper over this by fetching the issuer themselves.
            // Many API clients and older Android devices do not, so the site works
            // in a browser and fails for everything else — which is a miserable
            // thing to debug.
            yield return new CertificateFinding(
                CertificateFindings.ChainIncomplete, PreflightSeverity.Warning,
                "No intermediate certificates were included.",
                "Most browsers fetch the missing issuer themselves, but many API clients and older "
                + "Android devices do not — so the site works in a browser and fails elsewhere. Upload "
                + "the full chain your provider supplied.");

            yield break;
        }

        foreach (var intermediate in ordered.Skip(1))
        {
            if (now >= intermediate.NotAfter)
            {
                // A valid leaf under an expired intermediate fails. This took down
                // a substantial part of the internet when the DST Root CA X3
                // cross-sign expired, and it is invisible if only the leaf is
                // checked.
                yield return new CertificateFinding(
                    CertificateFindings.ChainIntermediateExpired, PreflightSeverity.Blocking,
                    $"An intermediate certificate expired on {intermediate.NotAfter:u}.",
                    $"'{intermediate.Subject}' is no longer valid, so the chain cannot be trusted even "
                    + "though the certificate itself is current. Ask your provider for an updated chain.");
            }
        }
    }

    private static IEnumerable<CertificateFinding> CheckKeyStrength(X509Certificate2 leaf)
    {
        using var rsa = leaf.GetRSAPublicKey();

        if (rsa is not null && rsa.KeySize < MinimumRsaKeySize)
        {
            yield return new CertificateFinding(
                CertificateFindings.KeyWeak, PreflightSeverity.Blocking,
                $"The key is only {rsa.KeySize} bits.",
                $"RSA keys below {MinimumRsaKeySize} bits are rejected by current browsers. Reissue with "
                + "a stronger key.");

            yield break;
        }

        using var ecdsa = leaf.GetECDsaPublicKey();

        if (ecdsa is not null && ecdsa.KeySize < 256)
        {
            yield return new CertificateFinding(
                CertificateFindings.KeyWeak, PreflightSeverity.Blocking,
                $"The elliptic curve key is only {ecdsa.KeySize} bits.",
                "Use P-256 or stronger.");
        }
    }

    /// <summary>
    /// Checks the SAN list, not the common name.
    /// </summary>
    /// <remarks>
    /// Browsers stopped honouring the common name years ago. A certificate whose
    /// CN is right and whose SAN list is wrong looks correct in every summary
    /// view and is rejected by every client.
    /// </remarks>
    private static IEnumerable<CertificateFinding> CheckHostname(X509Certificate2 leaf, string hostname)
    {
        var names = SubjectAlternativeNames(leaf);

        if (names.Count == 0)
        {
            yield return new CertificateFinding(
                CertificateFindings.HostnameNotCovered, PreflightSeverity.Blocking,
                "The certificate has no subject alternative names.",
                $"Modern clients ignore the common name entirely, so this certificate covers nothing. "
                + $"Reissue it with '{hostname}' in the SAN list.");

            yield break;
        }

        if (names.Exists(n => Covers(n, hostname)))
        {
            yield break;
        }

        var wildcards = names.Where(n => n.StartsWith("*.", StringComparison.Ordinal)).ToList();

        // The classic mistake, and worth naming precisely rather than reporting
        // "hostname not covered": a wildcard covers one level below the name, and
        // never the name itself.
        if (wildcards.Exists(w => string.Equals(w[2..], hostname, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new CertificateFinding(
                CertificateFindings.WildcardDoesNotCoverApex, PreflightSeverity.Blocking,
                $"'*.{hostname}' does not cover '{hostname}' itself.",
                $"A wildcard matches one level below the name, so it covers www.{hostname} but not "
                + $"{hostname}. The certificate needs '{hostname}' listed explicitly as well.");

            yield break;
        }

        yield return new CertificateFinding(
            CertificateFindings.HostnameNotCovered, PreflightSeverity.Blocking,
            $"The certificate does not cover '{hostname}'.",
            $"It is valid for {string.Join(", ", names)}. Reissue it with '{hostname}' included, or "
            + "attach it to a hostname it does cover.");
    }

    /// <summary>Wildcard matching, which is one label deep and no further.</summary>
    private static bool Covers(string san, string hostname)
    {
        if (string.Equals(san, hostname, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!san.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = san[1..];

        if (!hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "*.example.com" covers "a.example.com" but not "a.b.example.com".
        return !hostname[..^suffix.Length].Contains('.', StringComparison.Ordinal);
    }

    private static List<string> SubjectAlternativeNames(X509Certificate2 certificate)
    {
        var names = new List<string>();

        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                names.AddRange(san.EnumerateDnsNames());
            }
        }

        return names;
    }

    private static bool KeyMatches(X509Certificate2 leaf, string privateKeyPem)
    {
        // Signing with the private key and verifying with the certificate's public
        // key is the only check that is true by construction. Comparing moduli
        // works for RSA and not for anything else.
        var data = Encoding.UTF8.GetBytes("airside-key-match-probe");

        try
        {
            using var rsa = leaf.GetRSAPublicKey();

            if (rsa is not null)
            {
                using var candidate = RSA.Create();
                candidate.ImportFromPem(privateKeyPem);

                var signature = candidate.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            using var ecdsaPublic = leaf.GetECDsaPublicKey();

            if (ecdsaPublic is not null)
            {
                using var candidate = ECDsa.Create();
                candidate.ImportFromPem(privateKeyPem);

                var signature = candidate.SignData(data, HashAlgorithmName.SHA256);

                return ecdsaPublic.VerifyData(data, signature, HashAlgorithmName.SHA256);
            }

            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool LooksEncrypted(string keyPem) =>
        keyPem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal)
        || keyPem.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal);

    private static CertificateDetails Describe(X509Certificate2 leaf, int chainLength)
    {
        using var rsa = leaf.GetRSAPublicKey();
        using var ecdsa = leaf.GetECDsaPublicKey();

        return new CertificateDetails(
            leaf.Subject,
            leaf.Issuer,
            SubjectAlternativeNames(leaf),
            leaf.NotBefore,
            leaf.NotAfter,
            leaf.SerialNumber,
            Convert.ToHexString(SHA256.HashData(leaf.RawData)),
            rsa is not null ? "RSA" : ecdsa is not null ? "ECDSA" : "unknown",
            rsa?.KeySize ?? ecdsa?.KeySize ?? 0,
            leaf.SubjectName.Name == leaf.IssuerName.Name,
            chainLength);
    }

    private static string Encode(List<X509Certificate2> ordered)
    {
        var builder = new StringBuilder();

        foreach (var certificate in ordered)
        {
            builder.AppendLine(new string(PemEncoding.Write("CERTIFICATE", certificate.RawData)));
        }

        return builder.ToString();
    }

    private static CertificateValidation Fail(List<CertificateFinding> findings) =>
        new(IsAcceptable: false, findings, null, null);
}
