using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed class CredentialScopedRecoveryStageExecutor
{
    readonly ICredentialKeyProvider sourceKeys, destinationKeys; readonly TimeProvider clock;
    public CredentialScopedRecoveryStageExecutor(ICredentialKeyProvider sourceKeys, ICredentialKeyProvider destinationKeys, TimeProvider? timeProvider = null)
    { this.sourceKeys = sourceKeys ?? throw CredentialScopedRecoveryStaging.Failure("configuration-invalid"); this.destinationKeys = destinationKeys ?? throw CredentialScopedRecoveryStaging.Failure("configuration-invalid"); clock = timeProvider ?? TimeProvider.System; }
    public async Task<CredentialScopedRecoveryStageReceipt> StageAsync(CredentialScopedRecoverySet set, CredentialScopedRecoveryStagePlan plan,
        string reviewedPlanSha256, RSA pinnedPublicKey, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        var prepared = new List<PreparedCredentialRecoveryStageEnvelope>(); var reserved = false;
        try
        {
            if (!OperatingSystem.IsWindows()) throw CredentialScopedRecoveryStaging.Failure("windows-required");
            token.ThrowIfCancellationRequested(); CredentialScopedRecoveryStaging.ValidatePlan(plan, reviewedPlanSha256); CredentialScopedRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow());
            _ = CredentialRecoveryStaging.PreflightDestination(plan.RootPath, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId);
            using var retained = CredentialScopedRecoverySets.ValidateRetainedSet(set, plan.SourceSetAuthority, pinnedPublicKey, plan.SourceNamespace, clock);
            if (retained.Verification.Signature != plan.SourceSignature || retained.Verification.Inventory.Sha256 != plan.SourceInventory.Sha256) throw CredentialScopedRecoveryStaging.Failure("source-selection-mismatch");
            using var destinationKey = await CredentialRecoveryStaging.ResolvePinnedDestinationKeyAsync(plan.DestinationKey, destinationKeys, token);
            using var cache = new CredentialScopedStageKeyCache(plan.SourceNamespace, sourceKeys); long bytes = 0;
            foreach (var envelope in retained.Envelopes)
            {
                token.ThrowIfCancellationRequested();
                var candidate = await CredentialVault.PrepareScopedRecoveryStageEnvelopeAsync(envelope.EncryptedBytes, envelope.ProfileIdSha256, plan, cache, destinationKey, token);
                prepared.Add(candidate); bytes += candidate.Bytes.Length;
                if (prepared.Count > CredentialScopedRecoveryStaging.MaximumProfiles || bytes > CredentialScopedRecoveryStaging.MaximumStageBytes) throw CredentialScopedRecoveryStaging.Failure("capacity-exceeded");
                var original = plan.SourceInventory.ProfileDependencies.Single(p => p.ProfileIdSha256 == envelope.ProfileIdSha256);
                if (JsonSerializer.Serialize(original, CredentialRules.Json) != JsonSerializer.Serialize(candidate.Profile.Source, CredentialRules.Json)) throw CredentialScopedRecoveryStaging.Failure("source-profile-mismatch");
                var actual = await CredentialVault.AuthenticateScopedStageEnvelopeAsync(candidate.Bytes, envelope.ProfileIdSha256, plan, destinationKey, token);
                if (actual.PreservedProfileSha256 != candidate.Profile.PreservedProfileSha256 || JsonSerializer.Serialize(actual.Dependency, CredentialRules.Json) != JsonSerializer.Serialize(candidate.Profile.Destination, CredentialRules.Json)) throw CredentialScopedRecoveryStaging.Failure("destination-profile-mismatch");
            }
            foreach (var proof in retained.AbsenceProofs)
            {
                using var key = await cache.GetKeyAsync(plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId, proof.KeyId, token); var material = key.CopyBytes();
                try { CredentialAbsenceScopeProofs.Authenticate(proof, material); } finally { CryptographicOperations.ZeroMemory(material); }
                token.ThrowIfCancellationRequested();
            }
            if (prepared.Count != plan.PreservedProfiles.Count || retained.AbsenceProofs.Count != plan.AbsenceScopes.Count) throw CredentialScopedRecoveryStaging.Failure("source-selection-mismatch");
            token.ThrowIfCancellationRequested(); CredentialScopedRecoveryStaging.ValidatePlan(plan, reviewedPlanSha256); CredentialScopedRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow());
            _ = CredentialRecoveryStaging.PreflightDestination(plan.RootPath, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId);
            using (var current = WindowsIdentity.GetCurrent())
                if (!CredentialNamespaceCustody.CreateDirectory(plan.RootPath, current.User ?? throw CredentialScopedRecoveryStaging.Failure("owner-unavailable"))) throw CredentialScopedRecoveryStaging.Failure("reservation-conflict");
            reserved = true; CredentialRecoveryStaging.CheckPrivate(plan.RootPath, true);
            using var guard = CredentialNamespaceCustody.CreateFile(Path.Combine(plan.RootPath, CredentialScopedRecoveryStaging.GuardName)); guard.Flush(true);
            var reservedUtc = CredentialScopedRecoveryStaging.At(clock.GetUtcNow()); CredentialScopedRecoveryStaging.ValidateExecutionTime(plan, reservedUtc);
            var ids = new HashSet<string>(StringComparer.Ordinal) { plan.SourceNamespace.VaultNamespaceId, plan.SourceNamespace.AuthorityNamespaceId! };
            string NewId() { string id; do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id)); return id; }
            var stageId = NewId(); var vaultId = NewId(); var authorityId = NewId();
            var marker = new CredentialScopedRecoveryStaging.Reservation(2, "credential-scoped-recovery-stage-reservation", stageId, plan.Sha256, plan.SourceSetAuthority.ArtifactSha256,
                vaultId, authorityId, plan.DestinationRootCommitmentSha256, plan.DestinationKey, reservedUtc, true, "");
            CredentialScopedRecoveryStaging.WriteNew(plan.RootPath, CredentialScopedRecoveryStaging.MarkerName, marker with { Sha256 = CredentialScopedRecoveryStaging.ReservationDigest(marker) });
            CredentialRecoveryStaging.CreateStageDirectory(Path.Combine(plan.RootPath, "profiles")); CredentialRecoveryStaging.CreateStageDirectory(Path.Combine(plan.RootPath, "rollback"));
            foreach (var candidate in prepared.OrderBy(p => p.Profile.Destination.ProfileIdSha256, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested(); CredentialRecoveryStaging.WriteNewBytes(plan.RootPath, "profiles/" + candidate.Profile.Destination.ProfileIdSha256 + ".credential.json", candidate.Bytes);
                var actual = CredentialRecoveryStaging.ReadWrittenEnvelope(plan.RootPath, candidate.Profile.Destination.ProfileIdSha256);
                try { if (!actual.AsSpan().SequenceEqual(candidate.Bytes)) throw CredentialScopedRecoveryStaging.Failure("stage-changed"); }
                finally { CryptographicOperations.ZeroMemory(actual); }
            }
            var binding = new CredentialScopedRecoveryStaging.Binding(2, "credential-scoped-recovery-stage-binding", stageId, plan.Sha256, plan.SourceSetAuthority.ArtifactSha256,
                plan.SourceNamespace, vaultId, authorityId, plan.DestinationRootCommitmentSha256, plan.DestinationKey, true, "");
            binding = binding with { Sha256 = CredentialScopedRecoveryStaging.BindingDigest(binding) };
            var profiles = prepared.OrderBy(p => p.Profile.Source.ProfileIdSha256, StringComparer.Ordinal).Select(p => p.Profile).ToArray();
            var completed = CredentialScopedRecoveryStaging.At(clock.GetUtcNow()); CredentialScopedRecoveryStaging.ValidateExecutionTime(plan, completed);
            var core = CredentialScopedRecoveryStaging.CompletionCoreDigest(plan, stageId, vaultId, authorityId, binding.Sha256, reservedUtc, completed, profiles);
            var proofs = retained.AbsenceProofs.Select(p => CredentialScopedStageProofs.Create(p, plan, stageId, vaultId, authorityId, binding.Sha256, core, destinationKey)).ToArray();
            var entries = profiles.Select(p => new CredentialScopedRecoveryStaging.Entry(p.Destination.ProfileIdSha256, p.Destination.Generation, p.Destination.EnvelopeSha256, p.Destination.EnvelopeLength, null))
                .Concat(proofs.Select(p => new CredentialScopedRecoveryStaging.Entry(p.ProfileIdSha256, 0, "", 0, p))).OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal).ToArray();
            var state = new CredentialScopedRecoveryStaging.State(2, "credential-scoped-recovery-stage-state", stageId, plan.Sha256, vaultId, authorityId, 0, core, entries, true, "");
            state = state with { Sha256 = CredentialScopedRecoveryStaging.StateDigest(state) };
            CredentialScopedRecoveryStaging.WriteNew(plan.RootPath, "rollback/binding.json", binding); CredentialScopedRecoveryStaging.WriteNew(plan.RootPath, "rollback/state.json", state);
            var absences = proofs.OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal).Select(p => new CredentialScopedRecoveryStagedAbsence(
                plan.AbsenceScopes.Single(s => s.ProfileIdSha256 == p.ProfileIdSha256), CredentialScopedStageProofs.Projection(p))).ToArray();
            var receipt = new CredentialScopedRecoveryStageReceipt(plan.Sha256, plan.SourceSetAuthority.ArtifactSha256, stageId, vaultId, authorityId,
                plan.DestinationRootCommitmentSha256, plan.DestinationKey, binding.Sha256, state.Sha256, core, reservedUtc, completed, profiles, absences);
            token.ThrowIfCancellationRequested(); CredentialScopedRecoveryStaging.ValidateCompletedReceiptTime(plan, receipt, clock.GetUtcNow()); CredentialScopedRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow());
            CredentialScopedRecoveryStaging.WriteNew(plan.RootPath, "completion.json", receipt);
            foreach (var proof in CredentialScopedRecoveryStaging.ReadWrittenProofs(plan, receipt)) CredentialScopedStageProofs.Authenticate(proof, destinationKey);
            token.ThrowIfCancellationRequested(); CredentialScopedRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow()); return receipt;
        }
        catch (Exception) when (reserved) { throw CredentialScopedRecoveryStaging.Failure("outcome-unknown"); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Scoped credential staging canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw CredentialScopedRecoveryStaging.Failure("timeout"); }
        catch (OperationCanceledException) { throw CredentialScopedRecoveryStaging.Failure("source-unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw CredentialScopedRecoveryStaging.Failure("source-unavailable"); }
        finally { foreach (var candidate in prepared) candidate.Dispose(); }
    }
}

