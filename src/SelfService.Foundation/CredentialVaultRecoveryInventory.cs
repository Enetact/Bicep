using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    // Explicit trusted-owner composition only. This whole-cell inventory supplies no product/cloud/retirement authority.
    public async Task<CredentialRecoveryInventory> InspectRecoveryDependenciesAsync(CredentialVaultNamespaceIdentity expectedNamespace,
        CancellationToken cancellationToken = default)
    {
        RequireRecoverySourceProfile("inventory");
        if (ScopedRollbackFormat) throw InventoryFailure("scoped-format-unsupported");
        if (expectedNamespace is null || expectedNamespace.CustomerId != options.CustomerId || expectedNamespace.CellId != options.CellId)
            throw InventoryFailure("namespace-mismatch");
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken);
        var operationToken = deadline.Token;
        try
        {
            operationToken.ThrowIfCancellationRequested(); using var gate = await Gate(operationToken);
            using var authorityGate = rollback is null ? null : await RollbackGate(operationToken);
            var identity = NamespaceIdentity(); if (identity != expectedNamespace) throw InventoryFailure("namespace-mismatch");
            var authorityCustody = rollback is null ? null : new CredentialNamespaceCustody(options.RollbackProtection!.AuthorityDirectory,
                "credential-rollback-authority", options.CustomerId, options.CellId);
            if (authorityCustody is not null && authorityCustody.NamespaceId != identity.AuthorityNamespaceId) throw InventoryFailure("namespace-mismatch");
            var before = await CaptureRecoveryInventory(authorityCustody, operationToken);
            var state = rollback?.ReadState(); ValidateRecoveryCommitments(before, state);
            var profiles = new List<CredentialRecoveryProfileDependency>(before.ProfileNames.Count);
            foreach (var name in before.ProfileNames)
            {
                operationToken.ThrowIfCancellationRequested();
                // Both custody guards are already retained. Bypass skips nested authority locking, never AES authentication.
                var observed = await ReadObserved(Path.Combine(root, name), operationToken, bypassRollback: true);
                using var material = observed.Secret;
                try
                {
                    operationToken.ThrowIfCancellationRequested();
                    var captured = before.Files["vault/" + name]; var envelopeHash = HashMaintenance(observed.Bytes);
                    if (observed.Bytes.Length != captured.Length || envelopeHash != captured.Sha256) throw InventoryFailure("changed");
                    var profile = observed.Envelope.Profile; var scope = CredentialRules.Snapshot(profile.Scope);
                    var dependency = new CredentialRecoveryProfileDependency(CredentialRules.Hash(scope.AuthProfileId), CredentialRules.ScopeDigest(scope),
                        scope.ProjectId, scope.EnvironmentId, profile.Generation, profile.Enabled, profile.ExpiresUtc.ToUnixTimeMilliseconds(),
                        observed.Envelope.KeyId, envelopeHash, observed.Bytes.Length);
                    CredentialRecoveryInventory.ValidateProfile(dependency);
                    if (state is not null && state.Profiles[dependency.ProfileIdSha256].Generation != dependency.Generation) throw InventoryFailure("state-invalid");
                    profiles.Add(dependency);
                }
                finally { CryptographicOperations.ZeroMemory(observed.Bytes); }
            }
            // Every await is followed by a complete source-byte/name/custody check before a public report can exist.
            var after = await CaptureRecoveryInventory(authorityCustody, operationToken);
            var finalIdentity = NamespaceIdentity(); var finalState = rollback?.ReadState(); ValidateRecoveryCommitments(after, finalState);
            if (finalIdentity != identity || finalIdentity != expectedNamespace || !before.Files.SequenceEqual(after.Files) ||
                state?.Revision != finalState?.Revision || state?.Sha256 != finalState?.Sha256) throw InventoryFailure("changed");
            operationToken.ThrowIfCancellationRequested();
            var observedUtc = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            return new(finalIdentity, observedUtc, profiles, finalState?.Revision ?? 0, finalState?.Sha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Credential recovery inventory canceled.", cancellationToken); }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        { throw InventoryFailure("timeout"); }
        catch (OperationCanceledException) { throw InventoryFailure("unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            NotSupportedException or System.Security.SecurityException or FoundationStoreException)
        { throw InventoryFailure("unavailable"); }
    }

    sealed record RecoveryInventoryFile(long Length, string Sha256);
    sealed record RecoveryInventoryCapture(SortedDictionary<string, RecoveryInventoryFile> Files, IReadOnlyList<string> ProfileNames);
    sealed class RecoveryInventoryBudget { internal int Entries; internal long Bytes; }

    async Task<RecoveryInventoryCapture> CaptureRecoveryInventory(CredentialNamespaceCustody? authorityCustody, CancellationToken cancellationToken)
    {
        var files = new SortedDictionary<string, RecoveryInventoryFile>(StringComparer.Ordinal); var profiles = new List<string>(); var budget = new RecoveryInventoryBudget();
        await CaptureRecoveryRoot(custody, "vault/", rollback is null ? [CredentialNamespaceCustody.MarkerName, ".vault.lock"] :
            [CredentialNamespaceCustody.MarkerName, ".vault.lock", "rollback-binding.json"], allowProfiles: true, files, profiles, budget, cancellationToken);
        if (authorityCustody is not null)
            await CaptureRecoveryRoot(authorityCustody, "authority/", [CredentialNamespaceCustody.MarkerName, ".rollback.lock", "authority.json", "state.json"],
                allowProfiles: false, files, profiles, budget, cancellationToken);
        return new(files, Array.AsReadOnly(profiles.Order(StringComparer.Ordinal).ToArray()));
    }

    static async Task CaptureRecoveryRoot(CredentialNamespaceCustody source, string prefix, string[] requiredNames, bool allowProfiles,
        SortedDictionary<string, RecoveryInventoryFile> files, List<string> profiles, RecoveryInventoryBudget budget, CancellationToken cancellationToken)
    {
        source.ValidateCurrent(); var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(source.Root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++budget.Entries > CredentialRecoveryInventory.MaximumEntries) throw InventoryFailure("capacity-exceeded");
            var name = Path.GetFileName(path); var isProfile = allowProfiles && Regex.IsMatch(name, "\\A[a-f0-9]{64}\\.credential\\.json\\z", RegexOptions.CultureInvariant);
            if (!found.Add(name) || !isProfile && !requiredNames.Contains(name, StringComparer.Ordinal)) throw InventoryFailure("contents-invalid");
            source.ValidateFile(path);
            if (isProfile && profiles.Count >= CredentialRecoveryInventory.MaximumProfiles) throw InventoryFailure("capacity-exceeded");
            long length; string hash;
            if (name is ".vault.lock" or ".rollback.lock")
            {
                // Our retained exclusive guard excludes writers; inspecting its zero length does not reopen the held file.
                length = new FileInfo(path).Length; if (length != 0) throw InventoryFailure("contents-invalid"); hash = HashMaintenance([]);
            }
            else
            {
                var maximum = isProfile ? 131072 : name == "state.json" ? 1048576 : 16384;
                var bytes = await source.ReadFileAsync(path, maximum, cancellationToken);
                try { length = bytes.Length; hash = HashMaintenance(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            budget.Bytes += length; if (budget.Bytes > CredentialRecoveryInventory.MaximumCustodyBytes) throw InventoryFailure("capacity-exceeded");
            files.Add(prefix + name, new(length, hash)); if (isProfile) profiles.Add(name);
        }
        if (requiredNames.Any(name => !found.Contains(name))) throw InventoryFailure("contents-invalid");
    }

    static void ValidateRecoveryCommitments(RecoveryInventoryCapture captured, CredentialRollbackAuthority.State? state)
    {
        if (state is null) return;
        if (state.Profiles.Values.Any(entry => entry.Pending is not null)) throw InventoryFailure("pending-reconciliation");
        var current = state.Profiles.Where(pair => pair.Value.Generation > 0).ToArray();
        if (current.Length != captured.ProfileNames.Count) throw InventoryFailure("state-invalid");
        foreach (var pair in current)
            if (!captured.Files.TryGetValue("vault/" + pair.Key + ".credential.json", out var file) ||
                file.Sha256 != pair.Value.EnvelopeSha256 || file.Length != pair.Value.EnvelopeLength) throw InventoryFailure("state-invalid");
    }
    static CredentialException InventoryFailure(string suffix) => new("credential-recovery-inventory-" + suffix);
}
