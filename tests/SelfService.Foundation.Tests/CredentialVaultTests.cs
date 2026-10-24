using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class CredentialVaultTests
{
    [Fact]
    public async Task Stores_only_authenticated_ciphertext_and_reopens_with_customer_key()
    {
        using var f = new CredentialFixture(); using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
        var profile = await f.Vault.PutAsync(f.Scope, secret, f.Clock.Now.AddHours(1));
        Assert.Equal(1, profile.Generation); Assert.True(profile.Enabled);
        var file = Assert.Single(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
        var bytes = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain(CredentialFixture.Token, bytes); Assert.Contains("ciphertext", bytes);
        using var lease = await f.Reopen().ResolveAsync(f.Scope);
        Assert.Equal(1, lease.Profile.Generation); Assert.Equal(f.Scope.AuthProfileId, lease.Profile.Scope.AuthProfileId);
        Assert.DoesNotContain(CredentialFixture.Token, JsonSerializer.Serialize(lease.Profile));
        Assert.All(f.Keys.Requests, r => { Assert.Equal(f.Scope.CustomerId, r.Customer); Assert.Equal(f.Scope.CellId, r.Cell); });
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("connection")]
    [InlineData("generation")]
    [InlineData("digest")]
    [InlineData("profile")]
    [InlineData("audience")]
    [InlineData("scopes")]
    public async Task Each_customer_connection_and_permission_binding_is_enforced(string mutation)
    {
        using var f = new CredentialFixture(); await f.StoreToken();
        var altered = mutation switch
        {
            "customer" => f.Scope with { CustomerId = "10000000-0000-0000-0000-000000000099" },
            "cell" => f.Scope with { CellId = "20000000-0000-0000-0000-000000000099" },
            "project" => f.Scope with { ProjectId = "another" },
            "environment" => f.Scope with { EnvironmentId = "prod" },
            "connection" => f.Scope with { ConnectionId = "another" },
            "generation" => f.Scope with { ConnectionGeneration = 2 },
            "digest" => f.Scope with { ConnectionDigest = new string('b', 64) },
            "profile" => f.Scope with { AuthProfileId = "profile/other" },
            "audience" => f.Scope with { Audience = "https://graph.microsoft.com" },
            _ => f.Scope with { RequiredScopes = ["actions:write", "contents:read"] }
        };
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(altered));
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
    }

    [Fact]
    public async Task Expiry_revocation_and_generation_rotation_invalidate_prior_leases()
    {
        using var f = new CredentialFixture(); await f.StoreToken();
        using var lease = await f.Vault.ResolveAsync(f.Scope);
        var rotated = f.Scope with { ConnectionGeneration = 2, ConnectionDigest = new string('b', 64) };
        using var replacement = CredentialSecret.FromUtf8("synthetic-rotated-opaque-token");
        await f.Vault.PutAsync(rotated, replacement, f.Clock.Now.AddMinutes(10), expectedGeneration: 1);
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.RevalidateAsync(f.Scope, lease.Profile.Generation));
        Assert.Equal(2, (await f.Vault.GetProfileAsync(rotated.AuthProfileId)).Generation);
        await f.Vault.RevokeAsync(rotated.AuthProfileId, expectedGeneration: 2);
        Assert.False((await f.Vault.GetProfileAsync(rotated.AuthProfileId)).Enabled);
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(rotated));
    }

    [Fact]
    public async Task Reopened_vault_does_not_reenable_revoked_profiles_and_expired_tokens_fail_closed()
    {
        using var f = new CredentialFixture(); await f.StoreToken();
        await f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1);
        await Assert.ThrowsAsync<CredentialException>(() => f.Reopen().ResolveAsync(f.Scope));
        using var other = new CredentialFixture(); using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
        await other.Vault.PutAsync(other.Scope, secret, other.Clock.Now.AddSeconds(45));
        other.Clock.Now = other.Clock.Now.AddSeconds(31);
        await Assert.ThrowsAsync<CredentialException>(() => other.Reopen().ResolveAsync(other.Scope));
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("ciphertext")]
    [InlineData("environment")]
    [InlineData("generation")]
    [InlineData("enabled")]
    public async Task Ciphertext_and_public_binding_metadata_tampering_are_detected(string field)
    {
        using var f = new CredentialFixture(); await f.StoreToken();
        var file = Assert.Single(Directory.GetFiles(f.VaultRoot, "*.credential.json")); var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        switch (field)
        {
            case "environment": json["profile"]!["scope"]!["environmentId"] = "prod"; break;
            case "generation": json["profile"]!["generation"] = 100; break;
            case "enabled": json["profile"]!["enabled"] = false; break;
            default: json[field] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(field == "tag" ? 16 : 64)); break;
        }
        await File.WriteAllTextAsync(file, json.ToJsonString());
        Assert.Equal("credential-envelope-integrity", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Scope))).Code);
    }

    [Fact]
    public async Task Wrong_key_wrong_customer_and_duplicate_json_fail_without_exposing_material()
    {
        using var f = new CredentialFixture(); await f.StoreToken();
        var wrong = new CredentialTestKeys(); var vault = f.Reopen(wrong);
        Assert.Equal("credential-envelope-integrity", (await Assert.ThrowsAsync<CredentialException>(() => vault.ResolveAsync(f.Scope))).Code);
        var foreign = new CredentialVault(f.Options with { CustomerId = "10000000-0000-0000-0000-000000000099" }, f.Keys, f.Clock);
        await Assert.ThrowsAsync<CredentialException>(() => foreign.GetProfileAsync(f.Scope.AuthProfileId));
        var file = Assert.Single(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
        var json = await File.ReadAllTextAsync(file); json = json.Replace("\"schema\":1", "\"schema\":1,\"Schema\":1", StringComparison.Ordinal);
        await File.WriteAllTextAsync(file, json);
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Scope));
    }

    [Fact]
    public async Task Compare_and_swap_prevents_lost_updates_and_retargeting_a_stable_profile()
    {
        using var f = new CredentialFixture(); using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
        await f.Vault.PutAsync(f.Scope, secret, f.Clock.Now.AddMinutes(10));
        Assert.Equal("credential-generation-conflict", (await Assert.ThrowsAsync<CredentialException>(() => f.Reopen().PutAsync(f.Scope, secret, f.Clock.Now.AddMinutes(10)))).Code);
        Assert.Equal("credential-profile-retarget-denied", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.PutAsync(f.Scope with { ProjectId = "other" }, secret, f.Clock.Now.AddMinutes(10), 1))).Code);
        Assert.Single(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
        Assert.Empty(Directory.GetFiles(f.VaultRoot, ".write-*"));
    }

    [Fact]
    public async Task Concurrent_creates_admit_one_generation_and_never_leave_a_torn_secret()
    {
        using var f = new CredentialFixture();
        async Task<bool> Attempt(CredentialVault vault)
        {
            using var material = CredentialSecret.FromUtf8(CredentialFixture.Token);
            try { await vault.PutAsync(f.Scope, material, f.Clock.Now.AddHours(1)); return true; }
            catch (CredentialException e) when (e.Code == "credential-generation-conflict") { return false; }
        }
        var results = await Task.WhenAll(Attempt(f.Vault), Attempt(f.Reopen())); Assert.Single(results, v => v);
        using var lease = await f.Reopen().ResolveAsync(f.Scope); Assert.Equal(1, lease.Profile.Generation);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("profile/../../escape")]
    [InlineData("profile\n")]
    [InlineData("/rooted")]
    public async Task Credential_profile_identifiers_cannot_select_paths(string id)
    {
        using var f = new CredentialFixture(); await Assert.ThrowsAsync<CredentialException>(() => f.Vault.GetProfileAsync(id));
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("Bearer opaque-token")]
    [InlineData("embedded\rheader")]
    [InlineData("unicode-\u03b1")]
    public async Task Header_injection_and_non_token_material_are_rejected(string value)
    {
        using var f = new CredentialFixture(); using var secret = CredentialSecret.FromUtf8(value);
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.PutAsync(f.Scope, secret, f.Clock.Now.AddMinutes(10)));
        Assert.Empty(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
    }

    [Fact]
    public async Task Secret_key_and_lease_types_deny_serialization_and_redact_display()
    {
        using var f = new CredentialFixture(); using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); using var key = new CredentialEncryptionKey(RandomNumberGenerator.GetBytes(32));
        await f.StoreToken(); using var lease = await f.Vault.ResolveAsync(f.Scope);
        foreach (var value in new object[] { secret, key, lease })
        {
            var error = Assert.Throws<JsonException>(() => JsonSerializer.Serialize(value, value.GetType()));
            Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.DoesNotContain(CredentialFixture.Token, value.ToString());
        }
        secret.Dispose(); await Assert.ThrowsAsync<CredentialException>(() => f.Vault.PutAsync(f.Scope, secret, f.Clock.Now.AddMinutes(10), 1));
    }

    [Fact]
    public async Task Key_provider_errors_are_sanitized_and_rotation_keeps_custody_external()
    {
        using var f = new CredentialFixture(); await f.StoreToken();
        f.Keys.Fail = true;
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Scope));
        Assert.Equal("credential-key-unavailable", error.Code); Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
        f.Keys.Fail = false; f.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        var rotated = new CredentialVault(f.Options with { EncryptionKeyId = "key-2" }, f.Keys, f.Clock);
        using var token = CredentialSecret.FromUtf8("synthetic-rotated-token"); await rotated.PutAsync(f.Scope, token, f.Clock.Now.AddMinutes(10), 1);
        using var lease = await rotated.ResolveAsync(f.Scope); Assert.Equal(2, lease.Profile.Generation);
        Assert.Contains(f.Keys.Requests, r => r.KeyId == "key-2");
    }
}

