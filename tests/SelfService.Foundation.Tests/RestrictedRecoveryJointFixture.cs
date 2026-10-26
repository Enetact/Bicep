using System.Runtime.Versioning;
using System.Security.Cryptography;
using SelfService.Foundation;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
internal sealed class RestrictedRecoveryJointFixture : IDisposable
{
    internal RestrictedRecoveryReviewFixture Review { get; }
    internal RestrictedStageFixture Stage => Review.Stage;
    internal CredentialFixture Cell => Review.Cell;
    internal FoundationStore Store => Review.Store;
    internal CredentialClock Clock => Review.Clock;
    internal string Instance => Review.Instance;
    internal CredentialTestKeys Destination => Review.DestinationKeys;
    internal CredentialRestrictedRecoveryStagePlan Plan => Review.Plan;
    internal CredentialRestrictedRecoveryStageReceipt Receipt => Review.Receipt;
    internal string StageRoot => Review.StageRoot;
    internal string CheckpointRoot => Review.Child("joint-restricted-checkpoint");
    internal string StageGuard => Review.Guard;
    internal string CheckpointGuard => Path.Combine(CheckpointRoot, CredentialRestrictedRecoverySetCheckpointAuthority.GuardName);
    internal string CheckpointPath => Path.Combine(CheckpointRoot, "checkpoint-0001.json");
    internal string SignerSha => Convert.ToHexStringLower(SHA256.HashData(Stage.Signer.ExportSubjectPublicKeyInfo()));
    internal CredentialRestrictedRecoverySetCheckpointAuthority Checkpoints { get; private set; } = null!;
    internal CredentialRestrictedRecoverySetCheckpointAuthorityIdentity Identity => Checkpoints.Identity;
    internal CredentialRestrictedRecoverySetCheckpoint Head { get; private set; } = null!;
    internal RestrictedRecoveryJointFixture(string provider = PipelineRequestFactory.GitHub, string mode = "mixed") { Review = new(provider, mode); }
    internal async Task Initialize()
    {
        await Review.Initialize();
        Checkpoints = CredentialRestrictedRecoverySetCheckpointAuthority.Provision(CheckpointRoot, Plan.SourceNamespace, "restricted-stage-signer", SignerSha, Clock);
        Head = await Checkpoints.AcceptNextAsync(Stage.Set, Stage.Set.Sha256, Stage.Signer, Cell.Keys, 0, null); ClearKeys();
    }
    internal CredentialRestrictedRecoverySetCheckpointAuthority Reopen(TimeProvider clock) => new(CheckpointRoot, Plan.SourceNamespace, "restricted-stage-signer", SignerSha, clock);
    internal FoundationCredentialRestrictedRecoveryJointReviewCoordinator Coordinator(ICredentialKeyProvider? keys = null, TimeSpan? timeout = null,
        FoundationStore? store = null, TimeProvider? verifierClock = null, CredentialRestrictedRecoverySetCheckpointAuthority? authority = null) =>
        new(store ?? Store, authority ?? Checkpoints, Identity, new(keys ?? Destination, verifierClock ?? Clock), Instance, timeout);
    internal Task<FoundationCredentialRestrictedRecoveryStageReview> Approved() => Review.Approved();
    internal Task<FoundationCredentialRestrictedRecoveryStageReview> Prepare() => Review.Prepare();
    internal Task<IReadOnlyList<CustomerContext>> Contexts(string capability, string? actor = null, TimeSpan? lifetime = null, FoundationStore? store = null, string? signedRole = null) =>
        Review.Contexts(capability, actor, lifetime, store, signedRole);
    internal async Task<FoundationCredentialRestrictedRecoveryJointVerification> Execute(FoundationCredentialRestrictedRecoveryStageReview review,
        FoundationCredentialRestrictedRecoveryJointReviewCoordinator? coordinator = null, IReadOnlyList<CustomerContext>? contexts = null, CancellationToken token = default) =>
        await (coordinator ?? Coordinator()).ExecuteApprovedAsync(contexts ?? await Contexts(FoundationCapabilities.CredentialRecoveryVerify), review.Id, review.Digest,
            Plan, Plan.Sha256, Receipt.Sha256, Head.Sequence, Head.Sha256, token);
    internal async Task<FoundationCredentialRestrictedRecoveryJointVerification> Read(string id, FoundationStore? store = null) =>
        (store ?? Store).GetCredentialRestrictedRecoveryJointVerification(await Contexts(FoundationCapabilities.CredentialRecoveryRead, store: store), id);
    internal Task<CredentialRestrictedRecoverySet> Capture(long generation = 2) => Stage.Source.CaptureRestrictedSignedRecoverySetAsync(Plan.SourceNamespace,
        Stage.Signer, "restricted-stage-signer", generation, Clock.Now, Clock.Now.AddHours(1));
    internal Task<CredentialRestrictedRecoverySetCheckpoint> Accept(CredentialRestrictedRecoverySet set) =>
        Checkpoints.AcceptNextAsync(set, set.Sha256, Stage.Signer, Cell.Keys, Head.Sequence, Head.Sha256);
    internal long Admissions => Review.Admissions;
    internal long JointAdmissions => Number("SELECT count(*) FROM credential_restricted_recovery_joint_executions");
    internal long Number(string sql) => Review.Number(sql);
    internal void Sql(string sql) => Review.Sql(sql);
    internal void ClearKeys() => Review.ClearKeys();
    internal string[] Snapshot() => Review.StageAndSourceSnapshot().Concat(Directory.GetFiles(CheckpointRoot, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
    public void Dispose() => Review.Dispose();
}
