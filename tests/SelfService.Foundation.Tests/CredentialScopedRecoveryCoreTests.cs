using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialScopedRecoveryCoreTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [WindowsCredentialFact]
    public async Task Mixed_signed_capture_roundtrips_then_authenticates_actual_retained_profiles_and_proofs_after_original_roots_removed()
    {
        using var f = new Fixture(); await f.Initialize(); var before = f.Snapshot(); using var set = await f.Capture();
        var authority = CredentialScopedRecoverySets.Authority(set); var verified = CredentialScopedRecoverySets.Verify(set, authority, f.Signer, f.Namespace, f.Cell.Clock);
        Assert.Equal(before, f.Snapshot()); Assert.True(verified.SignatureVerified); Assert.True(verified.RetainedContentVerified);
        Assert.False(verified.EncryptedProfileAuthenticationPerformed); Assert.False(verified.AbsenceProofAuthenticationPerformed);
        Assert.Single(verified.Inventory.ProfileDependencies); Assert.Single(verified.Inventory.AbsenceScopes); Assert.Equal(new[] { "key-1", "key-2" }, verified.Inventory.CurrentRequiredKeyIds);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(set, Json)); Assert.Throws<NotSupportedException>(() => ((IList)verified.Inventory.AbsenceScopes).Clear());
        using var memory = new MemoryStream(); set.WritePrivateArtifact(memory); var bytes = memory.ToArray(); using var parsed = CredentialScopedRecoverySets.Parse(bytes);
        Assert.Throws<CredentialException>(() => CredentialRecoverySets.Parse(bytes)); Array.Fill(bytes, (byte)0); f.RemoveSourceRoots(); f.Cell.Keys.Requests.Clear();
        var result = await f.Rehearsal().RehearseAsync(parsed, authority, f.Signer);
        Assert.Equal(set.Sha256, result.SignedSetSha256); Assert.Equal(1, result.AuthenticatedProfileCount); Assert.Equal(1, result.AuthenticatedAbsenceCount);
        Assert.True(result.EncryptedProfileAuthenticationPerformed); Assert.True(result.AbsenceProofAuthenticationPerformed);
        Assert.Equal(new[] { "key-1", "key-2" }, f.Cell.Keys.Requests.Select(k => k.KeyId).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(f.Options.RootPath)); Assert.False(Directory.Exists(f.Authority)); Assert.False(result.CurrentVaultConsulted);
        Assert.False(result.FullRecoverySupported); Assert.False(result.CustomerReviewAuthorized); Assert.False(result.KeyRetirementAuthorized);
        Assert.False(result.IndependentKeyBackupVerified); Assert.Equal("NotAssessed", result.IndependentBackupCoverage);
        var json = JsonSerializer.Serialize(result, Json); Assert.DoesNotContain(f.Cell.Root, json); Assert.DoesNotContain(f.Positive.AuthProfileId, json);
        Assert.DoesNotContain(f.Absent.AuthProfileId, json); Assert.DoesNotContain(CredentialFixture.Token, json); Assert.DoesNotContain("firstCandidate", json, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsCredentialFact]
    public async Task Combined_inventory_preserves_original_absence_key_and_actual_envelope_key_without_current_write_key_fallback()
    {
        using var f = new Fixture(); await f.Initialize(); var inspection = new CredentialVault(f.Options with { EncryptionKeyId = "unavailable-current-write-key" }, f.Cell.Keys, f.Cell.Clock);
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); var inventory = await inspection.InspectScopedRecoveryDependenciesAsync(f.Namespace);
        Assert.Equal(new[] { "key-1", "key-2" }, inventory.CurrentRequiredKeyIds); Assert.Equal("key-1", Assert.Single(inventory.AbsenceScopes).SourceKeyId);
        Assert.Equal("key-2", Assert.Single(inventory.ProfileDependencies).SourceKeyId); Assert.Equal(2, f.Cell.Keys.Requests.Count);
        Assert.Equal(before, f.Snapshot()); Assert.Throws<NotSupportedException>(() => ((IList)inventory.CurrentRequiredKeyIds).Clear());
        Assert.DoesNotContain("AuthenticationPerformed", JsonSerializer.Serialize(inventory, Json)); Assert.False(inventory.KeyRetirementAuthorized);
    }

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public async Task Positive_only_absence_only_and_empty_sets_have_exact_key_unions_and_nonvacuous_authentication_flags(bool positive, bool absent)
    {
        using var f = new Fixture(); await f.Initialize(positive, absent); using var set = await f.Capture(); f.Cell.Keys.Requests.Clear();
        var result = await f.Rehearsal().RehearseAsync(set, CredentialScopedRecoverySets.Authority(set), f.Signer);
        Assert.Equal(positive ? 1 : 0, result.AuthenticatedProfileCount); Assert.Equal(absent ? 1 : 0, result.AuthenticatedAbsenceCount);
        Assert.Equal(positive, result.EncryptedProfileAuthenticationPerformed); Assert.Equal(absent, result.AbsenceProofAuthenticationPerformed);
        Assert.Equal((positive ? 1 : 0) + (absent ? 1 : 0), result.CurrentRequiredKeyIds.Count);
        Assert.Equal(result.CurrentRequiredKeyIds, f.Cell.Keys.Requests.Select(k => k.KeyId).Order(StringComparer.Ordinal).ToArray());
        Assert.True(result.AllRetainedEnvelopesAuthenticated); Assert.True(result.AllRetainedAbsenceProofsAuthenticated); Assert.False(result.RestorePerformed);
    }

    [WindowsCredentialFact]
    public async Task Wrong_exact_authority_or_namespace_is_rejected_before_RSA_or_source_key_provider()
    {
        using var f = new Fixture(); await f.Initialize(); using var set = await f.Capture(); var pin = CredentialScopedRecoverySets.Authority(set); f.Cell.Keys.Requests.Clear();
        using var forbidden = new ForbiddenPublicKey();
        await Assert.ThrowsAsync<CredentialException>(() => f.Rehearsal().RehearseAsync(set, pin with { ArtifactSha256 = new string('0', 64) }, forbidden));
        var other = f.Namespace with { VaultNamespaceId = Guid.NewGuid().ToString("N") };
        await Assert.ThrowsAsync<CredentialException>(() => new CredentialScopedRecoveryRehearsal(other, f.Cell.Keys, f.Cell.Clock).RehearseAsync(set, pin, forbidden));
        Assert.Equal(0, forbidden.Exports); Assert.Empty(f.Cell.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Wrong_retained_positive_key_fails_AES_without_a_partial_authentication_report()
    {
        using var f = new Fixture(); await f.Initialize(); using var set = await f.Capture(); var before = f.Snapshot();
        f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Rehearsal().RehearseAsync(set, CredentialScopedRecoverySets.Authority(set), f.Signer));
        Assert.Equal("key-2", Assert.Single(f.Cell.Keys.Requests).KeyId); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Wrong_original_absence_key_fails_HMAC_even_when_present_envelope_authenticates()
    {
        using var f = new Fixture(); await f.Initialize(); using var set = await f.Capture(); var before = f.Snapshot();
        f.Cell.Keys.Values["key-1"] = RandomNumberGenerator.GetBytes(32); f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Rehearsal().RehearseAsync(set, CredentialScopedRecoverySets.Authority(set), f.Signer));
        Assert.Equal("credential-absence-proof-authentication-failed", error.Code); Assert.Equal(new[] { "key-2", "key-1" }, f.Cell.Keys.Requests.Select(k => k.KeyId));
        Assert.Equal(before, f.Snapshot()); Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.DoesNotContain(f.Cell.Root, error.ToString());
    }

    [WindowsCredentialFact]
    public async Task Disposing_original_private_artifact_and_signer_during_key_await_cannot_mutate_detached_retained_rehearsal()
    {
        using var f = new Fixture(); await f.Initialize(); using var set = await f.Capture(); var pin = CredentialScopedRecoverySets.Authority(set);
        var paused = new PausedKeys(f.Cell.Keys); var operation = new CredentialScopedRecoveryRehearsal(f.Namespace, paused, f.Cell.Clock).RehearseAsync(set, pin, f.Signer);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); set.Dispose(); f.Signer.Dispose(); f.RemoveSourceRoots(); paused.Release.TrySetResult();
        var result = await operation; Assert.Equal(pin.ArtifactSha256, result.SignedSetSha256); Assert.Equal(2, result.CurrentRequiredKeyIds.Count);
    }

    [WindowsCredentialFact]
    public async Task Final_lifetime_and_cancellation_cutoff_refuses_expiry_rewind_and_cancellation_without_source_mutation()
    {
        using var f = new Fixture(); await f.Initialize(); using var set = await f.Capture(); var pin = CredentialScopedRecoverySets.Authority(set);
        var before = f.Snapshot(); var original = f.Cell.Clock.Now;
        foreach (var expiry in new[] { true, false })
        {
            f.Cell.Keys.OnGet = () => f.Cell.Clock.Now = expiry ? set.Signature.ExpiresUtc : set.Signature.IssuedUtc.AddMilliseconds(-1);
            await Assert.ThrowsAsync<CredentialException>(() => f.Rehearsal().RehearseAsync(set, pin, f.Signer)); f.Cell.Clock.Now = original;
            Assert.Equal(before, f.Snapshot());
        }
        f.Cell.Keys.OnGet = null; using var canceled = new CancellationTokenSource(); var clock = new FinalClock(original, canceled.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CredentialScopedRecoveryRehearsal(f.Namespace, f.Cell.Keys, clock).RehearseAsync(set, pin, f.Signer, canceled.Token));
        Assert.True(clock.Canceled); Assert.Equal(before, f.Snapshot());
    }

    sealed class FinalClock(DateTimeOffset now, Action cancel) : TimeProvider
    {
        int samples; internal bool Canceled { get; private set; }
        public override DateTimeOffset GetUtcNow() { if (++samples == 2) { Canceled = true; cancel(); } return now; }
    }
    sealed class PausedKeys(ICredentialKeyProvider source) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return await source.GetKeyAsync(customerId, cellId, keyId, cancellationToken); }
    }
    sealed class ForbiddenPublicKey : RSA
    {
        internal int Exports;
        public override byte[] ExportSubjectPublicKeyInfo() { Exports++; throw new InvalidOperationException("Forbidden provider."); }
        public override RSAParameters ExportParameters(bool includePrivateParameters) { Exports++; throw new InvalidOperationException("Forbidden provider."); }
        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();
    }
    sealed class Fixture : IDisposable
    {
        internal CredentialFixture Cell { get; } = new();
        internal RSA Signer { get; } = RSA.Create(2048);
        internal string Authority => Path.Combine(Cell.Root, "scoped-set-authority");
        internal CredentialVaultOptions Options { get; }
        internal CredentialVault Vault { get; private set; }
        internal CredentialVaultNamespaceIdentity Namespace { get; private set; } = null!;
        internal CredentialScope Positive => Cell.Scope with { AuthProfileId = "profile/scoped-positive" };
        internal CredentialScope Absent => Cell.Scope with { AuthProfileId = "profile/scoped-absence", ProjectId = "absence-project", EnvironmentId = "prod" };
        internal Fixture()
        {
            Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
            Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "scoped-set-vault"), RollbackProtection = new(Authority) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            Vault = CredentialVault.ProvisionStrongScoped(Options, Cell.Keys, Cell.Clock);
        }
        internal async Task Initialize(bool positive = true, bool absent = true)
        {
            if (absent)
            {
                typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Vault,
                    (Action<string>)(phase => { if (phase == "PendingPersisted") throw new CredentialException("synthetic-first-write-interruption"); }));
                using var material = CredentialSecret.FromUtf8(CredentialFixture.Token);
                await Assert.ThrowsAsync<CredentialException>(() => Vault.PutAsync(Absent, material, Cell.Clock.Now.AddHours(1)));
                var status = await Vault.InspectRollbackStateAsync(Absent.AuthProfileId);
                await Vault.ReconcilePendingWriteAsync(Absent.AuthProfileId, status.AuthorityRevision, status.AuthorityStateSha256, status.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
            }
            Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); Vault = new(Options with { EncryptionKeyId = "key-2" }, Cell.Keys, Cell.Clock);
            if (positive) { using var material = CredentialSecret.FromUtf8(CredentialFixture.Token); await Vault.PutAsync(Positive, material, Cell.Clock.Now.AddHours(1)); }
            Namespace = Vault.DescribeNamespace();
        }
        internal Task<CredentialScopedRecoverySet> Capture() => Vault.CaptureScopedSignedRecoverySetAsync(Namespace, Signer, "scoped-set-signer", 7, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        internal CredentialScopedRecoveryRehearsal Rehearsal() => new(Namespace, Cell.Keys, Cell.Clock);
        internal string[] Snapshot() => Directory.GetFiles(Options.RootPath).Concat(Directory.GetFiles(Authority)).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        internal void RemoveSourceRoots()
        {
            foreach (var path in new[] { Options.RootPath, Authority })
            {
                var full = Path.GetFullPath(path); if (!full.StartsWith(Path.GetFullPath(Cell.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(full) is not ("scoped-set-vault" or "scoped-set-authority")) throw new InvalidOperationException("Synthetic source deletion boundary rejected.");
                Directory.Delete(full, recursive: true);
            }
        }
        public void Dispose() { Signer.Dispose(); Cell.Dispose(); }
    }
}
