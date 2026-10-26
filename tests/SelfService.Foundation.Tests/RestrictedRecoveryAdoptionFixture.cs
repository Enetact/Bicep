using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using SelfService.Foundation;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
internal sealed class RestrictedRecoveryAdoptionFixture : IDisposable
{
    internal RestrictedRecoveryJointFixture Joint { get; }
    internal RestrictedRecoveryReviewFixture Review => Joint.Review;
    internal RestrictedStageFixture Stage => Joint.Stage;
    internal CredentialFixture Cell => Joint.Cell;
    internal CredentialClock Clock => Joint.Clock;
    internal FoundationStore SourceStore => Joint.Store;
    internal FoundationStore RestoredStore { get; private set; } = null!;
    internal FoundationCredentialRuntimeBinding? SourceBinding { get; private set; }
    internal FoundationCellActivationAuthority Activation { get; private set; } = null!;
    internal FoundationCellActivationAuthorityIdentity ActivationIdentity { get; private set; } = null!;
    internal FoundationCellActivationBinding ActivationHead { get; private set; } = null!;
    internal CredentialRestrictedRecoverySetCheckpointAuthority Checkpoints => Joint.Checkpoints;
    internal CredentialRestrictedRecoverySetCheckpointAuthorityIdentity Identity => Joint.Identity;
    internal CredentialRestrictedRecoverySetCheckpoint Head => Joint.Head;
    internal CredentialRestrictedRecoveryStagePlan Plan => Joint.Plan;
    internal CredentialRestrictedRecoveryStageReceipt Receipt => Joint.Receipt;
    internal CredentialTestKeys Destination => Joint.Destination;
    internal string ActivationRoot => Review.Child("adoption-selected-activation");
    internal string ActivationGuard => Path.Combine(ActivationRoot, ".activation.guard");
    internal string ActivationRecord => Path.Combine(ActivationRoot, "activation-0001.json");
    internal string Database => Review.Child("adoption-restored.db");
    internal string FutureVault => Review.Child("future-restricted-vault");
    internal string FutureRollback => Review.Child("future-restricted-rollback");
    internal string Instance => RestoredStore.GetCellInstanceIdentity().DatabaseInstanceId;
    internal FoundationBackup Backup { get; private set; } = null!;
    internal string SourceInstance => Joint.Instance;
    readonly bool bindSource;
    internal RestrictedRecoveryAdoptionFixture(string provider = PipelineRequestFactory.GitHub, string mode = "mixed", bool bindSource = true)
    { Joint = new(provider, mode); this.bindSource = bindSource; }
    internal async Task Initialize()
    {
        await Joint.Initialize();
        Activation = FoundationCellActivationAuthority.Provision(ActivationRoot, Review.Customer, Review.CellId, Clock);
        ActivationHead = Activation.Activate(Review.Database, Joint.Instance, 0, null); ActivationIdentity = Activation.DescribeIdentity();
        if (bindSource) SourceBinding = FoundationCredentialBoundTransport.BindCurrent(SourceStore, Stage.Source, Activation, Plan.SourceNamespace, ActivationIdentity, Joint.Instance);
        Backup = SourceStore.CreateBackup(Review.Child("adoption-origin-backup.sqlite"));
        RestoredStore = FoundationStore.RestoreBackup(Backup, Database, Clock); Joint.ClearKeys();
    }
    internal FoundationCredentialRestrictedRecoveryAdoptionCoordinator Coordinator(ICredentialKeyProvider? keys = null, TimeSpan? timeout = null,
        FoundationStore? store = null, TimeProvider? verifierClock = null, FoundationCellActivationAuthority? activation = null,
        CredentialRestrictedRecoverySetCheckpointAuthority? checkpoints = null, string? futureVault = null, string? futureRollback = null) =>
        new(store ?? RestoredStore, activation ?? Activation, ActivationIdentity, ActivationHead.Generation, ActivationHead.Sha256,
            checkpoints ?? Checkpoints, Identity, new(keys ?? Destination, verifierClock ?? Clock), (store ?? RestoredStore).GetCellInstanceIdentity().DatabaseInstanceId,
            futureVault ?? FutureVault, futureRollback ?? FutureRollback, timeout);
    internal Task<IReadOnlyList<CustomerContext>> Contexts(string capability, string? actor = null, TimeSpan? lifetime = null, FoundationStore? store = null, string? signedRole = null) =>
        Review.Contexts(capability, actor, lifetime, store ?? RestoredStore, signedRole);
    internal async Task<FoundationCredentialRestrictedRecoveryAdoptionIntent> Prepare(FoundationCredentialRestrictedRecoveryAdoptionCoordinator? coordinator = null,
        IReadOnlyList<CustomerContext>? contexts = null, CancellationToken token = default) =>
        await (coordinator ?? Coordinator()).PrepareAsync(contexts ?? await Contexts(FoundationCapabilities.CredentialRecoveryPrepare), Plan, Plan.Sha256,
            Receipt.Sha256, Head.Sequence, Head.Sha256, "Synthetic restricted adoption intent only", Clock.Now.AddMinutes(5), token);
    internal async Task<FoundationCredentialRestrictedRecoveryAdoptionIntent> Approve(FoundationCredentialRestrictedRecoveryAdoptionIntent intent,
        FoundationCredentialRestrictedRecoveryAdoptionCoordinator? coordinator = null, IReadOnlyList<CustomerContext>? contexts = null, CancellationToken token = default) =>
        await (coordinator ?? Coordinator()).ApproveAsync(contexts ?? await Contexts(FoundationCapabilities.CredentialRecoveryApprove), intent.Id, intent.Digest,
            Plan, Plan.Sha256, Receipt.Sha256, Head.Sequence, Head.Sha256, token);
    internal async Task<FoundationCredentialRestrictedRecoveryAdoptionIntent> Read(string id, FoundationStore? store = null) =>
        (store ?? RestoredStore).GetCredentialRestrictedRecoveryAdoptionIntent(await Contexts(FoundationCapabilities.CredentialRecoveryRead, store: store), id);
    internal long Number(string sql, FoundationStore? store = null)
    { using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store is null || ReferenceEquals(store, RestoredStore) ? Database : Review.Database, Pooling = false, DefaultTimeout = 1 }.ToString()); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    internal void Sql(string sql)
    { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    internal Microsoft.Data.Sqlite.SqliteConnection Open()
    { var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Database, Pooling = false, DefaultTimeout = 1 }.ToString()); db.Open(); return db; }
    internal void ClearKeys() => Joint.ClearKeys();
    internal string[] ProtectedSnapshot() => Joint.Snapshot().Concat(Directory.EnumerateFileSystemEntries(ActivationRoot, "*", SearchOption.AllDirectories).Prepend(ActivationRoot)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(
            (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm())) + ":" +
            (Directory.Exists(path) ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))))).ToArray();
    internal async Task<FoundationRecoveryExecutionHoldStatus> Hold() => RestoredStore.GetRecoveryExecutionHold((await Contexts(FoundationCapabilities.RecoveryRead))[0]);
    public void Dispose() => Joint.Dispose();
}
