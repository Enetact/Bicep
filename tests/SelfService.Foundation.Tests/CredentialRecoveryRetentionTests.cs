using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class CredentialRecoveryRetentionTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Rewrap_current_key_and_retained_source_key_are_both_held_without_any_key_lookup(string provider)
    {
        using var f = new CredentialFixture(provider); await f.StoreToken();
        var old = await Inspect(f.Vault); var destination = Destination(f);
        await destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1"); var current = await Inspect(destination);
        Assert.Equal(new[] { "key-1" }, old.CurrentRequiredKeyIds); Assert.Equal(new[] { "key-2" }, current.CurrentRequiredKeyIds);
        var before = VaultBytes(f); f.Keys.Requests.Clear();
        var result = CredentialRecoveryRetention.Assess(current, [old]);
        Assert.Equal(new[] { "key-1", "key-2" }, result.KeyHolds.Select(h => h.KeyId));
        Assert.Equal("RetentionHold", result.KeyHolds[0].Decision); Assert.False(result.KeyHolds[0].CurrentReference);
        Assert.Equal(new[] { old.Sha256 }, result.KeyHolds[0].RetainedInventorySha256);
        Assert.True(result.KeyHolds[1].CurrentReference); Assert.Empty(result.KeyHolds[1].RetainedInventorySha256);
        AssertConservative(result); Assert.Empty(f.Keys.Requests); Assert.Equal(before, VaultBytes(f));
        var publicJson = JsonSerializer.Serialize(result);
        foreach (var excluded in new[] { CredentialFixture.Token, f.Root, f.Scope.AuthProfileId, "ciphertext", "nonce", "tag" })
            Assert.DoesNotContain(excluded, publicJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_and_revoked_current_profiles_do_not_release_old_or_current_source_keys(bool revoked)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var old = await Inspect(f.Vault);
        if (revoked) await f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1); else f.Clock.Now = f.Clock.Now.AddHours(2);
        var profile = await f.Vault.GetProfileAsync(f.Scope.AuthProfileId); var destination = Destination(f);
        await destination.RewrapAsync(f.Scope.AuthProfileId, profile.Generation, "key-1");
        var result = CredentialRecoveryRetention.Assess(await Inspect(destination), [old]);
        Assert.Equal(new[] { "key-1", "key-2" }, result.KeyHolds.Select(h => h.KeyId)); AssertConservative(result);
    }

    [Fact]
    public async Task Empty_current_input_and_no_supplied_history_never_authorize_retirement_or_prove_backup_coverage()
    {
        using var f = new CredentialFixture(); var empty = await Inspect(f.Vault);
        var result = CredentialRecoveryRetention.Assess(empty, []);
        Assert.Empty(result.KeyHolds); Assert.Empty(result.RetainedInventorySha256); AssertConservative(result);
        // Labels and timestamps are caller observations: even an older empty input cannot erase an explicit later dependency.
        await f.StoreToken(); var retained = await Inspect(f.Vault);
        var withExplicitReference = CredentialRecoveryRetention.Assess(empty, [retained]);
        var hold = Assert.Single(withExplicitReference.KeyHolds); Assert.Equal("key-1", hold.KeyId); Assert.False(hold.CurrentReference);
        AssertConservative(withExplicitReference);
    }

    [Fact]
    public async Task Input_order_and_identical_duplicates_do_not_change_sorted_assessment_or_add_coverage()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var old = await Inspect(f.Vault);
        var destination = Destination(f); await destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1");
        var middle = await Inspect(destination); f.Clock.Now = f.Clock.Now.AddSeconds(1); var current = await Inspect(destination);
        var left = CredentialRecoveryRetention.Assess(current, [middle, old, old, current]);
        var right = CredentialRecoveryRetention.Assess(current, [old, middle]);
        Assert.Equal(left.Sha256, right.Sha256); Assert.Equal(left.CanonicalPayload(), right.CanonicalPayload());
        Assert.Equal(new[] { old.Sha256, middle.Sha256 }.Order(StringComparer.Ordinal), left.RetainedInventorySha256);
        Assert.Equal(new[] { "key-1", "key-2" }, left.HistoricalReferencedKeyIds);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(left.CanonicalPayload())), left.Sha256);
    }

    [Fact]
    public async Task Returned_collections_and_canonical_payload_cannot_mutate_assessment_commitment()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var inventory = await Inspect(f.Vault);
        var inputs = new List<CredentialRecoveryInventory> { inventory };
        var report = CredentialRecoveryRetention.Assess(inventory, inputs); var canonical = report.CanonicalPayload(); var hash = report.Sha256;
        inputs.Clear(); canonical[0] ^= 1;
        Assert.Throws<NotSupportedException>(() => ((IList)report.KeyHolds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList)report.CurrentReferencedKeyIds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList)report.RetainedInventorySha256).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList)report.KeyHolds[0].RetainedInventorySha256).Add(new string('a', 64)));
        Assert.Equal(hash, report.Sha256); Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(report.CanonicalPayload())));
        Assert.All(typeof(CredentialRecoveryRetentionAssessment).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.All(typeof(CredentialRecoveryKeyRetentionHold).GetProperties(), p => Assert.Null(p.SetMethod));
    }

    [Fact]
    public async Task Null_and_oversized_history_inputs_fail_closed_before_result_creation()
    {
        using var f = new CredentialFixture(); var current = await Inspect(f.Vault);
        Assert.Equal("credential-recovery-retention-input-invalid", Assert.Throws<CredentialException>(() => CredentialRecoveryRetention.Assess(null!, [])).Code);
        Assert.Equal("credential-recovery-retention-input-invalid", Assert.Throws<CredentialException>(() => CredentialRecoveryRetention.Assess(current, null!)).Code);
        Assert.Equal("credential-recovery-retention-input-invalid", Assert.Throws<CredentialException>(() => CredentialRecoveryRetention.Assess(current, [null!])).Code);
        var tooMany = Enumerable.Repeat(current, CredentialRecoveryRetention.MaximumRetainedInventories + 1).ToArray();
        Assert.Equal("credential-recovery-retention-inventory-limit", Assert.Throws<CredentialException>(() => CredentialRecoveryRetention.Assess(current, tooMany)).Code);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("vault")]
    [InlineData("commitment")]
    [InlineData("authority-namespace")]
    [InlineData("authority-binding")]
    [InlineData("authority-added")]
    [InlineData("authority-removed")]
    public void Valid_but_differently_bound_history_is_never_unionable(string difference)
    {
        var identity = Identity(difference.StartsWith("authority-", StringComparison.Ordinal) && difference != "authority-added");
        var changed = difference switch
        {
            "customer" => identity with { CustomerId = "10000000-0000-0000-0000-000000000099" },
            "cell" => identity with { CellId = "20000000-0000-0000-0000-000000000099" },
            "vault" => identity with { VaultNamespaceId = new string('2', 32) },
            "commitment" => identity with { NamespaceCommitmentSha256 = Hash("another-vault-commitment") },
            "authority-namespace" => identity with { AuthorityNamespaceId = new string('4', 32) },
            "authority-binding" => identity with { AuthorityBindingSha256 = Hash("another-authority-binding") },
            "authority-added" => identity with { AuthorityNamespaceId = new string('3', 32), AuthorityBindingSha256 = Hash("authority-binding") },
            _ => identity with { AuthorityNamespaceId = null, AuthorityBindingSha256 = null }
        };
        var current = Synthetic(identity, 0, 1); var retained = Synthetic(changed, 1, 1);
        Assert.Equal("credential-recovery-retention-scope-mismatch", Assert.Throws<CredentialException>(() =>
            CredentialRecoveryRetention.Assess(current, [retained])).Code);
    }

    [Fact]
    public void Authority_revision_and_state_changes_do_not_lose_historical_key_references()
    {
        var identity = Identity(strong: true); var old = Synthetic(identity, 0, 1, revision: 1); var current = Synthetic(identity, 1, 1, revision: 2);
        Assert.NotEqual(old.AuthorityStateSha256, current.AuthorityStateSha256);
        var report = CredentialRecoveryRetention.Assess(current, [old]);
        Assert.Equal(new[] { "retention-key-0000", "retention-key-0001" }, report.KeyHolds.Select(h => h.KeyId));
        Assert.Equal(identity.AuthorityNamespaceId, report.AuthorityNamespaceId); Assert.Equal(identity.AuthorityBindingSha256, report.AuthorityBindingSha256);
        AssertConservative(report);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_inventory_commitments_are_refused_in_current_and_history(string changed)
    {
        var current = Synthetic(Identity(), 0, 0); var malformed = Synthetic(Identity(), 1, 1);
        SetBacking(malformed, nameof(CredentialRecoveryInventory.Sha256), changed);
        Assert.Throws<CredentialException>(() => CredentialRecoveryRetention.Assess(malformed, []));
        Assert.Throws<CredentialException>(() => CredentialRecoveryRetention.Assess(current, [malformed]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conflicting_content_with_a_reused_current_or_retained_digest_is_refused(bool conflictsWithCurrent)
    {
        var original = Synthetic(Identity(), 0, 1); var changed = Synthetic(Identity(), 1, 1);
        SetBacking(changed, nameof(CredentialRecoveryInventory.Sha256), original.Sha256);
        var current = conflictsWithCurrent ? original : Synthetic(Identity(), 2, 0);
        var history = conflictsWithCurrent ? new[] { changed } : new[] { original, changed };
        Assert.Equal("credential-recovery-inventory-integrity-invalid", Assert.Throws<CredentialException>(() =>
            CredentialRecoveryRetention.Assess(current, history)).Code);
    }

    [Fact]
    public void Cached_required_key_list_cannot_hide_an_authenticated_profile_dependency()
    {
        var inventory = Synthetic(Identity(), 0, 1);
        SetBacking(inventory, nameof(CredentialRecoveryInventory.CurrentRequiredKeyIds), Array.AsReadOnly(Array.Empty<string>()));
        Assert.Throws<CredentialException>(() => CredentialRecoveryRetention.Assess(inventory, []));
    }

    [Fact]
    public void Maximum_history_count_accepts_identical_metadata_without_inventing_extra_coverage()
    {
        var current = Synthetic(Identity(), 0, 0); var old = Synthetic(Identity(), 1, 1);
        var report = CredentialRecoveryRetention.Assess(current, Enumerable.Repeat(old, CredentialRecoveryRetention.MaximumRetainedInventories).ToArray());
        Assert.Single(report.RetainedInventorySha256); Assert.Single(report.KeyHolds); AssertConservative(report);
    }

    [Fact]
    public void Exact_distinct_key_cap_is_bounded_and_one_additional_current_reference_cannot_be_omitted()
    {
        var identity = Identity(); var empty = Synthetic(identity, 0, 0);
        var history = Enumerable.Range(0, 16).Select(i => Synthetic(identity, i * 256, 256)).ToArray();
        var report = CredentialRecoveryRetention.Assess(empty, history);
        Assert.Equal(CredentialRecoveryRetention.MaximumReferencedKeys, report.KeyHolds.Count);
        Assert.InRange(report.CanonicalPayload().Length, 1, CredentialRecoveryRetention.MaximumReportBytes); AssertConservative(report);
        var additional = Synthetic(identity, 5000, 1);
        Assert.Equal("credential-recovery-retention-key-limit", Assert.Throws<CredentialException>(() =>
            CredentialRecoveryRetention.Assess(additional, history)).Code);
    }

    [Fact]
    public void Oversized_distinct_historical_union_is_refused_instead_of_returning_a_partial_report()
    {
        var identity = Identity(); var current = Synthetic(identity, 0, 0);
        var history = Enumerable.Range(0, 17).Select(i => Synthetic(identity, i * 256, 256)).ToArray();
        Assert.Equal("credential-recovery-retention-key-limit", Assert.Throws<CredentialException>(() =>
            CredentialRecoveryRetention.Assess(current, history)).Code);
    }

    // Pure synthetic metadata fixtures invoke existing internal constructors, never a production parser/authentication callback.
    static CredentialRecoveryInventory Synthetic(CredentialVaultNamespaceIdentity identity, int firstKey, int count, long revision = 1)
    {
        var profileConstructor = typeof(CredentialRecoveryProfileDependency).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var profiles = Enumerable.Range(firstKey, count).Select(i => (CredentialRecoveryProfileDependency)profileConstructor.Invoke(
            [Hash("profile-" + i), Hash("scope-" + i), "default", "dev", 1L, true, 1792846800000L, "retention-key-" + i.ToString("D4"), Hash("envelope-" + i), 1024L])).ToArray();
        var constructor = typeof(CredentialRecoveryInventory).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (CredentialRecoveryInventory)constructor.Invoke([identity, DateTimeOffset.Parse("2026-10-24T12:00:00Z"), profiles,
            identity.AuthorityNamespaceId is null ? 0L : revision, identity.AuthorityNamespaceId is null ? null : Hash("authority-state-" + revision)]);
    }
    static CredentialVaultNamespaceIdentity Identity(bool strong = false) => new("10000000-0000-0000-0000-000000000001",
        "20000000-0000-0000-0000-000000000001", new string('1', 32), Hash("namespace-commitment"),
        strong ? new string('3', 32) : null, strong ? Hash("authority-binding") : null);
    static void SetBacking<T>(T instance, string property, object value) where T : class =>
        typeof(T).GetField("<" + property + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static Task<CredentialRecoveryInventory> Inspect(CredentialVault vault) => vault.InspectRecoveryDependenciesAsync(vault.DescribeNamespace());
    static CredentialVault Destination(CredentialFixture f)
    { f.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); return new(f.Options with { EncryptionKeyId = "key-2" }, f.Keys, f.Clock); }
    static string VaultBytes(CredentialFixture f) => string.Join("|", Directory.GetFiles(f.VaultRoot).Order(StringComparer.Ordinal)
        .Select(p => Path.GetFileName(p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))));
    static void AssertConservative(CredentialRecoveryRetentionAssessment report)
    {
        Assert.False(report.KeyRetirementAuthorized); Assert.False(report.CompleteHistoricalCoverageVerified);
        Assert.True(report.RetainedInventoryMetadataOnly); Assert.Equal("NotAssessed", report.IndependentBackupCoverage);
        Assert.All(report.KeyHolds, h => Assert.Equal("RetentionHold", h.Decision));
    }
}