internal sealed class CredentialScopedStageKeyCache(CredentialVaultNamespaceIdentity identity, ICredentialKeyProvider source) : ICredentialKeyProvider, IDisposable
{
    readonly Dictionary<string, CredentialEncryptionKey> keys = new(StringComparer.Ordinal);
    public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (customerId != identity.CustomerId || cellId != identity.CellId) throw CredentialScopedRecoveryStaging.Failure("source-key-binding-invalid");
        if (!keys.TryGetValue(keyId, out var cached))
        {
            CredentialEncryptionKey borrowed;
            try { borrowed = await source.GetKeyAsync(customerId, cellId, keyId, token) ?? throw CredentialScopedRecoveryStaging.Failure("source-key-unavailable"); }
            catch (OperationCanceledException) { throw; } catch { throw CredentialScopedRecoveryStaging.Failure("source-key-unavailable"); }
            using (borrowed)
            {
                token.ThrowIfCancellationRequested(); var bytes = borrowed.CopyBytes();
                try { cached = new(bytes); keys.Add(keyId, cached); } finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        var clone = cached.CopyBytes(); try { return new CredentialEncryptionKey(clone); } finally { CryptographicOperations.ZeroMemory(clone); }
    }
    public void Dispose() { foreach (var key in keys.Values) key.Dispose(); }
}
