using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class CredentialRewrapTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Rewrap_preserves_authority_and_token_while_allowing_existing_destination_key_custody(string provider)
    {
        using var f = new CredentialFixture(provider); await f.StoreToken();
        var before = await f.Vault.GetProfileAsync(f.Scope.AuthProfileId); var path = Envelope(f); var original = await File.ReadAllBytesAsync(path);
        var destination = Destination(f); var after = await destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1");
        Assert.Equal(JsonSerializer.Serialize(before with { Generation = 2 }), JsonSerializer.Serialize(after));
        var json = await File.ReadAllTextAsync(path); Assert.DoesNotContain(CredentialFixture.Token, json);
        Assert.Equal("key-2", JsonNode.Parse(json)!["keyId"]!.GetValue<string>()); Assert.NotEqual(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(f.VaultRoot, ".write-*"));
        f.Keys.Values.Remove("key-1"); // Synthetic provider only: demonstrate that the live envelope no longer needs its former key.
        using var lease = await destination.ResolveAsync(f.Scope); Assert.Equal(2, lease.Profile.Generation);
        await Assert.ThrowsAsync<CredentialException>(() => destination.RevalidateAsync(f.Scope, 1));
        var handler = new HeldHandler(); handler.Release.TrySetResult();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(destination, new Permits(f), handler, f.Clock);
        await transport.SendAsync(f.Wire, default); Assert.Equal("Bearer " + CredentialFixture.Token, Assert.Single(handler.Authorizations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rewrapping_expired_or_revoked_profiles_never_renews_or_reenables_them(bool revoked)
    {
        using var f = new CredentialFixture(); await f.StoreToken();
        if (revoked) await f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1); else f.Clock.Now = f.Clock.Now.AddHours(2);
        var before = await f.Vault.GetProfileAsync(f.Scope.AuthProfileId); var destination = Destination(f);
        var after = await destination.RewrapAsync(f.Scope.AuthProfileId, before.Generation, "key-1");
        Assert.Equal(JsonSerializer.Serialize(before with { Generation = before.Generation + 1 }), JsonSerializer.Serialize(after));
        Assert.Equal("credential-not-current-or-authorized", (await Assert.ThrowsAsync<CredentialException>(() => destination.ResolveAsync(f.Scope))).Code);
    }

    [Theory]
    [InlineData("generation", "credential-generation-conflict")]
    [InlineData("source", "credential-rewrap-source-key-conflict")]
    [InlineData("same", "credential-rewrap-same-key")]
    [InlineData("zero", "credential-rewrap-input-invalid")]
    [InlineData("overflow", "credential-rewrap-input-invalid")]
    [InlineData("invalid-source", "credential-rewrap-input-invalid")]
    public async Task Exact_source_key_and_generation_are_required_without_modifying_the_envelope(string mutation, string code)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var before = await File.ReadAllBytesAsync(Envelope(f));
        var vault = mutation == "same" ? f.Reopen() : Destination(f);
        var generation = mutation switch { "generation" => 2, "zero" => 0, "overflow" => long.MaxValue, _ => 1 };
        var source = mutation switch { "source" => "other-key", "invalid-source" => "../invalid", _ => "key-1" };
        var error = await Assert.ThrowsAsync<CredentialException>(() => vault.RewrapAsync(f.Scope.AuthProfileId, generation, source));
        Assert.Equal(code, error.Code); Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(Envelope(f))); Assert.Empty(Directory.GetFiles(f.VaultRoot, ".write-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Customer_and_cell_rebinding_fail_before_destination_key_resolution(bool cell)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); Destination(f); var before = await File.ReadAllBytesAsync(Envelope(f));
        var foreign = f.Options with { EncryptionKeyId = "key-2", CustomerId = cell ? f.Options.CustomerId : "10000000-0000-0000-0000-000000000099",
            CellId = cell ? "20000000-0000-0000-0000-000000000099" : f.Options.CellId };
        f.Keys.Requests.Clear();
        Assert.Throws<CredentialException>(() => new CredentialVault(foreign, f.Keys, f.Clock));
        Assert.Empty(f.Keys.Requests); Assert.Equal(before, await File.ReadAllBytesAsync(Envelope(f)));
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("scope")]
    [InlineData("generation")]
    [InlineData("enabled")]
    public async Task Authenticated_source_metadata_is_not_trusted_merely_because_expected_fields_match(string field)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var destination = Destination(f); var path = Envelope(f);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        switch (field)
        {
            case "scope": json["profile"]!["scope"]!["environmentId"] = "prod"; break;
            case "generation": json["profile"]!["generation"] = 7; break;
            case "enabled": json["profile"]!["enabled"] = false; break;
            default: json["tag"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)); break;
        }
        await File.WriteAllTextAsync(path, json.ToJsonString()); var before = await File.ReadAllBytesAsync(path); f.Keys.Requests.Clear();
        Assert.Equal("credential-envelope-integrity", (await Assert.ThrowsAsync<CredentialException>(() =>
            destination.RewrapAsync(f.Scope.AuthProfileId, field == "generation" ? 7 : 1, "key-1"))).Code);
        Assert.DoesNotContain(f.Keys.Requests, r => r.KeyId == "key-2"); Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("key-1")]
    [InlineData("key-2")]
    public async Task Unavailable_source_or_unprovisioned_destination_keys_leave_original_ciphertext_intact(string unavailable)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var destination = Destination(f); var before = await File.ReadAllBytesAsync(Envelope(f));
        var heldSyntheticKey = f.Keys.Values[unavailable];
        f.Keys.Values.Remove(unavailable);
        var error = await Assert.ThrowsAsync<CredentialException>(() => destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1"));
        Assert.Equal("credential-key-unavailable", error.Code); Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(Envelope(f))); Assert.Empty(Directory.GetFiles(f.VaultRoot, ".write-*"));
        f.Keys.Values[unavailable] = heldSyntheticKey;
        // Restore only the original synthetic material; either failure must release the guard for later maintenance.
        Assert.Equal(2, (await destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1")).Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_admission_or_during_destination_lookup_preserves_original_and_releases_guard(bool duringWrite)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var destination = Destination(f); var before = await File.ReadAllBytesAsync(Envelope(f));
        using var cancellation = new CancellationTokenSource();
        if (duringWrite) f.Keys.OnGet = () => { if (f.Keys.Requests[^1].KeyId == "key-2") cancellation.Cancel(); };
        else cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(before, await File.ReadAllBytesAsync(Envelope(f))); Assert.Empty(Directory.GetFiles(f.VaultRoot, ".write-*"));
        f.Keys.OnGet = null; Assert.Equal(2, (await destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1")).Generation);
    }

    [Fact]
    public async Task Competing_vault_instances_cannot_commit_two_rewraps_against_one_generation()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var first = Destination(f);
        var second = new CredentialVault(f.Options with { EncryptionKeyId = "key-2" }, f.Keys, f.Clock);
        async Task<bool> Attempt(CredentialVault vault)
        {
            try { await vault.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1"); return true; }
            catch (CredentialException error) when (error.Code == "credential-generation-conflict") { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(first), Attempt(second)), committed => committed);
        using var lease = await first.ResolveAsync(f.Scope); Assert.Equal(2, lease.Profile.Generation);
        Assert.Single(Directory.GetFiles(f.VaultRoot, "*.credential.json")); Assert.Empty(Directory.GetFiles(f.VaultRoot, ".write-*"));
    }

    [Fact]
    public async Task Dispatch_admission_orders_rewrap_but_remote_response_does_not_hold_the_custody_gate()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var destination = Destination(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permits = new Permits(f) { BeforeFinal = async ct => { entered.TrySetResult(); await allow.Task.WaitAsync(ct); } };
        var handler = new HeldHandler(); using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        var send = transport.SendAsync(f.Wire, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rewrap = destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1"); await Task.Delay(80);
            Assert.False(rewrap.IsCompleted); Assert.Empty(handler.Authorizations);
            allow.TrySetResult(); await handler.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, (await rewrap.WaitAsync(TimeSpan.FromSeconds(5))).Generation); Assert.False(send.IsCompleted);
            using var lease = await destination.ResolveAsync(f.Scope); Assert.Equal(2, lease.Profile.Generation);
            Assert.Equal("Bearer " + CredentialFixture.Token, Assert.Single(handler.Authorizations));
        }
        finally { allow.TrySetResult(); handler.Release.TrySetResult(); await send; }
    }

    [Fact]
    public async Task Native_DPAPI_rewrap_reopens_with_destination_key_and_preserves_both_key_files()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new CredentialFixture(); var keys = f.ProvisionNativeKeys("native-keys");
        using (await keys.ProvisionKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1")) { }
        using (await keys.ProvisionKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-2")) { }
        var keyFiles = Directory.GetFiles(f.Root, "*.dpapi.json", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var original = new CredentialVault(f.Options, keys, f.Clock); using var token = CredentialSecret.FromUtf8(CredentialFixture.Token);
        await original.PutAsync(f.Scope, token, f.Clock.Now.AddHours(1));
        var destination = new CredentialVault(f.Options with { EncryptionKeyId = "key-2" }, keys, f.Clock);
        Assert.Equal(2, (await destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1")).Generation);
        var reopenedKeys = new CredentialWindowsUserKeyProvider(Path.Combine(f.Root, "native-keys"), f.Scope.CustomerId, f.Scope.CellId);
        using var lease = await new CredentialVault(f.Options with { EncryptionKeyId = "key-2" }, reopenedKeys, f.Clock).ResolveAsync(f.Scope);
        Assert.Equal(2, lease.Profile.Generation); Assert.Equal(2, keyFiles.Count);
        Assert.All(keyFiles, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }

    static string Envelope(CredentialFixture f) => Assert.Single(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
    static CredentialVault Destination(CredentialFixture f)
    {
        f.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        return new(f.Options with { EncryptionKeyId = "key-2" }, f.Keys, f.Clock);
    }
    sealed class Permits(CredentialFixture f) : ICredentialDispatchPermitSource
    {
        int calls;
        public Func<CancellationToken, Task>? BeforeFinal { get; init; }
        public async Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 2 && BeforeFinal is not null) await BeforeFinal(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); return CredentialDispatchPermit.FromDurableDispatch(f.Dispatch, wire, f.Scope, f.Clock.Now);
        }
    }
    sealed class HeldHandler : HttpMessageHandler
    {
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Authorizations { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization!.ToString()); Admitted.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"workflow_run_id\":123,\"run_url\":\"https://api.github.com/repos/customer-org/customer-repo/actions/runs/123\",\"html_url\":\"https://github.com/customer-org/customer-repo/actions/runs/123\"}") };
        }
    }
}
