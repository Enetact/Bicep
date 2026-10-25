using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class CredentialScopedRecoveryRetentionTests
{
    [Fact]
    public void Original_absence_key_remains_held_when_current_positive_profile_uses_another_key()
    {
        var old = Synthetic(Identity(), 0, 0, 1);
        var current = Synthetic(Identity(), 1, 1, 0, revision: 2);
        var report = CredentialScopedRecoveryRetention.Assess(current, [old]);
        Assert.Equal(new[] { "retained-key-0000", "retained-key-0001" }, report.KeyHolds.Select(h => h.KeyId));
        var absenceHold = report.KeyHolds[0];
        Assert.False(absenceHold.CurrentReference); Assert.Equal(new[] { old.Sha256 }, absenceHold.RetainedInventorySha256);
        Assert.True(report.KeyHolds[1].CurrentReference); Assert.Empty(report.KeyHolds[1].RetainedInventorySha256);
        AssertConservative(report);
    }

    [Fact]
    public void Combined_current_dependencies_include_proof_and_envelope_keys_without_a_current_write_key_reference()
    {
        var current = Synthetic(Identity(), 0, 1, 1);
        var report = CredentialScopedRecoveryRetention.Assess(current, []);
        Assert.Equal(new[] { "retained-key-0000", "retained-key-0001" }, report.CurrentReferencedKeyIds);
        Assert.All(report.KeyHolds, h => Assert.True(h.CurrentReference));
        Assert.Empty(report.HistoricalReferencedKeyIds); AssertConservative(report);
    }

    [Fact]
    public void Empty_current_or_missing_history_never_proves_no_old_key_dependencies()
    {
        var current = Synthetic(Identity(), 0, 0, 0);
        var empty = CredentialScopedRecoveryRetention.Assess(current, []);
        Assert.Empty(empty.KeyHolds); AssertConservative(empty);
        var old = Synthetic(Identity(), 1, 0, 1);
        var explicitHistory = CredentialScopedRecoveryRetention.Assess(current, [old]);
        Assert.False(Assert.Single(explicitHistory.KeyHolds).CurrentReference); AssertConservative(explicitHistory);
    }

    [Fact]
    public void Reordering_and_identical_repeats_do_not_add_coverage_or_change_the_commitment()
    {
        var current = Synthetic(Identity(), 0, 1, 0, 3);
        var first = Synthetic(Identity(), 1, 0, 1, 1); var second = Synthetic(Identity(), 2, 1, 1, 2);
        var left = CredentialScopedRecoveryRetention.Assess(current, [second, first, current, first]);
        var right = CredentialScopedRecoveryRetention.Assess(current, [first, second]);
        Assert.Equal(left.Sha256, right.Sha256); Assert.Equal(left.CanonicalPayload(), right.CanonicalPayload());
        Assert.Equal(new[] { first.Sha256, second.Sha256 }.Order(StringComparer.Ordinal), left.RetainedInventorySha256);
        AssertConservative(left);
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("vault")]
    [InlineData("commitment")] [InlineData("authority")] [InlineData("binding")]
    public void History_from_a_different_namespace_is_never_unionable(string difference)
    {
        var identity = Identity();
        var changed = difference switch
        {
            "customer" => identity with { CustomerId = "10000000-0000-0000-0000-000000000099" },
            "cell" => identity with { CellId = "20000000-0000-0000-0000-000000000099" },
            "vault" => identity with { VaultNamespaceId = new string('2', 32) },
            "commitment" => identity with { NamespaceCommitmentSha256 = Hash("other-vault") },
            "authority" => identity with { AuthorityNamespaceId = new string('4', 32) },
            _ => identity with { AuthorityBindingSha256 = Hash("other-binding") }
        };
        Assert.Equal("credential-scoped-recovery-retention-scope-mismatch", Assert.Throws<CredentialException>(() =>
            CredentialScopedRecoveryRetention.Assess(Synthetic(identity, 0, 0, 1), [Synthetic(changed, 1, 1, 0)])).Code);
    }

    [Fact]
    public void Returned_collections_and_payload_are_detached_and_the_public_report_contains_no_private_proof_material()
    {
        var input = Synthetic(Identity(), 0, 1, 1); var history = new List<CredentialScopedRecoveryInventory> { input };
        var report = CredentialScopedRecoveryRetention.Assess(input, history); history.Clear();
        var canonical = report.CanonicalPayload(); canonical[0] ^= 1;
        Assert.Throws<NotSupportedException>(() => ((IList)report.KeyHolds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList)report.CurrentReferencedKeyIds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList)report.RetainedInventorySha256).Clear());
        Assert.Equal(report.Sha256, Convert.ToHexStringLower(SHA256.HashData(report.CanonicalPayload())));
        Assert.All(typeof(CredentialScopedRecoveryRetentionAssessment).GetProperties(), p => Assert.Null(p.SetMethod));
        var json = JsonSerializer.Serialize(report);
        foreach (var value in new[] { "authProfileId", "ciphertext", "nonce", "\"tag\"", "requiredScopes", "firstTransactionId", "private-root" })
            Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);
        AssertConservative(report);
    }

    [Fact]
    public void Null_or_oversized_supplied_history_is_rejected_before_enumeration()
    {
        var current = Synthetic(Identity(), 0, 0, 0);
        Assert.Throws<CredentialException>(() => CredentialScopedRecoveryRetention.Assess(null!, []));
        Assert.Throws<CredentialException>(() => CredentialScopedRecoveryRetention.Assess(current, null!));
        Assert.Throws<CredentialException>(() => CredentialScopedRecoveryRetention.Assess(current, [null!]));
        var oversized = new OversizedHistory();
        Assert.Equal("credential-scoped-recovery-retention-inventory-limit", Assert.Throws<CredentialException>(() =>
            CredentialScopedRecoveryRetention.Assess(current, oversized)).Code);
        Assert.False(oversized.ReadAttempted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Missing_proof_key_in_cached_union_is_refused_in_current_or_history(bool historical)
    {
        var current = Synthetic(Identity(), 0, 0, 0); var corrupted = Synthetic(Identity(), 1, 1, 1);
        SetBacking(corrupted, nameof(CredentialScopedRecoveryInventory.CurrentRequiredKeyIds), Array.AsReadOnly(new[] { "retained-key-0001" }));
        Assert.Throws<CredentialException>(() => historical
            ? CredentialScopedRecoveryRetention.Assess(current, [corrupted])
            : CredentialScopedRecoveryRetention.Assess(corrupted, []));
    }

    [Theory]
    [InlineData("")] [InlineData("not-a-digest")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Changed_inventory_commitments_are_refused_before_holds_are_created(string digest)
    {
        var current = Synthetic(Identity(), 0, 0, 1); var corrupt = Synthetic(Identity(), 1, 1, 0);
        SetBacking(corrupt, nameof(CredentialScopedRecoveryInventory.Sha256), digest);
        Assert.Throws<CredentialException>(() => CredentialScopedRecoveryRetention.Assess(current, [corrupt]));
        Assert.Throws<CredentialException>(() => CredentialScopedRecoveryRetention.Assess(corrupt, []));
    }

    [Fact]
    public void Sixty_four_identical_observations_remain_one_supplied_commitment()
    {
        var current = Synthetic(Identity(), 0, 0, 0); var old = Synthetic(Identity(), 1, 0, 1);
        var report = CredentialScopedRecoveryRetention.Assess(current,
            Enumerable.Repeat(old, CredentialScopedRecoveryRetention.MaximumRetainedInventories).ToArray());
        Assert.Single(report.RetainedInventorySha256); Assert.Single(report.KeyHolds); AssertConservative(report);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Bounded_union_counts_absence_keys_and_refuses_any_additional_dependency(bool overflow)
    {
        var identity = Identity();
        var history = Enumerable.Range(0, 16).Select(i => Synthetic(identity, i * 256, 128, 128)).ToArray();
        var current = Synthetic(identity, 5000, 0, overflow ? 1 : 0);
        if (overflow)
            Assert.Equal("credential-scoped-recovery-retention-key-limit", Assert.Throws<CredentialException>(() =>
                CredentialScopedRecoveryRetention.Assess(current, history)).Code);
        else
        {
            var report = CredentialScopedRecoveryRetention.Assess(current, history);
            Assert.Equal(CredentialScopedRecoveryRetention.MaximumReferencedKeys, report.KeyHolds.Count);
            Assert.InRange(report.CanonicalPayload().Length, 1, CredentialScopedRecoveryRetention.MaximumReportBytes); AssertConservative(report);
        }
    }

    // Synthetic metadata exercises advisory accounting only, never a production authentication seam.
    static CredentialScopedRecoveryInventory Synthetic(CredentialVaultNamespaceIdentity identity, int first, int positiveCount, int absenceCount, long revision = 1)
    {
        var constructor = typeof(CredentialRecoveryProfileDependency).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var profiles = Enumerable.Range(first, positiveCount).Select(i => (CredentialRecoveryProfileDependency)constructor.Invoke(
            [Hash("profile-" + i), Hash("scope-" + i), "default", "dev", 1L, true, 1792846800000L,
                "retained-key-" + i.ToString("D4"), Hash("envelope-" + i), 1024L])).ToArray();
        var absences = Enumerable.Range(first + positiveCount, absenceCount).Select(i => new CredentialAbsenceScopeDependency(
            Hash("profile-" + i), Hash("scope-" + i), "default", "dev", "retained-key-" + i.ToString("D4"), Hash("proof-" + i))).ToArray();
        return (CredentialScopedRecoveryInventory)typeof(CredentialScopedRecoveryInventory)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke(
                [identity, DateTimeOffset.Parse("2026-10-25T12:00:00Z"), profiles, absences, revision, Hash("state-" + revision)]);
    }
    static CredentialVaultNamespaceIdentity Identity() => new("10000000-0000-0000-0000-000000000001",
        "20000000-0000-0000-0000-000000000001", new string('1', 32), Hash("namespace"), new string('3', 32), Hash("binding"));
    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static void SetBacking(object instance, string property, object value) => instance.GetType()
        .GetField("<" + property + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    static void AssertConservative(CredentialScopedRecoveryRetentionAssessment report)
    {
        Assert.True(report.RetainedInventoryMetadataOnly); Assert.False(report.PositiveEnvelopeAuthenticationPerformed);
        Assert.False(report.AbsenceScopeAuthenticationPerformed); Assert.False(report.KeyRetirementAuthorized);
        Assert.False(report.CompleteHistoricalCoverageVerified); Assert.Equal("NotAssessed", report.IndependentBackupCoverage);
        Assert.All(report.KeyHolds, h => Assert.Equal("RetentionHold", h.Decision));
    }
    sealed class OversizedHistory : IReadOnlyList<CredentialScopedRecoveryInventory>
    {
        public bool ReadAttempted { get; private set; }
        public int Count => CredentialScopedRecoveryRetention.MaximumRetainedInventories + 1;
        public CredentialScopedRecoveryInventory this[int index] { get { ReadAttempted = true; throw new InvalidOperationException(); } }
        public IEnumerator<CredentialScopedRecoveryInventory> GetEnumerator() { ReadAttempted = true; throw new InvalidOperationException(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
