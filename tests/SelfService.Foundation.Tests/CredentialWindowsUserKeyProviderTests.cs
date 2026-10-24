using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class CredentialWindowsUserKeyProviderTests
{
    [WindowsCredentialFact]
    public async Task DPAPI_CurrentUser_reopens_a_customer_vault_without_plaintext_key_files()
    {
        using var f = new CredentialFixture(); var baseRoot = Path.Combine(f.Root, "custody");
        var provider = new CredentialWindowsUserKeyProvider(baseRoot, f.Scope.CustomerId, f.Scope.CellId);
        var vault = new CredentialVault(f.Options, provider, f.Clock); using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
        await vault.PutAsync(f.Scope, secret, f.Clock.Now.AddHours(1));
        var reopenedProvider = new CredentialWindowsUserKeyProvider(baseRoot, f.Scope.CustomerId, f.Scope.CellId);
        var reopened = new CredentialVault(f.Options, reopenedProvider, f.Clock); using var lease = await reopened.ResolveAsync(f.Scope);
        Assert.Equal(1, lease.Profile.Generation);
        var files = Directory.GetFiles(baseRoot, "*", SearchOption.AllDirectories); Assert.Single(files, p => p.EndsWith(".dpapi.json", StringComparison.Ordinal));
        foreach (var file in files.Where(p => p.EndsWith(".json", StringComparison.Ordinal))) Assert.DoesNotContain(CredentialFixture.Token, await File.ReadAllTextAsync(file));
    }

    [WindowsCredentialFact]
    public void Key_directory_is_private_to_the_current_Windows_identity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new CredentialFixture(); var baseRoot = Path.Combine(f.Root, "custody");
        _ = new CredentialWindowsUserKeyProvider(baseRoot, f.Scope.CustomerId, f.Scope.CellId);
        var directory = new DirectoryInfo(Assert.Single(Directory.GetDirectories(baseRoot)));
        var security = directory.GetAccessControl(); Assert.True(security.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        var rule = Assert.Single(rules.Cast<FileSystemAccessRule>());
        Assert.Equal(identity.User, rule.IdentityReference); Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }

    [WindowsCredentialFact]
    public async Task Customer_cell_and_key_purpose_binding_cannot_be_retargeted()
    {
        using var f = new CredentialFixture(); var baseRoot = Path.Combine(f.Root, "custody");
        var provider = new CredentialWindowsUserKeyProvider(baseRoot, f.Scope.CustomerId, f.Scope.CellId);
        using (await provider.GetKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1", default)) { }
        await Assert.ThrowsAsync<CredentialException>(async () => { using var key = await provider.GetKeyAsync("10000000-0000-0000-0000-000000000099", f.Scope.CellId, "key-1", default); });
        await Assert.ThrowsAsync<CredentialException>(async () => { using var key = await provider.GetKeyAsync(f.Scope.CustomerId, "20000000-0000-0000-0000-000000000099", "key-1", default); });
        var source = Assert.Single(Directory.GetFiles(baseRoot, "*.dpapi.json", SearchOption.AllDirectories));
        var destination = Path.Combine(Path.GetDirectoryName(source)!, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("key-2"))).ToLowerInvariant() + ".dpapi.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(source))!; json["keyId"] = "key-2";
        await File.WriteAllTextAsync(destination, json.ToJsonString());
        await Assert.ThrowsAsync<CredentialException>(async () => { using var key = await provider.GetKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-2", default); });
    }

    [WindowsCredentialFact]
    public async Task Native_DPAPI_rejects_tampered_ciphertext_and_oversized_envelopes()
    {
        using var f = new CredentialFixture(); var baseRoot = Path.Combine(f.Root, "custody");
        var provider = new CredentialWindowsUserKeyProvider(baseRoot, f.Scope.CustomerId, f.Scope.CellId);
        using (await provider.GetKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1", default)) { }
        var path = Assert.Single(Directory.GetFiles(baseRoot, "*.dpapi.json", SearchOption.AllDirectories));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!; json["protectedKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(256));
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await Assert.ThrowsAsync<CredentialException>(async () => { using var key = await provider.GetKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1", default); });
        await File.WriteAllTextAsync(path, new string('x', 16385));
        await Assert.ThrowsAsync<CredentialException>(async () => { using var key = await provider.GetKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1", default); });
    }

    [WindowsCredentialFact]
    public void Provider_refuses_to_adopt_a_nonempty_unowned_directory_or_foreign_marker()
    {
        using var f = new CredentialFixture(); var baseRoot = Path.Combine(f.Root, "custody");
        var leaf = Path.Combine(baseRoot, "cell-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Scope.CustomerId + ":" + f.Scope.CellId))).ToLowerInvariant());
        Directory.CreateDirectory(leaf); File.WriteAllText(Path.Combine(leaf, "unrelated.txt"), "preserve");
        Assert.Throws<CredentialException>(() => new CredentialWindowsUserKeyProvider(baseRoot, f.Scope.CustomerId, f.Scope.CellId));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(leaf, "unrelated.txt")));
        File.WriteAllText(Path.Combine(leaf, "cell-binding.json"), "{\"schema\":1,\"customerId\":\"foreign\",\"cellId\":\"foreign\"}");
        Assert.Throws<CredentialException>(() => new CredentialWindowsUserKeyProvider(baseRoot, f.Scope.CustomerId, f.Scope.CellId));
    }

    [WindowsCredentialFact]
    public async Task Concurrent_native_custody_instances_generate_one_durable_key()
    {
        using var f = new CredentialFixture(); var baseRoot = Path.Combine(f.Root, "custody");
        var a = new CredentialWindowsUserKeyProvider(baseRoot, f.Scope.CustomerId, f.Scope.CellId);
        var b = new CredentialWindowsUserKeyProvider(baseRoot, f.Scope.CustomerId, f.Scope.CellId);
        await Task.WhenAll(Task.Run(async () => { using var key = await a.GetKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1", default); }),
            Task.Run(async () => { using var key = await b.GetKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1", default); }));
        Assert.Single(Directory.GetFiles(baseRoot, "*.dpapi.json", SearchOption.AllDirectories));
        using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); var first = new CredentialVault(f.Options, a, f.Clock);
        await first.PutAsync(f.Scope, secret, f.Clock.Now.AddHours(1)); using var lease = await new CredentialVault(f.Options, b, f.Clock).ResolveAsync(f.Scope);
        Assert.Equal(1, lease.Profile.Generation);
    }
}

public sealed class WindowsCredentialFactAttribute : FactAttribute
{
    public WindowsCredentialFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows CurrentUser DPAPI/ACL qualification only."; }
}
