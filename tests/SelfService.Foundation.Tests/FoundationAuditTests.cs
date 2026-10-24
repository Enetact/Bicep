using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationAuditTests
{
    [Fact]
    public void Audit_is_append_only_correlated_bounded_and_contains_no_plan_payload()
    {
        using var f = new StoreFixture(); var job = f.Queue();
        f.Store.RecordSecurityEvent("InvalidSignature", "correlation-1");
        var events = f.Store.ListAudit(f.Customer, "auditor", limit: 100);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor"));
        Assert.Contains(events, e => e.Action == "JobQueued" && e.TargetId == job.Id && e.CorrelationId == job.PlanId);
        Assert.All(events, e => { Assert.Equal(f.Customer, e.CustomerId); Assert.Equal(f.Cell, e.CellId); Assert.Equal(64, e.EventHash.Length); });
        Assert.DoesNotContain(events, e => e.Reason.Contains("eastus2", StringComparison.Ordinal));
        Assert.Throws<SqliteException>(() => f.Execute("UPDATE audit SET reason='modified'"));
        Assert.Throws<SqliteException>(() => f.Execute("DELETE FROM audit"));
        Assert.Equal("InvalidPage", Assert.Throws<FoundationStoreException>(() => f.Store.ListAudit(f.Customer, "auditor", limit: 1001)).Code);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ListAudit(f.Customer, "viewer")).Code);
        Assert.Equal("InvalidReasonCode", Assert.Throws<FoundationStoreException>(() => f.Store.RecordSecurityEvent("Bearer sample", "correlation-1")).Code);
        var page = f.Store.ListAudit(f.Customer, "auditor", limit: 2); Assert.Equal(2, page.Count);
        Assert.All(f.Store.ListAudit(f.Customer, "auditor", afterSequence: page[^1].Sequence), e => Assert.True(e.Sequence > page[^1].Sequence));
    }

    [Fact]
    public void Audit_hash_chain_detects_database_tampering_when_privileged_writer_bypasses_trigger()
    {
        using var f = new StoreFixture();
        f.Execute("DROP TRIGGER audit_no_update; UPDATE audit SET reason='tampered' WHERE sequence=2");
        Assert.False(f.Store.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Fact]
    public void Artifact_bytes_are_customer_scoped_hash_verified_and_filename_bounded()
    {
        using var f = new StoreFixture(); var bytes = "mock bicep parameter file"u8.ToArray();
        var saved = f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "main.bicepparam", bytes);
        bytes[0] = 0; Assert.Equal((byte)'m', f.Store.GetArtifact(f.Customer, "viewer", saved.Id).Content[0]);
        Assert.Equal("CustomerScopeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.GetArtifact("customer-b", "viewer", saved.Id)).Code);
        using var other = new StoreFixture("customer-b", "cell-b");
        Assert.Equal("NotFound", Assert.Throws<FoundationStoreException>(() => other.Store.GetArtifact(other.Customer, "viewer", saved.Id)).Code);
        Assert.Equal("InvalidArtifact", Assert.Throws<FoundationStoreException>(() => f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "../escape.bicep", bytes)).Code);
        Assert.Equal("InvalidArtifact", Assert.Throws<FoundationStoreException>(() => f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "huge.bicep", new byte[1048577])).Code);
        f.Execute("UPDATE artifacts SET content=X'00'");
        Assert.Equal("ArtifactIntegrityFailed", Assert.Throws<FoundationStoreException>(() => f.Store.GetArtifact(f.Customer, "viewer", saved.Id)).Code);
    }
}
