using System.Security.Cryptography;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    public async Task<CredentialRecoverySet> CaptureSignedRecoverySetAsync(CredentialVaultNamespaceIdentity expectedNamespace,
        RSA signer, string signerKeyId, long generation, DateTimeOffset notBeforeUtc, DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        if (ScopedRollbackFormat) throw CredentialRecoverySets.Failure("scoped-format-unsupported");
        CredentialRecoverySets.RequireIdentity(expectedNamespace);
        if (expectedNamespace.CustomerId != options.CustomerId || expectedNamespace.CellId != options.CellId)
            throw CredentialRecoverySets.Failure("namespace-mismatch");
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken);
        var token = deadline.Token; var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal); CredentialRecoverySet? result = null;
        try
        {
            token.ThrowIfCancellationRequested();
            // Validate fixed signer/shape before any source-key lookup. No private-key bytes are exported.
            using var publicKey = FoundationStore.ValidateBackupSigner(signer);
            {
                var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
                CredentialRecoverySets.RequireSignature(new(1, "credential-recovery-set", Guid.NewGuid().ToString("N"), options.CustomerId,
                    options.CellId, expectedNamespace, generation, now, now, notBeforeUtc, expiresUtc, new string('a', 64), 0,
                    new string('b', 64), 2, signerKeyId, FoundationAuditAnchors.PublicKeyFingerprint(publicKey), "RSA-PSS-SHA256", ""), allowUnsigned: true);
            }
            using var gate = await Gate(token); using var authorityGate = rollback is null ? null : await RollbackGate(token);
            var identity = NamespaceIdentity(); if (identity != expectedNamespace) throw CredentialRecoverySets.Failure("namespace-mismatch");
            var authorityCustody = rollback is null ? null : new CredentialNamespaceCustody(options.RollbackProtection!.AuthorityDirectory,
                "credential-rollback-authority", options.CustomerId, options.CellId);
            var before = await CaptureRecoveryInventory(authorityCustody, token); var state = rollback?.ReadState(); ValidateRecoveryCommitments(before, state);
            foreach (var pair in before.Files)
            {
                token.ThrowIfCancellationRequested(); var separator = pair.Key.IndexOf('/'); var name = pair.Key[(separator + 1)..];
                var source = pair.Key.StartsWith("vault/", StringComparison.Ordinal) ? custody : authorityCustody ?? throw CredentialRecoverySets.Failure("contents-invalid");
                var bytes = name is ".vault.lock" or ".rollback.lock" ? [] : await source.ReadFileAsync(Path.Combine(source.Root, name),
                    name == "state.json" ? 1048576 : name.EndsWith(".credential.json", StringComparison.Ordinal) ? 131072 : 16384, token);
                files.Add(pair.Key, bytes);
                if (bytes.Length != pair.Value.Length || CredentialRecoverySets.Hash(bytes) != pair.Value.Sha256) throw CredentialRecoverySets.Failure("changed");
            }
            var profiles = new List<CredentialRecoveryProfileDependency>(before.ProfileNames.Count);
            foreach (var name in before.ProfileNames)
            {
                token.ThrowIfCancellationRequested();
                var dependency = await AuthenticateRecoveryEnvelopeAsync(files["vault/" + name], name[..64], identity, keys, token);
                if (state is not null && state.Profiles[dependency.ProfileIdSha256].Generation != dependency.Generation) throw CredentialRecoverySets.Failure("contents-invalid");
                profiles.Add(dependency);
            }
            await RequireUnchanged();
            var observed = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            var inventory = new CredentialRecoveryInventory(identity, observed, profiles, state?.Revision ?? 0, state?.Sha256);
            result = CredentialRecoverySets.Create(inventory, files, signer, publicKey, signerKeyId, generation, observed, notBeforeUtc, expiresUtc);
            await RequireUnchanged(); token.ThrowIfCancellationRequested();
            CredentialRecoverySets.ValidateLifetime(result.Signature, clock);
            var captured = result; result = null; return captured;

            async Task RequireUnchanged()
            {
                var after = await CaptureRecoveryInventory(authorityCustody, token); var current = rollback?.ReadState(); ValidateRecoveryCommitments(after, current);
                if (NamespaceIdentity() != identity || identity != expectedNamespace || !before.Files.SequenceEqual(after.Files) ||
                    state?.Revision != current?.Revision || state?.Sha256 != current?.Sha256) throw CredentialRecoverySets.Failure("changed");
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Credential recovery capture canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw CredentialRecoverySets.Failure("timeout"); }
        catch (OperationCanceledException) { throw CredentialRecoverySets.Failure("unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            NotSupportedException or System.Security.SecurityException or CryptographicException or FoundationStoreException)
        { throw CredentialRecoverySets.Failure("unavailable"); }
        finally { result?.Dispose(); foreach (var bytes in files.Values) CryptographicOperations.ZeroMemory(bytes); }
    }
}
