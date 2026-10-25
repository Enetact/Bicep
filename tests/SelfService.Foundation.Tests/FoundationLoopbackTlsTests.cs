using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;
using Xunit.Abstractions;

namespace SelfService.Foundation.Tests;

public sealed class FoundationLoopbackTlsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_loopback_TLS_uses_ephemeral_CA_and_strict_client_trust_while_preserving_identity_authorization()
    {
        using var certificates = new EphemeralCertificates(output); using var server = certificates.Server();
        var thumbprint = server.Thumbprint;
        await using (var host = new LocalHost(Tls(server, certificates.Root)))
        {
            await host.Start();
            Assert.Equal($"https://127.0.0.1:{host.Port}", Assert.Single(host.App!.Urls));
            using var client = host.Client(certificates.Root);
            using var health = await client.GetAsync("/health"); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var anonymous = await client.GetAsync(host.Root + "/jobs"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            host.App.Services.GetRequiredService<FoundationStore>().SetMembership(FoundationApiTests.ApiCell.Customer,
                FoundationApiTests.ApiCell.Actor("admin"), FoundationApiTests.ApiCell.Actor("viewer"), "default", "dev", [FoundationRoles.Viewer]);
            using var request = new HttpRequestMessage(HttpMethod.Get, host.Root + "/jobs");
            request.Headers.Authorization = new("Bearer", host.Issuer.Token(host.Issuer.UserClaims(FoundationRoles.Viewer,
                FoundationApiTests.ApiCell.Subjects["viewer"])));
            using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(thumbprint, server.Thumbprint); Assert.True(server.HasPrivateKey); // Composition retains ownership.
    }

    [Fact]
    public async Task Wrong_client_root_and_wrong_server_name_are_rejected_without_a_validation_callback()
    {
        using var certificates = new EphemeralCertificates(output); using var other = new EphemeralCertificates(output);
        using var server = certificates.Server(ipOnly: true);
        await using var host = new LocalHost(Tls(server, certificates.Root)); await host.Start();
        using var wrongRoot = host.Client(other.Root);
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongRoot.GetAsync("/health"));
        using var wrongName = host.Client(certificates.Root, "localhost");
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongName.GetAsync("/health"));
        using var correct = host.Client(certificates.Root);
        using var response = await correct.GetAsync("/health"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("expired", "TlsCertificateValidity")]
    [InlineData("future", "TlsCertificateValidity")]
    [InlineData("keyless", "TlsPrivateKeyRequired")]
    [InlineData("wrong-san", "TlsLoopbackSanRequired")]
    [InlineData("cn-only", "TlsLoopbackSanRequired")]
    [InlineData("client-auth", "TlsServerAuthenticationRequired")]
    [InlineData("missing-eku", "TlsServerAuthenticationRequired")]
    [InlineData("thumbprint", "TlsThumbprintMismatch")]
    [InlineData("wrong-root", "TlsCertificateChainRejected")]
    [InlineData("system-untrusted", "TlsCertificateChainRejected")]
    public async Task Invalid_server_certificate_fails_closed_before_any_listener_starts(string variant, string code)
    {
        using var certificates = new EphemeralCertificates(output); using var other = new EphemeralCertificates(output);
        using var server = certificates.Server(variant: variant);
        var tls = Tls(server, certificates.Root);
        if (variant == "thumbprint") tls = tls with { ExpectedThumbprint = new string('0', 40) };
        if (variant == "wrong-root") tls = tls with { TrustedRoots = [other.Root] };
        if (variant == "system-untrusted") tls = tls with { TrustedRoots = [] };
        await using var host = new LocalHost(tls);
        var error = await Record.ExceptionAsync(host.Start); Assert.NotNull(error);
        Assert.Contains(code, error.ToString(), StringComparison.Ordinal);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<SocketException>(async () => await socket.ConnectAsync(IPAddress.Loopback, host.Port));
    }

    [Fact]
    public async Task Required_HTTPS_rejects_plain_HTTP_on_real_TLS_port_and_spoofed_forwarded_scheme_in_TestServer()
    {
        using var certificates = new EphemeralCertificates(output); using var server = certificates.Server();
        await using (var host = new LocalHost(Tls(server, certificates.Root)))
        {
            await host.Start(); using var plaintext = host.Client(certificates.Root, scheme: "http");
            try
            {
                using var response = await plaintext.GetAsync("/health");
                Assert.False(response.IsSuccessStatusCode); Assert.DoesNotContain("\"ready\":true", await response.Content.ReadAsStringAsync());
                Assert.False(response.Headers.Contains("Location"));
            }
            catch (HttpRequestException) { /* TLS listener closed the plaintext connection before application dispatch. */ }
        }
        await using (var host = new LocalHost(Tls(server, certificates.Root), testServer: true))
        {
            await host.Start(); using var client = host.App!.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/health");
            request.Headers.Add("X-Forwarded-Proto", "https");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("https_required", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task TLS12_negotiates_and_legacy_TLS11_is_rejected()
    {
        using var certificates = new EphemeralCertificates(output); using var server = certificates.Server();
        await using var host = new LocalHost(Tls(server, certificates.Root)); await host.Start();
        using (var socket = new TcpClient(AddressFamily.InterNetwork))
        {
            await socket.ConnectAsync(IPAddress.Loopback, host.Port); using var tls = new SslStream(socket.GetStream());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tls.AuthenticateAsClientAsync(Authentication(certificates.Root, SslProtocols.Tls12), timeout.Token);
            Assert.Equal(SslProtocols.Tls12, tls.SslProtocol);
        }
        using (var socket = new TcpClient(AddressFamily.InterNetwork))
        {
            await socket.ConnectAsync(IPAddress.Loopback, host.Port); using var tls = new SslStream(socket.GetStream());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Intentional rejected legacy probe; no credentials or HTTP request are sent.
            var error = await Record.ExceptionAsync(() => tls.AuthenticateAsClientAsync(Authentication(certificates.Root, (SslProtocols)768), timeout.Token));
            Assert.True(error is AuthenticationException or IOException, "Legacy TLS unexpectedly negotiated or did not fail promptly.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambient_endpoint_configuration_cannot_add_an_extra_listener_in_HTTP_or_HTTPS_mode(bool https)
    {
        using var certificates = new EphemeralCertificates(output); using var server = certificates.Server();
        await using var host = new LocalHost(https ? Tls(server, certificates.Root) : null, ambientEndpoint: true);
        await host.Start();
        Assert.Equal($"{(https ? "https" : "http")}://127.0.0.1:{host.Port}", Assert.Single(host.App!.Urls));
        using var client = host.Client(certificates.Root, scheme: https ? "https" : "http");
        using var response = await client.GetAsync("/health"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Public_configuration_accepts_only_pinned_Windows_store_metadata_and_never_private_material()
    {
        var valid = FoundationLoopbackTls.FromConfiguration(new(new string('A', 40), "LocalMachine"));
        Assert.Equal(StoreLocation.LocalMachine, valid.StoreLocation); Assert.Null(valid.Certificate); Assert.Empty(valid.TrustedRoots);
        foreach (var bad in new[] { "", " " + new string('A', 40), new string('A', 64), new string('G', 40) })
            Assert.Throws<ArgumentException>(() => FoundationLoopbackTls.FromConfiguration(new(bad)));
        foreach (var location in new[] { "", "currentuser", "Root", "1", "../My" })
            Assert.Throws<ArgumentException>(() => FoundationLoopbackTls.FromConfiguration(new(new string('A', 40), location)));
        foreach (var field in new[] { "pfxPath", "password", "privateKey", "trustedRoots", "certificate" })
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(new LoopbackHttpsConfiguration(new string('A', 40)), FoundationApplication.Json))!.AsObject();
            node[field] = "synthetic-forbidden-private-configuration";
            Assert.Throws<JsonException>(() => node.Deserialize<LoopbackHttpsConfiguration>(FoundationApplication.Json));
        }
    }

    [Fact]
    public async Task Missing_exact_Windows_store_certificate_has_no_development_certificate_fallback()
    {
        await using var host = new LocalHost(new(new string('0', 40)));
        var error = await Record.ExceptionAsync(host.Start); Assert.NotNull(error);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<SocketException>(async () => await socket.ConnectAsync(IPAddress.Loopback, host.Port));
    }

    [Fact]
    public void Public_host_configuration_maps_HTTPS_metadata_without_reading_a_key_or_creating_state()
    {
        using var key = RSA.Create(2048);
        var directory = Path.Combine(Path.GetTempPath(), "PlatformStudio-TlsConfiguration-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "public.json"); Directory.CreateDirectory(directory);
        try
        {
            var configuration = new FoundationHostConfiguration.CellConfiguration("cell-a", "customer-a", IdentityTestIssuer.Audience,
                "cell.sqlite", $"user:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.Subject}", "default", "dev",
                new() { [IdentityTestIssuer.Tenant] = [IdentityTestIssuer.Issuer()] },
                [new(IdentityTestIssuer.Tenant, IdentityTestIssuer.Client, PrincipalKind.User)],
                [new(IdentityTestIssuer.KeyId, key.ExportSubjectPublicKeyInfoPem())], LoopbackHttps: new(new string('A', 40), "LocalMachine"));
            File.WriteAllText(file, JsonSerializer.Serialize(configuration, FoundationApplication.Json));
            var options = FoundationHostConfiguration.Load(file);
            Assert.NotNull(options.LoopbackTls); Assert.Equal(StoreLocation.LocalMachine, options.LoopbackTls.StoreLocation);
            Assert.Equal(new string('A', 40), options.LoopbackTls.ExpectedThumbprint); Assert.Null(options.LoopbackTls.Certificate);
            Assert.Empty(options.LoopbackTls.TrustedRoots); Assert.False(File.Exists(options.DatabasePath));
        }
        finally { File.Delete(file); Directory.Delete(directory, false); }
    }

    static FoundationLoopbackTlsOptions Tls(X509Certificate2 server, X509Certificate2 root) => new(server.Thumbprint)
    { Certificate = server, TrustedRoots = [root] };
    static X509ChainPolicy Policy(X509Certificate2 root)
    {
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true, VerificationFlags = X509VerificationFlags.NoFlag };
        policy.CustomTrustStore.Add(root); policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); return policy;
    }
    static SslClientAuthenticationOptions Authentication(X509Certificate2 root, SslProtocols protocols) => new()
    { TargetHost = "127.0.0.1", CertificateChainPolicy = Policy(root), EnabledSslProtocols = protocols, CertificateRevocationCheckMode = X509RevocationMode.NoCheck };

    sealed class LocalHost(FoundationLoopbackTlsOptions? tls, bool testServer = false, bool ambientEndpoint = false) : IAsyncDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), "PlatformStudio-TlsTests", Guid.NewGuid().ToString("N"));
        public IdentityTestIssuer Issuer { get; } = new();
        public int Port { get; } = FreePort();
        public WebApplication? App { get; private set; }
        public string Root => $"/api/foundation/customers/{FoundationApiTests.ApiCell.Customer}/projects/default/environments/dev";
        public async Task Start()
        {
            var options = new FoundationHostOptions(Issuer.Options(FoundationApiTests.ApiCell.Customer, "00000000-0000-0000-0000-000000000003"),
                Path.Combine(directory, "cell.sqlite"), FoundationApiTests.ApiCell.Actor("admin")) { ListenPort = Port, LoopbackTls = tls };
            App = FoundationApplication.Build(options, configure: builder =>
            {
                if (testServer) builder.WebHost.UseTestServer();
                if (ambientEndpoint) builder.Configuration["Kestrel:Endpoints:Ambient:Url"] = $"http://127.0.0.1:{FreePort()}";
            });
            await App.StartAsync();
        }
        public HttpClient Client(X509Certificate2 root, string name = "127.0.0.1", string scheme = "https")
        {
            var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3),
                SslOptions = Authentication(root, SslProtocols.Tls12 | SslProtocols.Tls13) };
            handler.SslOptions.TargetHost = name;
            handler.ConnectCallback = async (context, cancellation) =>
            {
                if (context.DnsEndPoint.Port != Port || context.DnsEndPoint.Host is not ("localhost" or "127.0.0.1")) throw new IOException("Test loopback only.");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, Port, cancellation); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            };
            return new HttpClient(handler) { BaseAddress = new Uri($"{scheme}://{name}:{Port}"), Timeout = TimeSpan.FromSeconds(5) };
        }
        public async ValueTask DisposeAsync()
        {
            if (App is not null) await App.DisposeAsync(); Issuer.Dispose();
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory, false);
        }
        static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    }

    sealed class EphemeralCertificates : IDisposable
    {
        readonly RSA key = RSA.Create(2048);
        readonly List<(string Name, CngKey Key)> serverKeys = [];
        readonly ITestOutputHelper output;
        public X509Certificate2 Root { get; }
        public EphemeralCertificates(ITestOutputHelper output)
        {
            this.output = output;
            var request = new CertificateRequest("CN=Ephemeral test CA " + Guid.NewGuid().ToString("N"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddDays(7));
        }
        public X509Certificate2 Server(string variant = "valid", bool ipOnly = false)
        {
            using var serverKey = ServerKey();
            var request = new CertificateRequest(variant == "cn-only" ? "CN=localhost" : "CN=server.invalid", serverKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            if (variant != "missing-eku") request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
                { new(variant == "client-auth" ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
            if (variant != "cn-only")
            {
                var san = new SubjectAlternativeNameBuilder();
                if (variant == "wrong-san") san.AddDnsName("other.invalid");
                else { san.AddIpAddress(IPAddress.Loopback); if (!ipOnly) san.AddDnsName("localhost"); }
                request.CertificateExtensions.Add(san.Build());
            }
            var before = variant == "future" ? DateTimeOffset.UtcNow.AddDays(1) : DateTimeOffset.UtcNow.AddDays(-2);
            var after = variant == "expired" ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(2);
            using var publicCertificate = request.Create(Root, before, after, RandomNumberGenerator.GetBytes(16));
            return variant == "keyless" ? X509CertificateLoader.LoadCertificate(publicCertificate.RawData) : publicCertificate.CopyWithPrivateKey(serverKey);
        }
        RSA ServerKey()
        {
            if (!OperatingSystem.IsWindows()) return RSA.Create(2048);
            // Schannel cannot use in-memory-only keys. Create only a new, nonexportable, uniquely owned test key.
            var name = "PlatformStudio-TlsFixture-" + Guid.NewGuid().ToString("N");
            var creation = new CngKeyCreationParameters { Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                KeyCreationOptions = CngKeyCreationOptions.None, KeyUsage = CngKeyUsages.Signing, ExportPolicy = CngExportPolicies.None };
            creation.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            var owned = CngKey.Create(CngAlgorithm.Rsa, name, creation); serverKeys.Add((name, owned));
            Assert.Equal(name, owned.KeyName); Assert.Equal(CngExportPolicies.None, owned.ExportPolicy);
            return new RSACng(owned);
        }
        public void Dispose()
        {
            var deleted = 0; var failures = new List<Exception>();
            try
            {
                foreach (var owned in serverKeys)
                {
                    try
                    {
                        Assert.Equal(owned.Name, owned.Key.KeyName);
                        owned.Key.Delete();
                        if (OperatingSystem.IsWindows()) Assert.False(CngKey.Exists(owned.Name, CngProvider.MicrosoftSoftwareKeyStorageProvider,
                            CngKeyOpenOptions.UserKey), "Owned TLS fixture key cleanup failed.");
                        deleted++;
                    }
                    catch (Exception error) { failures.Add(error); }
                    finally { owned.Key.Dispose(); }
                }
                output.WriteLine("PLATFORM_FOUNDATION_TLS_CLEANUP " + JsonSerializer.Serialize(new
                { schema = "platform-studio.foundation-tls-fixture-cleanup.v1", createdTemporaryKeys = serverKeys.Count,
                    deletedTemporaryKeys = deleted, allOwnedKeysRemoved = failures.Count == 0, certificateStoreWrites = 0 }));
                if (failures.Count > 0) throw new AggregateException("Owned TLS fixture key cleanup failed.", failures);
            }
            finally { Root.Dispose(); key.Dispose(); }
        }
    }
}
