using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialScopedRecoveryEndToEndTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, false)]
    [InlineData(PipelineRequestFactory.GitHub, true)]
    [InlineData(PipelineRequestFactory.AzureDevOps, false)]
    [InlineData(PipelineRequestFactory.AzureDevOps, true)]
    public async Task Scoped_checkpoint_stage_and_complete_human_review_authenticate_retained_output_without_source_roots_or_keys(string provider, bool absenceOnly)
    {
        using var f = new Fixture(provider); await f.Initialize(absenceOnly);
        Assert.Equal(absenceOnly ? 1 : 2, f.Plan.AffectedScopes.Count);
        Assert.Single(f.Receipt.Absences); Assert.Equal(absenceOnly ? 0 : 1, f.Receipt.Profiles.Count);
        Assert.Equal(f.Head.SetAuthority.ArtifactSha256, f.Receipt.SourceSetSha256);
        Assert.True(f.Staged.CheckpointReadLeaseRetainedDuringStaging);
        var before = f.StageSnapshot(); f.SourceKeys.Values.Clear(); f.SourceKeys.Requests.Clear(); f.DestinationKeys.Requests.Clear();
        var coordinator = f.Coordinator();
        var review = await coordinator.PrepareAsync(await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare), f.Plan,
            f.Plan.Sha256, f.Receipt.Sha256, "Synthetic full scoped recovery review", f.Cell.Clock.Now.AddMinutes(5));
        Assert.Equal(f.Plan.AffectedScopes, review.Target.Scopes);
        Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.SourceKeys.Requests);
        review = f.Store.ApproveCredentialScopedRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest);
        Assert.Empty(f.DestinationKeys.Requests);
        var completed = await coordinator.ExecuteAsync(await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify), review.Id,
            review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256);
        Assert.Equal("Succeeded", completed.Execution!.State); Assert.NotNull(completed.Execution.Result);
        Assert.False(completed.Execution.Result.PromotionAuthorized); Assert.False(completed.Execution.Result.LiveAdoptionAuthorized);
        Assert.False(completed.Execution.Result.ActivationPerformed); Assert.False(completed.Execution.Result.ExternalExecutionAuthorized);
        Assert.Single(f.DestinationKeys.Requests); Assert.Empty(f.SourceKeys.Requests); Assert.Equal(before, f.StageSnapshot());
        Assert.Equal(f.Head, f.Checkpoints.ReadCurrent());
        var json = JsonSerializer.Serialize(completed);
        Assert.DoesNotContain(CredentialFixture.Token, json); Assert.DoesNotContain(f.Cell.Root, json); Assert.DoesNotContain("profile/recover-absence", json);
        f.DestinationKeys.Requests.Clear();
        var replayContexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAsync<FoundationStoreException>(() => coordinator.ExecuteAsync(replayContexts,
            review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.Empty(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Present_profile_scope_alone_never_authorizes_review_of_an_additional_absence_scope(string provider)
    {
        using var f = new Fixture(provider); await f.Initialize(false);
        var before = f.StageSnapshot(); f.DestinationKeys.Requests.Clear();
        var incomplete = (await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare)).Where(c => c.ProjectId == "default").ToArray();
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Coordinator().PrepareAsync(incomplete, f.Plan, f.Plan.Sha256,
            f.Receipt.Sha256, "Synthetic omitted absence scope", f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.StageSnapshot());
    }

    sealed class Fixture : IDisposable
    {
        const string RequesterSubject = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        readonly IdentityTestIssuer issuer = new();
        readonly RSA signer = RSA.Create(2048);
        public CredentialFixture Cell { get; }
        public CredentialTestKeys SourceKeys => Cell.Keys;
        public CredentialTestKeys DestinationKeys { get; } = new();
        public FoundationStore Store { get; }
        public CredentialScopedRecoverySet Set { get; private set; } = null!;
        public CredentialScopedRecoverySetCheckpointAuthority Checkpoints { get; private set; } = null!;
        public CredentialScopedRecoverySetCheckpoint Head { get; private set; } = null!;
        public CredentialScopedRecoveryCheckpointStageResult Staged { get; private set; } = null!;
        public CredentialScopedRecoveryStagePlan Plan { get; private set; } = null!;
        public CredentialScopedRecoveryStageReceipt Receipt => Staged.StageReceipt;
        string Child(string name) => Path.Combine(Cell.Root, name);
        public Fixture(string provider)
        {
            Cell = new(provider); Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
            Store = new(Child("cell.db"), Cell.Scope.CellId, Cell.Scope.CustomerId, Cell.Clock);
            var onboarding = Store.PrepareScopeOnboarding(Cell.Scope.CustomerId, "admin", "recover-absence", "qa", "absence-admin", "Synthetic recovery isolation scope");
            Store.ApproveAdministrativePlan(Cell.Scope.CustomerId, "approver", onboarding.Id, onboarding.Digest);
            Store.ExecuteScopeOnboarding(Cell.Scope.CustomerId, "admin", onboarding.Id, onboarding.Digest);
            Grant(RequesterSubject, "Operator"); Grant(IdentityTestIssuer.Subject, "Operator"); Grant(IdentityTestIssuer.OtherSubject, "Approver");
            DestinationKeys.Values.Clear(); DestinationKeys.Values.Add("scoped-destination", RandomNumberGenerator.GetBytes(32));
        }
        void Grant(string subject, string role)
        {
            var actor = "user:" + IdentityTestIssuer.Tenant + ":" + subject;
            Store.SetMembership(Cell.Scope.CustomerId, "admin", actor, "default", "dev", [role]);
            Store.SetMembership(Cell.Scope.CustomerId, "absence-admin", actor, "recover-absence", "qa", [role]);
        }
        public async Task Initialize(bool absenceOnly)
        {
            var options = Cell.Options with { RootPath = Child("scoped-source"), RollbackProtection = new(Child("scoped-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            var source = CredentialVault.ProvisionStrongScoped(options, SourceKeys, Cell.Clock);
            var absent = Cell.Scope with { AuthProfileId = "profile/recover-absence", ProjectId = "recover-absence", EnvironmentId = "qa" };
            using var material = CredentialSecret.FromUtf8(CredentialFixture.Token);
            var fault = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!;
            fault.SetValue(source, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic abandoned first write."); }));
            await Assert.ThrowsAsync<IOException>(() => source.PutAsync(absent, material, Cell.Clock.Now.AddHours(1)));
            fault.SetValue(source, null); var pending = await source.InspectRollbackStateAsync(absent.AuthProfileId);
            await source.ReconcilePendingWriteAsync(absent.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256,
                pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
            SourceKeys.Values.Add("key-2", RandomNumberGenerator.GetBytes(32));
            source = new(options with { EncryptionKeyId = "key-2" }, SourceKeys, Cell.Clock);
            if (!absenceOnly) await source.PutAsync(Cell.Scope, material, Cell.Clock.Now.AddHours(1));
            var identity = source.DescribeNamespace();
            Set = await source.CaptureScopedSignedRecoverySetAsync(identity, signer, "scoped-build-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
            var authority = CredentialScopedRecoverySets.Authority(Set);
            Checkpoints = CredentialScopedRecoverySetCheckpointAuthority.Provision(Child("scoped-checkpoints"), identity,
                authority.SignerKeyId, authority.PublicKeySha256, Cell.Clock);
            Directory.Move(options.RootPath, Child("retained-source-original"));
            Directory.Move(options.RollbackProtection!.AuthorityDirectory, Child("retained-authority-original"));
            Head = await Checkpoints.AcceptNextAsync(Set, Set.Sha256, signer, SourceKeys, 0, null);
            var selected = CredentialScopedRecoverySets.Verify(Set, authority, signer, identity, Cell.Clock);
            var destination = await CredentialScopedRecoveryStaging.ObserveDestinationKeyAsync(identity.CustomerId, identity.CellId, "scoped-destination", DestinationKeys);
            Plan = CredentialScopedRecoveryStaging.PreparePlan(selected, authority, identity, Child("scoped-output"), destination,
                "Synthetic exact checkpoint-bound staging", Cell.Clock.Now.AddMinutes(20), Cell.Clock);
            Staged = await new CredentialScopedRecoveryCheckpointStageExecutor(Checkpoints, Checkpoints.DescribeIdentity(),
                new CredentialScopedRecoveryStageExecutor(SourceKeys, DestinationKeys, Cell.Clock))
                .StageLatestAsync(Set, Plan, Plan.Sha256, signer, Head.Sequence, Head.Sha256);
        }
        public FoundationCredentialScopedRecoveryStageReviewCoordinator Coordinator() => new(Store,
            new CredentialScopedRecoveryStageVerifier(DestinationKeys, Cell.Clock), Store.GetCellInstanceIdentity().DatabaseInstanceId);
        public async Task<IReadOnlyList<CustomerContext>> Contexts(string capability)
        {
            var approve = capability == FoundationCapabilities.CredentialRecoveryApprove;
            var subject = approve ? IdentityTestIssuer.OtherSubject : capability == FoundationCapabilities.CredentialRecoveryPrepare ? RequesterSubject : IdentityTestIssuer.Subject;
            var options = issuer.Options(Cell.Scope.CustomerId, Cell.Scope.CellId); var claims = issuer.UserClaims(approve ? "Approver" : "Operator", subject);
            claims["iat"] = claims["nbf"] = Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.AddMinutes(10).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims));
            var resolver = new CustomerContextResolver(options, new Memberships(Store), Cell.Clock);
            return Plan.AffectedScopes.Select(scope => resolver.Require(principal, Cell.Scope.CustomerId, scope.ProjectId, scope.EnvironmentId, capability)).ToArray();
        }
        public string[] StageSnapshot() => Directory.GetFiles(Child("scoped-output"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { Set?.Dispose(); signer.Dispose(); issuer.Dispose(); Cell.Dispose(); }
        sealed class Memberships(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customer, string actor) => store.GetCredentialMaintenanceMemberships(customer, actor)
            .Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
