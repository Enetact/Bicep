using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public static partial class CredentialRecoveryStaging
{
    internal sealed record StageReservation(int Version, string Kind, string StageId, string PlanSha256, string SourceSetSha256,
        string DestinationRootCommitmentSha256, CredentialRecoveryDestinationKeyIdentity DestinationKey, string StagedVaultNamespaceId,
        string StagedAuthorityNamespaceId, DateTimeOffset ReservedUtc, bool InactiveRecoveryHold, string Sha256);
    internal sealed record StageBinding(int Version, string Kind, string StageId, string PlanSha256, string SourceSetSha256,
        CredentialVaultNamespaceIdentity SourceNamespace, string StagedVaultNamespaceId, string StagedAuthorityNamespaceId,
        string DestinationRootCommitmentSha256, CredentialRecoveryDestinationKeyIdentity DestinationKey, bool InactiveRecoveryHold, string Sha256);
    internal sealed record StageEntry(string ProfileIdSha256, long Generation, string EnvelopeSha256, long EnvelopeLength);
    internal sealed record StageState(int Version, string Kind, string StageId, string PlanSha256, string StagedVaultNamespaceId,
        string StagedAuthorityNamespaceId, long Revision, StageEntry[] Entries, bool InactiveRecoveryHold, string Sha256);
    sealed record StagedProfileDocument(CredentialRecoverySets.ProfileDocument Source, CredentialRecoverySets.ProfileDocument Destination, string PreservedProfileSha256);
    sealed record ReceiptDocument(string Schema, string PlanSha256, string SourceSetSha256, string StageId, string StagedVaultNamespaceId,
        string StagedAuthorityNamespaceId, string DestinationRootCommitmentSha256, CredentialRecoveryDestinationKeyIdentity DestinationKey,
        string BindingSha256, string StateSha256, DateTimeOffset ReservedUtc, DateTimeOffset CompletedUtc, StagedProfileDocument[] Profiles,
        string[] AbsentProfileIds, bool InactiveRecoveryHold, bool NormalVaultUsable, bool ActivationPerformed, bool SourceHostFenced,
        bool ExternalExecutionAuthorized, bool KeyMaterialExported, bool KeyRetirementAuthorized, string IndependentBackupCoverage,
        bool ProductionRecoveryQualified, string Sha256);
    sealed class StageSnapshot : IDisposable
    {
        internal SortedDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        internal List<string> Fingerprints { get; } = [];
        public void Dispose() { foreach (var bytes in Files.Values) CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static CredentialRecoveryStageReadLease OpenCompletedReadLease(CredentialRecoveryStagePlan plan, string reviewedPlanSha256,
        string expectedReceiptSha256)
    {
        FileStream? guard = null; StageSnapshot? snapshot = null;
        try
        {
            ValidatePlan(plan, reviewedPlanSha256); if (!Digest(expectedReceiptSha256)) throw Failure("receipt-selection-invalid");
            CheckParent(plan.RootPath); CheckPrivate(plan.RootPath, true); var guardPath = Path.Combine(plan.RootPath, GuardName); CheckPrivate(guardPath, false);
            guard = new FileStream(guardPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (guard.Length != 0) throw Failure("guard-invalid");
            snapshot = ReadStage(plan); var receipt = ValidateCompleted(plan, snapshot, expectedReceiptSha256);
            var fingerprints = snapshot.Fingerprints.ToArray();
            var envelopes = receipt.Profiles.Select(p => new RetainedCredentialRecoveryEnvelope(p.Destination.ProfileIdSha256,
                snapshot.Files["profiles/" + p.Destination.ProfileIdSha256 + ".credential.json"])).ToArray();
            var owned = snapshot.Files.Values.ToArray();
            var lease = new CredentialRecoveryStageReadLease(guard, receipt, envelopes, owned, () =>
            {
                try
                {
                    ValidatePlan(plan, reviewedPlanSha256); CheckParent(plan.RootPath); using var current = ReadStage(plan);
                    _ = ValidateCompleted(plan, current, expectedReceiptSha256);
                    if (!current.Fingerprints.SequenceEqual(fingerprints, StringComparer.Ordinal)) throw Failure("stage-changed");
                }
                catch (CredentialException) { throw; }
                catch (Exception error) when (LocalFailure(error)) { throw Failure("stage-unavailable"); }
            });
            guard = null; snapshot = null; return lease;
        }
        catch (CredentialException) { throw; }
        catch (Exception error) when (LocalFailure(error)) { throw Failure("stage-unavailable"); }
        finally { snapshot?.Dispose(); guard?.Dispose(); }
    }

    internal static void ValidateWrittenStage(CredentialRecoveryStagePlan plan, CredentialRecoveryStageReceipt receipt)
    { using var snapshot = ReadStage(plan); _ = ValidateCompleted(plan, snapshot, receipt.Sha256); }
    internal static byte[] ReadWrittenEnvelope(string root, string profileHash)
    {
        if (!Digest(profileHash)) throw Failure("stage-envelope-mismatch");
        var path = Path.Combine(root, "profiles", profileHash + ".credential.json"); CheckPrivate(path, false);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 131072) throw Failure("capacity-exceeded");
        var bytes = new byte[checked((int)input.Length)];
        try { input.ReadExactly(bytes); CheckPrivate(path, false); if (input.ReadByte() != -1) throw Failure("stage-changed"); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    static StageSnapshot ReadStage(CredentialRecoveryStagePlan plan)
    {
        var snapshot = new StageSnapshot(); var entries = 0; long total = 0;
        try
        {
            Walk(plan.RootPath, ""); snapshot.Fingerprints.Sort(StringComparer.Ordinal); return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
        void Walk(string path, string relative)
        {
            if (++entries > MaximumEntries) throw Failure("capacity-exceeded");
            var directory = Directory.Exists(path); CheckPrivate(path, directory);
            if (directory)
            {
                if (relative is not ("" or "profiles" or "rollback")) throw Failure("stage-contents-invalid");
                snapshot.Fingerprints.Add(relative + ":directory:" + SecurityHash(path, true));
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                    Walk(child, relative == "" ? Path.GetFileName(child) : relative + "/" + Path.GetFileName(child));
                return;
            }
            var maximum = relative == GuardName ? 0 : Regex.IsMatch(relative, "\\Aprofiles/[a-f0-9]{64}\\.credential\\.json\\z", RegexOptions.CultureInvariant) ? 131072 :
                relative is MarkerName or "rollback/binding.json" or "rollback/state.json" or "completion.json" ? MaximumMetadataBytes : throw Failure("stage-contents-invalid");
            byte[] bytes;
            if (relative == GuardName) { if (new FileInfo(path).Length != 0) throw Failure("guard-invalid"); bytes = []; }
            else
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); CheckPrivate(path, false);
                if (input.Length is < 1 || input.Length > maximum) throw Failure("capacity-exceeded");
                bytes = new byte[checked((int)input.Length)];
                try { input.ReadExactly(bytes); if (input.ReadByte() != -1) throw Failure("stage-changed"); }
                catch { CryptographicOperations.ZeroMemory(bytes); throw; }
            }
            snapshot.Files.Add(relative, bytes); total += bytes.Length;
            if (total > MaximumStageBytes) throw Failure("capacity-exceeded");
            snapshot.Fingerprints.Add(relative + ":file:" + bytes.Length + ":" + Hash(bytes) + ":" + SecurityHash(path, false));
        }
    }

    static CredentialRecoveryStageReceipt ValidateCompleted(CredentialRecoveryStagePlan plan, StageSnapshot snapshot, string expectedReceiptSha)
    {
        var expectedNames = new SortedSet<string>(StringComparer.Ordinal) { GuardName, MarkerName, "rollback/binding.json", "rollback/state.json", "completion.json" };
        foreach (var profile in plan.PreservedProfiles) expectedNames.Add("profiles/" + profile.ProfileIdSha256 + ".credential.json");
        if (!snapshot.Files.Keys.SequenceEqual(expectedNames, StringComparer.Ordinal)) throw Failure("stage-contents-invalid");
        // Empty child directories are also required, including for an empty profile set.
        if (!snapshot.Fingerprints.Any(s => s.StartsWith("profiles:directory:", StringComparison.Ordinal)) ||
            !snapshot.Fingerprints.Any(s => s.StartsWith("rollback:directory:", StringComparison.Ordinal))) throw Failure("stage-contents-invalid");
        var reservation = Parse<StageReservation>(snapshot.Files[MarkerName]); var binding = Parse<StageBinding>(snapshot.Files["rollback/binding.json"]);
        var state = ParseState(snapshot.Files["rollback/state.json"]); var receipt = ParseReceipt(snapshot.Files["completion.json"]);
        if (reservation.Version != 1 || reservation.Kind != "credential-recovery-stage-reservation" || !reservation.InactiveRecoveryHold ||
            reservation.Sha256 != ReservationDigest(reservation) || reservation.PlanSha256 != plan.Sha256 || reservation.SourceSetSha256 != plan.SourceSetAuthority.ArtifactSha256 ||
            reservation.DestinationRootCommitmentSha256 != plan.DestinationRootCommitmentSha256 || reservation.DestinationKey != plan.DestinationKey ||
            !CredentialRecoverySets.NamespaceShape(reservation.StageId) || !CredentialRecoverySets.NamespaceShape(reservation.StagedVaultNamespaceId) ||
            !CredentialRecoverySets.NamespaceShape(reservation.StagedAuthorityNamespaceId) ||
            new[] { reservation.StageId, reservation.StagedVaultNamespaceId, reservation.StagedAuthorityNamespaceId }.Distinct(StringComparer.Ordinal).Count() != 3 ||
            new[] { reservation.StageId, reservation.StagedVaultNamespaceId, reservation.StagedAuthorityNamespaceId }
                .Any(id => id == plan.SourceNamespace.VaultNamespaceId || id == plan.SourceNamespace.AuthorityNamespaceId))
            throw Failure("stage-reservation-invalid");
        if (binding.Version != 1 || binding.Kind != "credential-recovery-stage-binding" || !binding.InactiveRecoveryHold || binding.StageId != reservation.StageId ||
            binding.PlanSha256 != plan.Sha256 || binding.SourceSetSha256 != reservation.SourceSetSha256 || binding.SourceNamespace != plan.SourceNamespace ||
            binding.StagedVaultNamespaceId != reservation.StagedVaultNamespaceId || binding.StagedAuthorityNamespaceId != reservation.StagedAuthorityNamespaceId ||
            binding.DestinationRootCommitmentSha256 != plan.DestinationRootCommitmentSha256 || binding.DestinationKey != plan.DestinationKey || binding.Sha256 != BindingDigest(binding))
            throw Failure("stage-binding-invalid");
        if (state.Version != 1 || state.Kind != "credential-recovery-stage-state" || !state.InactiveRecoveryHold || state.Revision != 0 ||
            state.StageId != reservation.StageId || state.PlanSha256 != plan.Sha256 || state.StagedVaultNamespaceId != reservation.StagedVaultNamespaceId ||
            state.StagedAuthorityNamespaceId != reservation.StagedAuthorityNamespaceId || state.Sha256 != StateDigest(state)) throw Failure("stage-state-invalid");
        if (receipt.Sha256 != expectedReceiptSha || receipt.PlanSha256 != plan.Sha256 || receipt.SourceSetSha256 != reservation.SourceSetSha256 ||
            receipt.StageId != reservation.StageId || receipt.StagedVaultNamespaceId != reservation.StagedVaultNamespaceId ||
            receipt.StagedAuthorityNamespaceId != reservation.StagedAuthorityNamespaceId || receipt.DestinationRootCommitmentSha256 != plan.DestinationRootCommitmentSha256 ||
            receipt.DestinationKey != plan.DestinationKey || receipt.BindingSha256 != binding.Sha256 || receipt.StateSha256 != state.Sha256 ||
            receipt.ReservedUtc != reservation.ReservedUtc || receipt.Profiles.Count != plan.PreservedProfiles.Count ||
            !receipt.AbsentProfileIds.SequenceEqual(plan.AbsentProfileIds, StringComparer.Ordinal) ||
            !receipt.Profiles.Select(p => p.Source.ProfileIdSha256).SequenceEqual(plan.PreservedProfiles.Select(p => p.ProfileIdSha256), StringComparer.Ordinal)) throw Failure("receipt-mismatch");
        var expectedEntries = new List<StageEntry>(MaximumProfiles);
        for (var i = 0; i < receipt.Profiles.Count; i++)
        {
            var profile = receipt.Profiles[i]; var original = plan.SourceInventory.ProfileDependencies[i]; var preserved = plan.PreservedProfiles[i];
            CredentialRecoveryInventory.ValidateProfile(profile.Source); CredentialRecoveryInventory.ValidateProfile(profile.Destination);
            if (JsonSerializer.Serialize(profile.Source, CredentialRules.Json) != JsonSerializer.Serialize(original, CredentialRules.Json) ||
                profile.PreservedProfileSha256 != preserved.ProfileSha256 || profile.Destination.SourceKeyId != plan.DestinationKey.KeyId || !PreservedDependency(profile.Source, profile.Destination))
                throw Failure("preserved-profile-mismatch");
            var name = "profiles/" + profile.Destination.ProfileIdSha256 + ".credential.json"; var bytes = snapshot.Files[name];
            if (bytes.Length != profile.Destination.EnvelopeLength || Hash(bytes) != profile.Destination.EnvelopeSha256 || CredentialVault.RecoveryProfileSha256(bytes) != preserved.ProfileSha256)
                throw Failure("stage-envelope-mismatch");
            expectedEntries.Add(new(profile.Destination.ProfileIdSha256, profile.Destination.Generation, profile.Destination.EnvelopeSha256, profile.Destination.EnvelopeLength));
        }
        expectedEntries.AddRange(plan.AbsentProfileIds.Select(p => new StageEntry(p, 0, "", 0)));
        if (!state.Entries.SequenceEqual(expectedEntries.OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal))) throw Failure("stage-state-mismatch");
        return receipt;
    }
    internal static bool PreservedDependency(CredentialRecoveryProfileDependency source, CredentialRecoveryProfileDependency destination) =>
        source.ProfileIdSha256 == destination.ProfileIdSha256 && source.ScopeSha256 == destination.ScopeSha256 && source.ProjectId == destination.ProjectId &&
        source.EnvironmentId == destination.EnvironmentId && source.Generation == destination.Generation && source.Enabled == destination.Enabled && source.ExpiresUnixMilliseconds == destination.ExpiresUnixMilliseconds;

    static T Parse<T>(byte[] bytes)
    { using var document = CredentialRules.Document(bytes); return document.RootElement.Deserialize<T>(CredentialRecoverySets.Json) ?? throw Failure("metadata-invalid"); }
    static StageState ParseState(byte[] bytes)
    {
        using var doc = CredentialRules.Document(bytes);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) throw Failure("metadata-invalid");
        if (entries.GetArrayLength() > MaximumProfiles) throw Failure("capacity-exceeded");
        var state = doc.RootElement.Deserialize<StageState>(CredentialRecoverySets.Json) ?? throw Failure("metadata-invalid");
        if (state.Entries is null || state.Entries.Any(e => e is null)) throw Failure("metadata-invalid"); return state;
    }
    static CredentialRecoveryStageReceipt ParseReceipt(byte[] bytes)
    {
        using var doc = CredentialRules.Document(bytes);
        if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array ||
            !doc.RootElement.TryGetProperty("absentProfileIds", out var absent) || absent.ValueKind != JsonValueKind.Array) throw Failure("metadata-invalid");
        if (profiles.GetArrayLength() + absent.GetArrayLength() > MaximumProfiles) throw Failure("capacity-exceeded");
        var r = doc.RootElement.Deserialize<ReceiptDocument>(CredentialRecoverySets.Json) ?? throw Failure("metadata-invalid");
        if (r.Schema != "platform-studio.credential-recovery-stage-receipt.v1" || r.Profiles is null || r.Profiles.Any(p => p is null || p.Source is null || p.Destination is null) ||
            r.AbsentProfileIds is null || !r.InactiveRecoveryHold || r.NormalVaultUsable || r.ActivationPerformed || r.SourceHostFenced || r.ExternalExecutionAuthorized ||
            r.KeyMaterialExported || r.KeyRetirementAuthorized || r.IndependentBackupCoverage != "NotAssessed" || r.ProductionRecoveryQualified) throw Failure("receipt-invalid");
        var receipt = new CredentialRecoveryStageReceipt(r.PlanSha256, r.SourceSetSha256, r.StageId, r.StagedVaultNamespaceId, r.StagedAuthorityNamespaceId,
            r.DestinationRootCommitmentSha256, r.DestinationKey, r.BindingSha256, r.StateSha256, r.ReservedUtc, r.CompletedUtc,
            r.Profiles.Select(p => new CredentialRecoveryStagedProfile(Dependency(p.Source), Dependency(p.Destination), p.PreservedProfileSha256)), r.AbsentProfileIds);
        if (r.Sha256 != receipt.Sha256) throw Failure("receipt-invalid"); return receipt;
    }
    static CredentialRecoveryProfileDependency Dependency(CredentialRecoverySets.ProfileDocument p) => new(p.ProfileIdSha256, p.ScopeSha256, p.ProjectId,
        p.EnvironmentId, p.Generation, p.Enabled, p.ExpiresUnixMilliseconds, p.SourceKeyId, p.EnvelopeSha256, p.EnvelopeLength);

    internal static string ReservationDigest(StageReservation r) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { r.Version, r.Kind, r.StageId, r.PlanSha256, r.SourceSetSha256, r.DestinationRootCommitmentSha256, r.DestinationKey, r.StagedVaultNamespaceId,
        r.StagedAuthorityNamespaceId, reservedUnixMilliseconds = r.ReservedUtc.ToUnixTimeMilliseconds(), r.InactiveRecoveryHold }, CredentialRecoverySets.Json));
    internal static string BindingDigest(StageBinding b) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { b.Version, b.Kind, b.StageId, b.PlanSha256, b.SourceSetSha256, b.SourceNamespace, b.StagedVaultNamespaceId, b.StagedAuthorityNamespaceId,
        b.DestinationRootCommitmentSha256, b.DestinationKey, b.InactiveRecoveryHold }, CredentialRecoverySets.Json));
    internal static string StateDigest(StageState s) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { s.Version, s.Kind, s.StageId, s.PlanSha256, s.StagedVaultNamespaceId, s.StagedAuthorityNamespaceId, s.Revision, s.Entries, s.InactiveRecoveryHold }, CredentialRecoverySets.Json));
    internal static void WriteNew<T>(string root, string relative, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CredentialRecoverySets.Json);
        try { if (bytes.Length > MaximumMetadataBytes) throw Failure("capacity-exceeded"); WriteNewBytes(root, relative, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static void WriteNewBytes(string root, string relative, byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); _ = LocalPath(path); CheckPrivate(Path.GetDirectoryName(path)!, true);
        using (var output = CredentialNamespaceCustody.CreateFile(path)) { output.Write(bytes); output.Flush(true); }
        CheckPrivate(path, false);
    }
    internal static bool ReserveRoot(CredentialRecoveryStagePlan plan)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw Failure("owner-unavailable");
        return CredentialNamespaceCustody.CreateDirectory(plan.RootPath, sid);
    }
    internal static void CreateStageDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        using var identity = WindowsIdentity.GetCurrent();
        if (!CredentialNamespaceCustody.CreateDirectory(path, identity.User ?? throw Failure("owner-unavailable"))) throw Failure("reservation-conflict");
        CheckPrivate(path, true);
    }
    internal static void CheckPrivate(string path, bool directory)
    {
        _ = LocalPath(path); if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        if (directory ? !Directory.Exists(path) : !File.Exists(path) || Directory.Exists(path)) throw Failure("stage-incomplete");
        var security = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0); var sid = CurrentSid(); var full = false;
        if (raw.DiscretionaryAcl is null || (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || !security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier))?.Value != sid) throw Failure("stage-custody-denied");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference.Value != sid) throw Failure("stage-custody-denied");
            full |= (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
        }
        if (!full) throw Failure("stage-custody-denied");
    }
    static string SecurityHash(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        return Hash((directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm());
    }
}
