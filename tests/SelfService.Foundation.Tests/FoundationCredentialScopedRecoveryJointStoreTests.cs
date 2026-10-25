using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;
using JointFixture = SelfService.Foundation.Tests.FoundationCredentialScopedRecoveryJointCoordinatorTests.JointFixture;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialScopedRecoveryJointStoreTests
{
    [WindowsCredentialFact]
    public async Task Completed_joint_evidence_binds_current_checkpoint_and_same_ordinary_admission_without_new_approval()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.ProtectedSnapshot();
        var completed = await f.Execute(review); var read = f.Store.GetCredentialScopedRecoveryJointVerification(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead), review.Id);
        Assert.Equal(JsonSerializer.Serialize(completed), JsonSerializer.Serialize(read)); Assert.Equal("Succeeded", read.State); Assert.False(read.Quarantined);
        Assert.Equal(f.Head, read.Target.Checkpoint); Assert.Equal(f.Identity, read.Target.CheckpointAuthority); Assert.Equal(f.Receipt.CompletionCoreSha256, read.Target.StageTarget.CompletionCoreSha256);
        Assert.Equal("CurrentCheckpointHeldStageVerified", read.Result!.Status); Assert.True(read.Result.CheckpointReadLeaseRetainedDuringVerification);
        Assert.False(read.Result.PromotionAuthorized); Assert.False(read.Result.LiveAdoptionAuthorized); Assert.False(read.Result.ActivationPerformed); Assert.False(read.Result.ExternalExecutionAuthorized);
        Assert.False(read.Result.IndependentKeyBackupVerified); Assert.Equal("NotAssessed", read.Result.IndependentBackupCoverage);
        var ordinary = await f.Read(review.Id); Assert.Equal(ordinary.Execution!.AdmissionId, read.AdmissionId); Assert.Equal(ordinary.Execution.Result!.StageVerificationSha256, read.Result.StageVerificationSha256);
        Assert.Equal(ordinary.Digest, read.ReviewDigest); Assert.Equal(ordinary.Execution.CompletedAt, read.CompletedAt); Assert.Equal(1, f.Admissions); Assert.Equal(1, f.JointAdmissions);
        Assert.Equal(2, f.Number("SELECT count(*) FROM audit WHERE action='CredentialScopedRecoveryJointCompleted'"));
        Assert.Equal(before, f.ProtectedSnapshot());
        var json = JsonSerializer.Serialize(read); foreach (var forbidden in new[] { CredentialFixture.Token, f.Cell.Root, f.Cell.Scope.AuthProfileId, "\"tag\":", "\"scope\":" }) Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<NotSupportedException>(() => ((IList<FoundationCredentialRecoveryScope>)read.Target.StageTarget.Scopes).Clear());
    }

    [WindowsCredentialFact]
    public async Task Approved_but_unconsumed_review_has_no_joint_evidence_and_no_lookup_side_effects()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.ProtectedSnapshot(); f.ClearKeys();
        var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead);
        Assert.Equal("NotFound", Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialScopedRecoveryJointVerification(readers, review.Id)).Code);
        Assert.Equal(0, f.JointAdmissions); Assert.Equal(0, f.Admissions); Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests); Assert.Equal(before, f.ProtectedSnapshot());
    }

    [Theory]
    [InlineData("DELETE FROM credential_scoped_recovery_joint_executions")]
    [InlineData("UPDATE credential_scoped_recovery_joint_executions SET admission_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("UPDATE credential_scoped_recovery_joint_executions SET target='{}'")]
    [InlineData("UPDATE credential_scoped_recovery_joint_executions SET digest='changed'")]
    [InlineData("UPDATE credential_scoped_recovery_joint_executions SET reason='CallerCancelled'")]
    [InlineData("UPDATE credential_scoped_recovery_joint_executions SET state='OutcomeUnknown'")]
    public async Task Retained_success_and_authority_fields_cannot_be_deleted_or_rewritten(string sql)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var result = await f.Execute(review);
        Assert.Throws<SqliteException>(() => f.Sql(sql)); var read = f.Store.GetCredentialScopedRecoveryJointVerification(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead), review.Id);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(read));
    }

    [Theory]
    [InlineData("unknown-property")] [InlineData("head-hash")] [InlineData("foreign-identity")] [InlineData("over-scopes")]
    public async Task Corrupt_persisted_joint_target_is_refused_without_mutation_or_key_access(string change)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); await f.Execute(review);
        f.Sql("DROP TRIGGER credential_scoped_recovery_joint_immutable;DROP TRIGGER credential_scoped_recovery_joint_success_immutable;");
        var target = JsonNode.Parse(Text(f, "target"))!;
        if (change == "unknown-property") target["unexpectedAuthority"] = true;
        else if (change == "head-hash") target["checkpoint"]!["sha256"] = new string('a', 64);
        else if (change == "foreign-identity") target["checkpointAuthority"]!["customerId"] = Guid.NewGuid().ToString();
        else { var scopes = (JsonArray)target["stageTarget"]!["scopes"]!; while (scopes.Count < 257) scopes.Add(scopes[0]!.DeepClone()); }
        SetText(f, "target", target.ToJsonString()); var unchanged = Text(f, "target"); f.ClearKeys();
        var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead);
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialScopedRecoveryJointVerification(readers, review.Id));
        Assert.Equal(unchanged, Text(f, "target")); Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests);
    }

    [Theory]
    [InlineData("promotion")] [InlineData("checkpoint")] [InlineData("ordinary-verification")] [InlineData("unknown-property")]
    public async Task Coherently_rehashed_result_cannot_claim_other_checkpoint_or_verification_or_promotion(string change)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); await f.Execute(review);
        f.Sql("DROP TRIGGER credential_scoped_recovery_joint_success_immutable;"); var result = JsonNode.Parse(Text(f, "result_json"))!;
        if (change == "promotion") result["promotionAuthorized"] = true;
        else if (change == "checkpoint") result["checkpointSequence"] = 42;
        else if (change == "ordinary-verification") result["stageVerificationSha256"] = new string('b', 64);
        else result["unknownAuthority"] = "invented";
        var json = result.ToJsonString(); SetText(f, "result_json", json); SetText(f, "result_sha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead); f.ClearKeys();
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialScopedRecoveryJointVerification(readers, review.Id));
        Assert.Equal(json, Text(f, "result_json")); Assert.Equal("Succeeded", (await f.Read(review.Id)).Execution!.State); Assert.Empty(f.Destination.Requests);
    }

    static string Text(JointFixture f, string field)
    { using var db = f.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT " + field + " FROM credential_scoped_recovery_joint_executions"; return (string)command.ExecuteScalar()!; }
    static void SetText(JointFixture f, string field, string value)
    { using var db = f.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE credential_scoped_recovery_joint_executions SET " + field + "=$value"; command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery(); }
}
