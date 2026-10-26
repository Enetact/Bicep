using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

public static partial class CredentialRestrictedRecoveryStaging
{
    public const int MaximumProfiles = 256, MaximumEntries = 265, MaximumMetadataBytes = 4 * 1024 * 1024;
    public const long MaximumStageBytes = 40L * 1024 * 1024;
    internal const string GuardName = ".credential-recovery-stage.guard", MarkerName = ".credential-recovery-stage.json";
    internal const string Profile = "FoundationBoundDispatchOnly";
    public static async Task<CredentialRestrictedRecoveryDestinationKeyIdentity> ObserveDestinationKeyAsync(string customerId, string cellId, string keyId,
        ICredentialKeyProvider keys, CancellationToken cancellationToken = default)
    {
        RequireKey(new(customerId, cellId, keyId, new string('a', 64)));
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken);
        try
        {
            using var key = await ExistingKey(customerId, cellId, keyId, keys, deadline.Token); var bytes = key.CopyBytes();
            try { deadline.Token.ThrowIfCancellationRequested(); return new(customerId, cellId, keyId, KeyCommitment(customerId, cellId, keyId, bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Restricted destination observation canceled.", cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw Failure("timeout"); }
        catch (OperationCanceledException) { throw Failure("destination-key-unavailable"); }
    }

    public static CredentialRestrictedRecoveryStagePlan PreparePlan(CredentialRestrictedRecoverySetVerification selectedSet, CredentialRestrictedRecoverySetAuthority exactAuthority,
        CredentialVaultNamespaceIdentity expectedSourceNamespace, string freshStageDirectory, CredentialRestrictedRecoveryDestinationKeyIdentity destinationKey,
        string purpose, DateTimeOffset expiresUtc, TimeProvider? timeProvider = null)
    {
        try
        {
            CredentialRecoverySets.RequireIdentity(expectedSourceNamespace); RequireKey(destinationKey);
            if (selectedSet is null || exactAuthority is null || selectedSet.Signature.SourceNamespace != expectedSourceNamespace ||
                exactAuthority != new CredentialRestrictedRecoverySetAuthority(selectedSet.Signature.CustomerId, selectedSet.Signature.CellId, selectedSet.Signature.SetId,
                    selectedSet.Signature.Generation, selectedSet.Sha256, selectedSet.Signature.KeyId, selectedSet.Signature.PublicKeySha256, Profile)) throw Failure("source-selection-mismatch");
            var now = At((timeProvider ?? TimeProvider.System).GetUtcNow()); CredentialRestrictedRecoverySets.ValidateLifetime(selectedSet.Signature, now);
            if (!Utc(expiresUtc) || expiresUtc <= now || expiresUtc > now.AddHours(1) || expiresUtc > selectedSet.Signature.ExpiresUtc) throw Failure("plan-expiry-invalid");
            if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 1024) throw Failure("purpose-invalid");
            _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { purpose }, CredentialRules.Json));
            var path = CredentialRecoveryStaging.PreflightDestination(freshStageDirectory, expectedSourceNamespace.CustomerId, expectedSourceNamespace.CellId); var sid = CurrentSid();
            var plan = new CredentialRestrictedRecoveryStagePlan(path, sid, Guid.NewGuid().ToString("N"), exactAuthority, selectedSet.Signature, expectedSourceNamespace,
                RootCommitment(path, sid, expectedSourceNamespace), destinationKey, Hash(Encoding.UTF8.GetBytes(purpose)), now, expiresUtc,
                selectedSet.PreservedProfileHashes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new CredentialRecoveryPreservedProfile(p.Key, p.Value)), selectedSet.Inventory);
            ValidatePlan(plan, plan.Sha256); return plan;
        }
        catch (CredentialException) { throw; }
        catch (Exception) { throw Failure("plan-unavailable"); }
    }
    internal static void ValidatePlan(CredentialRestrictedRecoveryStagePlan p, string reviewedSha)
    {
        if (p is null || !Digest(reviewedSha) || p.Sha256 != reviewedSha || p.Sha256 != PlanDigest(p)) throw Failure("plan-mismatch");
        CredentialRestrictedRecoverySets.RequireSignature(p.SourceSignature); CredentialRecoverySets.RequireIdentity(p.SourceNamespace); RequireKey(p.DestinationKey); p.SourceInventory.ValidateIntegrity();
        var scopes = p.SourceInventory.ProfileDependencies.Select(x => new FoundationCredentialRecoveryScope(x.ProjectId, x.EnvironmentId))
            .Concat(p.SourceInventory.AbsenceScopes.Select(x => new FoundationCredentialRecoveryScope(x.ProjectId, x.EnvironmentId))).Distinct()
            .OrderBy(x => x.ProjectId, StringComparer.Ordinal).ThenBy(x => x.EnvironmentId, StringComparer.Ordinal);
        if (!Id(p.PlanId) || p.SourceNamespace != p.SourceSignature.SourceNamespace || p.SourceInventory.Namespace != p.SourceNamespace ||
            p.SourceSetAuthority != new CredentialRestrictedRecoverySetAuthority(p.SourceSignature.CustomerId, p.SourceSignature.CellId, p.SourceSignature.SetId,
                p.SourceSignature.Generation, p.SourceSetAuthority.ArtifactSha256, p.SourceSignature.KeyId, p.SourceSignature.PublicKeySha256, Profile) ||
            !Digest(p.SourceSetAuthority.ArtifactSha256) || p.SourceInventory.Sha256 != p.SourceSignature.InventorySha256 ||
            p.DestinationKey.CustomerId != p.SourceNamespace.CustomerId || p.DestinationKey.CellId != p.SourceNamespace.CellId || !Digest(p.PurposeSha256) ||
            !Utc(p.PreparedUtc) || !Utc(p.ExpiresUtc) || p.ExpiresUtc <= p.PreparedUtc || p.ExpiresUtc > p.PreparedUtc.AddHours(1) || p.ExpiresUtc > p.SourceSignature.ExpiresUtc ||
            p.PreservedProfiles.Count != p.SourceInventory.ProfileDependencies.Count || p.PreservedProfiles.Count != p.SourceSignature.ProfileCount ||
            p.AbsenceScopes.Count != p.SourceSignature.AbsenceProofCount || p.PreservedProfiles.Count + p.AbsenceScopes.Count > MaximumProfiles ||
            !p.PreservedProfiles.Select(x => x.ProfileIdSha256).SequenceEqual(p.SourceInventory.ProfileDependencies.Select(x => x.ProfileIdSha256), StringComparer.Ordinal) ||
            p.PreservedProfiles.Any(x => !Digest(x.ProfileIdSha256) || !Digest(x.ProfileSha256)) || !p.AbsenceScopes.SequenceEqual(p.SourceInventory.AbsenceScopes) ||
            !p.AffectedScopes.SequenceEqual(scopes)) throw Failure("plan-invalid");
        var path = CredentialNamespaceCustody.LocalPath(p.RootPath);
        if (path != p.RootPath || CurrentSid() != p.OwnerSid || RootCommitment(path, p.OwnerSid, p.SourceNamespace) != p.DestinationRootCommitmentSha256) throw Failure("destination-binding-mismatch");
    }
    internal static void ValidateExecutionTime(CredentialRestrictedRecoveryStagePlan p, DateTimeOffset now)
    { if (now < p.PreparedUtc || now >= p.ExpiresUtc) throw Failure("plan-not-current"); CredentialRestrictedRecoverySets.ValidateLifetime(p.SourceSignature, now); }
    internal static void ValidateCompletedReceiptTime(CredentialRestrictedRecoveryStagePlan p, CredentialRestrictedRecoveryStageReceipt r, DateTimeOffset now)
    {
        if (!Utc(r.ReservedUtc) || !Utc(r.CompletedUtc) || r.ReservedUtc < p.PreparedUtc || r.CompletedUtc < r.ReservedUtc || r.CompletedUtc >= p.ExpiresUtc || r.CompletedUtc > now)
            throw Failure("receipt-time-invalid");
        CredentialRestrictedRecoverySets.ValidateLifetime(p.SourceSignature, r.ReservedUtc); CredentialRestrictedRecoverySets.ValidateLifetime(p.SourceSignature, r.CompletedUtc);
    }
    internal static bool PreservedDependency(CredentialRecoveryProfileDependency s, CredentialRecoveryProfileDependency d) => CredentialRecoveryStaging.PreservedDependency(s, d);
    internal static bool PreservedAbsenceDependency(CredentialAbsenceScopeDependency s, CredentialAbsenceScopeDependency d) =>
        s.ProfileIdSha256 == d.ProfileIdSha256 && s.ScopeSha256 == d.ScopeSha256 && s.ProjectId == d.ProjectId && s.EnvironmentId == d.EnvironmentId;
    internal static string PlanDigest(CredentialRestrictedRecoveryStagePlan p) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { p.Schema, p.AccessProfile, p.PlanId, p.SourceSetAuthority, p.SourceSignature, p.SourceNamespace, p.DestinationRootCommitmentSha256, p.DestinationKey, p.PurposeSha256,
        preparedUnixMilliseconds = p.PreparedUtc.ToUnixTimeMilliseconds(), expiresUnixMilliseconds = p.ExpiresUtc.ToUnixTimeMilliseconds(),
        p.PreservedProfiles, p.AbsenceScopes, p.AffectedScopes, sourceInventorySha256 = p.SourceInventory.Sha256 }, CredentialRules.Json));
    internal static string ReceiptDigest(CredentialRestrictedRecoveryStageReceipt r) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { r.Schema, r.AccessProfile, r.PlanSha256, r.SourceSetSha256, r.StageId, r.StagedVaultNamespaceId, r.StagedAuthorityNamespaceId, r.DestinationRootCommitmentSha256,
        r.DestinationKey, r.BindingSha256, r.StateSha256, r.CompletionCoreSha256, r.CompletionProofSha256, reservedUnixMilliseconds = r.ReservedUtc.ToUnixTimeMilliseconds(),
        completedUnixMilliseconds = r.CompletedUtc.ToUnixTimeMilliseconds(), r.Profiles, r.Absences, r.InactiveRecoveryHold, r.NormalVaultUsable, r.PromotionAuthorized,
        r.ActivationPerformed, r.SourceHostFenced, r.ExternalExecutionAuthorized, r.KeyMaterialExported, r.KeyRetirementAuthorized, r.IndependentBackupCoverage, r.ProductionRecoveryQualified }, CredentialRules.Json));
    internal static string CompletionCoreDigest(CredentialRestrictedRecoveryStagePlan p, string stageId, string vaultId, string authorityId, string bindingSha,
        DateTimeOffset reserved, DateTimeOffset completed, IEnumerable<CredentialRecoveryStagedProfile> profiles) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "platform-studio.credential-restricted-stage-completion-core.v1", p.AccessProfile, planSha256 = p.Sha256, sourceSetSha256 = p.SourceSetAuthority.ArtifactSha256,
        p.SourceNamespace, p.DestinationRootCommitmentSha256, p.DestinationKey, stageId, stagedVaultNamespaceId = vaultId, stagedAuthorityNamespaceId = authorityId,
        bindingSha256 = bindingSha, reservedUnixMilliseconds = reserved.ToUnixTimeMilliseconds(), completedUnixMilliseconds = completed.ToUnixTimeMilliseconds(),
        profiles = profiles.ToArray(), absences = p.AbsenceScopes.Select(x => new { source = x, generation = 0, envelopeSha256 = "", envelopeLength = 0 }).ToArray(), inactiveRecoveryHold = true
    }, CredentialRules.Json));
    static string RootCommitment(string path, string sid, CredentialVaultNamespaceIdentity ns) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { domain = "platform-studio.credential-restricted-recovery-stage-root.v1", accessProfile = Profile, ns.CustomerId, ns.CellId, rootPath = path, ownerSid = sid }, CredentialRules.Json));
    internal static async Task<CredentialEncryptionKey> ResolvePinnedDestinationKeyAsync(CredentialRestrictedRecoveryDestinationKeyIdentity identity,
        ICredentialKeyProvider keys, CancellationToken token)
    {
        RequireKey(identity); CredentialEncryptionKey? key = null; byte[]? bytes = null;
        try
        {
            key = await ExistingKey(identity.CustomerId, identity.CellId, identity.KeyId, keys, token); bytes = key.CopyBytes();
            var observed = KeyCommitment(identity.CustomerId, identity.CellId, identity.KeyId, bytes);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(observed), Convert.FromHexString(identity.CommitmentSha256))) throw Failure("destination-key-changed");
            token.ThrowIfCancellationRequested(); return new CredentialEncryptionKey(bytes);
        }
        finally { key?.Dispose(); if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    static async Task<CredentialEncryptionKey> ExistingKey(string customer, string cell, string keyId, ICredentialKeyProvider keys, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await keys.GetKeyAsync(customer, cell, keyId, token) ?? throw Failure("destination-key-unavailable"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw Failure("destination-key-unavailable"); }
    }
    static string KeyCommitment(string customer, string cell, string keyId, byte[] key)
    {
        if (key.Length != 32) throw Failure("destination-key-invalid");
        var context = Encoding.UTF8.GetBytes("platform-studio.credential-restricted-stage-key.v1\0" + Profile + "\0" + customer + "\0" + cell + "\0" + keyId + "\0");
        var payload = new byte[context.Length + key.Length]; context.CopyTo(payload, 0); key.CopyTo(payload, context.Length);
        try { return Hash(payload); } finally { CryptographicOperations.ZeroMemory(payload); }
    }
    internal static string Hash(ReadOnlySpan<byte> bytes) => CredentialRecoverySets.Hash(bytes);
    internal static bool Digest(string? value) => CredentialRecoverySets.DigestShape(value);
    internal static bool Id(string? value) => CredentialRecoverySets.NamespaceShape(value);
    internal static bool Utc(DateTimeOffset value) => CredentialRecoverySets.Utc(value);
    internal static DateTimeOffset At(DateTimeOffset value) => CredentialRecoveryStaging.Milliseconds(value);
    static void RequireKey(CredentialRestrictedRecoveryDestinationKeyIdentity key)
    {
        if (key is null || !Guid.TryParseExact(key.CustomerId, "D", out var c) || c == Guid.Empty || !Guid.TryParseExact(key.CellId, "D", out var e) || e == Guid.Empty ||
            !CredentialRules.ProfileId(key.KeyId) || !Digest(key.CommitmentSha256)) throw Failure("destination-key-invalid");
    }
    internal static string CurrentSid()
    { if (!OperatingSystem.IsWindows()) throw Failure("windows-required"); using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw Failure("owner-unavailable"); }
    internal static void CheckParent(string root)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required"); var parent = Path.GetDirectoryName(root);
        if (parent is null || !Directory.Exists(parent)) throw Failure("parent-missing");
        var security = new DirectoryInfo(parent).GetAccessControl(); var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var trusted = new HashSet<string>(StringComparer.Ordinal) { CurrentSid(), "S-1-5-18", "S-1-5-32-544" };
        if (raw.DiscretionaryAcl is null || (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
            security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value)) throw Failure("parent-custody-denied");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value)) throw Failure("parent-custody-denied");
    }
    internal static CredentialException Failure(string code) => new("credential-restricted-recovery-stage-" + code);
}
