using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed class CredentialRestrictedRecoveryStageExecutor
{
    readonly ICredentialKeyProvider sourceKeys, destinationKeys; readonly TimeProvider clock;
    public CredentialRestrictedRecoveryStageExecutor(ICredentialKeyProvider sourceKeys, ICredentialKeyProvider destinationKeys, TimeProvider? timeProvider = null)
    { this.sourceKeys = sourceKeys ?? throw CredentialRestrictedRecoveryStaging.Failure("configuration-invalid"); this.destinationKeys = destinationKeys ?? throw CredentialRestrictedRecoveryStaging.Failure("configuration-invalid"); clock = timeProvider ?? TimeProvider.System; }
    public async Task<CredentialRestrictedRecoveryStageReceipt> StageAsync(CredentialRestrictedRecoverySet set, CredentialRestrictedRecoveryStagePlan plan,
        string reviewedPlanSha256, RSA pinnedPublicKey, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        var prepared = new List<PreparedCredentialRecoveryStageEnvelope>(); var reserved = false;
        try
        {
            if (!OperatingSystem.IsWindows()) throw CredentialRestrictedRecoveryStaging.Failure("windows-required");
            token.ThrowIfCancellationRequested(); CredentialRestrictedRecoveryStaging.ValidatePlan(plan, reviewedPlanSha256); CredentialRestrictedRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow());
            _ = CredentialRecoveryStaging.PreflightDestination(plan.RootPath, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId);
            using var retained = CredentialRestrictedRecoverySets.ValidateRetainedSet(set, plan.SourceSetAuthority, pinnedPublicKey, plan.SourceNamespace, clock);
            if (retained.Verification.Signature != plan.SourceSignature || retained.Verification.Inventory.Sha256 != plan.SourceInventory.Sha256) throw CredentialRestrictedRecoveryStaging.Failure("source-selection-mismatch");
            var ids = new HashSet<string>(StringComparer.Ordinal) { plan.SourceNamespace.VaultNamespaceId, plan.SourceNamespace.AuthorityNamespaceId! };
            string NewId() { string id; do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id)); return id; }
            var stageId = NewId(); var vaultId = NewId(); var authorityId = NewId();
            var binding = new CredentialRestrictedRecoveryStaging.Binding(3, "credential-restricted-recovery-stage-binding", CredentialRestrictedRecoveryStaging.Profile,
                stageId, plan.Sha256, plan.SourceSetAuthority.ArtifactSha256, plan.SourceNamespace, vaultId, authorityId,
                plan.DestinationRootCommitmentSha256, plan.DestinationKey, true, "");
            binding = binding with { Sha256 = CredentialRestrictedRecoveryStaging.BindingDigest(binding) };
            var context = new CredentialRestrictedStageEnvelopeContext(stageId, vaultId, authorityId, binding.Sha256);
            using var destinationKey = await CredentialRestrictedRecoveryStaging.ResolvePinnedDestinationKeyAsync(plan.DestinationKey, destinationKeys, token);
            using var cache = new CredentialRestrictedStageKeyCache(plan.SourceNamespace, sourceKeys); long bytes = 0;
            foreach (var envelope in retained.Envelopes)
            {
                token.ThrowIfCancellationRequested();
                var candidate = await CredentialVault.PrepareRestrictedRecoveryStageEnvelopeAsync(envelope.EncryptedBytes, envelope.ProfileIdSha256, plan, context, cache, destinationKey, token);
                prepared.Add(candidate); bytes += candidate.Bytes.Length;
                if (prepared.Count > CredentialRestrictedRecoveryStaging.MaximumProfiles || bytes > CredentialRestrictedRecoveryStaging.MaximumStageBytes) throw CredentialRestrictedRecoveryStaging.Failure("capacity-exceeded");
                var original = plan.SourceInventory.ProfileDependencies.Single(p => p.ProfileIdSha256 == envelope.ProfileIdSha256);
                if (JsonSerializer.Serialize(original, CredentialRules.Json) != JsonSerializer.Serialize(candidate.Profile.Source, CredentialRules.Json)) throw CredentialRestrictedRecoveryStaging.Failure("source-profile-mismatch");
                var actual = await CredentialVault.AuthenticateRestrictedStageEnvelopeAsync(candidate.Bytes, envelope.ProfileIdSha256, plan, context, destinationKey, token);
                if (actual.PreservedProfileSha256 != candidate.Profile.PreservedProfileSha256 || JsonSerializer.Serialize(actual.Dependency, CredentialRules.Json) != JsonSerializer.Serialize(candidate.Profile.Destination, CredentialRules.Json)) throw CredentialRestrictedRecoveryStaging.Failure("destination-profile-mismatch");
            }
            foreach (var proof in retained.AbsenceProofs)
            {
                using var key = await cache.GetKeyAsync(plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId, proof.KeyId, token); var material = key.CopyBytes();
                try { CredentialAbsenceScopeProofs.Authenticate(proof, material); } finally { CryptographicOperations.ZeroMemory(material); }
                token.ThrowIfCancellationRequested();
            }
            if (prepared.Count != plan.PreservedProfiles.Count || retained.AbsenceProofs.Count != plan.AbsenceScopes.Count) throw CredentialRestrictedRecoveryStaging.Failure("source-selection-mismatch");
            token.ThrowIfCancellationRequested(); CredentialRestrictedRecoveryStaging.ValidatePlan(plan, reviewedPlanSha256); CredentialRestrictedRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow());
            _ = CredentialRecoveryStaging.PreflightDestination(plan.RootPath, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId);
            using (var current = WindowsIdentity.GetCurrent())
                if (!CredentialNamespaceCustody.CreateDirectory(plan.RootPath, current.User ?? throw CredentialRestrictedRecoveryStaging.Failure("owner-unavailable"))) throw CredentialRestrictedRecoveryStaging.Failure("reservation-conflict");
            reserved = true; CredentialRecoveryStaging.CheckPrivate(plan.RootPath, true);
            using var guard = CredentialNamespaceCustody.CreateFile(Path.Combine(plan.RootPath, CredentialRestrictedRecoveryStaging.GuardName)); guard.Flush(true);
            var reservedUtc = CredentialRestrictedRecoveryStaging.At(clock.GetUtcNow()); CredentialRestrictedRecoveryStaging.ValidateExecutionTime(plan, reservedUtc);
            var marker = new CredentialRestrictedRecoveryStaging.Reservation(3, "credential-restricted-recovery-stage-reservation", CredentialRestrictedRecoveryStaging.Profile, stageId, plan.Sha256, plan.SourceSetAuthority.ArtifactSha256,
                vaultId, authorityId, plan.DestinationRootCommitmentSha256, plan.DestinationKey, reservedUtc, true, "");
            CredentialRestrictedRecoveryStaging.WriteNew(plan.RootPath, CredentialRestrictedRecoveryStaging.MarkerName, marker with { Sha256 = CredentialRestrictedRecoveryStaging.ReservationDigest(marker) });
            CredentialRecoveryStaging.CreateStageDirectory(Path.Combine(plan.RootPath, "profiles")); CredentialRecoveryStaging.CreateStageDirectory(Path.Combine(plan.RootPath, "rollback"));
            foreach (var candidate in prepared.OrderBy(p => p.Profile.Destination.ProfileIdSha256, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested(); CredentialRecoveryStaging.WriteNewBytes(plan.RootPath, "profiles/" + candidate.Profile.Destination.ProfileIdSha256 + ".credential.json", candidate.Bytes);
                var actual = CredentialRecoveryStaging.ReadWrittenEnvelope(plan.RootPath, candidate.Profile.Destination.ProfileIdSha256);
                try { if (!actual.AsSpan().SequenceEqual(candidate.Bytes)) throw CredentialRestrictedRecoveryStaging.Failure("stage-changed"); }
                finally { CryptographicOperations.ZeroMemory(actual); }
            }
            var profiles = prepared.OrderBy(p => p.Profile.Source.ProfileIdSha256, StringComparer.Ordinal).Select(p => p.Profile).ToArray();
            var completed = CredentialRestrictedRecoveryStaging.At(clock.GetUtcNow()); CredentialRestrictedRecoveryStaging.ValidateExecutionTime(plan, completed);
            var core = CredentialRestrictedRecoveryStaging.CompletionCoreDigest(plan, stageId, vaultId, authorityId, binding.Sha256, reservedUtc, completed, profiles);
            var proofs = retained.AbsenceProofs.Select(p => CredentialRestrictedStageProofs.Create(p, plan, stageId, vaultId, authorityId, binding.Sha256, core, destinationKey)).ToArray();
            var entries = profiles.Select(p => new CredentialRestrictedRecoveryStaging.Entry(p.Destination.ProfileIdSha256, p.Destination.Generation, p.Destination.EnvelopeSha256, p.Destination.EnvelopeLength, null))
                .Concat(proofs.Select(p => new CredentialRestrictedRecoveryStaging.Entry(p.ProfileIdSha256, 0, "", 0, p))).OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal).ToArray();
            var state = new CredentialRestrictedRecoveryStaging.State(3, "credential-restricted-recovery-stage-state", CredentialRestrictedRecoveryStaging.Profile, stageId, plan.Sha256, vaultId, authorityId, 0, core, entries, true, "");
            state = state with { Sha256 = CredentialRestrictedRecoveryStaging.StateDigest(state) };
            CredentialRestrictedRecoveryStaging.WriteNew(plan.RootPath, "rollback/binding.json", binding); CredentialRestrictedRecoveryStaging.WriteNew(plan.RootPath, "rollback/state.json", state);
            var absences = proofs.OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal).Select(p => new CredentialRestrictedRecoveryStagedAbsence(
                plan.AbsenceScopes.Single(s => s.ProfileIdSha256 == p.ProfileIdSha256), CredentialRestrictedStageProofs.Projection(p))).ToArray();
            var receipt = new CredentialRestrictedRecoveryStageReceipt(plan.Sha256, plan.SourceSetAuthority.ArtifactSha256, stageId, vaultId, authorityId,
                plan.DestinationRootCommitmentSha256, plan.DestinationKey, binding.Sha256, state.Sha256, core, "", reservedUtc, completed, profiles, absences);
            var completionProof = CredentialRestrictedStageCompletionProofs.Create(plan, receipt, destinationKey);
            receipt = new(plan.Sha256, plan.SourceSetAuthority.ArtifactSha256, stageId, vaultId, authorityId,
                plan.DestinationRootCommitmentSha256, plan.DestinationKey, binding.Sha256, state.Sha256, core, completionProof.Sha256, reservedUtc, completed, profiles, absences);
            CredentialRestrictedStageCompletionProofs.Authenticate(plan, receipt, completionProof, destinationKey);
            token.ThrowIfCancellationRequested(); CredentialRestrictedRecoveryStaging.ValidateCompletedReceiptTime(plan, receipt, clock.GetUtcNow()); CredentialRestrictedRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow());
            CredentialRestrictedRecoveryStaging.WriteNew(plan.RootPath, "rollback/completion-proof.json", completionProof);
            CredentialRestrictedRecoveryStaging.WriteNew(plan.RootPath, "completion.json", receipt);
            var finalObserved = CredentialRestrictedRecoveryStaging.At(clock.GetUtcNow());
            CredentialRestrictedRecoveryStaging.ValidateCompletedReceiptTime(plan, receipt, finalObserved);
            CredentialRestrictedRecoveryStaging.ValidateExecutionTime(plan, finalObserved);
            foreach (var proof in CredentialRestrictedRecoveryStaging.ReadWrittenProofs(plan, receipt)) CredentialRestrictedStageProofs.Authenticate(proof, destinationKey);
            CredentialRestrictedStageCompletionProofs.Authenticate(plan, receipt, CredentialRestrictedRecoveryStaging.ReadWrittenCompletionProof(plan, receipt), destinationKey);
            token.ThrowIfCancellationRequested(); return receipt;
        }
        catch (Exception) when (reserved) { throw CredentialRestrictedRecoveryStaging.Failure("outcome-unknown"); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Restricted credential staging canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw CredentialRestrictedRecoveryStaging.Failure("timeout"); }
        catch (OperationCanceledException) { throw CredentialRestrictedRecoveryStaging.Failure("source-unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw CredentialRestrictedRecoveryStaging.Failure("source-unavailable"); }
        finally { foreach (var candidate in prepared) candidate.Dispose(); }
    }
}

internal sealed class CredentialRestrictedStageKeyCache(CredentialVaultNamespaceIdentity identity, ICredentialKeyProvider source) : ICredentialKeyProvider, IDisposable
{
    readonly Dictionary<string, CredentialEncryptionKey> keys = new(StringComparer.Ordinal);
    public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (customerId != identity.CustomerId || cellId != identity.CellId) throw CredentialRestrictedRecoveryStaging.Failure("source-key-binding-invalid");
        if (!keys.TryGetValue(keyId, out var cached))
        {
            CredentialEncryptionKey borrowed;
            try { borrowed = await source.GetKeyAsync(customerId, cellId, keyId, token) ?? throw CredentialRestrictedRecoveryStaging.Failure("source-key-unavailable"); }
            catch (OperationCanceledException) { throw; } catch { throw CredentialRestrictedRecoveryStaging.Failure("source-key-unavailable"); }
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
