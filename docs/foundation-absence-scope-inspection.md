# Inspect authenticated source-v2 absence scopes

`CredentialVault.InspectAbsenceScopeProvenanceAsync` authenticates committed absence-scope proofs in an explicitly provisioned [source-v2 vault](foundation-absence-scope-provenance.md). It returns a redacted observation of those proofs. It does not capture a recovery set, authorize project access, create a stage, adopt a vault, renew a credential or activate a host. No HTTP, AHP, MCP, model, worker or default host route selects it.

An absence is a committed generation-zero entry after an unpublished first credential creation was abandoned. Legacy v1 entries retain only a profile hash. They cannot supply historical project/environment authority. Source v2 seals the original attempted canonical scope and first transaction/candidate bindings with its original source key. Inspection authenticates that retained proof; it does not assign scope from today's connections or infer the scope of an old v1 absence.

```csharp
var options = new CredentialVaultOptions(
    freshVaultPath, customerId, cellId, existingWriteKeyId)
{
    RollbackProtection = new CredentialRollbackProtectionOptions(freshAuthorityPath)
    {
        Format = CredentialRollbackFormat.AuthenticatedScopeV2
    }
};
var vault = CredentialVault.ProvisionStrongScoped(options, existingKeys, clock);
// Persist this identity through separately reviewed trusted configuration.
var expectedNamespace = vault.DescribeNamespace();
var report = await vault.InspectAbsenceScopeProvenanceAsync(
    expectedNamespace, cancellationToken);
```

Provisioning is a separate fresh-root operation. Inspection itself uses existing-only custody and does not provision, migrate, repair, adopt, delete or rewrite either root. Passing a legacy vault returns `credential-absence-scope-inspection-scoped-format-required` and leaves its bytes unchanged.

The inspector takes the existing vault guard, then the rollback-authority guard. It requires the exact expected customer/cell, vault identity and commitment, authority identity and binding, both protected custody markers and valid bounded current state. Any pending write anywhere in the namespace blocks the whole inspection before source-key access. Generation-zero profile paths must be genuinely absent, including file, directory and link entries. Unknown files, unexpected directories and partial write files also fail the whole operation.

For each committed absence, the original proof `KeyId` is mandatory. The inspector clones and freezes one key handle per distinct ID, verifies proof structure and current namespace bindings, and authenticates the domain-separated proof MAC. It never substitutes the configured current write key. An unavailable historical proof key causes failure even when the current write key exists. Conversely, an unavailable current write key does not prevent inspection when every historical absence key remains available.

After each external key-provider await, and once more before returning, inspection checks the full bounded source inventory, file bytes/names/custody, namespace commitments, authority revision/state digest and actual absence paths again. Both guards remain held through authentication and those checks. There is no database transaction or application audit write. Missing keys, changed state or failed authentication produce no partial report and do not repair the observed state.

`CredentialAbsenceScopeInspection.AbsenceScopes` contains only:

- Profile ID SHA256 and full canonical scope SHA256.
- Authenticated original project and environment IDs.
- Original source key ID and proof SHA256.

The report also binds the namespace, authority revision/state digest and observation time. Collections are copied and read-only. The full original scope, `AuthProfileId`, required scopes, proof payload, MAC/tag, keys, credential material, SID and filesystem paths are excluded. Proof bytes and full scope metadata remain private local authority data.

`AbsenceRequiredKeyIds` covers **only these absence proofs**. `PositiveEnvelopeCount` counts structurally committed present envelopes, but the inspector does not decrypt or authenticate their credential values and does not enumerate their key dependencies. `PositiveEnvelopeAuthenticationPerformed`, `PositiveEnvelopeKeyDependenciesIncluded`, `CompleteRecoveryInventory`, `FullRecoverySupported`, `CustomerReviewAuthorized`, `ExternalExecutionAuthorized`, `KeyRetirementAuthorized`, `IndependentKeyBackupVerified`, `SourceHostFenced` and `ProductionRecoveryQualified` remain false. `IndependentBackupCoverage` is `NotAssessed`. This report must not be used as a complete retention assessment, backup acceptance, customer approval or key-retirement permit.

The existing v1 full recovery inventory and signed-set capture APIs explicitly refuse source-v2 roots before source-key or signer access, with `credential-recovery-inventory-scoped-format-unsupported` and `credential-recovery-set-scoped-format-unsupported`. Existing v1 roots, serialization and recovery behavior remain supported. Revision 22 adds separate [source-v2 complete inventory, signed capture, retained-byte rehearsal and combined key holds](foundation-scoped-recovery.md). This absence-only inspector is still not a full recovery inventory. Scoped checkpoint acceptance, inactive staging, completed-stage verification and union-of-scope customer review remain separate protocol work. The current v1 customer-review refusal of unsupported/unscoped absences remains in force.

Inspection has a cooperative 20-second operation deadline. An uncooperative provider can keep the task, both guards and private state in memory beyond cancellation until it actually returns; the inspector does not detach the await or report success after cancellation. Source key buffers are zeroed and cached handles disposed when the operation ends. Same-owner or administrator bypass, trusted higher ancestors, independent organizational/key custody and proof of globally latest state are outside this local observation boundary.

The native Windows synthetic tests cover exact namespace checks before keys, global pending refusal, original-key caching and missing historical keys, absent current write/positive keys, empty and positive-only reports, unknown/partial/nonabsent entries, mutations during provider waits, actual held guards and a cooperating mutation, cancellation with cooperative and uncooperative providers, opaque errors, immutable redaction and early v1 recovery refusal. They compare source file names, bytes and ACLs across read-only operations. No real credential caches, customer migrations, cloud operations or live pipelines are involved.
