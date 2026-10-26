using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    readonly CredentialVaultOptions options;
    readonly ICredentialKeyProvider keys;
    readonly TimeProvider clock;
    readonly string root;
    readonly CredentialNamespaceCustody custody;
    readonly CredentialRollbackAuthority? rollback;
    internal bool ScopedRollbackFormat => rollback?.IsScoped == true;
    // Per-instance synthetic fault seam; not constructor/configuration/request input.
    internal Action<string>? RollbackFault { get; set; }
    sealed record Envelope(int Schema, string KeyId, CredentialProfile Profile, string Nonce, string Ciphertext, string Tag);

    public CredentialVault(CredentialVaultOptions options, ICredentialKeyProvider keys, TimeProvider? clock = null)
    {
        ValidateConfiguration(options, keys);
        this.options = options; this.keys = keys; this.clock = clock ?? TimeProvider.System;
        custody = new(options.RootPath, options.AccessProfile == CredentialVaultAccessProfile.FoundationBoundDispatchOnly ? "credential-vault-scoped-bound-dispatch" : options.RollbackProtection is null ? "credential-vault" :
            options.RollbackProtection.Format == CredentialRollbackFormat.AuthenticatedScopeV2 ? "credential-vault-scoped-fenced" : "credential-vault-fenced", options.CustomerId, options.CellId); root = custody.Root;
        if (options.RollbackProtection is { } protection) rollback = new(custody, protection.AuthorityDirectory, options.CustomerId, options.CellId, protection.Format);
    }

    public static CredentialVault Provision(CredentialVaultOptions options, ICredentialKeyProvider keys, TimeProvider? clock = null)
    {
        ValidateConfiguration(options, keys);
        RequireGeneralProvisionProfile(options);
        if (options.RollbackProtection is not null) throw new CredentialException("credential-rollback-explicit-provision-required");
        CredentialNamespaceCustody.Provision(options.RootPath, "credential-vault", options.CustomerId, options.CellId);
        return new(options, keys, clock);
    }

    public static CredentialVault ProvisionStrong(CredentialVaultOptions options, ICredentialKeyProvider keys, TimeProvider? clock = null)
    {
        ValidateConfiguration(options, keys);
        RequireGeneralProvisionProfile(options);
        if (options.RollbackProtection is not { } protection) throw new CredentialException("credential-rollback-configuration-required");
        if (protection.Format != CredentialRollbackFormat.LegacyV1) throw new CredentialException("credential-rollback-scoped-explicit-provision-required");
        return ProvisionStrongCore(options, keys, clock, scoped: false);
    }

    public static CredentialVault ProvisionStrongScoped(CredentialVaultOptions options, ICredentialKeyProvider keys, TimeProvider? clock = null)
    {
        ValidateConfiguration(options, keys);
        RequireGeneralProvisionProfile(options);
        if (options.RollbackProtection?.Format != CredentialRollbackFormat.AuthenticatedScopeV2)
            throw new CredentialException("credential-rollback-scoped-configuration-required");
        return ProvisionStrongCore(options, keys, clock, scoped: true);
    }
    public static CredentialVault ProvisionStrongBoundDispatch(CredentialVaultOptions options, ICredentialKeyProvider keys, TimeProvider? clock = null)
    {
        ValidateConfiguration(options, keys);
        if (options.AccessProfile != CredentialVaultAccessProfile.FoundationBoundDispatchOnly ||
            options.RollbackProtection?.Format != CredentialRollbackFormat.AuthenticatedScopeV2)
            throw new CredentialException("credential-bound-dispatch-profile-invalid");
        return ProvisionStrongCore(options, keys, clock, scoped: true);
    }
    static CredentialVault ProvisionStrongCore(CredentialVaultOptions options, ICredentialKeyProvider keys, TimeProvider? clock, bool scoped)
    {
        var protection = options.RollbackProtection!;
        var restricted = options.AccessProfile == CredentialVaultAccessProfile.FoundationBoundDispatchOnly;
        var vaultKind = restricted ? "credential-vault-scoped-bound-dispatch" : scoped ? "credential-vault-scoped-fenced" : "credential-vault-fenced";
        var authorityKind = restricted ? "credential-rollback-scoped-bound-authority" : scoped ? "credential-rollback-scoped-authority" : "credential-rollback-authority";
        var vault = CredentialNamespaceCustody.PreflightNew(options.RootPath, vaultKind, options.CustomerId, options.CellId);
        var authority = CredentialNamespaceCustody.PreflightNew(protection.AuthorityDirectory, authorityKind, options.CustomerId, options.CellId);
        if (string.Equals(vault, authority, StringComparison.OrdinalIgnoreCase) || vault.StartsWith(authority + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            authority.StartsWith(vault + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new CredentialException("credential-rollback-roots-must-be-separate");
        CredentialNamespaceCustody.Provision(vault, vaultKind, options.CustomerId, options.CellId);
        CredentialRollbackAuthority.Provision(new(vault, vaultKind, options.CustomerId, options.CellId), authority, options.CustomerId, options.CellId, protection.Format);
        return new(options, keys, clock);
    }

    public async Task<CredentialRollbackStatus> InspectRollbackStateAsync(string profileId, CancellationToken cancellationToken = default)
    {
        _ = ProfilePath(profileId); var authority = rollback ?? throw new CredentialException("credential-rollback-not-enabled");
        using var gate = await Gate(cancellationToken); using var authorityGate = await RollbackGate(cancellationToken);
        return authority.Status(authority.ReadState(), CredentialRules.Hash(profileId));
    }

    public async Task<CredentialRollbackStatus> ReconcilePendingWriteAsync(string profileId, long expectedAuthorityRevision,
        string expectedStateSha256, string expectedPendingSha256, CredentialRollbackResolution disposition, CancellationToken cancellationToken = default)
    {
        var path = ProfilePath(profileId); var authority = rollback ?? throw new CredentialException("credential-rollback-not-enabled");
        if (!Enum.IsDefined(disposition)) throw new CredentialException("credential-rollback-pending-mismatch");
        using var gate = await Gate(cancellationToken); using var authorityGate = await RollbackGate(cancellationToken);
        var state = authority.ReadState(); var hash = CredentialRules.Hash(profileId); var status = authority.Status(state, hash);
        if (status.Pending is null || state.Revision != expectedAuthorityRevision || state.Sha256 != expectedStateSha256 || status.Pending.PendingSha256 != expectedPendingSha256)
            throw new CredentialException("credential-rollback-state-changed");
        await AuthenticateScopedAbsence(state, hash, null, disposition == CredentialRollbackResolution.AbandonUnpublished, cancellationToken);
        byte[]? observed = null;
        CredentialRules.RejectLinks(path);
        if (Path.Exists(path) && !File.Exists(path)) throw new CredentialException("credential-rollback-vault-contents-invalid");
        if (File.Exists(path))
        {
            var authenticated = await ReadObserved(path, cancellationToken, bypassRollback: true); var envelope = authenticated.Envelope; using var secret = authenticated.Secret;
            var expectedGeneration = disposition == CredentialRollbackResolution.AcceptPublished ? status.Pending.NewGeneration : status.Pending.PreviousGeneration;
            if (envelope.Profile.Generation != expectedGeneration) throw new CredentialException("credential-rollback-envelope-mismatch");
            if (ScopedRollbackFormat && state.Profiles[hash].Generation == 0 && state.Profiles[hash].AbsenceProof is { } proof &&
                JsonSerializer.Serialize(envelope.Profile.Scope, CredentialRules.Json) != JsonSerializer.Serialize(proof.Scope, CredentialRules.Json))
                throw new CredentialException("credential-absence-proof-scope-changed");
            observed = authenticated.Bytes;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (ScopedRollbackFormat) await AssertScopedWriteTarget(state, path, observed, cancellationToken);
        authority.Reconcile(state, hash, expectedAuthorityRevision, expectedStateSha256, expectedPendingSha256, disposition, observed);
        return authority.Status(authority.ReadState(), hash);
    }

    static void ValidateConfiguration(CredentialVaultOptions options, ICredentialKeyProvider keys)
    {
        if (options is null || keys is null || !CredentialRules.ProfileId(options.EncryptionKeyId) ||
            !Guid.TryParseExact(options.CustomerId, "D", out var customer) || customer == Guid.Empty ||
            !Guid.TryParseExact(options.CellId, "D", out var cell) || cell == Guid.Empty || string.IsNullOrWhiteSpace(options.RootPath) ||
            !Enum.IsDefined(options.AccessProfile) || options.RollbackProtection is { } protection && !Enum.IsDefined(protection.Format))
            throw new CredentialException("credential-vault-configuration-invalid");
        if (options.AccessProfile == CredentialVaultAccessProfile.FoundationBoundDispatchOnly &&
            options.RollbackProtection?.Format != CredentialRollbackFormat.AuthenticatedScopeV2)
            throw new CredentialException("credential-bound-dispatch-profile-invalid");
    }

    public async Task<CredentialProfile> PutAsync(CredentialScope scope, CredentialSecret material, DateTimeOffset expiresUtc,
        long expectedGeneration = 0, CancellationToken cancellationToken = default)
    {
        scope = Bound(scope); CredentialRules.Token(material.Text());
        if (expiresUtc <= clock.GetUtcNow().AddSeconds(30) || expiresUtc > clock.GetUtcNow().AddHours(24) || expectedGeneration < 0)
            throw new CredentialException("credential-lifetime-invalid");
        using var gate = await Gate(cancellationToken);
        var path = ProfilePath(scope.AuthProfileId); Envelope? existing = null;
        if (File.Exists(path))
        {
            (existing, var old) = await Read(path, cancellationToken); old.Dispose();
            if (!CredentialRules.SameIdentity(existing.Profile.Scope, scope)) throw new CredentialException("credential-profile-retarget-denied");
        }
        if ((existing?.Profile.Generation ?? 0) != expectedGeneration) throw new CredentialException("credential-generation-conflict");
        var profile = new CredentialProfile(scope, scope.Provider == PipelineRequestFactory.GitHub ? "github-installation-token" : "entra-access-token", expiresUtc, expectedGeneration + 1, true);
        await Write(path, profile, material, cancellationToken); return Detached(profile);
    }

    public async Task<CredentialProfile> RevokeAsync(string profileId, long expectedGeneration, CancellationToken cancellationToken = default)
    {
        using var gate = await Gate(cancellationToken);
        var path = ProfilePath(profileId); var (envelope, secret) = await Read(path, cancellationToken); using var material = secret;
        if (envelope.Profile.Generation != expectedGeneration) throw new CredentialException("credential-generation-conflict");
        var revoked = envelope.Profile with { Generation = expectedGeneration + 1, Enabled = false };
        await Write(path, revoked, material, cancellationToken); return Detached(revoked);
    }

    // Trusted custody maintenance only: no token renewal, scope change, provisioning or key retirement.
    public async Task<CredentialProfile> RewrapAsync(string profileId, long expectedGeneration, string expectedSourceKeyId,
        CancellationToken cancellationToken = default)
    {
        if (expectedGeneration <= 0 || expectedGeneration == long.MaxValue || !CredentialRules.ProfileId(expectedSourceKeyId))
            throw new CredentialException("credential-rewrap-input-invalid");
        var path = ProfilePath(profileId);
        using var gate = await Gate(cancellationToken);
        var (envelope, secret) = await Read(path, cancellationToken); using var material = secret;
        if (envelope.Profile.Generation != expectedGeneration) throw new CredentialException("credential-generation-conflict");
        if (envelope.KeyId != expectedSourceKeyId) throw new CredentialException("credential-rewrap-source-key-conflict");
        if (envelope.KeyId == options.EncryptionKeyId) throw new CredentialException("credential-rewrap-same-key");
        var rewrapped = envelope.Profile with { Generation = expectedGeneration + 1 };
        await Write(path, rewrapped, material, cancellationToken); return Detached(rewrapped);
    }

    public async Task<CredentialProfile> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        using var gate = await Gate(cancellationToken); var (envelope, material) = await Read(ProfilePath(profileId), cancellationToken);
        material.Dispose(); return Detached(envelope.Profile);
    }

    public async Task<CredentialLease> ResolveAsync(CredentialScope scope, CancellationToken cancellationToken = default)
    {
        scope = Bound(scope); cancellationToken.ThrowIfCancellationRequested();
        // An unsupported direct reader must not wait behind a real bound operation's asynchronous key lookup.
        if (RequiresBoundDispatchSession) RequireGenericCredentialSourceUse("credential-bound-dispatch-session-required");
        using var gate = await Gate(cancellationToken);
        RequireCredentialReadSession(scope, null);
        var (envelope, material) = await Read(ProfilePath(scope.AuthProfileId), cancellationToken);
        try
        {
            Require(envelope.Profile, scope);
            return new CredentialLease(Detached(envelope.Profile), material);
        }
        catch { material.Dispose(); throw; }
    }

    // Trusted transport-only admission lease. The caller releases the gate immediately after SendAsync invocation,
    // while retaining secret custody until its ordinary lease disposal. Authorizers must not reenter this vault.
    internal async Task<CredentialLease> ResolveForDispatchAsync(CredentialScope scope, CancellationToken cancellationToken,
        FoundationCredentialUseSession? session = null)
    {
        scope = Bound(scope); var gate = await Gate(cancellationToken); CredentialSecret? material = null;
        try
        {
            RequireCredentialReadSession(scope, session);
            var (envelope, secret) = await Read(ProfilePath(scope.AuthProfileId), cancellationToken); material = secret;
            RequireCredentialReadSession(scope, session);
            Require(envelope.Profile, scope); cancellationToken.ThrowIfCancellationRequested();
            return new CredentialLease(Detached(envelope.Profile), material, gate);
        }
        catch { material?.Dispose(); gate.Dispose(); throw; }
    }

    public async Task RevalidateAsync(CredentialScope scope, long generation, CancellationToken cancellationToken = default)
    {
        using var lease = await ResolveAsync(scope, cancellationToken);
        if (lease.Profile.Generation != generation) throw new CredentialException("credential-generation-changed");
    }

    CredentialScope Bound(CredentialScope scope)
    {
        scope = CredentialRules.Snapshot(scope);
        if (scope.CustomerId != options.CustomerId || scope.CellId != options.CellId) throw new CredentialException("credential-customer-binding");
        return scope;
    }
    void Require(CredentialProfile profile, CredentialScope scope)
    {
        if (!profile.Enabled || profile.Generation <= 0 || profile.ExpiresUtc <= clock.GetUtcNow().AddSeconds(15) ||
            CredentialRules.ScopeDigest(profile.Scope) != CredentialRules.ScopeDigest(scope))
            throw new CredentialException("credential-not-current-or-authorized");
    }
    static CredentialProfile Detached(CredentialProfile profile) => profile with { Scope = CredentialRules.Snapshot(profile.Scope) };
    string ProfilePath(string id)
    {
        if (!CredentialRules.ProfileId(id)) throw new CredentialException("credential-profile-invalid");
        var path = Path.Combine(root, CredentialRules.Hash(id) + ".credential.json"); CredentialRules.RejectLinks(path); return path;
    }

    async Task<FileStream> Gate(CancellationToken cancellationToken)
    {
        var until = clock.GetUtcNow().AddSeconds(10); var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return custody.OpenGuard(); }
            catch (IOException) when (++attempts < 100 && clock.GetUtcNow() < until) { await Task.Delay(20, cancellationToken); }
            catch (IOException) { throw new CredentialException("credential-vault-busy"); }
        }
    }
    async Task<(Envelope Envelope, CredentialSecret Secret)> Read(string path, CancellationToken cancellationToken, bool bypassRollback = false)
    {
        var observed = await ReadObserved(path, cancellationToken, bypassRollback);
        return (observed.Envelope, observed.Secret);
    }
    async Task<(Envelope Envelope, CredentialSecret Secret, byte[] Bytes)> ReadObserved(string path, CancellationToken cancellationToken, bool bypassRollback = false)
    {
        CredentialRules.RejectLinks(path);
        using var authorityGate = rollback is not null && !bypassRollback ? await RollbackGate(cancellationToken) : null;
        var bytes = File.Exists(path) ? await custody.ReadFileAsync(path, 131072, cancellationToken) : null;
        if (rollback is not null && !bypassRollback) rollback.RequireCurrent(rollback.ReadState(), Path.GetFileName(path)[..64], bytes);
        if (bytes is null) throw new CredentialException("credential-profile-not-found");
        var authenticated = await AuthenticateEnvelopeBytes(bytes, Path.GetFileName(path)[..64], options.CustomerId, options.CellId, keys, cancellationToken);
        try
        {
            if (Path.GetFileName(path) != CredentialRules.Hash(authenticated.Envelope.Profile.Scope.AuthProfileId) + ".credential.json")
                throw new CredentialException("credential-envelope-integrity");
            custody.ValidateCurrent(); custody.ValidateFile(path);
            return (authenticated.Envelope, authenticated.Secret, bytes);
        }
        catch { authenticated.Secret.Dispose(); throw; }
    }
    async Task Write(string path, CredentialProfile profile, CredentialSecret material, CancellationToken cancellationToken)
    {
        using var authorityGate = rollback is null ? null : await RollbackGate(cancellationToken);
        var state = rollback?.ReadState();
        var previous = rollback is not null && File.Exists(path) ? await custody.ReadFileAsync(path, 131072, cancellationToken) : null;
        if (rollback is not null) rollback.RequireCurrent(state!, CredentialRules.Hash(profile.Scope.AuthProfileId), previous, permitAbsent: true);
        var hash = CredentialRules.Hash(profile.Scope.AuthProfileId);
        if (ScopedRollbackFormat) await AuthenticateScopedAbsence(state!, hash, profile.Scope, mustBeAbsent: true, cancellationToken);
        byte[]? frozenCandidateKey = null; byte[] candidate;
        if (ScopedRollbackFormat)
        {
            using var key = await Key(options.EncryptionKeyId, cancellationToken); frozenCandidateKey = key.CopyBytes();
            try
            {
                await AssertScopedWriteTarget(state!, path, previous, cancellationToken);
                candidate = EncryptEnvelopeCandidate(profile, material, options.EncryptionKeyId, frozenCandidateKey);
            }
            catch { CryptographicOperations.ZeroMemory(frozenCandidateKey); throw; }
        }
        else candidate = await EncryptMaintenanceCandidate(profile, material, cancellationToken);
        string? temporary = null;
        try
        {
            temporary = Path.Combine(root, ".write-" + Guid.NewGuid().ToString("N")); CredentialRules.RejectLinks(temporary);
            await using (var file = custody.CreateNew(temporary))
            {
                await file.WriteAsync(candidate, cancellationToken); await file.FlushAsync(cancellationToken); file.Flush(flushToDisk: true);
            }
            custody.ValidateCurrent(); custody.ValidateFile(temporary); CredentialRules.RejectLinks(path);
            if (File.Exists(path)) custody.ValidateFile(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (ScopedRollbackFormat) await AssertScopedWriteTarget(state!, path, previous, cancellationToken);
            var pending = rollback?.Begin(state!, hash, previous, profile.Generation, candidate, profile.Scope, options.EncryptionKeyId, frozenCandidateKey);
            var pendingState = ScopedRollbackFormat && pending is not null ? rollback!.ReadState() : null;
            if (pending is not null) RollbackFault?.Invoke("PendingPersisted");
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true); temporary = null;
            if (pending is not null)
            {
                RollbackFault?.Invoke("EnvelopePublished");
                var published = await custody.ReadFileAsync(path, 131072, cancellationToken);
                if (pendingState is not null) rollback!.AssertUnchanged(pendingState);
                rollback!.FinalizePublished(hash, pending, published);
            }
        }
        finally
        {
            if (frozenCandidateKey is not null) CryptographicOperations.ZeroMemory(frozenCandidateKey);
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }
    async Task AuthenticateScopedAbsence(CredentialRollbackAuthority.State state, string hash, CredentialScope? expectedScope,
        bool mustBeAbsent, CancellationToken cancellationToken)
    {
        if (!ScopedRollbackFormat || !state.Profiles.TryGetValue(hash, out var entry) || entry.Generation > 0) return;
        var proof = entry.AbsenceProof ?? throw new CredentialException("credential-absence-proof-required"); rollback!.ValidateProofBinding(proof);
        if (expectedScope is not null && JsonSerializer.Serialize(expectedScope, CredentialRules.Json) != JsonSerializer.Serialize(proof.Scope, CredentialRules.Json))
            throw new CredentialException("credential-absence-proof-scope-changed");
        var path = Path.Combine(root, hash + ".credential.json");
        void RequireAbsence()
        {
            CredentialRules.RejectLinks(path);
            if (mustBeAbsent && Path.Exists(path)) throw new CredentialException("credential-rollback-envelope-mismatch");
        }
        RequireAbsence(); using var key = await Key(proof.KeyId, cancellationToken); var bytes = key.CopyBytes();
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); rollback.AssertUnchanged(state); RequireAbsence();
            CredentialAbsenceScopeProofs.Authenticate(proof, bytes); rollback.AssertUnchanged(state);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    async Task AssertScopedWriteTarget(CredentialRollbackAuthority.State state, string path, byte[]? expected, CancellationToken cancellationToken)
    {
        rollback!.AssertUnchanged(state); CredentialRules.RejectLinks(path);
        if (expected is null)
        {
            if (Path.Exists(path)) throw new CredentialException("credential-rollback-envelope-mismatch");
            return;
        }
        var actual = await custody.ReadFileAsync(path, 131072, cancellationToken);
        try
        {
            rollback.AssertUnchanged(state);
            if (!actual.AsSpan().SequenceEqual(expected)) throw new CredentialException("credential-rollback-envelope-mismatch");
        }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }
    async Task<FileStream> RollbackGate(CancellationToken cancellationToken)
    {
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return rollback!.OpenGuard(); }
            catch (IOException) when (++attempts < 100) { await Task.Delay(20, cancellationToken); }
            catch (IOException) { throw new CredentialException("credential-rollback-authority-busy"); }
        }
    }
    async ValueTask<CredentialEncryptionKey> Key(string keyId, CancellationToken cancellationToken)
    {
        try { return await keys.GetKeyAsync(options.CustomerId, options.CellId, keyId, cancellationToken) ?? throw new CredentialException("credential-key-unavailable"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new CredentialException("credential-key-unavailable"); }
    }
    static byte[] Aad(string keyId, CredentialProfile profile) => JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, keyId, profile }, CredentialRules.Json);
}