internal sealed class CredentialClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class CredentialTestKeys : ICredentialKeyProvider
{
    public Dictionary<string, byte[]> Values { get; } = new() { ["key-1"] = RandomNumberGenerator.GetBytes(32) };
    public List<(string Customer, string Cell, string KeyId)> Requests { get; } = [];
    public bool Fail { get; set; }
    public Action? OnGet { get; set; }
    public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Requests.Add((customerId, cellId, keyId)); OnGet?.Invoke();
        if (Fail || !Values.TryGetValue(keyId, out var value)) throw new InvalidOperationException(CredentialFixture.Token);
        return ValueTask.FromResult(new CredentialEncryptionKey(value));
    }
}
internal sealed class CredentialFixture : IDisposable
{
    public const string Token = "synthetic-customer-opaque-access-token-0123456789";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "studio-credential-tests-" + Guid.NewGuid().ToString("N"));
    public string VaultRoot => Path.Combine(Root, "vault");
    public CredentialTestKeys Keys { get; } = new();
    public CredentialClock Clock { get; } = new();
    public CredentialVaultOptions Options { get; }
    public CredentialVault Vault { get; }
    public CredentialScope Scope { get; }
    public PipelineConnection Connection { get; }
    public PipelineDispatchRequest Request { get; }
    public FoundationDispatch Dispatch { get; }
    public PipelineWireRequest Wire { get; }
    public CredentialFixture(string provider = PipelineRequestFactory.GitHub)
    {
        const string customer = "10000000-0000-0000-0000-000000000001", cell = "20000000-0000-0000-0000-000000000001";
        Connection = new(customer, cell, "customer-ci", provider, "customer-org", provider == PipelineRequestFactory.AzureDevOps ? "e1111111-1111-1111-1111-111111111111" : null,
            provider == PipelineRequestFactory.GitHub ? "customer-repo" : "d1111111-1111-1111-1111-111111111111", provider == PipelineRequestFactory.GitHub ? "studio.yml" : "42",
            ["main"], ["dev"], [.. PipelineRequestFactory.ReservedInputs, "location"], "profile/customer-ci", 1)
        { EnforcesSourceRevision = true, PipelineVersion = provider == PipelineRequestFactory.AzureDevOps ? 4 : null };
        var store = new FoundationStore(Path.Combine(Root, "cell.db"), cell, customer, Clock); store.BootstrapAdministrator("admin");
        foreach (var (actor, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator") })
            store.SetMembership(customer, "admin", actor, "default", "dev", [role]);
        var config = JsonSerializer.Serialize(new { Connection.Provider, Connection.Organization, Connection.Project, Connection.Repository,
            Connection.PipelineOrWorkflowId, Connection.AllowedRefs, Connection.AllowedEnvironments, Connection.AllowedInputKeys,
            authProfileId = Connection.CredentialReference, Connection.EnforcesSourceRevision, Connection.PipelineVersion, enabled = true }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        store.PutConnection(customer, "admin", Connection.Id, provider, config);
        Request = new(customer, cell, Connection.Id, 1, "30000000-0000-0000-0000-000000000001", "main", "dev", new string('a', 40), new() { ["location"] = "eastus2" });
        var plan = store.CreatePlan(customer, "requester", new("default", "dev", Connection.Id, "pipeline.dispatch", JsonSerializer.Serialize(Request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), true, Clock.Now.AddMinutes(20), Request.SourceRevision));
        store.ApprovePlan(customer, "approver", plan.Id, plan.Digest); var job = store.EnqueuePlan(customer, "operator", plan.Id, plan.Digest, "fixture-request");
        var claim = store.ClaimNext(customer, "credential-worker", TimeSpan.FromMinutes(2))!; Dispatch = store.StartDispatch(customer, job.Id, "credential-worker", claim.Fence);
        Wire = PipelineRequestFactory.Create(Connection, Request, new(customer, cell, Connection.Id, 1, PipelineRequestFactory.ConnectionDigest(Connection),
            PipelineRequestFactory.RequestDigest(Connection, Request), plan.Digest, plan.ExpiresAt), Clock.Now);
        Scope = new(customer, cell, "default", "dev", provider, Connection.Id, 1, PipelineRequestFactory.ConnectionDigest(Connection), Connection.CredentialReference,
            provider == PipelineRequestFactory.GitHub ? "https://api.github.com" : "499b84ac-1321-427f-aa17-267ca6975798",
            provider == PipelineRequestFactory.GitHub ? ["actions:write"] : ["499b84ac-1321-427f-aa17-267ca6975798/.default"]);
        Options = new(VaultRoot, customer, cell, "key-1"); Vault = new(Options, Keys, Clock);
    }
    public async Task StoreToken() { using var secret = CredentialSecret.FromUtf8(Token); await Vault.PutAsync(Scope, secret, Clock.Now.AddHours(1)); }
    public CredentialVault Reopen(ICredentialKeyProvider? keys = null) => new(Options, keys ?? Keys, Clock);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}
