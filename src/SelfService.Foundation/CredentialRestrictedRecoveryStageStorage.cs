using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public static partial class CredentialRestrictedRecoveryStaging
{
    internal sealed record Reservation(int Version, string Kind, string AccessProfile, string StageId, string PlanSha256, string SourceSetSha256,
        string StagedVaultNamespaceId, string StagedAuthorityNamespaceId, string DestinationRootCommitmentSha256,
        CredentialRestrictedRecoveryDestinationKeyIdentity DestinationKey, DateTimeOffset ReservedUtc, bool InactiveRecoveryHold, string Sha256);
    internal sealed record Binding(int Version, string Kind, string AccessProfile, string StageId, string PlanSha256, string SourceSetSha256, CredentialVaultNamespaceIdentity SourceNamespace,
        string StagedVaultNamespaceId, string StagedAuthorityNamespaceId, string DestinationRootCommitmentSha256, CredentialRestrictedRecoveryDestinationKeyIdentity DestinationKey,
        bool InactiveRecoveryHold, string Sha256);
    internal sealed record Entry(string ProfileIdSha256, long Generation, string EnvelopeSha256, long EnvelopeLength, CredentialRestrictedStageAbsenceProof? AbsenceProof);
    internal sealed record State(int Version, string Kind, string AccessProfile, string StageId, string PlanSha256, string StagedVaultNamespaceId, string StagedAuthorityNamespaceId,
        long Revision, string CompletionCoreSha256, Entry[] Entries, bool InactiveRecoveryHold, string Sha256);
    sealed record ProfileDocument(CredentialRecoverySets.ProfileDocument Source, CredentialRecoverySets.ProfileDocument Destination, string PreservedProfileSha256);
    sealed record ReceiptDocument(string Schema, string AccessProfile, string PlanSha256, string SourceSetSha256, string StageId, string StagedVaultNamespaceId, string StagedAuthorityNamespaceId,
        string DestinationRootCommitmentSha256, CredentialRestrictedRecoveryDestinationKeyIdentity DestinationKey, string BindingSha256, string StateSha256, string CompletionCoreSha256, string CompletionProofSha256,
        DateTimeOffset ReservedUtc, DateTimeOffset CompletedUtc, ProfileDocument[] Profiles, CredentialRestrictedRecoveryStagedAbsence[] Absences,
        bool InactiveRecoveryHold, bool NormalVaultUsable, bool PromotionAuthorized, bool ActivationPerformed, bool SourceHostFenced, bool ExternalExecutionAuthorized,
        bool KeyMaterialExported, bool KeyRetirementAuthorized, string IndependentBackupCoverage, bool ProductionRecoveryQualified, string Sha256);
    sealed class Snapshot : IDisposable
    {
        internal SortedDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        internal List<string> Fingerprints { get; } = [];
        public void Dispose() { foreach (var bytes in Files.Values) CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static CredentialRestrictedRecoveryStageReadLease OpenCompletedReadLease(CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha, string expectedReceiptSha)
    {
        FileStream? guard = null; Snapshot? snapshot = null;
        try
        {
            ValidatePlan(plan, reviewedPlanSha); if (!Digest(expectedReceiptSha)) throw Failure("receipt-selection-invalid");
            CheckParent(plan.RootPath); CredentialRecoveryStaging.CheckPrivate(plan.RootPath, true); var path = Path.Combine(plan.RootPath, GuardName); CredentialRecoveryStaging.CheckPrivate(path, false);
            guard = new(path, FileMode.Open, FileAccess.Read, FileShare.Read); if (guard.Length != 0) throw Failure("guard-invalid");
            snapshot = ReadStage(plan); var receipt = ValidateCompleted(plan, snapshot, expectedReceiptSha); var fingerprints = snapshot.Fingerprints.ToArray();
            var envelopes = receipt.Profiles.Select(p => new RetainedCredentialRecoveryEnvelope(p.Destination.ProfileIdSha256, snapshot.Files["profiles/" + p.Destination.ProfileIdSha256 + ".credential.json"])).ToArray();
            var state = ParseState(snapshot.Files["rollback/state.json"]); var proofs = state.Entries.Where(e => e.Generation == 0).Select(e => e.AbsenceProof!).ToArray();
            var completionProof = Parse<CredentialRestrictedStageCompletionProof>(snapshot.Files["rollback/completion-proof.json"]);
            var lease = new CredentialRestrictedRecoveryStageReadLease(guard, receipt, envelopes, proofs, completionProof, snapshot.Files.Values, () =>
            {
                ValidatePlan(plan, reviewedPlanSha); CheckParent(plan.RootPath); using var current = ReadStage(plan); _ = ValidateCompleted(plan, current, expectedReceiptSha);
                if (!current.Fingerprints.SequenceEqual(fingerprints, StringComparer.Ordinal)) throw Failure("stage-changed");
            });
            guard = null; snapshot = null; return lease;
        }
        catch (CredentialException) { throw; }
        catch (Exception) { throw Failure("stage-unavailable"); }
        finally { snapshot?.Dispose(); guard?.Dispose(); }
    }
    internal static void ValidateWrittenStage(CredentialRestrictedRecoveryStagePlan p, CredentialRestrictedRecoveryStageReceipt r)
    { using var snapshot = ReadStage(p); _ = ValidateCompleted(p, snapshot, r.Sha256); }
    internal static IReadOnlyList<CredentialRestrictedStageAbsenceProof> ReadWrittenProofs(CredentialRestrictedRecoveryStagePlan p, CredentialRestrictedRecoveryStageReceipt r)
    {
        using var snapshot = ReadStage(p); _ = ValidateCompleted(p, snapshot, r.Sha256);
        return Array.AsReadOnly(ParseState(snapshot.Files["rollback/state.json"]).Entries.Where(e => e.Generation == 0).Select(e => e.AbsenceProof!).ToArray());
    }
    internal static CredentialRestrictedStageCompletionProof ReadWrittenCompletionProof(CredentialRestrictedRecoveryStagePlan p, CredentialRestrictedRecoveryStageReceipt r)
    {
        using var snapshot = ReadStage(p); _ = ValidateCompleted(p, snapshot, r.Sha256);
        return Parse<CredentialRestrictedStageCompletionProof>(snapshot.Files["rollback/completion-proof.json"]);
    }
    static Snapshot ReadStage(CredentialRestrictedRecoveryStagePlan plan)
    {
        CheckParent(plan.RootPath);
        var snapshot = new Snapshot(); var count = 0; long total = 0;
        try { Walk(plan.RootPath, ""); snapshot.Fingerprints.Sort(StringComparer.Ordinal); return snapshot; }
        catch { snapshot.Dispose(); throw; }
        void Walk(string path, string name)
        {
            if (++count > MaximumEntries) throw Failure("capacity-exceeded"); var directory = Directory.Exists(path); CredentialRecoveryStaging.CheckPrivate(path, directory);
            if (directory)
            {
                if (name is not ("" or "profiles" or "rollback")) throw Failure("stage-contents-invalid");
                snapshot.Fingerprints.Add(name + ":directory:" + SecurityHash(path, true));
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) Walk(child, name == "" ? Path.GetFileName(child) : name + "/" + Path.GetFileName(child));
                return;
            }
            var maximum = name == GuardName ? 0 : Regex.IsMatch(name, "\\Aprofiles/[a-f0-9]{64}\\.credential\\.json\\z", RegexOptions.CultureInvariant) ? 131072 :
                name is MarkerName or "rollback/binding.json" or "rollback/state.json" or "rollback/completion-proof.json" or "completion.json" ? MaximumMetadataBytes : throw Failure("stage-contents-invalid");
            byte[] bytes;
            if (name == GuardName) { if (new FileInfo(path).Length != 0) throw Failure("guard-invalid"); bytes = []; }
            else
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is < 1 || stream.Length > maximum) throw Failure("capacity-exceeded"); bytes = new byte[checked((int)stream.Length)];
                try { stream.ReadExactly(bytes); if (stream.ReadByte() != -1 || stream.Length != bytes.Length) throw Failure("stage-changed"); }
                catch { CryptographicOperations.ZeroMemory(bytes); throw; }
            }
            snapshot.Files.Add(name, bytes); total += bytes.Length; if (total > MaximumStageBytes) throw Failure("capacity-exceeded");
            snapshot.Fingerprints.Add(name + ":file:" + bytes.Length + ":" + Hash(bytes) + ":" + SecurityHash(path, false));
        }
    }
    static CredentialRestrictedRecoveryStageReceipt ValidateCompleted(CredentialRestrictedRecoveryStagePlan p, Snapshot snapshot, string expectedSha)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal) { GuardName, MarkerName, "rollback/binding.json", "rollback/state.json", "rollback/completion-proof.json", "completion.json" };
        foreach (var profile in p.PreservedProfiles) names.Add("profiles/" + profile.ProfileIdSha256 + ".credential.json");
        if (!snapshot.Files.Keys.SequenceEqual(names, StringComparer.Ordinal) || !snapshot.Fingerprints.Any(x => x.StartsWith("profiles:directory:", StringComparison.Ordinal)) ||
            !snapshot.Fingerprints.Any(x => x.StartsWith("rollback:directory:", StringComparison.Ordinal))) throw Failure("stage-contents-invalid");
        var reservation = Parse<Reservation>(snapshot.Files[MarkerName]); var binding = Parse<Binding>(snapshot.Files["rollback/binding.json"]);
        var state = ParseState(snapshot.Files["rollback/state.json"]); var receipt = ParseReceipt(snapshot.Files["completion.json"]);
        var ids = new[] { reservation.StageId, reservation.StagedVaultNamespaceId, reservation.StagedAuthorityNamespaceId };
        if (reservation.Version != 3 || reservation.Kind != "credential-restricted-recovery-stage-reservation" || reservation.AccessProfile != "FoundationBoundDispatchOnly" || !reservation.InactiveRecoveryHold ||
            reservation.PlanSha256 != p.Sha256 || reservation.SourceSetSha256 != p.SourceSetAuthority.ArtifactSha256 ||
            reservation.DestinationRootCommitmentSha256 != p.DestinationRootCommitmentSha256 || reservation.DestinationKey != p.DestinationKey ||
            reservation.Sha256 != ReservationDigest(reservation) || ids.Any(x => !Id(x) || x == p.SourceNamespace.VaultNamespaceId || x == p.SourceNamespace.AuthorityNamespaceId) ||
            ids.Distinct(StringComparer.Ordinal).Count() != 3) throw Failure("stage-reservation-invalid");
        if (binding.Version != 3 || binding.Kind != "credential-restricted-recovery-stage-binding" || binding.AccessProfile != "FoundationBoundDispatchOnly" || !binding.InactiveRecoveryHold ||
            binding.StageId != reservation.StageId || binding.StagedVaultNamespaceId != reservation.StagedVaultNamespaceId || binding.StagedAuthorityNamespaceId != reservation.StagedAuthorityNamespaceId ||
            binding.PlanSha256 != p.Sha256 || binding.SourceSetSha256 != reservation.SourceSetSha256 || binding.SourceNamespace != p.SourceNamespace ||
            binding.DestinationRootCommitmentSha256 != p.DestinationRootCommitmentSha256 || binding.DestinationKey != p.DestinationKey || binding.Sha256 != BindingDigest(binding)) throw Failure("stage-binding-invalid");
        if (state.Version != 3 || state.Kind != "credential-restricted-recovery-stage-state" || state.AccessProfile != "FoundationBoundDispatchOnly" || !state.InactiveRecoveryHold || state.Revision != 0 || state.StageId != reservation.StageId ||
            state.PlanSha256 != p.Sha256 || state.StagedVaultNamespaceId != reservation.StagedVaultNamespaceId || state.StagedAuthorityNamespaceId != reservation.StagedAuthorityNamespaceId ||
            state.Sha256 != StateDigest(state)) throw Failure("stage-state-invalid");
        if (receipt.Sha256 != expectedSha || receipt.PlanSha256 != p.Sha256 || receipt.SourceSetSha256 != p.SourceSetAuthority.ArtifactSha256 ||
            receipt.StageId != reservation.StageId || receipt.StagedVaultNamespaceId != reservation.StagedVaultNamespaceId || receipt.StagedAuthorityNamespaceId != reservation.StagedAuthorityNamespaceId ||
            receipt.DestinationRootCommitmentSha256 != p.DestinationRootCommitmentSha256 || receipt.DestinationKey != p.DestinationKey || receipt.BindingSha256 != binding.Sha256 ||
            receipt.StateSha256 != state.Sha256 || receipt.ReservedUtc != reservation.ReservedUtc || receipt.Profiles.Count != p.PreservedProfiles.Count || receipt.Absences.Count != p.AbsenceScopes.Count ||
            receipt.CompletionCoreSha256 != state.CompletionCoreSha256 || receipt.CompletionCoreSha256 != CompletionCoreDigest(p, receipt.StageId, receipt.StagedVaultNamespaceId,
                receipt.StagedAuthorityNamespaceId, binding.Sha256, receipt.ReservedUtc, receipt.CompletedUtc, receipt.Profiles)) throw Failure("receipt-mismatch");
        var all = receipt.Profiles.Select(x => x.Source.ProfileIdSha256).Concat(receipt.Absences.Select(x => x.Source.ProfileIdSha256)).Order(StringComparer.Ordinal).ToArray();
        if (!state.Entries.Select(x => x.ProfileIdSha256).SequenceEqual(all, StringComparer.Ordinal)) throw Failure("stage-state-mismatch");
        for (var i = 0; i < receipt.Profiles.Count; i++)
        {
            var profile = receipt.Profiles[i]; var source = p.SourceInventory.ProfileDependencies[i]; var preserved = p.PreservedProfiles[i];
            CredentialRecoveryInventory.ValidateProfile(profile.Source); CredentialRecoveryInventory.ValidateProfile(profile.Destination);
            if (JsonSerializer.Serialize(profile.Source, CredentialRules.Json) != JsonSerializer.Serialize(source, CredentialRules.Json) || profile.PreservedProfileSha256 != preserved.ProfileSha256 ||
                profile.Destination.SourceKeyId != p.DestinationKey.KeyId || !PreservedDependency(source, profile.Destination)) throw Failure("preserved-profile-mismatch");
            var entry = state.Entries.Single(e => e.ProfileIdSha256 == source.ProfileIdSha256); var bytes = snapshot.Files["profiles/" + source.ProfileIdSha256 + ".credential.json"];
            if (entry.AbsenceProof is not null || entry.Generation != profile.Destination.Generation || entry.EnvelopeSha256 != profile.Destination.EnvelopeSha256 || entry.EnvelopeLength != bytes.Length ||
                bytes.Length != profile.Destination.EnvelopeLength || Hash(bytes) != profile.Destination.EnvelopeSha256 ||
                CredentialVault.RestrictedStageProfileSha256(bytes, source.ProfileIdSha256, p,
                    new(receipt.StageId, receipt.StagedVaultNamespaceId, receipt.StagedAuthorityNamespaceId, receipt.BindingSha256)) != preserved.ProfileSha256)
                throw Failure("stage-envelope-mismatch");
        }
        for (var i = 0; i < receipt.Absences.Count; i++)
        {
            var absence = receipt.Absences[i]; var source = p.AbsenceScopes[i];
            if (absence.Source != source || !PreservedAbsenceDependency(source, absence.Destination) || absence.Destination.SourceKeyId != p.DestinationKey.KeyId) throw Failure("source-absence-mismatch");
            var entry = state.Entries.Single(e => e.ProfileIdSha256 == source.ProfileIdSha256); var proof = entry.AbsenceProof ?? throw Failure("destination-proof-missing");
            CredentialRestrictedStageProofs.Validate(proof);
            if (entry.Generation != 0 || entry.EnvelopeSha256 != "" || entry.EnvelopeLength != 0 || proof.SourceProofSha256 != source.ProofSha256 || proof.SourceKeyId != source.SourceKeyId ||
                proof.ProfileIdSha256 != source.ProfileIdSha256 || proof.ScopeSha256 != source.ScopeSha256 || proof.Scope.ProjectId != source.ProjectId || proof.Scope.EnvironmentId != source.EnvironmentId ||
                proof.SourceNamespace != p.SourceNamespace || proof.StageId != receipt.StageId || proof.StagedVaultNamespaceId != receipt.StagedVaultNamespaceId ||
                proof.StagedAuthorityNamespaceId != receipt.StagedAuthorityNamespaceId || proof.PlanSha256 != p.Sha256 || proof.SourceSetSha256 != p.SourceSetAuthority.ArtifactSha256 ||
                proof.DestinationRootCommitmentSha256 != p.DestinationRootCommitmentSha256 || proof.DestinationKey != p.DestinationKey || proof.BindingSha256 != binding.Sha256 ||
                proof.CompletionCoreSha256 != receipt.CompletionCoreSha256 || CredentialRestrictedStageProofs.Projection(proof) != absence.Destination) throw Failure("destination-proof-mismatch");
        }
        var completionProof = Parse<CredentialRestrictedStageCompletionProof>(snapshot.Files["rollback/completion-proof.json"]);
        CredentialRestrictedStageCompletionProofs.Validate(p, receipt, completionProof);
        if (completionProof.Sha256 != receipt.CompletionProofSha256) throw Failure("completion-proof-mismatch");
        return receipt;
    }
    static T Parse<T>(byte[] bytes) { using var doc = CredentialRules.Document(bytes); return doc.RootElement.Deserialize<T>(CredentialRules.Json) ?? throw Failure("metadata-invalid"); }
    static State ParseState(byte[] bytes)
    {
        using var doc = CredentialRules.Document(bytes);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) throw Failure("metadata-invalid");
        if (entries.GetArrayLength() > MaximumProfiles) throw Failure("capacity-exceeded");
        var state = doc.RootElement.Deserialize<State>(CredentialRules.Json) ?? throw Failure("metadata-invalid");
        if (state.Entries is null || state.Entries.Any(e => e is null)) throw Failure("metadata-invalid"); return state;
    }
    static CredentialRestrictedRecoveryStageReceipt ParseReceipt(byte[] bytes)
    {
        using var doc = CredentialRules.Document(bytes);
        if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array || !doc.RootElement.TryGetProperty("absences", out var absences) || absences.ValueKind != JsonValueKind.Array) throw Failure("metadata-invalid");
        if (profiles.GetArrayLength() > MaximumProfiles || absences.GetArrayLength() > MaximumProfiles || profiles.GetArrayLength() + absences.GetArrayLength() > MaximumProfiles) throw Failure("capacity-exceeded");
        var r = doc.RootElement.Deserialize<ReceiptDocument>(CredentialRules.Json) ?? throw Failure("metadata-invalid");
        if (r.Schema != "platform-studio.credential-restricted-recovery-stage-receipt.v1" || r.AccessProfile != "FoundationBoundDispatchOnly" || r.Profiles is null || r.Absences is null ||
            r.Profiles.Any(x => x is null || x.Source is null || x.Destination is null) || r.Absences.Any(x => x is null || x.Source is null || x.Destination is null) ||
            !r.InactiveRecoveryHold || r.NormalVaultUsable || r.PromotionAuthorized || r.ActivationPerformed || r.SourceHostFenced || r.ExternalExecutionAuthorized || r.KeyMaterialExported ||
            r.KeyRetirementAuthorized || r.IndependentBackupCoverage != "NotAssessed" || r.ProductionRecoveryQualified) throw Failure("receipt-invalid");
        CredentialRecoveryProfileDependency Dep(CredentialRecoverySets.ProfileDocument d) => new(d.ProfileIdSha256, d.ScopeSha256, d.ProjectId, d.EnvironmentId, d.Generation, d.Enabled, d.ExpiresUnixMilliseconds, d.SourceKeyId, d.EnvelopeSha256, d.EnvelopeLength);
        var receipt = new CredentialRestrictedRecoveryStageReceipt(r.PlanSha256, r.SourceSetSha256, r.StageId, r.StagedVaultNamespaceId, r.StagedAuthorityNamespaceId, r.DestinationRootCommitmentSha256,
            r.DestinationKey, r.BindingSha256, r.StateSha256, r.CompletionCoreSha256, r.CompletionProofSha256, r.ReservedUtc, r.CompletedUtc, r.Profiles.Select(x => new CredentialRecoveryStagedProfile(Dep(x.Source), Dep(x.Destination), x.PreservedProfileSha256)), r.Absences);
        if (receipt.Sha256 != r.Sha256) throw Failure("receipt-invalid"); return receipt;
    }
    internal static string ReservationDigest(Reservation r) => Hash(JsonSerializer.SerializeToUtf8Bytes(r with { Sha256 = "" }, CredentialRules.Json));
    internal static string BindingDigest(Binding b) => Hash(JsonSerializer.SerializeToUtf8Bytes(b with { Sha256 = "" }, CredentialRules.Json));
    internal static string StateDigest(State s) => Hash(JsonSerializer.SerializeToUtf8Bytes(s with { Sha256 = "" }, CredentialRules.Json));
    internal static void WriteNew<T>(string root, string relative, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CredentialRules.Json);
        try { if (bytes.Length > MaximumMetadataBytes) throw Failure("capacity-exceeded"); CredentialRecoveryStaging.WriteNewBytes(root, relative, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    static string SecurityHash(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        return Hash((directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm());
    }
}
