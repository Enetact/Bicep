using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

// Public observations constrain a trusted owner operation; they supply neither credentials nor authority.
public sealed record CredentialVaultNamespaceIdentity(string CustomerId, string CellId, string VaultNamespaceId,
    string NamespaceCommitmentSha256, string? AuthorityNamespaceId, string? AuthorityBindingSha256);

public sealed partial class CredentialVault
{
    public CredentialVaultNamespaceIdentity DescribeNamespace()
    {
        using var gate = custody.OpenGuard();
        using var authorityGate = rollback?.OpenGuard();
        _ = rollback?.ReadState();
        return NamespaceIdentity();
    }

    public async Task<FoundationCredentialMaintenanceTarget> InspectMaintenanceAsync(string profileId,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); cancellationToken = deadline.Token;
        var path = ProfilePath(profileId); using var gate = await Gate(cancellationToken);
        var observed = await ReadObserved(path, cancellationToken); using var secret = observed.Secret;
        return await ObserveMaintenance(path, observed.Envelope.Profile, observed.Envelope.KeyId, observed.Bytes, cancellationToken);
    }

    CredentialVaultNamespaceIdentity NamespaceIdentity()
    {
        custody.ValidateCurrent();
        return new(options.CustomerId, options.CellId, custody.NamespaceId, custody.NamespaceCommitmentSha256,
            rollback?.AuthorityNamespaceId, rollback?.AuthorityBindingSha256);
    }

    async Task<FoundationCredentialMaintenanceTarget> ObserveMaintenance(string path, CredentialProfile profile,
        string sourceKeyId, byte[] authenticatedBytes, CancellationToken cancellationToken)
    {
        using var authorityGate = rollback is null ? null : await RollbackGate(cancellationToken);
        return await ObserveMaintenanceLocked(path, profile, sourceKeyId, authenticatedBytes, cancellationToken);
    }
    async Task<FoundationCredentialMaintenanceTarget> ObserveMaintenanceLocked(string path, CredentialProfile profile,
        string sourceKeyId, byte[] authenticatedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var identity = NamespaceIdentity();
        var current = await custody.ReadFileAsync(path, 131072, cancellationToken);
        if (!current.AsSpan().SequenceEqual(authenticatedBytes)) throw new CredentialException("credential-maintenance-target-changed");
        var state = rollback?.ReadState(); rollback?.RequireCurrent(state!, CredentialRules.Hash(profile.Scope.AuthProfileId), current);
        var scope = CredentialRules.Snapshot(profile.Scope);
        return new(JsonSerializer.Serialize(scope, CredentialRules.Json), CredentialRules.ScopeDigest(scope),
            CredentialRules.Hash(scope.AuthProfileId), identity.VaultNamespaceId, identity.NamespaceCommitmentSha256,
            profile.Generation, profile.Enabled, profile.ExpiresUtc.ToUnixTimeMilliseconds(), HashMaintenance(current), current.Length,
            sourceKeyId, options.EncryptionKeyId, identity.AuthorityNamespaceId, identity.AuthorityBindingSha256,
            state?.Revision ?? 0, state?.Sha256);
    }

    internal async Task<PreparedMaintenance> PrepareMaintenanceAsync(CredentialMaintenanceOperation operation,
        FoundationCredentialMaintenanceTarget expected, CredentialVaultNamespaceIdentity expectedNamespace,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(operation) || expected is null) throw new CredentialException("credential-maintenance-input-invalid");
        var gate = await Gate(cancellationToken);
        try
        {
            if (NamespaceIdentity() != expectedNamespace) throw new CredentialException("credential-maintenance-namespace-changed");
            using var document = CredentialRules.Document(System.Text.Encoding.UTF8.GetBytes(expected.ProfileScopeJson));
            var scope = Bound(document.RootElement.Deserialize<CredentialScope>(CredentialRules.Json)
                ?? throw new CredentialException("credential-maintenance-input-invalid"));
            var path = ProfilePath(scope.AuthProfileId); var observed = await ReadObserved(path, cancellationToken);
            using var material = observed.Secret;
            var actual = await ObserveMaintenance(path, observed.Envelope.Profile, observed.Envelope.KeyId, observed.Bytes, cancellationToken);
            if (actual != expected) throw new CredentialException("credential-maintenance-target-changed");
            if (actual.ProfileGeneration is <= 0 or >= 9007199254740991 ||
                operation == CredentialMaintenanceOperation.Rewrap && actual.SourceKeyId == actual.DestinationKeyId)
                throw new CredentialException("credential-maintenance-operation-invalid");
            var after = observed.Envelope.Profile with { Generation = actual.ProfileGeneration + 1,
                Enabled = operation == CredentialMaintenanceOperation.Revoke ? false : actual.Enabled };
            var candidate = await EncryptMaintenanceCandidate(after, material, cancellationToken);
            try
            {
                var latest = await ObserveMaintenance(path, observed.Envelope.Profile, observed.Envelope.KeyId, observed.Bytes, cancellationToken);
                if (latest != expected) throw new CredentialException("credential-maintenance-target-changed");
                return new(this, gate, operation, actual, observed.Envelope.Profile, path, observed.Bytes, candidate);
            }
            catch { CryptographicOperations.ZeroMemory(candidate); throw; }
        }
        catch { gate.Dispose(); throw; }
    }

    async Task<byte[]> EncryptMaintenanceCandidate(CredentialProfile profile, CredentialSecret material, CancellationToken cancellationToken)
    {
        byte[]? keyBytes = null;
        try
        {
            using var key = await Key(options.EncryptionKeyId, cancellationToken); keyBytes = key.CopyBytes();
            cancellationToken.ThrowIfCancellationRequested();
            return EncryptEnvelopeCandidate(profile, material, options.EncryptionKeyId, keyBytes);
        }
        finally { if (keyBytes is not null) CryptographicOperations.ZeroMemory(keyBytes); }
    }
    static byte[] EncryptEnvelopeCandidate(CredentialProfile profile, CredentialSecret material, string keyId, ReadOnlySpan<byte> keyBytes)
    {
        var clear = material.CopyBytes();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[clear.Length]; var tag = new byte[16];
            using var aes = new AesGcm(keyBytes, 16); aes.Encrypt(nonce, clear, cipher, tag, Aad(keyId, profile));
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, keyId, profile,
                Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag)), CredentialRules.Json);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    static string HashMaintenance(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // No public constructor/session/callback. The concrete store-backed executor owns this retained guard.
    internal sealed class PreparedMaintenance(CredentialVault owner, FileStream gate, CredentialMaintenanceOperation operation,
        FoundationCredentialMaintenanceTarget target, CredentialProfile profile, string path, byte[] before, byte[] candidate) : IDisposable
    {
        int used, disposed;
        internal FoundationCredentialMaintenanceTarget Target => target;
        internal string ExpectedAfterSha256 { get; } = HashMaintenance(candidate);
        internal async Task RevalidateAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref disposed) != 0) throw new CredentialException("credential-maintenance-session-disposed");
            if (await owner.ObserveMaintenance(path, profile, target.SourceKeyId, before, cancellationToken) != target)
                throw new CredentialException("credential-maintenance-target-changed");
        }
        internal async Task<FoundationCredentialMaintenanceResult> ApplyAsync(CredentialMaintenanceAdmission admission,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref used, 1) != 0 ||
                admission.Intent.Operation != operation || admission.Intent.Target != target || admission.ExpectedAfterSha256 != ExpectedAfterSha256)
                throw new CredentialException("credential-maintenance-admission-invalid");
            using var authorityGate = owner.rollback is null ? null : await owner.RollbackGate(cancellationToken);
            if (await owner.ObserveMaintenanceLocked(path, profile, target.SourceKeyId, before, cancellationToken) != target)
                throw new CredentialException("credential-maintenance-target-changed");
            var state = owner.rollback?.ReadState(); string? temporary = Path.Combine(owner.root, ".write-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var file = owner.custody.CreateNew(temporary))
                { await file.WriteAsync(candidate, cancellationToken); await file.FlushAsync(cancellationToken); file.Flush(true); }
                owner.custody.ValidateCurrent(); owner.custody.ValidateFile(temporary); owner.custody.ValidateFile(path);
                cancellationToken.ThrowIfCancellationRequested();
                var pending = owner.rollback?.Begin(state!, target.ProfileIdSha256, before, target.ProfileGeneration + 1, candidate);
                if (pending is not null) owner.RollbackFault?.Invoke("PendingPersisted");
                cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, path, true); temporary = null;
                if (pending is not null) owner.RollbackFault?.Invoke("EnvelopePublished");
                var published = await owner.custody.ReadFileAsync(path, 131072, cancellationToken);
                if (HashMaintenance(published) != ExpectedAfterSha256) throw new CredentialException("credential-maintenance-publication-mismatch");
                if (pending is not null) owner.rollback!.FinalizePublished(target.ProfileIdSha256, pending, published);
                var after = owner.rollback?.ReadState();
                return new(operation, admission.Intent.TargetSha256, target.ProfileGeneration, target.ProfileGeneration + 1,
                    target.EnvelopeSha256, ExpectedAfterSha256, target.NamespaceCommitmentSha256, after?.Revision ?? 0, after?.Sha256);
            }
            finally
            {
                if (temporary is not null && File.Exists(temporary))
                { owner.custody.ValidateCurrent(); owner.custody.ValidateFile(temporary); File.Delete(temporary); }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            CryptographicOperations.ZeroMemory(before); CryptographicOperations.ZeroMemory(candidate); gate.Dispose();
        }
    }
}
