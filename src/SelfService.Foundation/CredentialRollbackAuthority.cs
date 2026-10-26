using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public enum CredentialRollbackResolution { AcceptPublished, AbandonUnpublished }
public sealed record CredentialRollbackPending(string TransactionId, long PreviousGeneration, string PreviousEnvelopeSha256,
    long NewGeneration, string NewEnvelopeSha256, long NewEnvelopeLength, string PendingSha256);
public sealed record CredentialRollbackStatus(long AuthorityRevision, string AuthorityStateSha256, string ProfileIdSha256,
    long CommittedGeneration, string CommittedEnvelopeSha256, long CommittedEnvelopeLength, CredentialRollbackPending? Pending);

// A separate local high-water file, not an audit ledger or protection against rollback of both roots.
internal sealed partial class CredentialRollbackAuthority
{
    internal const int MaximumProfiles = 256, MaximumStateBytes = 1048576;
    const long MaximumCounter = 9007199254740991;
    readonly CredentialNamespaceCustody vault, authority;
    readonly Binding binding;
    readonly CredentialRollbackFormat format;
    internal bool IsScoped => format == CredentialRollbackFormat.AuthenticatedScopeV2;
    // Metadata only; callers revalidate ReadState while holding the existing authority guard first.
    internal string AuthorityNamespaceId => binding.AuthorityNamespaceId;
    internal string AuthorityBindingSha256 => binding.Sha256;
    sealed record Binding(int Schema, string Kind, string CustomerId, string CellId, string VaultPath, string VaultNamespaceId,
        string AuthorityPath, string AuthorityNamespaceId, string Sha256);
    internal sealed record Entry(long Generation, string EnvelopeSha256, long EnvelopeLength, CredentialRollbackPending? Pending)
    {
        // Internal-only adapter state: legacy public-property serialization and its canonical digest remain byte-identical.
        internal CredentialAbsenceScopeProof? AbsenceProof { get; init; }
    }
    internal sealed record State(int Schema, long Revision, Dictionary<string, Entry> Profiles, string Sha256);

    internal CredentialRollbackAuthority(CredentialNamespaceCustody vault, string authorityPath, string customer, string cell,
        CredentialRollbackFormat format = CredentialRollbackFormat.LegacyV1)
    {
        this.format = format; this.vault = vault; authority = new(authorityPath,
            vault.IsBoundDispatchOnly ? "credential-rollback-scoped-bound-authority" : IsScoped ? "credential-rollback-scoped-authority" : "credential-rollback-authority", customer, cell);
        binding = Read<Binding>(vault, "rollback-binding.json", 16384);
        var other = Read<Binding>(authority, "authority.json", 16384);
        if (binding != other || binding.Schema != (IsScoped ? 2 : 1) || binding.Kind != (IsScoped ? "credential-rollback-scoped-binding" : "credential-rollback-binding") || binding.CustomerId != customer || binding.CellId != cell ||
            binding.VaultPath != vault.Root || binding.VaultNamespaceId != vault.NamespaceId || binding.AuthorityPath != authority.Root ||
            binding.AuthorityNamespaceId != authority.NamespaceId || binding.Sha256 != BindingDigest(binding)) throw Failure("credential-rollback-binding-invalid");
        using var guard = OpenGuard(); _ = ReadState();
    }

    internal static void Provision(CredentialNamespaceCustody vault, string authorityPath, string customer, string cell,
        CredentialRollbackFormat format = CredentialRollbackFormat.LegacyV1)
    {
        var scoped = format == CredentialRollbackFormat.AuthenticatedScopeV2;
        if (vault.IsBoundDispatchOnly && !scoped) throw Failure("credential-bound-dispatch-profile-invalid");
        var authorityKind = vault.IsBoundDispatchOnly ? "credential-rollback-scoped-bound-authority" : scoped ? "credential-rollback-scoped-authority" : "credential-rollback-authority";
        CredentialNamespaceCustody.Provision(authorityPath, authorityKind, customer, cell);
        var authority = new CredentialNamespaceCustody(authorityPath, authorityKind, customer, cell);
        var unsigned = new Binding(scoped ? 2 : 1, scoped ? "credential-rollback-scoped-binding" : "credential-rollback-binding", customer, cell, vault.Root, vault.NamespaceId, authority.Root, authority.NamespaceId, "");
        var binding = unsigned with { Sha256 = BindingDigest(unsigned) };
        WriteNew(vault, "rollback-binding.json", binding); WriteNew(authority, "authority.json", binding);
        if (scoped) WriteEmptyScopedState(authority, binding);
        else { var empty = new State(1, 0, new(StringComparer.Ordinal), ""); WriteNew(authority, "state.json", empty with { Sha256 = StateDigest(empty, binding) }); }
    }

