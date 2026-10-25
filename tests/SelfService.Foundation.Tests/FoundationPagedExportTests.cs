using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationPagedExportTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Keyset_capture_pages_more_than_the_legacy_limit_without_gaps_or_duplicates()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, 1301);
        f.Execute("INSERT INTO artifacts VALUES('customer-a','foreign-artifact','other','prod','fixture.bicep',X'','foreign-digest',0)");
        var plan = f.Plan();
        Assert.Equal("ExportPageRequired", Assert.Throws<FoundationStoreException>(() => f.Store.ExportScope(f.Customer, "auditor", "default", "dev")).Code);
        var start = Create(f, 100); var pages = ReadAll(f, start); var items = pages.SelectMany(p => p.Items).ToArray();
        Assert.Equal(start.Snapshot.PageCount, pages.Count); Assert.True(pages.Count > 13);
        Assert.Equal(start.Snapshot.ItemCount, items.LongLength);
        var artifacts = items.Where(i => i.Kind == "artifact").Select(i => i.Metadata.GetProperty("id").GetString()).ToArray();
        Assert.Equal(1301, artifacts.Length); Assert.Equal(1301, artifacts.Distinct().Count());
        Assert.DoesNotContain("foreign-artifact", artifacts);
        Assert.Equal(artifacts.OrderBy(i => i, StringComparer.Ordinal), artifacts);
        Assert.Contains(items, i => i.Kind == "plan" && i.Metadata.GetProperty("digest").GetString() == plan.Digest);
        var audits = items.Where(i => i.Kind == "audit").Select(i => i.Metadata.GetProperty("sequence").GetInt64()).ToArray();
        Assert.Equal(audits.Order(), audits); Assert.DoesNotContain(start.Snapshot.AuditSequence + 1, audits);
        Assert.Equal(start.Snapshot.AuditSequence + 1 + pages.Count - 1, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal("", pages[0].PreviousPageSha256);
        for (var n = 0; n < pages.Count; n++)
        {
            Assert.Equal(n, pages[n].PageNumber); Assert.Equal(start.Snapshot.PageSha256[n], pages[n].Sha256);
            Assert.Equal(start.Snapshot.Sha256, pages[n].SnapshotSha256); Assert.InRange(pages[n].Items.Length, 1, 100);
            if (n > 0) Assert.Equal(pages[n - 1].Sha256, pages[n].PreviousPageSha256);
        }
        Assert.True(pages[^1].Complete); Assert.Null(pages[^1].NextCursor);
    }

    [Fact]
    public void Audit_keyset_capture_exceeds_a_thousand_rows_with_verified_provenance_and_bounded_responses()
    {
        using var f = new StoreFixture();
        for (var n = 0; n < 1001; n++) f.Store.RecordSecurityEvent("ExportTest", "fixture-" + n);
        var start = Create(f, 200); var pages = ReadAll(f, start);
        var audit = pages.SelectMany(p => p.Items).Where(i => i.Kind == "audit").Select(i => i.Metadata).ToArray();
        Assert.True(audit.Length > 1000); Assert.Equal(start.Snapshot.AuditSequence, audit.LongLength);
        Assert.Equal(Enumerable.Range(1, audit.Length).Select(n => (long)n), audit.Select(i => i.GetProperty("sequence").GetInt64()));
        Assert.Equal(start.Snapshot.AuditHash, audit[^1].GetProperty("eventHash").GetString());
        Assert.All(pages, p => Assert.InRange(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p, Json)), 1, FoundationStore.MaximumPagedExportPageResponseBytes));
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Fact]
    public void Bounded_canonical_preimages_allow_independent_offline_snapshot_and_page_verification()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, 9); var start = Create(f, 2); var pages = ReadAll(f, start);
        var manifestBytes = Convert.FromBase64String(start.Snapshot.CanonicalManifestBase64);
        Assert.Equal(start.Snapshot.Sha256, Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
        using var manifest = JsonDocument.Parse(manifestBytes);
        Assert.Equal(f.Customer, manifest.RootElement.GetProperty("customerId").GetString());
        Assert.Equal("auditor", manifest.RootElement.GetProperty("actorKey").GetString());
        Assert.Equal(start.Snapshot.PageSha256, manifest.RootElement.GetProperty("pageSha256").EnumerateArray().Select(p => p.GetString()).ToArray());
        Assert.False(manifest.RootElement.TryGetProperty("firstCursor", out _)); Assert.False(manifest.RootElement.TryGetProperty("pageRows", out _));
        foreach (var page in pages)
        {
            var bytes = Convert.FromBase64String(page.CanonicalPageBase64);
            Assert.Equal(page.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            using var envelope = JsonDocument.Parse(bytes);
            Assert.Equal(page.SnapshotId, envelope.RootElement.GetProperty("snapshotId").GetString());
            Assert.Equal(JsonSerializer.Serialize(page.Items, Json), envelope.RootElement.GetProperty("items").GetRawText());
            Assert.InRange(bytes.Length, 1, FoundationStore.MaximumPagedExportPageBytes);
            Assert.InRange(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(page, Json)), 1, FoundationStore.MaximumPagedExportPageResponseBytes);
        }
        Assert.InRange(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(start, Json)), 1, FoundationStore.MaximumPagedExportStartResponseBytes);
    }

    [Fact]
    public void Snapshot_is_durable_and_exports_only_the_explicit_metadata_projection()
    {
        using var f = new StoreFixture(); var job = f.Queue();
        f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "secret.bicep", "test-only-private-source-marker"u8.ToArray());
        f.Execute("UPDATE jobs SET receipt='test-only-provider-receipt-marker'");
        var start = Create(f, 2); var pages = ReadAll(f, start); var json = JsonSerializer.Serialize(pages, Json);
        Assert.DoesNotContain("test-only-private-source-marker", json); Assert.DoesNotContain("test-only-provider-receipt-marker", json);
        Assert.DoesNotContain("instanceCount", json); Assert.DoesNotContain("configurationJson", json); Assert.DoesNotContain("refs/heads/main", json);
        Assert.All(pages.SelectMany(p => p.Items), item =>
        {
            var actual = item.Metadata.EnumerateObject().Select(p => p.Name).Order().ToArray();
            if (item.Kind == "artifact") Assert.Equal(new[] { "id", "sha256", "length", "createdUtc" }.Order(), actual);
            if (item.Kind == "plan") Assert.Equal(new[] { "id", "digest", "connectionGeneration", "policyVersion", "expiresUtc" }.Order(), actual);
        });
        var recovered = f.Reopen().GetPagedScopeExport(f.Customer, "auditor", "default", "dev", start.Snapshot.Id, start.Snapshot.Sha256);
        Assert.Equal(start.FirstCursor, recovered.FirstCursor); Assert.Equal(start.FirstPage.Sha256, recovered.FirstPage.Sha256);
        Assert.Equal(start.Snapshot.Sha256, recovered.Snapshot.Sha256); Assert.True(recovered.Snapshot.MetadataOnly); Assert.True(recovered.Snapshot.HistoricalSnapshot);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor")); Assert.NotNull(job);
    }

    [Fact]
    public void Mutable_source_changes_and_retention_do_not_shift_or_mix_an_existing_snapshot()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, 12); var start = Create(f, 3);
        f.Execute("DELETE FROM artifacts WHERE id IN ('artifact-000001','artifact-000004'); UPDATE artifacts SET digest='changed' WHERE id='artifact-000008'");
        f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "later.bicep", "later"u8.ToArray());
        var captured = ReadAll(f, start).SelectMany(p => p.Items).Where(i => i.Kind == "artifact").ToArray();
        Assert.Equal(12, captured.Length); Assert.All(captured, i => Assert.NotEqual("changed", i.Metadata.GetProperty("sha256").GetString()));
        Assert.Contains(captured, i => i.Metadata.GetProperty("id").GetString() == "artifact-000001");
        Assert.Equal(11, f.Scalar("SELECT count(*) FROM artifacts"));
    }

    [Fact]
    public void Snapshots_cursors_and_raw_exports_cannot_cross_actor_customer_project_environment_or_snapshot()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, 4); var start = Create(f, 1); var other = Create(f, 1);
        f.Store.SetMembership(f.Customer, "admin", "second-auditor", "default", "dev", ["Auditor"]);
        var scope = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "finance", "qa", "finance-admin", "Export route isolation fixture");
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", scope.Id, scope.Digest);
        f.Store.ExecuteScopeOnboarding(f.Customer, "admin", scope.Id, scope.Digest);
        f.Store.SetMembership(f.Customer, "finance-admin", "auditor", "finance", "qa", ["Auditor"]);
        // The new grant invalidates the creator version of the first snapshots; use a fresh one for route-isolation checks.
        start = Create(f, 1);
        Assert.Equal("NotFound", Fail(() => Page(f, start, start.FirstCursor, "second-auditor")).Code);
        Assert.Equal("CustomerScopeDenied", Fail(() => f.Store.ReadPagedScopeExportPage("foreign", "auditor", "default", "dev", start.Snapshot.Id, start.FirstCursor, start.Snapshot.Sha256)).Code);
        Assert.Equal("NotFound", Fail(() => f.Store.ReadPagedScopeExportPage(f.Customer, "auditor", "finance", "qa", start.Snapshot.Id, start.FirstCursor, start.Snapshot.Sha256)).Code);
        Assert.Equal("NotFound", Fail(() => Page(f, start, other.FirstCursor)).Code);
        Assert.Equal("NotFound", Fail(() => f.Store.GetScopeExport(f.Customer, "auditor", start.Snapshot.Id, "default", "dev")).Code);
        var pageRow = "xpg-page-" + Hash(start.Snapshot.Id + ":" + start.FirstCursor);
        Assert.Equal("NotFound", Fail(() => f.Store.GetScopeExport(f.Customer, "auditor", pageRow, "default", "dev")).Code);
        Assert.Equal("NotFound", Fail(() => f.Store.GetPagedScopeExport(f.Customer, "second-auditor", "default", "dev", start.Snapshot.Id, start.Snapshot.Sha256)).Code);
    }

    [Theory]
    [InlineData("viewer", "RoleDenied")]
    [InlineData("admin", "RoleDenied")]
    [InlineData("workload:test:actor:client", "HumanPrincipalRequired")]
    public void Creation_and_reads_require_the_current_human_auditor_role(string actor, string code)
    {
        using var f = new StoreFixture(); var start = Create(f);
        Assert.Equal(code, Fail(() => f.Store.CreatePagedScopeExport(f.Customer, actor, "default", "dev")).Code);
        Assert.Equal(code, Fail(() => Page(f, start, start.FirstCursor, actor)).Code);
        Assert.Equal(code, Fail(() => f.Store.PurgeExpiredPagedScopeExports(f.Customer, actor, "default", "dev")).Code);
    }

    [Fact]
    public void Revocation_regrant_and_policy_changes_fence_each_page_and_recovery()
    {
        using var f = new StoreFixture(); var start = Create(f, 1);
        f.Store.SetMembership(f.Customer, "admin", "auditor", "default", "dev", ["Viewer"]);
        Assert.Equal("RoleDenied", Fail(() => Page(f, start, start.FirstCursor)).Code);
        f.Store.SetMembership(f.Customer, "admin", "auditor", "default", "dev", ["Auditor"]);
        Assert.Equal("ExportAuthorityChanged", Fail(() => Page(f, start, start.FirstCursor)).Code);
        Assert.Equal("ExportAuthorityChanged", Fail(() => f.Store.GetPagedScopeExport(f.Customer, "auditor", "default", "dev", start.Snapshot.Id, start.Snapshot.Sha256)).Code);
        var fresh = Create(f, 1); f.Execute("UPDATE cell SET policy_version=policy_version+1");
        Assert.Equal("ExportPolicyChanged", Fail(() => Page(f, fresh, fresh.FirstCursor)).Code);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(201, 15)]
    [InlineData(1, 0)]
    [InlineData(1, 61)]
    public void Page_policy_has_explicit_small_bounded_limits(int pageSize, int minutes)
    {
        using var f = new StoreFixture();
        Assert.Equal("InvalidExportPagePolicy", Fail(() => f.Store.CreatePagedScopeExport(f.Customer, "auditor", "default", "dev", pageSize, TimeSpan.FromMinutes(minutes))).Code);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM scope_exports"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("page:1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Cursors_have_no_user_editable_offset_or_type_and_reject_malformed_input(string cursor)
    {
        using var f = new StoreFixture(); var start = Create(f);
        Assert.Equal("InvalidExportCursor", Fail(() => Page(f, start, cursor)).Code);
        Assert.Equal("InvalidExportCursor", Fail(() => f.Store.ReadPagedScopeExportPage(f.Customer, "auditor", "default", "dev", start.Snapshot.Id, start.FirstCursor, cursor)).Code);
    }

    [Fact]
    public void Client_snapshot_commitment_rejects_a_forged_root_even_after_database_hashes_are_recomputed()
    {
        using var f = new StoreFixture(); var start = Create(f, 1);
        RewriteRow(f, start.Snapshot.Id, json =>
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            root["manifest"]!["itemCount"] = 999;
            root["snapshotSha256"] = Hash(root["manifest"]!.ToJsonString(Json));
            return root.ToJsonString(Json);
        });
        Assert.Equal("DigestMismatch", Fail(() => Page(f, start, start.FirstCursor)).Code);
    }

    [Fact]
    public void Rehashed_page_tampering_and_missing_pages_fail_explicitly_instead_of_skipping_data()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, 5); var start = Create(f, 1);
        var second = start.FirstPage.NextCursor!; var row = "xpg-page-" + Hash(start.Snapshot.Id + ":" + second);
        RewriteRow(f, row, json => json.Replace("artifact-000002", "artifact-tampered", StringComparison.Ordinal));
        Assert.Equal("DigestMismatch", Fail(() => Page(f, start, second)).Code);
        f.Execute($"DELETE FROM scope_exports WHERE id='{row}'");
        Assert.Equal("NotFound", Fail(() => Page(f, start, second)).Code);
        Assert.Equal(start.FirstPage.Sha256, Page(f, start, start.FirstCursor).Sha256);
    }

    [Fact]
    public void Duplicate_unknown_and_null_fields_in_persisted_roots_are_rejected()
    {
        using var f = new StoreFixture(); var start = Create(f);
        RewriteRow(f, start.Snapshot.Id, json => "{\"unknown\":true," + json[1..]);
        Assert.Equal("ExportIntegrityFailed", Fail(() => Page(f, start, start.FirstCursor)).Code);
        var second = Create(f);
        RewriteRow(f, second.Snapshot.Id, json => "{\"schema\":\"ambiguous\"," + json[1..]);
        Assert.Equal("ExportIntegrityFailed", Fail(() => Page(f, second, second.FirstCursor)).Code);
        var third = Create(f);
        RewriteRow(f, third.Snapshot.Id, json => json.Replace("\"pageRows\":[", "\"pageRows\":[null,", StringComparison.Ordinal));
        Assert.Equal("ExportIntegrityFailed", Fail(() => Page(f, third, third.FirstCursor)).Code);
    }

    [Fact]
    public void Expiry_blocks_replay_and_audited_cleanup_removes_only_expired_protocol_cache()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, 6); var legacy = f.Store.ExportScope(f.Customer, "auditor", "default", "dev");
        var longLived = f.Store.CreatePagedScopeExport(f.Customer, "auditor", "default", "dev", 2, TimeSpan.FromHours(1));
        var expired = f.Store.CreatePagedScopeExport(f.Customer, "auditor", "default", "dev", 2, TimeSpan.FromMinutes(1));
        var sources = f.Scalar("SELECT count(*) FROM artifacts"); var audits = f.Scalar("SELECT count(*) FROM audit");
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("ExportSnapshotExpired", Fail(() => Page(f, expired, expired.FirstCursor)).Code);
        var result = f.Store.PurgeExpiredPagedScopeExports(f.Customer, "auditor", "default", "dev", 1);
        Assert.Equal(1, result.SnapshotsRemoved); Assert.Equal(expired.Snapshot.PageCount, result.PagesRemoved); Assert.True(result.LogicalBytesRemoved > 0);
        Assert.False(result.SourceOrAuditDeleted); Assert.False(result.SecureErasureVerified);
        Assert.Equal(sources, f.Scalar("SELECT count(*) FROM artifacts")); Assert.Equal(audits + 1, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(legacy, f.Store.GetScopeExport(f.Customer, "auditor", legacy.Id, "default", "dev"));
        Assert.Equal(longLived.FirstPage.Sha256, Page(f, longLived, longLived.FirstCursor).Sha256);
        Assert.Equal("NotFound", Fail(() => Page(f, expired, expired.FirstCursor)).Code);
        Assert.Equal(0, f.Store.PurgeExpiredPagedScopeExports(f.Customer, "auditor", "default", "dev").SnapshotsRemoved);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Fact]
    public void Corrupt_expired_page_rolls_back_cleanup_including_previous_pages_and_audit()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, 4);
        var start = f.Store.CreatePagedScopeExport(f.Customer, "auditor", "default", "dev", 1, TimeSpan.FromMinutes(1));
        var lastCursor = ReadAll(f, start)[^2].NextCursor!; var lastId = "xpg-page-" + Hash(start.Snapshot.Id + ":" + lastCursor);
        f.Execute($"UPDATE scope_exports SET manifest='{{}}' WHERE id='{lastId}'");
        var rows = f.Scalar("SELECT count(*) FROM scope_exports"); var audits = f.Scalar("SELECT count(*) FROM audit");
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("DigestMismatch", Fail(() => f.Store.PurgeExpiredPagedScopeExports(f.Customer, "auditor", "default", "dev")).Code);
        Assert.Equal(rows, f.Scalar("SELECT count(*) FROM scope_exports")); Assert.Equal(audits, f.Scalar("SELECT count(*) FROM audit"));
    }

    [Fact]
    public void Storage_failure_after_multiple_page_inserts_rolls_back_the_entire_snapshot()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, 8); var audits = f.Scalar("SELECT count(*) FROM audit");
        f.Execute("CREATE TRIGGER fail_later_export BEFORE INSERT ON scope_exports WHEN (SELECT count(*) FROM scope_exports)>=3 BEGIN SELECT RAISE(ABORT,'fixture storage failure'); END");
        Assert.Throws<SqliteException>(() => Create(f, 1));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM scope_exports")); Assert.Equal(audits, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(8, f.Scalar("SELECT count(*) FROM artifacts"));
    }

    [Fact]
    public void Cancellation_after_pages_and_snapshot_are_staged_rolls_back_all_rows_and_audit()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, 6); var audits = f.Scalar("SELECT count(*) FROM audit");
        using var cancelled = new CancellationTokenSource(); var clock = new CancellingClock(f.Clock, cancelled);
        var store = new FoundationStore(f.Path, f.Cell, f.Customer, clock); clock.Arm = true;
        Assert.Throws<OperationCanceledException>(() => store.CreatePagedScopeExport(f.Customer, "auditor", "default", "dev", 1, cancellationToken: cancelled.Token));
        Assert.True(clock.Reads >= 3); Assert.Equal(0, f.Scalar("SELECT count(*) FROM scope_exports"));
        Assert.Equal(audits, f.Scalar("SELECT count(*) FROM audit")); Assert.Equal(6, f.Scalar("SELECT count(*) FROM artifacts"));
        Assert.Throws<OperationCanceledException>(() => f.Store.CreatePagedScopeExport(f.Customer, "auditor", "default", "dev", cancellationToken: cancelled.Token));
    }

    [Fact]
    public void Page_count_limit_and_oversized_metadata_are_explicit_atomic_failures()
    {
        using var f = new StoreFixture(); SeedArtifacts(f, FoundationStore.MaximumPagedExportPages + 1);
        Assert.Equal("ExportPageCountExceeded", Fail(() => Create(f, 1)).Code);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM scope_exports"));
        f.Execute("DELETE FROM artifacts; INSERT INTO artifacts VALUES('customer-a','one','default','dev','fixture',X'',printf('%0300000d',0),0)");
        Assert.Equal("ExportItemTooLarge", Fail(() => Create(f)).Code); Assert.Equal(0, f.Scalar("SELECT count(*) FROM scope_exports"));
    }

    [Fact]
    public void Invalid_audit_chain_fails_capture_before_producing_pages()
    {
        using var f = new StoreFixture();
        f.Execute("DROP TRIGGER audit_no_update; UPDATE audit SET previous_hash='corrupt' WHERE sequence=2");
        Assert.Equal("AuditIntegrityFailed", Fail(() => Create(f)).Code); Assert.Equal(0, f.Scalar("SELECT count(*) FROM scope_exports"));
    }

    static FoundationPagedExportStart Create(StoreFixture f, int pageSize = 100) => f.Store.CreatePagedScopeExport(f.Customer, "auditor", "default", "dev", pageSize);
    static FoundationPagedExportPage Page(StoreFixture f, FoundationPagedExportStart start, string cursor, string actor = "auditor") =>
        f.Store.ReadPagedScopeExportPage(f.Customer, actor, "default", "dev", start.Snapshot.Id, cursor, start.Snapshot.Sha256);
    static FoundationStoreException Fail(Action action) => Assert.Throws<FoundationStoreException>(action);
    static List<FoundationPagedExportPage> ReadAll(StoreFixture f, FoundationPagedExportStart start)
    {
        var pages = new List<FoundationPagedExportPage> { start.FirstPage };
        while (pages[^1].NextCursor is { } cursor) pages.Add(Page(f, start, cursor));
        return pages;
    }
    static void SeedArtifacts(StoreFixture f, int count) => f.Execute($"""
        WITH RECURSIVE seed(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seed WHERE n<{count})
        INSERT INTO artifacts SELECT '{f.Customer}',printf('artifact-%06d',n),'default','dev','fixture.bicep',X'',
          '{Hash("")}',0 FROM seed;
        """);
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    static void RewriteRow(StoreFixture f, string id, Func<string, string> rewrite)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString()); db.Open();
        using var read = db.CreateCommand(); read.CommandText = "SELECT manifest FROM scope_exports WHERE id=$id"; read.Parameters.AddWithValue("$id", id);
        var json = rewrite((string)read.ExecuteScalar()!);
        using var update = db.CreateCommand(); update.CommandText = "UPDATE scope_exports SET manifest=$manifest,digest=$digest WHERE id=$id";
        update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$manifest", json); update.Parameters.AddWithValue("$digest", Hash(json)); update.ExecuteNonQuery();
    }
    sealed class CancellingClock(TimeProvider inner, CancellationTokenSource source) : TimeProvider
    {
        public bool Arm { get; set; }
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            if (Arm && ++Reads == 3) source.Cancel();
            return inner.GetUtcNow();
        }
    }
}
