using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryJointStoreTests
{
    [Fact]
    public void Composite_foreign_key_rejects_crossed_ordinary_review_and_admission_even_when_each_parent_exists()
    {
        using var f = new StoreFixture();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false, ForeignKeys = true }.ToString()); db.Open();
        using var tx = db.BeginTransaction();
        // These deliberately minimal SQL rows test the physical relationship only; they never pass application authorization or a verifier.
        foreach (var id in new[] { "first", "second" })
        {
            using var parent = db.CreateCommand(); parent.Transaction = tx;
            parent.CommandText = """
                INSERT INTO credential_restricted_recovery_stage_review_requests VALUES($c,$id,'default','dev','{}',$sha,'admin',1,100,1,$instance,$sha,100,$sha,'Prepared',1);
                INSERT INTO credential_restricted_recovery_stage_review_executions VALUES($c,$id,$admission,'admin',1,$instance,'OutcomeUnknown','AdmittedBeforeVerification',NULL,NULL,2,NULL,2);
                """;
            parent.Parameters.AddWithValue("$c", f.Customer); parent.Parameters.AddWithValue("$id", id);
            parent.Parameters.AddWithValue("$admission", id + "-admission"); parent.Parameters.AddWithValue("$sha", new string('a', 64));
            parent.Parameters.AddWithValue("$instance", f.Store.GetCellInstanceIdentity().DatabaseInstanceId); parent.ExecuteNonQuery();
        }
        using var insert = db.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO credential_restricted_recovery_joint_executions VALUES($c,'first',$admission,'{}',$sha,$sha,'OutcomeUnknown','AdmittedBeforeVerification',NULL,NULL,2,NULL,2)";
        insert.Parameters.AddWithValue("$c", f.Customer); var admission = insert.Parameters.AddWithValue("$admission", "second-admission");
        insert.Parameters.AddWithValue("$sha", new string('a', 64));
        var denied = Assert.Throws<SqliteException>(() => insert.ExecuteNonQuery()); Assert.Equal(787, denied.SqliteExtendedErrorCode);
        admission.Value = "first-admission"; Assert.Equal(1, insert.ExecuteNonQuery());
        tx.Rollback(); Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_restricted_recovery_joint_executions"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_restricted_recovery_stage_review_executions"));
    }

    [WindowsCredentialFact]
    public async Task Joined_history_binds_same_ordinary_spend_actual_checkpoint_and_completion_MAC_without_source_keys()
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.Snapshot();
        f.Cell.Keys.Values.Clear(); f.ClearKeys(); var completed = await f.Execute(review); var read = await f.Read(review.Id);
        Assert.Equal("Succeeded", read.State); Assert.Equal(JsonSerializer.Serialize(completed), JsonSerializer.Serialize(read));
        Assert.Equal(f.Identity, read.Target.CheckpointAuthority); Assert.Equal(f.Head, read.Target.Checkpoint);
        Assert.Equal(f.Receipt.CompletionProofSha256, read.Target.StageTarget.CompletionProofSha256);
        Assert.Equal(f.Receipt.CompletionProofSha256, read.Result!.CompletionProofSha256); Assert.True(read.Result.DestinationCompletionProofAuthenticated);
        Assert.Equal("CurrentCheckpointHeldRestrictedStageVerified", read.Result.Status); Assert.Equal("FoundationBoundDispatchOnly", read.AccessProfile);
        Assert.False(read.Result.PromotionAuthorized); Assert.False(read.Result.ActivationPerformed); Assert.False(read.Result.LiveAdoptionAuthorized);
        Assert.False(read.Result.IndependentKeyBackupVerified); Assert.Equal("NotAssessed", read.Result.IndependentBackupCoverage);
        var ordinary = await f.Review.Read(review.Id); Assert.Equal(ordinary.Execution!.AdmissionId, read.AdmissionId);
        Assert.Equal(ordinary.Execution.Result!.StageVerificationSha256, read.Result.StageVerificationSha256); Assert.Equal(ordinary.Execution.CompletedAt, read.CompletedAt);
        Assert.Equal(1, f.Admissions); Assert.Equal(1, f.JointAdmissions); Assert.Equal(2, f.Number("SELECT count(*) FROM audit WHERE action='CredentialRestrictedRecoveryJointCompleted'"));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Single(f.Destination.Requests); Assert.Equal(before, f.Snapshot());
        var json = JsonSerializer.Serialize(read); foreach (var forbidden in new[] { CredentialFixture.Token, f.Cell.Root, "\"tag\":", "\"ciphertext\":" }) Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DELETE FROM credential_restricted_recovery_joint_executions")]
    [InlineData("UPDATE credential_restricted_recovery_joint_executions SET admission_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("UPDATE credential_restricted_recovery_joint_executions SET target='{}'")]
    [InlineData("UPDATE credential_restricted_recovery_joint_executions SET digest='changed'")]
    [InlineData("UPDATE credential_restricted_recovery_joint_executions SET state='OutcomeUnknown'")]
    [InlineData("UPDATE credential_restricted_recovery_joint_executions SET reason='CallerCancelled'")]
    public async Task Completed_joint_authority_and_result_cannot_be_rewritten_or_deleted(string sql)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); var result = await f.Execute(review);
        Assert.Throws<SqliteException>(() => f.Sql(sql)); Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await f.Read(review.Id)));
    }

    [Theory]
    [InlineData("promotion")] [InlineData("checkpoint")] [InlineData("completion-proof")] [InlineData("authentication")]
    public async Task Coherently_rehashed_joint_result_cannot_change_checkpoint_completion_or_authority(string change)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); await f.Execute(review);
        f.Sql("DROP TRIGGER credential_restricted_recovery_joint_success_immutable;");
        using var db = f.Review.Open(); using var read = db.CreateCommand(); read.CommandText = "SELECT result_json FROM credential_restricted_recovery_joint_executions";
        var node = JsonNode.Parse((string)read.ExecuteScalar()!)!;
        if (change == "promotion") node["promotionAuthorized"] = true;
        else if (change == "checkpoint") node["checkpointSequence"] = 42;
        else if (change == "completion-proof") node["completionProofSha256"] = new string('a', 64);
        else node["destinationCompletionProofAuthenticated"] = false;
        var json = node.ToJsonString(); using var write = db.CreateCommand();
        write.CommandText = "UPDATE credential_restricted_recovery_joint_executions SET result_json=$json,result_sha256=$sha";
        write.Parameters.AddWithValue("$json", json); write.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))); write.ExecuteNonQuery();
        f.ClearKeys(); var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead);
        Assert.Equal("RestrictedRecoveryJointIntegrityFailed", Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialRestrictedRecoveryJointVerification(readers, review.Id)).Code);
        Assert.Equal("Succeeded", (await f.Review.Read(review.Id)).Execution!.State); Assert.Empty(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(json, (string)read.ExecuteScalar()!);
    }
}
