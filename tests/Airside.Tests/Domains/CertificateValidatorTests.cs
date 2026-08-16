using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Airside.Core.Common;
using Airside.Core.Domains;
using Airside.Runtime.Domains;

namespace Airside.Tests.Domains;

/// <summary>
/// Certificate upload validation, against certificates generated for each case.
/// </summary>
/// <remarks>
/// Real X.509 material rather than fixtures, because the failures being caught
/// are properties of the certificates themselves — a mismatched key or an expired
/// intermediate has to actually be mismatched or expired for the check to mean
/// anything. Every one of these produces the same opaque browser handshake
/// failure in production, which is the reason they are caught at upload.
/// </remarks>
public class CertificateValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static CertificateValidator Build() => new(new FixedClock(Now));

    /// <summary>Certificate validity is entirely about "now", so the tests pin it.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void AMatchingCertificateAndKeyIsAccepted()
    {
        var (certificate, key) = Authority.Leaf("app.example.com", Now.AddDays(-1), Now.AddDays(90));

        var result = Build().Validate(new CertificateUpload(certificate, new Secret(key)), "app.example.com");

        Assert.True(result.IsAcceptable);
        Assert.NotNull(result.Details);
        Assert.Contains("app.example.com", result.Details!.SubjectAlternativeNames);
        Assert.NotNull(result.NormalisedChainPem);
    }

    [Fact]
    public void AKeyFromADifferentPairIsRejected()
    {
        // The commonest upload mistake. Detected by signing with the candidate key
        // and verifying with the certificate's public key, which is true by
        // construction rather than by comparing moduli.
        var (certificate, _) = Authority.Leaf("app.example.com", Now.AddDays(-1), Now.AddDays(90));
        var (_, otherKey) = Authority.Leaf("app.example.com", Now.AddDays(-1), Now.AddDays(90));

        var result = Build().Validate(
            new CertificateUpload(certificate, new Secret(otherKey)), "app.example.com");

        Assert.False(result.IsAcceptable);
        Assert.Contains(result.Findings, f => f.Id == CertificateFindings.KeyMismatch);
    }

    [Fact]
    public void AnExpiredCertificateIsRejected()
    {
        var (certificate, key) = Authority.Leaf("app.example.com", Now.AddDays(-100), Now.AddDays(-1));

        var result = Build().Validate(new CertificateUpload(certificate, new Secret(key)), "app.example.com");

        Assert.False(result.IsAcceptable);
        Assert.Contains(result.Findings, f => f.Id == CertificateFindings.Expired);
    }

    [Fact]
    public void ACertificateExpiringSoonIsAcceptedWithAWarning()
    {
        var (certificate, key) = Authority.Leaf("app.example.com", Now.AddDays(-60), Now.AddDays(10));

        var result = Build().Validate(new CertificateUpload(certificate, new Secret(key)), "app.example.com");

        Assert.True(result.IsAcceptable);
        Assert.Contains(result.Findings, f =>
            f.Id == CertificateFindings.ExpiringSoon && f.Severity == PreflightSeverity.Warning);
    }

    [Fact]
    public void AWildcardDoesNotCoverTheApexAndSaysSoPrecisely()
    {
        // Reported as its own finding rather than a generic "not covered",
        // because the certificate looks right and the reason it is not is a rule
        // about wildcards that people reasonably do not know.
        var (certificate, key) = Authority.Leaf("*.example.com", Now.AddDays(-1), Now.AddDays(90));

        var result = Build().Validate(new CertificateUpload(certificate, new Secret(key)), "example.com");

        Assert.False(result.IsAcceptable);
        Assert.Contains(result.Findings, f => f.Id == CertificateFindings.WildcardDoesNotCoverApex);
    }

    [Fact]
    public void AWildcardCoversOneLevelAndNoDeeper()
    {
        var (certificate, key) = Authority.Leaf("*.example.com", Now.AddDays(-1), Now.AddDays(90));
        var validator = Build();

        var single = validator.Validate(
            new CertificateUpload(certificate, new Secret(key)), "app.example.com");
        Assert.True(single.IsAcceptable);

        var nested = validator.Validate(
            new CertificateUpload(certificate, new Secret(key)), "a.b.example.com");
        Assert.False(nested.IsAcceptable);
    }

    [Fact]
    public void ACertificateForAnotherHostnameIsRejectedAndNamesWhatItCovers()
    {
        var (certificate, key) = Authority.Leaf("other.example.com", Now.AddDays(-1), Now.AddDays(90));

        var result = Build().Validate(new CertificateUpload(certificate, new Secret(key)), "app.example.com");

        Assert.False(result.IsAcceptable);

        var finding = Assert.Single(result.Findings, f => f.Id == CertificateFindings.HostnameNotCovered);
        Assert.Contains("other.example.com", finding.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedPrivateKeyIsRejectedBeforeAnythingElse()
    {
        var (certificate, _) = Authority.Leaf("app.example.com", Now.AddDays(-1), Now.AddDays(90));

        var result = Build().Validate(
            new CertificateUpload(certificate, new Secret("-----BEGIN ENCRYPTED PRIVATE KEY-----\nx\n-----END ENCRYPTED PRIVATE KEY-----")),
            "app.example.com");

        Assert.False(result.IsAcceptable);
        Assert.Contains(result.Findings, f => f.Id == CertificateFindings.KeyEncrypted);
    }

    [Fact]
    public void AWeakRsaKeyIsRejected()
    {
        var (certificate, key) = Authority.Leaf(
            "app.example.com", Now.AddDays(-1), Now.AddDays(90), keySize: 1024);

        var result = Build().Validate(new CertificateUpload(certificate, new Secret(key)), "app.example.com");

        Assert.False(result.IsAcceptable);
        Assert.Contains(result.Findings, f => f.Id == CertificateFindings.KeyWeak);
    }

    [Fact]
    public void AnUnparseableUploadIsRejected()
    {
        var result = Build().Validate(
            new CertificateUpload("this is not a certificate", new Secret("nor is this")),
            "app.example.com");

        Assert.False(result.IsAcceptable);
        Assert.Contains(result.Findings, f => f.Id == CertificateFindings.Unparseable);
    }

    [Fact]
    public void ALeafWithNoIntermediateIsAcceptedWithAWarning()
    {
        // Accepted because browsers usually recover by fetching the issuer, and
        // warned because many API clients and older Android devices do not — so
        // the site works in a browser and fails everywhere else.
        var authority = Authority.Root("Test Intermediate CA");
        var (certificate, key) = authority.Issue("app.example.com", Now.AddDays(-1), Now.AddDays(90));

        var result = Build().Validate(new CertificateUpload(certificate, new Secret(key)), "app.example.com");

        Assert.True(result.IsAcceptable);
        Assert.Contains(result.Findings, f =>
            f.Id == CertificateFindings.ChainIncomplete && f.Severity == PreflightSeverity.Warning);
    }

    [Fact]
    public void AFullChainProducesNoCompletenessWarning()
    {
        var authority = Authority.Root("Test Intermediate CA");
        var (leaf, key) = authority.Issue("app.example.com", Now.AddDays(-1), Now.AddDays(90));

        var result = Build().Validate(
            new CertificateUpload(leaf + authority.CertificatePem, new Secret(key)), "app.example.com");

        Assert.True(result.IsAcceptable);
        Assert.DoesNotContain(result.Findings, f => f.Id == CertificateFindings.ChainIncomplete);
        Assert.Equal(2, result.Details!.ChainLength);
    }

    [Fact]
    public void AnOutOfOrderChainIsReorderedAndReported()
    {
        var authority = Authority.Root("Test Intermediate CA");
        var (leaf, key) = authority.Issue("app.example.com", Now.AddDays(-1), Now.AddDays(90));

        // Intermediate first, which is a very common way for a bundle to arrive.
        var result = Build().Validate(
            new CertificateUpload(authority.CertificatePem + leaf, new Secret(key)), "app.example.com");

        Assert.True(result.IsAcceptable);
        Assert.Contains(result.Findings, f => f.Id == CertificateFindings.ChainReordered);

        // The leaf is first in the normalised output regardless of upload order.
        var normalised = X509Certificate2.CreateFromPem(result.NormalisedChainPem);
        Assert.Contains("app.example.com", normalised.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExpiredIntermediateIsRejectedEvenThoughTheLeafIsValid()
    {
        // The DST Root CA X3 failure mode: a current leaf under an intermediate
        // that has since expired. Checking only the leaf reports this as perfectly
        // healthy while every client refuses the chain.
        //
        // Built from two authorities sharing a subject name because .NET refuses
        // to issue a certificate outliving its issuer — which is a guard on
        // creation, not on what can arrive in an uploaded bundle. What is under
        // test is the bundle, and bundles like this are exactly what CAs shipped
        // when the cross-sign lapsed.
        var signer = Authority.Root("Test Intermediate CA", Now.AddDays(-800), Now.AddDays(400));
        var expired = Authority.Root("Test Intermediate CA", Now.AddDays(-800), Now.AddDays(-5));

        var (leaf, key) = signer.Issue("app.example.com", Now.AddDays(-1), Now.AddDays(90));

        var result = Build().Validate(
            new CertificateUpload(leaf + expired.CertificatePem, new Secret(key)), "app.example.com");

        Assert.False(result.IsAcceptable);
        Assert.Contains(result.Findings, f => f.Id == CertificateFindings.ChainIntermediateExpired);
    }

    [Fact]
    public void ASelfSignedCertificateIsFlagged()
    {
        var (certificate, key) = Authority.Leaf("app.example.com", Now.AddDays(-1), Now.AddDays(90));

        var result = Build().Validate(new CertificateUpload(certificate, new Secret(key)), "app.example.com");

        Assert.Contains(result.Findings, f => f.Id == CertificateFindings.SelfSigned);
    }

    /// <summary>Generates certificates so each case is genuinely what it claims to be.</summary>
    private sealed class Authority
    {
        private readonly X509Certificate2 _certificate;

        private Authority(X509Certificate2 certificate)
        {
            _certificate = certificate;
            CertificatePem = new string(PemEncoding.Write("CERTIFICATE", certificate.RawData)) + "\n";
        }

        public string CertificatePem { get; }

        public static Authority Root(string name, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
        {
            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest(
                $"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            var certificate = request.CreateSelfSigned(
                notBefore ?? Now.AddDays(-365), notAfter ?? Now.AddDays(365));

            return new Authority(certificate);
        }

        public (string Certificate, string Key) Issue(string hostname, DateTimeOffset from, DateTimeOffset to)
        {
            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest(
                $"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(hostname);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            using var issued = request.Create(
                _certificate, from, to, Guid.NewGuid().ToByteArray()[..8]);

            return (
                new string(PemEncoding.Write("CERTIFICATE", issued.RawData)) + "\n",
                new string(PemEncoding.Write("PRIVATE KEY", rsa.ExportPkcs8PrivateKey())));
        }

        /// <summary>A self-signed leaf, for the cases that do not need an issuer.</summary>
        public static (string Certificate, string Key) Leaf(
            string hostname, DateTimeOffset from, DateTimeOffset to, int keySize = 2048)
        {
            using var rsa = RSA.Create(keySize);

            var request = new CertificateRequest(
                $"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(hostname);
            request.CertificateExtensions.Add(san.Build());

            using var certificate = request.CreateSelfSigned(from, to);

            return (
                new string(PemEncoding.Write("CERTIFICATE", certificate.RawData)) + "\n",
                new string(PemEncoding.Write("PRIVATE KEY", rsa.ExportPkcs8PrivateKey())));
        }
    }
}
