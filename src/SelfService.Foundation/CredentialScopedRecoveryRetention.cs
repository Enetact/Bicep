using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

// Supplied inventory commitments are metadata only. This assessor performs no envelope or proof authentication.
public sealed class CredentialScopedRecoveryRetentionAssessment
{
    readonly byte[] canonical;
    internal CredentialScopedRecoveryRetentionAssessment(CredentialScopedRecoveryInventory current, IEnumerable<string> retained,
        IEnumerable<string> currentKeys, IEnumerable<string> historicalKeys, IEnumerable<CredentialRecoveryKeyRetentionHold> holds)
    {
        CustomerId = current.CustomerId; CellId = current.CellId; VaultNamespaceId = current.VaultNamespaceId;
        NamespaceCommitmentSha256 = current.NamespaceCommitmentSha256; CurrentInventorySha256 = current.Sha256;
        AuthorityNamespaceId = current.AuthorityNamespaceId; AuthorityBindingSha256 = current.AuthorityBindingSha256;
        RetainedInventorySha256 = Array.AsReadOnly(retained.ToArray());
        CurrentReferencedKeyIds = Array.AsReadOnly(currentKeys.ToArray());
        HistoricalReferencedKeyIds = Array.AsReadOnly(historicalKeys.ToArray());
        KeyHolds = Array.AsReadOnly(holds.ToArray());
        canonical = CreateCanonicalPayload();
        if (canonical.Length > CredentialScopedRecoveryRetention.MaximumReportBytes)
            throw new CredentialException("credential-scoped-recovery-retention-report-limit");
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
    public string Schema => "platform-studio.credential-scoped-recovery-retention.v2";
    public string CustomerId { get; }
    public string CellId { get; }
    public string VaultNamespaceId { get; }
    public string NamespaceCommitmentSha256 { get; }
    public string? AuthorityNamespaceId { get; }
    public string? AuthorityBindingSha256 { get; }
    public string CurrentInventorySha256 { get; }
    public ReadOnlyCollection<string> RetainedInventorySha256 { get; }
    public ReadOnlyCollection<string> CurrentReferencedKeyIds { get; }
    public ReadOnlyCollection<string> HistoricalReferencedKeyIds { get; }
    public ReadOnlyCollection<CredentialRecoveryKeyRetentionHold> KeyHolds { get; }
    public string IndependentBackupCoverage => "NotAssessed";
    public bool RetainedInventoryMetadataOnly => true;
    public bool PositiveEnvelopeAuthenticationPerformed => false;
    public bool AbsenceScopeAuthenticationPerformed => false;
    public bool CompleteHistoricalCoverageVerified => false;
    public bool KeyRetirementAuthorized => false;
    public string Sha256 { get; }
    public byte[] CanonicalPayload() => canonical.ToArray();

    byte[] CreateCanonicalPayload()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("schema", Schema);
            writer.WriteString("customerId", CustomerId); writer.WriteString("cellId", CellId);
            writer.WriteString("vaultNamespaceId", VaultNamespaceId); writer.WriteString("namespaceCommitmentSha256", NamespaceCommitmentSha256);
            writer.WriteString("authorityNamespaceId", AuthorityNamespaceId); writer.WriteString("authorityBindingSha256", AuthorityBindingSha256);
            writer.WriteString("currentInventorySha256", CurrentInventorySha256);
            Strings(writer, "retainedInventorySha256", RetainedInventorySha256);
            Strings(writer, "currentReferencedKeyIds", CurrentReferencedKeyIds);
            Strings(writer, "historicalReferencedKeyIds", HistoricalReferencedKeyIds);
            writer.WriteStartArray("keyHolds");
            foreach (var hold in KeyHolds)
            {
                writer.WriteStartObject(); writer.WriteString("keyId", hold.KeyId); writer.WriteString("decision", hold.Decision);
                writer.WriteBoolean("currentReference", hold.CurrentReference);
                Strings(writer, "retainedInventorySha256", hold.RetainedInventorySha256); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteString("independentBackupCoverage", IndependentBackupCoverage);
            writer.WriteBoolean("retainedInventoryMetadataOnly", RetainedInventoryMetadataOnly);
            writer.WriteBoolean("positiveEnvelopeAuthenticationPerformed", PositiveEnvelopeAuthenticationPerformed);
            writer.WriteBoolean("absenceScopeAuthenticationPerformed", AbsenceScopeAuthenticationPerformed);
            writer.WriteBoolean("completeHistoricalCoverageVerified", CompleteHistoricalCoverageVerified);
            writer.WriteBoolean("keyRetirementAuthorized", KeyRetirementAuthorized); writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    { writer.WriteStartArray(name); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
}

public static class CredentialScopedRecoveryRetention
{
    public const int MaximumRetainedInventories = 64;
    public const int MaximumReferencedKeys = 4096;
    public const int MaximumReportBytes = 4 * 1024 * 1024;

    public static CredentialScopedRecoveryRetentionAssessment Assess(CredentialScopedRecoveryInventory current,
        IReadOnlyList<CredentialScopedRecoveryInventory> retainedInventories)
    {
        if (current is null || retainedInventories is null) throw new CredentialException("credential-scoped-recovery-retention-input-invalid");
        var count = retainedInventories.Count;
        if (count is < 0 or > MaximumRetainedInventories) throw new CredentialException("credential-scoped-recovery-retention-inventory-limit");
        current.ValidateIntegrity();
        var retained = new SortedDictionary<string, CredentialScopedRecoveryInventory>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var input = retainedInventories[i] ?? throw new CredentialException("credential-scoped-recovery-retention-input-invalid");
            input.ValidateIntegrity();
            if (input.CustomerId != current.CustomerId || input.CellId != current.CellId || input.VaultNamespaceId != current.VaultNamespaceId ||
                input.NamespaceCommitmentSha256 != current.NamespaceCommitmentSha256 || input.AuthorityNamespaceId != current.AuthorityNamespaceId ||
                input.AuthorityBindingSha256 != current.AuthorityBindingSha256)
                throw new CredentialException("credential-scoped-recovery-retention-scope-mismatch");
            // A duplicate commitment carries no additional history or coverage. Integrity was checked before deduplication.
            if (input.Sha256 == current.Sha256)
            {
                RequireIdenticalContent(current, input); continue;
            }
            if (retained.TryGetValue(input.Sha256, out var prior)) RequireIdenticalContent(prior, input);
            else retained.Add(input.Sha256, input);
        }
        var currentKeys = new SortedSet<string>(current.CurrentRequiredKeyIds, StringComparer.Ordinal);
        var historicalKeys = new SortedSet<string>(StringComparer.Ordinal);
        var references = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var inventory in retained.Values)
            foreach (var key in inventory.CurrentRequiredKeyIds)
            {
                historicalKeys.Add(key);
                if (!references.TryGetValue(key, out var pins)) references.Add(key, pins = new(StringComparer.Ordinal));
                pins.Add(inventory.Sha256);
                if (references.Count > MaximumReferencedKeys) throw new CredentialException("credential-scoped-recovery-retention-key-limit");
            }
        var union = new SortedSet<string>(currentKeys, StringComparer.Ordinal); union.UnionWith(historicalKeys);
        if (union.Count > MaximumReferencedKeys) throw new CredentialException("credential-scoped-recovery-retention-key-limit");
        var holds = union.Select(key => new CredentialRecoveryKeyRetentionHold(key, currentKeys.Contains(key),
            references.TryGetValue(key, out var pins) ? pins : Array.Empty<string>()));
        return new(current, retained.Keys, currentKeys, historicalKeys, holds);
    }

    static void RequireIdenticalContent(CredentialScopedRecoveryInventory left, CredentialScopedRecoveryInventory right)
    {
        if (!left.CanonicalPayload().AsSpan().SequenceEqual(right.CanonicalPayload()))
            throw new CredentialException("credential-scoped-recovery-retention-conflicting-inventory");
    }
}