    internal FileStream OpenGuard() => authority.OpenGuard();
    void ValidateBindingDocuments()
    {
        if (Read<Binding>(vault, "rollback-binding.json", 16384) != binding || Read<Binding>(authority, "authority.json", 16384) != binding)
            throw Failure("credential-rollback-binding-invalid");
    }
    internal void ValidateNamespaceMetadata()
    {
        authority.ValidateMetadataIdentity(); vault.ValidateMetadataIdentity(); ValidateBindingDocuments();
    }
    internal CredentialNamespaceMetadataPin[] HoldNamespaceMetadata()
    {
        var pins = new List<CredentialNamespaceMetadataPin>(3);
        try
        {
            pins.Add(authority.HoldMetadataFile(CredentialNamespaceCustody.MarkerName));
            pins.Add(vault.HoldMetadataFile("rollback-binding.json"));
            pins.Add(authority.HoldMetadataFile("authority.json"));
            ValidateNamespaceMetadata(); return pins.ToArray();
        }
        catch { foreach (var pin in pins) pin.Dispose(); throw; }
    }
    internal State ReadState()
    {
        authority.ValidateCurrent(); vault.ValidateCurrent();
        ValidateBindingDocuments();
        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(authority.Root))
        {
            if (++count > 260 || !File.Exists(path)) throw Failure("credential-rollback-authority-contents-invalid");
            authority.ValidateFile(path); var name = Path.GetFileName(path);
            if (name is not (CredentialNamespaceCustody.MarkerName or ".rollback.lock" or "authority.json" or "state.json") &&
                !Regex.IsMatch(name, "\\A\\.state-write-[a-f0-9]{32}\\z")) throw Failure("credential-rollback-authority-contents-invalid");
            if (new FileInfo(path).Length > MaximumStateBytes) throw Failure("credential-rollback-state-limit");
        }
        var state = IsScoped ? ReadScopedState() : Read<State>(authority, "state.json", MaximumStateBytes);
        if (state.Schema != (IsScoped ? 2 : 1) || state.Revision is < 0 or > MaximumCounter || state.Profiles is null || state.Profiles.Count > MaximumProfiles ||
            state.Sha256 != (IsScoped ? ScopedStateDigest(state, binding) : StateDigest(state, binding))) throw Failure("credential-rollback-state-invalid");
        var pending = 0;
        foreach (var item in state.Profiles)
        {
            var e = item.Value;
            if (!DigestShape(item.Key) || e is null || e.Generation is < 0 or > MaximumCounter ||
                (e.Generation == 0 ? e.EnvelopeSha256 != "" || e.EnvelopeLength != 0 : !DigestShape(e.EnvelopeSha256) || e.EnvelopeLength is < 1 or > 131072))
                throw Failure("credential-rollback-state-invalid");
            if (IsScoped) ValidateScopedEntry(item.Key, e);
            if (e.Pending is { } p)
            {
                if (++pending > 1 || !Guid.TryParseExact(p.TransactionId, "N", out var id) || id == Guid.Empty || p.TransactionId != id.ToString("N") ||
                    p.PreviousGeneration != e.Generation || p.PreviousEnvelopeSha256 != e.EnvelopeSha256 || p.NewGeneration != e.Generation + 1 ||
                    p.NewGeneration > MaximumCounter || !DigestShape(p.NewEnvelopeSha256) || p.NewEnvelopeLength is < 1 or > 131072 ||
                    p.PendingSha256 != PendingDigest(item.Key, p)) throw Failure("credential-rollback-state-invalid");
            }
        }
        return state;
    }

    // Validate original bindings and committed high-water metadata without consulting any current namespace path.
    internal static IReadOnlyList<string> ValidateRecoverySnapshot(CredentialNamespaceCustody.Marker vaultMarker, CredentialNamespaceCustody.Marker authorityMarker,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, CredentialRecoveryInventory inventory)
    {
        T Parse<T>(string name)
        {
            if (!files.TryGetValue(name, out var bytes)) throw Failure("credential-recovery-set-metadata-invalid");
            using var document = CredentialRules.Document(bytes);
            if (typeof(T) == typeof(State))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("profiles", out var profiles) ||
                    profiles.ValueKind != JsonValueKind.Object) throw Failure("credential-recovery-set-metadata-invalid");
                if (profiles.EnumerateObject().Take(MaximumProfiles + 1).Count() > MaximumProfiles)
                    throw Failure("credential-recovery-set-capacity-exceeded");
            }
            return document.RootElement.Deserialize<T>(CredentialRecoverySets.Json) ?? throw Failure("credential-recovery-set-metadata-invalid");
        }
        var binding = Parse<Binding>("vault/rollback-binding.json"); var other = Parse<Binding>("authority/authority.json");
        if (binding != other || binding.Schema != 1 || binding.Kind != "credential-rollback-binding" ||
            binding.CustomerId != inventory.CustomerId || binding.CellId != inventory.CellId ||
            binding.VaultNamespaceId != vaultMarker.NamespaceId || binding.VaultPath != vaultMarker.RootPath ||
            binding.AuthorityNamespaceId != authorityMarker.NamespaceId || binding.AuthorityPath != authorityMarker.RootPath ||
            vaultMarker.OwnerSid != authorityMarker.OwnerSid || binding.Sha256 != BindingDigest(binding) || binding.Sha256 != inventory.AuthorityBindingSha256)
            throw Failure("credential-recovery-set-metadata-invalid");
        var state = Parse<State>("authority/state.json");
        if (state.Schema != 1 || state.Revision is < 0 or > MaximumCounter || state.Revision != inventory.AuthorityRevision ||
            state.Profiles is null || state.Profiles.Count > MaximumProfiles || state.Sha256 != StateDigest(state, binding) || state.Sha256 != inventory.AuthorityStateSha256)
            throw Failure("credential-recovery-set-metadata-invalid");
        foreach (var pair in state.Profiles)
        {
            var entry = pair.Value;
            if (!DigestShape(pair.Key) || entry is null || entry.Pending is not null || entry.Generation is < 0 or > MaximumCounter ||
                (entry.Generation == 0 ? entry.EnvelopeSha256 != "" || entry.EnvelopeLength != 0 : !DigestShape(entry.EnvelopeSha256) || entry.EnvelopeLength is < 1 or > 131072))
                throw Failure("credential-recovery-set-metadata-invalid");
            if (entry.Generation == 0) continue;
            var profile = inventory.ProfileDependencies.SingleOrDefault(p => p.ProfileIdSha256 == pair.Key);
            if (profile is null || profile.Generation != entry.Generation || profile.EnvelopeSha256 != entry.EnvelopeSha256 || profile.EnvelopeLength != entry.EnvelopeLength)
                throw Failure("credential-recovery-set-metadata-invalid");
        }
        if (state.Profiles.Count(p => p.Value.Generation > 0) != inventory.ProfileDependencies.Count) throw Failure("credential-recovery-set-metadata-invalid");
        return Array.AsReadOnly(state.Profiles.Where(p => p.Value.Generation == 0).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
    }

    internal Entry RequireCurrent(State state, string profileHash, byte[]? envelope, bool permitAbsent = false)
    {
        if (state.Profiles.Values.Any(e => e.Pending is not null)) throw Failure("credential-rollback-reconciliation-required");
        VerifyVaultInventory(state);
        var entry = state.Profiles.GetValueOrDefault(profileHash) ?? new Entry(0, "", 0, null);
        if (!Matches(envelope, entry.Generation, entry.EnvelopeSha256, entry.EnvelopeLength) || !permitAbsent && entry.Generation == 0)
            throw Failure("credential-rollback-envelope-mismatch");
        return entry;
    }
    void VerifyVaultInventory(State state)
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(vault.Root))
        {
            if (++count > 520 || !File.Exists(path)) throw Failure("credential-rollback-vault-contents-invalid");
            vault.ValidateFile(path); var name = Path.GetFileName(path);
            if (name is CredentialNamespaceCustody.MarkerName or ".vault.lock" or "rollback-binding.json") continue;
            if (Regex.IsMatch(name, "\\A\\.write-[a-f0-9]{32}\\z")) continue;
            if (!Regex.IsMatch(name, "\\A[a-f0-9]{64}\\.credential\\.json\\z") || !state.Profiles.ContainsKey(name[..64]))
                throw Failure("credential-rollback-vault-contents-invalid");
        }
        foreach (var item in state.Profiles)
            if (item.Value.Generation > 0 && !File.Exists(ProfilePath(item.Key))) throw Failure("credential-rollback-envelope-mismatch");
    }
    internal CredentialRollbackPending Begin(State state, string profileHash, byte[]? previous, long newGeneration, byte[] candidate,
        CredentialScope? candidateScope = null, string? candidateKeyId = null, byte[]? frozenCandidateKey = null)
    {
        var current = RequireCurrent(state, profileHash, previous, permitAbsent: true);
        if (state.Revision > MaximumCounter - 2) throw Failure("credential-rollback-state-limit");
        if (newGeneration != current.Generation + 1 || newGeneration > MaximumCounter) throw Failure("credential-rollback-generation-conflict");
        if (!state.Profiles.ContainsKey(profileHash) && state.Profiles.Count >= MaximumProfiles) throw Failure("credential-rollback-profile-limit");
        var unsigned = new CredentialRollbackPending(Guid.NewGuid().ToString("N"), current.Generation, current.EnvelopeSha256,
            newGeneration, Hash(candidate), candidate.Length, "");
        var pending = unsigned with { PendingSha256 = PendingDigest(profileHash, unsigned) };
        var prepared = current with { Pending = pending };
        if (IsScoped && current.Generation == 0)
        {
            if (candidateScope is null || candidateKeyId is null || frozenCandidateKey is null) throw Failure("credential-absence-proof-required");
            if (current.AbsenceProof is { } original)
            {
                ValidateProofBinding(original);
                if (JsonSerializer.Serialize(original.Scope, CredentialRules.Json) != JsonSerializer.Serialize(candidateScope, CredentialRules.Json))
                    throw Failure("credential-absence-proof-scope-changed");
                prepared = prepared with { AbsenceProof = original };
            }
            else prepared = prepared with { AbsenceProof = CredentialAbsenceScopeProofs.Create(binding.CustomerId, binding.CellId,
                vault.NamespaceId, vault.NamespaceCommitmentSha256, authority.NamespaceId, authority.NamespaceCommitmentSha256,
                binding.Sha256, profileHash, candidateScope, candidateKeyId, pending.TransactionId, candidate, frozenCandidateKey) };
        }
        var entries = new Dictionary<string, Entry>(state.Profiles, StringComparer.Ordinal) { [profileHash] = prepared };
        Save(state, entries); return pending;
    }
    internal void FinalizePublished(string profileHash, CredentialRollbackPending expected, byte[] published)
    {
        var state = ReadState(); var entry = state.Profiles.GetValueOrDefault(profileHash);
        if (entry?.Pending != expected || !Matches(published, expected.NewGeneration, expected.NewEnvelopeSha256, expected.NewEnvelopeLength))
            throw Failure("credential-rollback-envelope-mismatch");
        Save(state, new(state.Profiles, StringComparer.Ordinal) { [profileHash] = new(expected.NewGeneration, expected.NewEnvelopeSha256, expected.NewEnvelopeLength, null) });
    }
    internal CredentialRollbackStatus Status(State state, string hash)
    {
        var e = state.Profiles.GetValueOrDefault(hash) ?? new Entry(0, "", 0, null);
        return new(state.Revision, state.Sha256, hash, e.Generation, e.EnvelopeSha256, e.EnvelopeLength, e.Pending);
    }
    internal void Reconcile(State state, string hash, long revision, string stateSha256, string pendingSha256,
        CredentialRollbackResolution disposition, byte[]? observed)
    {
        VerifyVaultInventory(state);
        if (state.Revision != revision || state.Sha256 != stateSha256) throw Failure("credential-rollback-state-changed");
        var e = state.Profiles.GetValueOrDefault(hash); var p = e?.Pending;
        if (p is null || p.PendingSha256 != pendingSha256 || !Enum.IsDefined(disposition)) throw Failure("credential-rollback-pending-mismatch");
        Entry final;
        if (disposition == CredentialRollbackResolution.AcceptPublished)
        {
            if (!Matches(observed, p.NewGeneration, p.NewEnvelopeSha256, p.NewEnvelopeLength)) throw Failure("credential-rollback-envelope-mismatch");
            final = new(p.NewGeneration, p.NewEnvelopeSha256, p.NewEnvelopeLength, null);
        }
        else
        {
            if (!Matches(observed, e!.Generation, e.EnvelopeSha256, e.EnvelopeLength)) throw Failure("credential-rollback-envelope-mismatch");
            final = e with { Pending = null };
        }
        Save(state, new(state.Profiles, StringComparer.Ordinal) { [hash] = final });
    }
    void Save(State before, Dictionary<string, Entry> entries)
    {
        if (before.Revision >= MaximumCounter || entries.Count > MaximumProfiles) throw Failure("credential-rollback-state-limit");
        var next = new State(IsScoped ? 2 : 1, before.Revision + 1, entries, ""); next = next with { Sha256 = IsScoped ? ScopedStateDigest(next, binding) : StateDigest(next, binding) };
        var bytes = IsScoped ? SerializeScopedState(next) : JsonSerializer.SerializeToUtf8Bytes(next, CredentialRules.Json);
        if (bytes.Length > MaximumStateBytes) throw Failure("credential-rollback-state-limit");
        var temporary = Path.Combine(authority.Root, ".state-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = authority.CreateNew(temporary)) { file.Write(bytes); file.Flush(true); }
            authority.ValidateCurrent(); authority.ValidateFile(Path.Combine(authority.Root, "state.json"));
            File.Move(temporary, Path.Combine(authority.Root, "state.json"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    string ProfilePath(string hash) => Path.Combine(vault.Root, hash + ".credential.json");
    static bool Matches(byte[]? bytes, long generation, string digest, long length) => generation == 0
        ? bytes is null && digest == "" && length == 0 : bytes is not null && bytes.Length == length && Hash(bytes) == digest;
    string PendingDigest(string profile, CredentialRollbackPending p) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { binding.CustomerId, binding.CellId, binding.VaultNamespaceId, binding.AuthorityNamespaceId, profile,
        p.TransactionId, p.PreviousGeneration, p.PreviousEnvelopeSha256, p.NewGeneration, p.NewEnvelopeSha256, p.NewEnvelopeLength }, CredentialRules.Json));
    static string BindingDigest(Binding b) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { b.Schema, b.Kind, b.CustomerId, b.CellId, b.VaultPath, b.VaultNamespaceId, b.AuthorityPath, b.AuthorityNamespaceId }, CredentialRules.Json));
    static string StateDigest(State s, Binding b) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { b.CustomerId, b.CellId, b.VaultNamespaceId, b.AuthorityNamespaceId, s.Schema, s.Revision,
        profiles = s.Profiles.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { id = p.Key, entry = p.Value }).ToArray() }, CredentialRules.Json));
    static bool DigestShape(string value) => Regex.IsMatch(value ?? "", "\\A[a-f0-9]{64}\\z");
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static T Read<T>(CredentialNamespaceCustody custody, string name, int limit)
    {
        try { using var doc = CredentialRules.Document(custody.ReadFile(Path.Combine(custody.Root, name), limit)); return doc.RootElement.Deserialize<T>(CredentialRules.Json) ?? throw new JsonException(); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw Failure("credential-rollback-metadata-invalid"); }
    }
    static void WriteNew<T>(CredentialNamespaceCustody custody, string name, T value)
    { using var file = custody.CreateNew(Path.Combine(custody.Root, name)); JsonSerializer.Serialize(file, value, CredentialRules.Json); file.Flush(true); }
    static CredentialException Failure(string code) => new(code);
}
