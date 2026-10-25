using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace SelfService.Foundation.Host;

// Trusted composition only. Certificate/private key and custom trust roots never belong in JSON configuration.
public sealed record FoundationLoopbackTlsOptions(string ExpectedThumbprint)
{
    public X509Certificate2? Certificate { get; init; }
    public StoreLocation StoreLocation { get; init; } = StoreLocation.CurrentUser;
    public IReadOnlyList<X509Certificate2> TrustedRoots { get; init; } = [];
}

public sealed record LoopbackHttpsConfiguration(string ExpectedThumbprint, string StoreLocation = "CurrentUser");

public static class FoundationLoopbackTls
{
    const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    static readonly Regex Thumbprint = new("\\A[a-fA-F0-9]{40}\\z", RegexOptions.CultureInvariant);

    public static FoundationLoopbackTlsOptions FromConfiguration(LoopbackHttpsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var location = configuration.StoreLocation switch
        { "CurrentUser" => StoreLocation.CurrentUser, "LocalMachine" => StoreLocation.LocalMachine, _ => throw Rejected("TlsStoreLocationInvalid") };
        var options = new FoundationLoopbackTlsOptions(configuration.ExpectedThumbprint) { StoreLocation = location };
        Settings(options); return options;
    }

    public static void Configure(IWebHostBuilder builder, FoundationHostOptions options)
    {
        if (options.LoopbackTls is not null)
        {
            Settings(options.LoopbackTls);
            builder.ConfigureServices(services => services.AddSingleton(_ => new CertificateLease(options.LoopbackTls)));
        }
        builder.UseUrls($"{(options.LoopbackTls is null ? "http" : "https")}://127.0.0.1:{options.ListenPort}");
        builder.ConfigureKestrel(server =>
        {
            // Ignore ambient endpoint configuration in both modes: admit only the intended literal loopback listener.
            server.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            server.Listen(IPAddress.Loopback, options.ListenPort, listener =>
            {
                if (options.LoopbackTls is null) return;
                var lease = server.ApplicationServices.GetRequiredService<CertificateLease>();
                listener.UseHttps(https =>
                {
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.ServerCertificateSelector = (_, _) => lease.CurrentCertificate();
                });
            });
        });
    }

    static void Settings(FoundationLoopbackTlsOptions options)
    {
        if (options.ExpectedThumbprint is null || !Thumbprint.IsMatch(options.ExpectedThumbprint)) throw Rejected("TlsThumbprintInvalid");
        if (options.StoreLocation is not (StoreLocation.CurrentUser or StoreLocation.LocalMachine)) throw Rejected("TlsStoreLocationInvalid");
        if (options.TrustedRoots is null || options.TrustedRoots.Count > 8 || options.TrustedRoots.Any(c => c is null))
            throw Rejected("TlsTrustRootsInvalid");
    }

    static void ValidNow(X509Certificate2 certificate)
    {
        var now = DateTime.UtcNow;
        if (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now)
            throw Rejected("TlsCertificateValidity");
    }

    static void Validate(X509Certificate2 certificate, FoundationLoopbackTlsOptions options)
    {
        if (!string.Equals(certificate.Thumbprint, options.ExpectedThumbprint, StringComparison.OrdinalIgnoreCase)) throw Rejected("TlsThumbprintMismatch");
        if (!certificate.HasPrivateKey) throw Rejected("TlsPrivateKeyRequired");
        ValidNow(certificate);
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (usage.Length != 1 || !usage[0].EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ServerAuthentication))
            throw Rejected("TlsServerAuthenticationRequired");
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(x => x.CertificateAuthority)) throw Rejected("TlsLeafCertificateRequired");
        if (certificate.Extensions.OfType<X509KeyUsageExtension>().Any(x => !x.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)))
            throw Rejected("TlsDigitalSignatureRequired");
        var alternatives = certificate.Extensions.Cast<X509Extension>().Where(x => x.Oid?.Value == "2.5.29.17").ToArray();
        if (alternatives.Length != 1) throw Rejected("TlsLoopbackSanRequired");
        var san = new X509SubjectAlternativeNameExtension(alternatives[0].RawData, alternatives[0].Critical);
        if (!san.EnumerateDnsNames().Any(name => string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)) &&
            !san.EnumerateIPAddresses().Any(ip => ip.Equals(IPAddress.Loopback))) throw Rejected("TlsLoopbackSanRequired");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        var roots = new List<X509Certificate2>();
        try
        {
            if (options.TrustedRoots.Count > 0)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                foreach (var root in options.TrustedRoots)
                {
                    // Public-only snapshot; no private key is read/exported or installed into an OS store.
                    var snapshot = X509CertificateLoader.LoadCertificate(root.RawData); roots.Add(snapshot);
                    if (!snapshot.Extensions.OfType<X509BasicConstraintsExtension>().Any(x => x.CertificateAuthority))
                        throw Rejected("TlsTrustRootsInvalid");
                    chain.ChainPolicy.CustomTrustStore.Add(snapshot);
                }
            }
            if (!chain.Build(certificate)) throw Rejected("TlsCertificateChainRejected");
        }
        finally { foreach (var root in roots) root.Dispose(); }
    }

    static X509Certificate2 ReadStore(FoundationLoopbackTlsOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw Rejected("TlsWindowsStoreRequired");
        using var store = new X509Store(StoreName.My, options.StoreLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificates = store.Certificates;
        try
        {
            var matches = certificates.Cast<X509Certificate2>().Where(c => string.Equals(c.Thumbprint, options.ExpectedThumbprint,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw Rejected("TlsCertificateSelectionRejected");
            return new X509Certificate2(matches[0]); // Copies the existing context; never serializes/imports private material.
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    static ArgumentException Rejected(string code) => new(code);

    sealed class CertificateLease : IDisposable
    {
        readonly X509Certificate2 certificate;
        readonly bool owned;
        public CertificateLease(FoundationLoopbackTlsOptions options)
        {
            owned = options.Certificate is null;
            certificate = options.Certificate ?? ReadStore(options);
            try { Validate(certificate, options); }
            catch (ArgumentException) { if (owned) certificate.Dispose(); throw; }
            catch (Exception e) when (e is CryptographicException or InvalidOperationException)
            { if (owned) certificate.Dispose(); throw Rejected("TlsCertificateRejected"); }
        }
        public X509Certificate2 CurrentCertificate() { ValidNow(certificate); return certificate; }
        public void Dispose() { if (owned) certificate.Dispose(); }
    }
}
