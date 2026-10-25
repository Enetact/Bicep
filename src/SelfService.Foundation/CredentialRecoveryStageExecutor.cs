using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed class CredentialRecoveryStageExecutor
{
    readonly ICredentialKeyProvider sourceKeys, destinationKeys;
    readonly TimeProvider clock;
    public CredentialRecoveryStageExecutor(ICredentialKeyProvider sourceKeys, ICredentialKeyProvider destinationKeys, TimeProvider? timeProvider = null)
    {
        this.sourceKeys = sourceKeys ?? throw CredentialRecoveryStaging.Failure("configuration-invalid");
        this.destinationKeys = destinationKeys ?? throw CredentialRecoveryStaging.Failure("configuration-invalid"); clock = timeProvider ?? TimeProvider.System;
    }
    public async Task<CredentialRecoveryStageReceipt> StageAsync(CredentialRecoverySet set, CredentialRecoveryStagePlan plan,
        string reviewedPlanSha256, RSA pinnedPublicKey, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        var prepared = new List<PreparedCredentialRecoveryStageEnvelope>(); var reserved = false;
        try
        {
            if (!OperatingSystem.IsWindows()) throw CredentialRecoveryStaging.Failure("windows-required");
            token.ThrowIfCancellationRequested(); CredentialRecoveryStaging.ValidatePlan(plan, reviewedPlanSha256);
            CredentialRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow());
            _ = CredentialRecoveryStaging.PreflightDestination(plan.RootPath, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId);
            using var retained = CredentialRecoverySets.ValidateRetainedSet(set, plan.SourceSetAuthority, pinnedPublicKey, plan.SourceNamespace, clock);
            if (retained.Verification.Signature != plan.SourceSignature || retained.Verification.Inventory.Sha256 != plan.SourceInventory.Sha256 ||
                !retained.AbsentProfileIds.SequenceEqual(plan.AbsentProfileIds, StringComparer.Ordinal)) throw CredentialRecoveryStaging.Failure("source-selection-mismatch");
            using var destinationKey = await CredentialRecoveryStaging.ResolvePinnedDestinationKeyAsync(plan.DestinationKey, destinationKeys, token);
            long bytes = 0;
            foreach (var envelope in retained.Envelopes)
            {
                token.ThrowIfCancellationRequested();
                var candidate = await CredentialVault.PrepareRecoveryStageEnvelopeAsync(envelope.EncryptedBytes, envelope.ProfileIdSha256, plan, sourceKeys, destinationKey, token);
                prepared.Add(candidate); bytes += candidate.Bytes.Length;
                if (prepared.Count > CredentialRecoveryStaging.MaximumProfiles || bytes > CredentialRecoveryStaging.MaximumStageBytes) throw CredentialRecoveryStaging.Failure("capacity-exceeded");
                var original = plan.SourceInventory.ProfileDependencies.Single(p => p.ProfileIdSha256 == envelope.ProfileIdSha256);
                if (JsonSerializer.Serialize(original, CredentialRules.Json) != JsonSerializer.Serialize(candidate.Profile.Source, CredentialRules.Json))
                    throw CredentialRecoveryStaging.Failure("source-profile-mismatch");
                var authenticated = await CredentialVault.AuthenticateStageEnvelopeAsync(candidate.Bytes, envelope.ProfileIdSha256, plan, destinationKey, token);
                if (authenticated.PreservedProfileSha256 != candidate.Profile.PreservedProfileSha256 ||
                    JsonSerializer.Serialize(authenticated.Dependency, CredentialRules.Json) != JsonSerializer.Serialize(candidate.Profile.Destination, CredentialRules.Json))
                    throw CredentialRecoveryStaging.Failure("destination-profile-mismatch");
            }
            if (prepared.Count != plan.PreservedProfiles.Count) throw CredentialRecoveryStaging.Failure("source-profile-mismatch");
            token.ThrowIfCancellationRequested(); CredentialRecoveryStaging.ValidatePlan(plan, reviewedPlanSha256);
            CredentialRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow());
            _ = CredentialRecoveryStaging.PreflightDestination(plan.RootPath, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId);
            if (!CredentialRecoveryStaging.ReserveRoot(plan)) throw CredentialRecoveryStaging.Failure("reservation-conflict");
            reserved = true;
            CredentialRecoveryStaging.CheckPrivate(plan.RootPath, true);
            // This registered partial-root sentinel is retained through completion and never removed on failure.
            using var guard = CredentialNamespaceCustody.CreateFile(Path.Combine(plan.RootPath, CredentialRecoveryStaging.GuardName)); guard.Flush(true);
            var reservedUtc = CredentialRecoveryStaging.Milliseconds(clock.GetUtcNow()); CredentialRecoveryStaging.ValidateExecutionTime(plan, reservedUtc);
            var ids = new HashSet<string>(StringComparer.Ordinal) { plan.SourceNamespace.VaultNamespaceId };
            if (plan.SourceNamespace.AuthorityNamespaceId is { } originalAuthority) ids.Add(originalAuthority);
            string NewId() { string id; do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id)); return id; }
            var stageId = NewId(); var vaultId = NewId(); var authorityId = NewId();
            var marker = new CredentialRecoveryStaging.StageReservation(1, "credential-recovery-stage-reservation", stageId, plan.Sha256,
                plan.SourceSetAuthority.ArtifactSha256, plan.DestinationRootCommitmentSha256, plan.DestinationKey, vaultId, authorityId, reservedUtc, true, "");
            marker = marker with { Sha256 = CredentialRecoveryStaging.ReservationDigest(marker) };
            CredentialRecoveryStaging.WriteNew(plan.RootPath, CredentialRecoveryStaging.MarkerName, marker);
            CredentialRecoveryStaging.CreateStageDirectory(Path.Combine(plan.RootPath, "profiles"));
            CredentialRecoveryStaging.CreateStageDirectory(Path.Combine(plan.RootPath, "rollback"));
            foreach (var candidate in prepared.OrderBy(p => p.Profile.Destination.ProfileIdSha256, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                CredentialRecoveryStaging.WriteNewBytes(plan.RootPath, "profiles/" + candidate.Profile.Destination.ProfileIdSha256 + ".credential.json", candidate.Bytes);
            }
            var binding = new CredentialRecoveryStaging.StageBinding(1, "credential-recovery-stage-binding", stageId, plan.Sha256,
                plan.SourceSetAuthority.ArtifactSha256, plan.SourceNamespace, vaultId, authorityId, plan.DestinationRootCommitmentSha256, plan.DestinationKey, true, "");
            binding = binding with { Sha256 = CredentialRecoveryStaging.BindingDigest(binding) };
            var entries = prepared.Select(p => new CredentialRecoveryStaging.StageEntry(p.Profile.Destination.ProfileIdSha256, p.Profile.Destination.Generation,
                p.Profile.Destination.EnvelopeSha256, p.Profile.Destination.EnvelopeLength)).Concat(plan.AbsentProfileIds.Select(p => new CredentialRecoveryStaging.StageEntry(p, 0, "", 0)))
                .OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal).ToArray();
            var state = new CredentialRecoveryStaging.StageState(1, "credential-recovery-stage-state", stageId, plan.Sha256, vaultId, authorityId, 0, entries, true, "");
            state = state with { Sha256 = CredentialRecoveryStaging.StateDigest(state) };
            CredentialRecoveryStaging.WriteNew(plan.RootPath, "rollback/binding.json", binding); CredentialRecoveryStaging.WriteNew(plan.RootPath, "rollback/state.json", state);
            foreach (var candidate in prepared)
            {
                token.ThrowIfCancellationRequested(); var bytesOnDisk = CredentialRecoveryStaging.ReadWrittenEnvelope(plan.RootPath, candidate.Profile.Destination.ProfileIdSha256);
                try
                {
                    if (!bytesOnDisk.AsSpan().SequenceEqual(candidate.Bytes)) throw CredentialRecoveryStaging.Failure("stage-changed");
                    var verified = await CredentialVault.AuthenticateStageEnvelopeAsync(bytesOnDisk, candidate.Profile.Destination.ProfileIdSha256, plan, destinationKey, token);
                    if (verified.PreservedProfileSha256 != candidate.Profile.PreservedProfileSha256 ||
                        JsonSerializer.Serialize(verified.Dependency, CredentialRules.Json) != JsonSerializer.Serialize(candidate.Profile.Destination, CredentialRules.Json))
                        throw CredentialRecoveryStaging.Failure("destination-profile-mismatch");
                }
                finally { CryptographicOperations.ZeroMemory(bytesOnDisk); }
            }
            token.ThrowIfCancellationRequested(); var completed = CredentialRecoveryStaging.Milliseconds(clock.GetUtcNow());
            CredentialRecoveryStaging.ValidateExecutionTime(plan, completed);
            var receipt = new CredentialRecoveryStageReceipt(plan.Sha256, plan.SourceSetAuthority.ArtifactSha256, stageId, vaultId, authorityId,
                plan.DestinationRootCommitmentSha256, plan.DestinationKey, binding.Sha256, state.Sha256, reservedUtc, completed,
                prepared.OrderBy(p => p.Profile.Destination.ProfileIdSha256, StringComparer.Ordinal).Select(p => p.Profile), plan.AbsentProfileIds);
            CredentialRecoveryStaging.ValidateCompletedReceiptTime(plan, receipt, completed);
            CredentialRecoveryStaging.WriteNew(plan.RootPath, "completion.json", receipt); // Publication of completion is last; no normal-vault state is created.
            CredentialRecoveryStaging.ValidateWrittenStage(plan, receipt); token.ThrowIfCancellationRequested();
            CredentialRecoveryStaging.ValidateExecutionTime(plan, clock.GetUtcNow()); return receipt;
        }
        catch (Exception) when (reserved) { throw CredentialRecoveryStaging.Failure("outcome-unknown"); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Credential recovery staging canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw CredentialRecoveryStaging.Failure("timeout"); }
        catch (OperationCanceledException) { throw CredentialRecoveryStaging.Failure("source-unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw CredentialRecoveryStaging.Failure("source-unavailable"); }
        finally { foreach (var candidate in prepared) candidate.Dispose(); }
    }
}
