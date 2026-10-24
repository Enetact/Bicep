using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationHostConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Public_RSA_metadata_loads_without_creating_database_or_provider_connection(bool pkcs1)
    {
        using var f = new ConfigurationFixture();
        var config = f.Configuration();
        config = config with { PublicKeys = [new(IdentityTestIssuer.KeyId,
            pkcs1 ? f.Rsa.ExportRSAPublicKeyPem() : f.Rsa.ExportSubjectPublicKeyInfoPem())] };
        var options = FoundationHostConfiguration.Load(f.Write(config));
        Assert.Equal("customer-a", options.Identity.CustomerId); Assert.Equal("cell-a", options.Identity.CellId);
        Assert.Equal(Path.Combine(f.DirectoryPath, "state", "foundation.db"), options.DatabasePath);
        Assert.False(File.Exists(options.DatabasePath)); Assert.False(options.EnableWorker);
        Assert.Null(options.DisconnectedTransport);
        Assert.Single(options.Identity.SigningKeys);
        Assert.Equal($"user:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.Subject}", options.BootstrapActorKey);
        Assert.NotNull(new IdentityTokenValidator(options.Identity));
    }

    [Theory]
    [InlineData("pkcs8_private")]
    [InlineData("pkcs1_private")]
    [InlineData("malformed")]
    [InlineData("multiple")]
    [InlineData("missing")]
    [InlineData("weak_key")]
    [InlineData("duplicate_kid")]
    public void Private_malformed_weak_or_ambiguous_keys_are_rejected_without_echoing_material(string variant)
    {
        using var f = new ConfigurationFixture(); using var weak = RSA.Create(1024);
        var config = f.Configuration();
        var pem = variant switch
        {
            "pkcs8_private" => f.Rsa.ExportPkcs8PrivateKeyPem(), "pkcs1_private" => f.Rsa.ExportRSAPrivateKeyPem(),
            "malformed" => "-----BEGIN PUBLIC KEY-----\nsynthetic-invalid-key\n-----END PUBLIC KEY-----",
            "multiple" => f.Rsa.ExportSubjectPublicKeyInfoPem() + "\n" + f.Rsa.ExportSubjectPublicKeyInfoPem(),
            "weak_key" => weak.ExportSubjectPublicKeyInfoPem(), "missing" => "",
            _ => f.Rsa.ExportSubjectPublicKeyInfoPem()
        };
        config = config with { PublicKeys = [new(IdentityTestIssuer.KeyId, pem)] };
        if (variant == "duplicate_kid") config = config with { PublicKeys = [.. config.PublicKeys, .. config.PublicKeys] };
        var exception = Record.Exception(() => FoundationHostConfiguration.Load(f.Write(config)));
        Assert.NotNull(exception);
        if (pem.Length > 0) Assert.DoesNotContain(pem, exception.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(f.DirectoryPath, "state")));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("tenant")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("unknown_client_tenant")]
    [InlineData("no_clients")]
    [InlineData("workload_bootstrap")]
    [InlineData("foreign_bootstrap")]
    [InlineData("malformed_bootstrap")]
    [InlineData("zero_bootstrap")]
    [InlineData("no_human_bootstrap_client")]
    [InlineData("port_zero")]
    [InlineData("port_privileged")]
    [InlineData("port_out_of_range")]
    public void Invalid_cell_identity_or_bootstrap_binding_is_rejected_before_state_is_created(string variant)
    {
        using var f = new ConfigurationFixture();
        var config = f.Configuration();
        config = variant switch
        {
            "port_zero" => config with { ListenPort = 0 }, "port_privileged" => config with { ListenPort = 443 },
            "port_out_of_range" => config with { ListenPort = 65536 },
            "customer" => config with { CustomerId = "../customer-a" }, "cell" => config with { CellId = "cell-a\n" },
            "project" => config with { ProjectId = "*" }, "environment" => config with { EnvironmentId = "../prod" },
            "tenant" => config with { TrustedIssuersByTenant = new() { [Guid.Empty.ToString()] = [IdentityTestIssuer.Issuer()] } },
            "issuer" => config with { TrustedIssuersByTenant = new() { [IdentityTestIssuer.Tenant] = ["http://attacker.invalid/issuer"] } },
            "audience" => config with { Audience = "api://another cell" },
            "unknown_client_tenant" => config with { AllowedClients = [new(IdentityTestIssuer.OtherTenant, IdentityTestIssuer.Client, PrincipalKind.User)] },
            "no_clients" => config with { AllowedClients = [] },
            "workload_bootstrap" => config with { BootstrapActorKey = $"workload:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.WorkerSubject}:{IdentityTestIssuer.WorkerClient}" },
            "foreign_bootstrap" => config with { BootstrapActorKey = $"user:{IdentityTestIssuer.OtherTenant}:{IdentityTestIssuer.Subject}" },
            "zero_bootstrap" => config with { BootstrapActorKey = $"user:{IdentityTestIssuer.Tenant}:{Guid.Empty:D}" },
            "no_human_bootstrap_client" => config with { AllowedClients = [new(IdentityTestIssuer.Tenant,
                IdentityTestIssuer.WorkerClient, PrincipalKind.Workload, IdentityTestIssuer.WorkerSubject)] },
            _ => config with { BootstrapActorKey = "synthetic-user@example.invalid" }
        };
        Assert.NotNull(Record.Exception(() => FoundationHostConfiguration.Load(f.Write(config))));
        Assert.False(Directory.Exists(Path.Combine(f.DirectoryPath, "state")));
    }

    [Fact]
    public void Unrecognized_credential_fields_and_malformed_config_are_rejected()
    {
        using var f = new ConfigurationFixture();
        var node = JsonNode.Parse(JsonSerializer.Serialize(f.Configuration(), FoundationApplication.Json))!.AsObject();
        node["accessToken"] = "synthetic-value-must-not-be-accepted-as-configuration";
        var path = f.WriteRaw(node.ToJsonString());
        Assert.Throws<JsonException>(() => FoundationHostConfiguration.Load(path));
        path = f.WriteRaw("{");
        Assert.Throws<JsonException>(() => FoundationHostConfiguration.Load(path));
        Assert.Throws<InvalidOperationException>(() => FoundationHostConfiguration.Load(" "));
    }

    [Theory]
    [InlineData("https://dev.azure.com/")]
    [InlineData("http://example.invalid:55123/")]
    [InlineData("http://127.0.0.1:55123/not-an-origin")]
    public void Mock_transport_configuration_rejects_external_or_mis_scoped_endpoints(string endpoint)
    {
        using var f = new ConfigurationFixture();
        Assert.Throws<PipelineContractException>(() => FoundationHostConfiguration.Load(f.Write(f.Configuration() with { MockEndpoint = endpoint })));
    }

    [Fact]
    public void Explicit_mock_transport_is_loopback_only_and_is_not_started_by_configuration_loader()
    {
        using var f = new ConfigurationFixture();
        var options = FoundationHostConfiguration.Load(f.Write(f.Configuration() with { MockEndpoint = "http://127.0.0.1:55123/" }));
        Assert.True(options.EnableWorker); Assert.IsType<PipelineLoopbackTransport>(options.DisconnectedTransport);
        ((IDisposable)options.DisconnectedTransport!).Dispose();
        Assert.False(File.Exists(options.DatabasePath));
    }

    sealed class ConfigurationFixture : IDisposable
    {
        public RSA Rsa { get; } = RSA.Create(2048);
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "platform-foundation-config-" + Guid.NewGuid().ToString("N"));
        string ConfigPath => Path.Combine(DirectoryPath, "synthetic-cell.json");

        public FoundationHostConfiguration.CellConfiguration Configuration() => new("cell-a", "customer-a", IdentityTestIssuer.Audience,
            Path.Combine("state", "foundation.db"), $"user:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.Subject}", "project-a", "dev",
            new() { [IdentityTestIssuer.Tenant] = [IdentityTestIssuer.Issuer()] },
            [new(IdentityTestIssuer.Tenant, IdentityTestIssuer.Client, PrincipalKind.User)],
            [new(IdentityTestIssuer.KeyId, Rsa.ExportSubjectPublicKeyInfoPem())]);
        public string Write(FoundationHostConfiguration.CellConfiguration config) => WriteRaw(JsonSerializer.Serialize(config, FoundationApplication.Json));
        public string WriteRaw(string text) { Directory.CreateDirectory(DirectoryPath); File.WriteAllText(ConfigPath, text); return ConfigPath; }
        public void Dispose()
        {
            Rsa.Dispose();
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            if (Directory.Exists(DirectoryPath) && !Directory.EnumerateFileSystemEntries(DirectoryPath).Any()) Directory.Delete(DirectoryPath);
        }
    }
}
