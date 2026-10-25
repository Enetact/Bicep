using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

public static partial class CredentialRecoveryStaging
{
    public const int MaximumProfiles = 256, MaximumEntries = 264, MaximumMetadataBytes = 1048576;
    public const long MaximumStageBytes = 40L * 1024 * 1024;
    internal const string GuardName = ".credential-recovery-stage.guard", MarkerName = ".credential-recovery-stage.json";

    public static async Task<CredentialRecoveryDestinationKeyIdentity> ObserveDestinationKeyAsync(string customerId, string cellId,
        string keyId, ICredentialKeyProvider keys, CancellationToken cancellationToken = default)
    {
        RequireKeyIdentity(new(customerId, cellId, keyId, new string('a', 64)));
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken);
        try
        {
            using var key = await ExistingKey(customerId, cellId, keyId, keys, deadline.Token); var bytes = key.CopyBytes();
            try { deadline.Token.ThrowIfCancellationRequested(); return new(customerId, cellId, keyId, KeyCommitment(customerId, cellId, keyId, bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Recovery destination observation canceled.", cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw Failure("timeout"); }
        catch (OperationCanceledException) { throw Failure("destination-key-unavailable"); }
    }

    public static CredentialRecoveryStagePlan PreparePlan(CredentialRecoverySetVerification selectedSet,
        CredentialRecoverySetAuthority exactAuthority, CredentialVaultNamespaceIdentity expectedSourceNamespace, string freshStageDirectory,
        CredentialRecoveryDestinationKeyIdentity destinationKey, string purpose, DateTimeOffset expiresUtc, TimeProvider? timeProvider = null)
    {
        try
        {
            CredentialRecoverySets.RequireIdentity(expectedSourceNamespace); RequireKeyIdentity(destinationKey);
            if (selectedSet is null || exactAuthority is null || selectedSet.Signature.SourceNamespace != expectedSourceNamespace ||
                destinationKey.CustomerId != expectedSourceNamespace.CustomerId || destinationKey.CellId != expectedSourceNamespace.CellId ||
                exactAuthority != new CredentialRecoverySetAuthority(selectedSet.Signature.CustomerId, selectedSet.Signature.CellId,
                    selectedSet.Signature.SetId, selectedSet.Signature.Generation, selectedSet.Sha256, selectedSet.Signature.KeyId, selectedSet.Signature.PublicKeySha256))
                throw Failure("source-selection-mismatch");
            var now = Milliseconds((timeProvider ?? TimeProvider.System).GetUtcNow());
            CredentialRecoverySets.ValidateLifetime(selectedSet.Signature, now);
            if (!CredentialRecoverySets.Utc(expiresUtc) || expiresUtc <= now || expiresUtc > now.AddHours(1) || expiresUtc > selectedSet.Signature.ExpiresUtc)
                throw Failure("plan-expiry-invalid");
            if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 1024) throw Failure("purpose-invalid");
            try { _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { purpose }, CredentialRecoverySets.Json)); }
            catch (FoundationStoreException) { throw Failure("purpose-invalid"); }
            var path = PreflightDestination(freshStageDirectory, expectedSourceNamespace.CustomerId, expectedSourceNamespace.CellId); var sid = CurrentSid();
            var plan = new CredentialRecoveryStagePlan(path, sid, Guid.NewGuid().ToString("N"), exactAuthority, selectedSet.Signature,
                expectedSourceNamespace, RootCommitment(path, sid, expectedSourceNamespace.CustomerId, expectedSourceNamespace.CellId), destinationKey,
                Hash(Encoding.UTF8.GetBytes(purpose)), now, expiresUtc,
                selectedSet.PreservedProfileHashes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new CredentialRecoveryPreservedProfile(p.Key, p.Value)),
                selectedSet.AbsentProfileIds, selectedSet.Inventory);
            ValidatePlan(plan, plan.Sha256); return plan;
        }
        catch (CredentialException) { throw; }
        catch (Exception error) when (LocalFailure(error)) { throw Failure("plan-unavailable"); }
    }

    internal static void ValidatePlan(CredentialRecoveryStagePlan plan, string reviewedPlanSha256)
    {
        if (plan is null || !Digest(reviewedPlanSha256) || plan.Sha256 != reviewedPlanSha256 || plan.Sha256 != PlanDigest(plan)) throw Failure("plan-mismatch");
        CredentialRecoverySets.RequireSignature(plan.SourceSignature); CredentialRecoverySets.RequireIdentity(plan.SourceNamespace); RequireKeyIdentity(plan.DestinationKey);
        plan.SourceInventory.ValidateIntegrity();
        if (!CredentialRecoverySets.NamespaceShape(plan.PlanId) || plan.SourceSignature.SourceNamespace != plan.SourceNamespace ||
            plan.SourceSetAuthority != new CredentialRecoverySetAuthority(plan.SourceSignature.CustomerId, plan.SourceSignature.CellId,
                plan.SourceSignature.SetId, plan.SourceSignature.Generation, plan.SourceSetAuthority.ArtifactSha256, plan.SourceSignature.KeyId, plan.SourceSignature.PublicKeySha256) ||
            !Digest(plan.SourceSetAuthority.ArtifactSha256) || plan.SourceInventory.Sha256 != plan.SourceSignature.InventorySha256 ||
            plan.SourceInventory.CustomerId != plan.SourceNamespace.CustomerId || plan.SourceInventory.CellId != plan.SourceNamespace.CellId ||
            plan.DestinationKey.CustomerId != plan.SourceNamespace.CustomerId || plan.DestinationKey.CellId != plan.SourceNamespace.CellId ||
            !Digest(plan.PurposeSha256) || !CredentialRecoverySets.Utc(plan.PreparedUtc) || !CredentialRecoverySets.Utc(plan.ExpiresUtc) ||
            plan.ExpiresUtc <= plan.PreparedUtc || plan.ExpiresUtc > plan.PreparedUtc.AddHours(1) || plan.ExpiresUtc > plan.SourceSignature.ExpiresUtc ||
            plan.PreservedProfiles.Count + plan.AbsentProfileIds.Count > MaximumProfiles ||
            plan.PreservedProfiles.Count != plan.SourceInventory.ProfileDependencies.Count || plan.PreservedProfiles.Count != plan.SourceSignature.ProfileCount ||
            !plan.PreservedProfiles.Select(p => p.ProfileIdSha256).SequenceEqual(plan.SourceInventory.ProfileDependencies.Select(p => p.ProfileIdSha256), StringComparer.Ordinal) ||
            plan.PreservedProfiles.Any(p => !Digest(p.ProfileIdSha256) || !Digest(p.ProfileSha256)) ||
            !plan.AbsentProfileIds.SequenceEqual(plan.AbsentProfileIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            plan.AbsentProfileIds.Any(p => !Digest(p)) || plan.AbsentProfileIds.Intersect(plan.PreservedProfiles.Select(p => p.ProfileIdSha256), StringComparer.Ordinal).Any())
            throw Failure("plan-invalid");
        var path = LocalPath(plan.RootPath);
        if (path != plan.RootPath || CurrentSid() != plan.OwnerSid || RootCommitment(path, plan.OwnerSid, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId) != plan.DestinationRootCommitmentSha256)
            throw Failure("destination-binding-mismatch");
    }
    internal static void ValidateExecutionTime(CredentialRecoveryStagePlan plan, DateTimeOffset now)
    {
        if (now < plan.PreparedUtc || now >= plan.ExpiresUtc) throw Failure("plan-not-current");
        CredentialRecoverySets.ValidateLifetime(plan.SourceSignature, now);
    }
    internal static void ValidateCompletedReceiptTime(CredentialRecoveryStagePlan plan, CredentialRecoveryStageReceipt receipt, DateTimeOffset observedNow)
    {
        if (!CredentialRecoverySets.Utc(receipt.ReservedUtc) || !CredentialRecoverySets.Utc(receipt.CompletedUtc) ||
            receipt.ReservedUtc < plan.PreparedUtc || receipt.CompletedUtc < receipt.ReservedUtc || receipt.CompletedUtc >= plan.ExpiresUtc ||
            receipt.CompletedUtc > observedNow) throw Failure("receipt-time-invalid");
        CredentialRecoverySets.ValidateLifetime(plan.SourceSignature, receipt.ReservedUtc);
        CredentialRecoverySets.ValidateLifetime(plan.SourceSignature, receipt.CompletedUtc);
    }

    internal static async Task<CredentialEncryptionKey> ResolvePinnedDestinationKeyAsync(CredentialRecoveryDestinationKeyIdentity identity,
        ICredentialKeyProvider keys, CancellationToken cancellationToken)
    {
        RequireKeyIdentity(identity); CredentialEncryptionKey? key = null; byte[]? bytes = null;
        try
        {
            key = await ExistingKey(identity.CustomerId, identity.CellId, identity.KeyId, keys, cancellationToken); bytes = key.CopyBytes();
            var observed = KeyCommitment(identity.CustomerId, identity.CellId, identity.KeyId, bytes);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(observed), Convert.FromHexString(identity.CommitmentSha256))) throw Failure("destination-key-changed");
            cancellationToken.ThrowIfCancellationRequested(); return new CredentialEncryptionKey(bytes);
        }
        finally { key?.Dispose(); if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    static async Task<CredentialEncryptionKey> ExistingKey(string customerId, string cellId, string keyId, ICredentialKeyProvider keys, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return await keys.GetKeyAsync(customerId, cellId, keyId, cancellationToken) ?? throw Failure("destination-key-unavailable"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw Failure("destination-key-unavailable"); }
    }
    static string KeyCommitment(string customer, string cell, string keyId, byte[] key)
    {
        if (key.Length != 32) throw Failure("destination-key-invalid");
        var context = Encoding.UTF8.GetBytes("platform-studio.credential-recovery-stage-key.v1\0" + customer + "\0" + cell + "\0" + keyId + "\0");
        var payload = new byte[context.Length + key.Length]; context.CopyTo(payload, 0); key.CopyTo(payload, context.Length);
        try { return Hash(payload); } finally { CryptographicOperations.ZeroMemory(payload); }
    }
    static void RequireKeyIdentity(CredentialRecoveryDestinationKeyIdentity identity)
    {
        if (identity is null || !Guid.TryParseExact(identity.CustomerId, "D", out var customer) || customer == Guid.Empty ||
            !Guid.TryParseExact(identity.CellId, "D", out var cell) || cell == Guid.Empty || !CredentialRules.ProfileId(identity.KeyId) || !Digest(identity.CommitmentSha256))
            throw Failure("destination-key-invalid");
        try { _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { identity.KeyId }, CredentialRecoverySets.Json)); }
        catch (FoundationStoreException) { throw Failure("destination-key-invalid"); }
    }

    internal static string PlanDigest(CredentialRecoveryStagePlan p) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { p.Schema, p.PlanId, p.SourceSetAuthority, p.SourceSignature, p.SourceNamespace, p.DestinationRootCommitmentSha256, p.DestinationKey,
        p.PurposeSha256, preparedUnixMilliseconds = p.PreparedUtc.ToUnixTimeMilliseconds(), expiresUnixMilliseconds = p.ExpiresUtc.ToUnixTimeMilliseconds(),
        p.PreservedProfiles, p.AbsentProfileIds, sourceInventorySha256 = p.SourceInventory.Sha256 }, CredentialRecoverySets.Json));
    internal static string ReceiptDigest(CredentialRecoveryStageReceipt r) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { r.Schema, r.PlanSha256, r.SourceSetSha256, r.StageId, r.StagedVaultNamespaceId, r.StagedAuthorityNamespaceId, r.DestinationRootCommitmentSha256,
        r.DestinationKey, r.BindingSha256, r.StateSha256, reservedUnixMilliseconds = r.ReservedUtc.ToUnixTimeMilliseconds(), completedUnixMilliseconds = r.CompletedUtc.ToUnixTimeMilliseconds(),
        r.Profiles, r.AbsentProfileIds, r.InactiveRecoveryHold, r.NormalVaultUsable, r.ActivationPerformed, r.SourceHostFenced, r.ExternalExecutionAuthorized,
        r.KeyMaterialExported, r.KeyRetirementAuthorized, r.IndependentBackupCoverage, r.ProductionRecoveryQualified }, CredentialRecoverySets.Json));
    static string RootCommitment(string path, string sid, string customer, string cell) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { schema = 1, kind = "credential-recovery-stage-root", customerId = customer, cellId = cell, rootPath = path, ownerSid = sid }, CredentialRecoverySets.Json));
    internal static string Hash(ReadOnlySpan<byte> bytes) => CredentialRecoverySets.Hash(bytes);
    static bool Digest(string? value) => CredentialRecoverySets.DigestShape(value);
    internal static DateTimeOffset Milliseconds(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
    internal static CredentialException Failure(string suffix) => new("credential-recovery-stage-" + suffix);
    internal static bool LocalFailure(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        NotSupportedException or System.Security.SecurityException or JsonException or FormatException or OverflowException;

    static string CurrentSid()
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        using var current = WindowsIdentity.GetCurrent(); return current.User?.Value ?? throw Failure("owner-unavailable");
    }
    static string LocalPath(string value)
    {
        try { return CredentialNamespaceCustody.LocalPath(value); }
        catch (CredentialException) { throw Failure("path-invalid"); }
    }
    internal static string PreflightDestination(string value, string customer, string cell)
    {
        var path = LocalPath(value);
        if (ProtectedName(Path.GetFileName(path)) || new[] { "-wal", "-shm", "-journal" }.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase))) throw Failure("protected-namespace");
        _ = CredentialNamespaceCustody.PreflightNew(path, "credential-vault", customer, cell); CheckParent(path); return path;
    }
    static bool ProtectedName(string name) => new[] { GuardName, MarkerName, ".activation.guard", "authority.json", ".backup-checkpoint.guard", ".installation-owner.json",
        ".installation.guard", ".package-owner.json", ".credential-namespace.json", ".vault.lock", ".keys.lock", ".rollback.lock", "cell-binding.json", ".foundation-owner-initialization.json",
        ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json" }
        .Contains(name, StringComparer.OrdinalIgnoreCase) || name.EndsWith(".credential.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dpapi.json", StringComparison.OrdinalIgnoreCase);
    static void CheckParent(string path)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        var parent = Path.GetDirectoryName(path); if (parent is null || !Directory.Exists(parent)) throw Failure("parent-missing");
        var security = new DirectoryInfo(parent).GetAccessControl(); var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var trusted = new HashSet<string>(StringComparer.Ordinal) { CurrentSid(), "S-1-5-18", "S-1-5-32-544" };
        if (descriptor.DiscretionaryAcl is null || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
            security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value)) throw Failure("parent-custody-denied");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 && (rule.FileSystemRights & writes) != 0 &&
                !trusted.Contains(rule.IdentityReference.Value)) throw Failure("parent-custody-denied");
    }
}
