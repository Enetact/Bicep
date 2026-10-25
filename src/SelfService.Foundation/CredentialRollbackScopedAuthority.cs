using System.Text.Json;

namespace SelfService.Foundation;

internal sealed partial class CredentialRollbackAuthority
{
    sealed record ScopedEntry(long Generation, string EnvelopeSha256, long EnvelopeLength,
        CredentialRollbackPending? Pending, CredentialAbsenceScopeProof? AbsenceProof);
    sealed record ScopedState(int Schema, long Revision, Dictionary<string, ScopedEntry> Profiles, string Sha256);

    internal void ValidateProofBinding(CredentialAbsenceScopeProof proof)
    {
        if (!IsScoped) throw Failure("credential-absence-proof-format-required");
        CredentialAbsenceScopeProofs.Validate(proof);
        if (proof.CustomerId != binding.CustomerId || proof.CellId != binding.CellId || proof.VaultNamespaceId != vault.NamespaceId ||
            proof.VaultNamespaceCommitmentSha256 != vault.NamespaceCommitmentSha256 || proof.AuthorityNamespaceId != authority.NamespaceId ||
            proof.AuthorityNamespaceCommitmentSha256 != authority.NamespaceCommitmentSha256 || proof.BindingSha256 != binding.Sha256)
            throw Failure("credential-absence-proof-binding-invalid");
    }
    internal IReadOnlyList<CredentialAbsenceScopeProof> SnapshotAbsenceProofs(State state)
    {
        if (!IsScoped) throw Failure("credential-absence-proof-format-required");
        if (state.Profiles.Values.Any(e => e.Pending is not null)) throw Failure("credential-rollback-reconciliation-required");
        return Array.AsReadOnly(state.Profiles.Where(p => p.Value.Generation == 0).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Value.AbsenceProof ?? throw Failure("credential-absence-proof-required")).ToArray());
    }
    internal void AssertUnchanged(State expected)
    {
        var actual = ReadState();
        if (actual.Revision != expected.Revision || actual.Sha256 != expected.Sha256) throw Failure("credential-rollback-state-changed");
    }
    void ValidateScopedEntry(string hash, Entry entry)
    {
        if (entry.Generation > 0)
        {
            if (entry.AbsenceProof is not null) throw Failure("credential-absence-proof-unexpected");
            return;
        }
        var proof = entry.AbsenceProof ?? throw Failure("credential-absence-proof-required"); ValidateProofBinding(proof);
        if (proof.ProfileIdSha256 != hash) throw Failure("credential-absence-proof-binding-invalid");
        if (entry.Pending is { } pending && pending.TransactionId == proof.FirstTransactionId &&
            (pending.NewGeneration != 1 || pending.NewEnvelopeSha256 != proof.FirstCandidateSha256 || pending.NewEnvelopeLength != proof.FirstCandidateLength))
            throw Failure("credential-absence-proof-binding-invalid");
    }
    State ReadScopedState()
    {
        try
        {
            using var document = CredentialRules.Document(authority.ReadFile(Path.Combine(authority.Root, "state.json"), MaximumStateBytes));
            if (!document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object ||
                profiles.EnumerateObject().Take(MaximumProfiles + 1).Count() > MaximumProfiles) throw Failure("credential-rollback-state-limit");
            var state = document.RootElement.Deserialize<ScopedState>(CredentialRules.Json) ?? throw Failure("credential-rollback-state-invalid");
            if (state.Profiles is null || state.Profiles.Values.Any(e => e is null)) throw Failure("credential-rollback-state-invalid");
            return new(state.Schema, state.Revision, state.Profiles.ToDictionary(p => p.Key,
                p => new Entry(p.Value.Generation, p.Value.EnvelopeSha256, p.Value.EnvelopeLength, p.Value.Pending) { AbsenceProof = p.Value.AbsenceProof }, StringComparer.Ordinal), state.Sha256);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw Failure("credential-rollback-metadata-invalid"); }
    }
    static ScopedState ProjectScopedState(State state) => new(state.Schema, state.Revision,
        state.Profiles.ToDictionary(p => p.Key, p => new ScopedEntry(p.Value.Generation, p.Value.EnvelopeSha256, p.Value.EnvelopeLength, p.Value.Pending, p.Value.AbsenceProof), StringComparer.Ordinal), state.Sha256);
    static byte[] SerializeScopedState(State state) => JsonSerializer.SerializeToUtf8Bytes(ProjectScopedState(state), CredentialRules.Json);
    static string ScopedStateDigest(State state, Binding binding) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "platform-studio.credential-rollback-scoped-state.v2", binding.CustomerId, binding.CellId, binding.VaultNamespaceId, binding.AuthorityNamespaceId,
        state.Schema, state.Revision, profiles = ProjectScopedState(state).Profiles.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { id = p.Key, entry = p.Value }).ToArray()
    }, CredentialRules.Json));
    static void WriteEmptyScopedState(CredentialNamespaceCustody custody, Binding binding)
    {
        var state = new State(2, 0, new(StringComparer.Ordinal), ""); state = state with { Sha256 = ScopedStateDigest(state, binding) };
        WriteNew(custody, "state.json", ProjectScopedState(state));
    }
}
