using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class PortalCredentialCheckpointTests
{
    [Fact] public void ResumeExcludesHttpAndAhpActionsWhileOrdinaryCancellationCanOverlap()
    {
        var gate = new SessionResumeGate();
        using (gate.BeginAction())
        {
            using var cancellation = gate.BeginAction(); Assert.False(gate.TryBeginResume());
        }
        Assert.True(gate.TryBeginResume()); Assert.False(gate.TryBeginResume());
        Assert.Throws<PortalException>(() => gate.BeginAction()); gate.EndResume();
        using var next = gate.BeginAction();
    }
    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "portal-checkpoint-tests", Guid.NewGuid().ToString("N"));
        public string Azure { get; }
        public string Codex { get; }
        public string Cache => Path.Combine(Root, ".local/portal/credential-cache/5087/restart.bin");
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "config")); File.WriteAllText(Path.Combine(Root, "config/workloads.json"), "{}");
            Azure = Path.Combine(Root, ".local/portal/azure-cli", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Azure);
            Codex = Path.Combine(Root, ".local/portal/agents", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Codex);
            File.WriteAllText(Path.Combine(Azure, "azureProfile.json"), "synthetic-profile-only");
            File.WriteAllText(Path.Combine(Azure, "msal_token_cache.bin"), "synthetic-microsoft-only");
            File.WriteAllText(Path.Combine(Codex, "auth.json"), "synthetic-codex-only");
            File.WriteAllText(Path.Combine(Codex, "config.toml"), "must-not-copy-config");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    [Fact] public void ProtectedRoundTripContainsOnlyAllowlistedFilesAndIsSingleUse()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var cache = new PortalCredentialCheckpoint(f.Root, 5087);
        var proof = cache.Checkpoint(f.Azure, f.Codex);
        Assert.DoesNotContain("synthetic", Encoding.UTF8.GetString(File.ReadAllBytes(f.Cache)));
        Assert.DoesNotContain(proof, Encoding.UTF8.GetString(File.ReadAllBytes(f.Cache)));
        Assert.Equal(proof, cache.RecoveryProof());
        using (var lease = cache.Open(proof, "fresh-browser-csrf", false))
        {
            Assert.Equal(2, lease.Data.AzureFiles.Count); Assert.Equal("synthetic-codex-only", Encoding.UTF8.GetString(lease.Data.CodexAuth!));
            Assert.DoesNotContain("config.toml", lease.Data.AzureFiles.Keys);
            lease.AzureRestored(); lease.CodexRestored(); lease.Complete();
        }
        Assert.False(File.Exists(f.Cache)); Assert.Throws<PortalException>(() => cache.Open(proof, "fresh-browser-csrf", false));
    }
    [Fact] public void ClaimCannotBeReusedByAnotherBrowserAndPartialRetryCannotCloneRestoredProvider()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var cache = new PortalCredentialCheckpoint(f.Root, 5087); var proof = cache.Checkpoint(f.Azure, f.Codex);
        using (var lease = cache.Open(proof, "browser-one", false))
        {
            lease.AzureRestored(); lease.GitHubAttempted();
            Assert.Throws<PortalException>(() => cache.Open(proof, "browser-one", false)); // exclusive active lease
        }
        Assert.Throws<InvalidOperationException>(() => cache.RecoveryProof());
        Assert.Throws<PortalException>(() => cache.Open(proof, "browser-two", false));
        using var retry = cache.Open(proof, "browser-one", true);
        Assert.Empty(retry.Data.AzureFiles); Assert.NotNull(retry.Data.CodexAuth); Assert.False(retry.Data.AttachGitHub);
    }
    [Fact] public void ExplicitDisconnectRemovesUnrestoredBoundProviderAndPreservesOtherProvider()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var cache = new PortalCredentialCheckpoint(f.Root, 5087); var proof = cache.Checkpoint(f.Azure, f.Codex);
        using (cache.Open(proof, "browser-one", false)) { }
        cache.Forget("other-browser", "microsoft");
        using (var lease = cache.Open(proof, "browser-one", false)) Assert.Equal(2, lease.Data.AzureFiles.Count);
        cache.Forget("browser-one", "microsoft");
        using (var lease = cache.Open(proof, "browser-one", false)) { Assert.Empty(lease.Data.AzureFiles); Assert.NotNull(lease.Data.CodexAuth); }
        cache.Forget("browser-one", "codex"); Assert.False(File.Exists(f.Cache));
    }
    [Fact] public void WrongProofExistingSessionAndPendingOverwriteAreRejectedWithoutDeletingCheckpoint()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var cache = new PortalCredentialCheckpoint(f.Root, 5087); var proof = cache.Checkpoint(f.Azure, f.Codex);
        Assert.Throws<PortalException>(() => cache.Open(new string('0', 64), "new", false));
        Assert.Throws<PortalException>(() => cache.Open(proof, "new", true));
        Assert.Throws<InvalidOperationException>(() => cache.Checkpoint(f.Azure, f.Codex));
        Assert.Equal(proof, cache.RecoveryProof());
    }
    [Fact] public void TamperWrongRepositoryAndWrongPortCannotDecrypt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var other = new Fixture();
        var cache = new PortalCredentialCheckpoint(f.Root, 5087); var proof = cache.Checkpoint(f.Azure, f.Codex);
        _ = new PortalCredentialCheckpoint(f.Root, 5099);
        var otherPort = Path.Combine(f.Root, ".local/portal/credential-cache/5099/restart.bin"); File.Copy(f.Cache, otherPort);
        var keyRoot = Path.Combine(Path.GetDirectoryName(f.Cache)!, "keys");
        foreach (var key in Directory.GetFiles(keyRoot)) File.Copy(key, Path.Combine(Path.GetDirectoryName(otherPort)!, "keys", Path.GetFileName(key)));
        Assert.Throws<PortalException>(() => new PortalCredentialCheckpoint(f.Root, 5099).Open(proof, "csrf", false));
        _ = new PortalCredentialCheckpoint(other.Root, 5087); File.Copy(f.Cache, other.Cache);
        foreach (var key in Directory.GetFiles(keyRoot)) File.Copy(key, Path.Combine(Path.GetDirectoryName(other.Cache)!, "keys", Path.GetFileName(key)));
        Assert.Throws<PortalException>(() => new PortalCredentialCheckpoint(other.Root, 5087).Open(proof, "csrf", false));
        var bytes = File.ReadAllBytes(f.Cache); bytes[^1] ^= 0xff; File.WriteAllBytes(f.Cache, bytes);
        Assert.Throws<PortalException>(() => cache.Open(proof, "csrf", false));
    }
    [Fact] public void ExpiredCheckpointCannotAttachAndRebuildKeepsOriginalCaches()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var cache = new PortalCredentialCheckpoint(f.Root, 5087); var proof = cache.Checkpoint(f.Azure, f.Codex);
        using (var lease = cache.Open(proof, "csrf", false)) { lease.Data.Expires = DateTimeOffset.UtcNow.AddMinutes(-1); lease.AzureRestored(); }
        Assert.Throws<PortalException>(() => cache.Open(proof, "csrf", false));
        Assert.True(File.Exists(Path.Combine(f.Azure, "msal_token_cache.bin"))); Assert.True(File.Exists(Path.Combine(f.Codex, "auth.json")));
        Assert.NotEqual(proof, cache.Checkpoint(f.Azure, f.Codex));
    }
    [Fact] public void ForeignHomeIncompleteFilesAndOversizeAreRejectedBeforeCheckpointCreation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var other = new Fixture(); var cache = new PortalCredentialCheckpoint(f.Root, 5087);
        Assert.Throws<InvalidOperationException>(() => cache.Checkpoint(other.Azure, null));
        File.Delete(Path.Combine(f.Azure, "msal_token_cache.bin"));
        Assert.Throws<InvalidOperationException>(() => cache.Checkpoint(f.Azure, null));
        using (var stream = File.Create(Path.Combine(f.Azure, "msal_token_cache.bin"))) stream.SetLength(8 * 1024 * 1024 + 1);
        Assert.Throws<InvalidOperationException>(() => cache.Checkpoint(f.Azure, null)); Assert.False(File.Exists(f.Cache));
    }
    [Fact] public void CacheAndKeyDirectoryAreRestrictedAndKeysAreDpapiEncrypted()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var cache = new PortalCredentialCheckpoint(f.Root, 5087); _ = cache.Checkpoint(f.Azure, null);
        var folder = Path.GetDirectoryName(f.Cache)!;
        foreach (var path in new[] { folder, Path.Combine(folder, "keys") })
        {
            var acl = new DirectoryInfo(path).GetAccessControl(); Assert.True(acl.AreAccessRulesProtected);
            var allowed = new[] { WindowsIdentity.GetCurrent().User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier))) Assert.Contains(rule.IdentityReference.Value, allowed);
        }
        var key = File.ReadAllText(Directory.GetFiles(Path.Combine(folder, "keys"), "key-*.xml").Single());
        Assert.Contains("encryptedSecret", key); Assert.Contains("Dpapi", key); Assert.DoesNotContain("<masterKey", key);
    }
    [Fact] public void ResumeReceiptRecordsReadinessWithoutBrowserProofOrCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var cache = new PortalCredentialCheckpoint(f.Root, 5087);
        var proof = cache.Checkpoint(f.Azure, f.Codex); using var session = new BrowserSession(new() { RepositoryRoot = f.Root });
        session.Connected["azure"] = true;
        cache.RecordResult(session, ["Synthetic verification failure."]);
        var text = File.ReadAllText(Path.Combine(Path.GetDirectoryName(f.Cache)!, "resume-status.json"));
        Assert.Contains("\"azure\":true", text); Assert.Contains("\"ado\":false", text);
        Assert.DoesNotContain("synthetic-codex-only", text); Assert.DoesNotContain("synthetic-microsoft-only", text);
        Assert.DoesNotContain(proof, text); Assert.DoesNotContain(session.Csrf, text);
    }
}
