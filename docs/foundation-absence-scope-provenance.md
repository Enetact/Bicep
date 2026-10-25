# Authenticated scope provenance for unpublished credential profiles

This is an explicit trusted-owner API for new local Windows namespaces. It preserves the authenticated scope of an initial credential write that was reserved and then abandoned before publication. It does not restore, import, promote or activate credentials, and it does not authorize a customer recovery review.

The existing [strong rollback authority](foundation-credential-rollback.md) records a generation-zero entry after an abandoned initial write. Its legacy format preserves the profile hash, but cannot recover the original project/environment or source-key dependency from that hash. The new format records a private source-key MAC over the original full scope before the first pending record is published. A separate [read-only inspector](foundation-absence-scope-inspection.md) authenticates those proofs and projects bounded metadata.

## Explicit new-root composition

The caller supplies an existing customer/cell key provider, private local paths and an exact key ID. Example values below are placeholders; the example creates no keys or sign-ins.

```csharp
var options = new CredentialVaultOptions(
    freshVaultDirectory, customerId, cellId, sourceKeyId)
{
    RollbackProtection = new CredentialRollbackProtectionOptions(
        freshAuthorityDirectory)
    {
        Format = CredentialRollbackFormat.AuthenticatedScopeV2
    }
};

var vault = CredentialVault.ProvisionStrongScoped(options, sourceKeys, clock);
var expectedNamespace = vault.DescribeNamespace();
var inspection = await vault.InspectAbsenceScopeProvenanceAsync(
    expectedNamespace, cancellationToken);
```

Both paths must be fresh and separate. Provisioning uses the existing private Windows namespace custody controls, rejects existing entries and protected namespaces, and preserves partial state on failure. The new namespace kinds are `credential-vault-scoped-fenced` and `credential-rollback-scoped-authority`. They use binding/state format 2 with the existing guarded file layout. Reopening requires the same explicit format and customer/cell configuration. It does not convert, repair or adopt another namespace.

Default options and `ProvisionStrong` retain legacy format 1. `ProvisionStrongScoped` requires the explicit v2 setting; selecting a different setting does not downgrade an existing root. There is no in-place migration or proof backfill. A legacy profile hash, database record, supplied project/environment, signature or operator assertion cannot manufacture authenticated absence scope.

## What the proof binds

An absence proof includes the canonical full `CredentialScope`, its digest and profile hash; customer/cell and both namespace commitments; the rollback binding; the original source-key ID; and the first transaction, candidate digest, candidate length and generation. A versioned domain and algorithm bind the intended use. HKDF-SHA256 derives a purpose-specific MAC key from the same frozen source-key bytes used to encrypt that first candidate, and HMAC-SHA256 authenticates the proof. Verification compares MAC bytes in fixed time. The platform's [.NET cryptographic implementation provides HKDF](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.hkdf?view=net-10.0).

The proof contains private plaintext scope and custody metadata. It contains no credential token or key material. Its unkeyed SHA256 and the authority state's checksum help bind bytes; neither substitutes for source-key authentication. A party holding that source key can create matching MACs. This provides provenance within the configured local key-custody boundary, rather than an independent witness, human approval or protection against rollback of all owner-controlled state.

## Interrupted writes and key dependencies

The initial candidate and proof exist before the first pending decision is saved. The existing vault and authority guards remain held across key access and publication. If the write stops after pending persistence, normal operations remain blocked until an exact explicit reconciliation decision is made. Cancellation does not automatically abandon, publish or retry a pending write.

For `AbandonUnpublished` with a truly absent initial profile, the authority preserves the original proof and source-key reference. A subsequent attempt must authenticate that original proof with its original source key and match the entire original canonical scope before obtaining a destination write key. A newer configured write key does not replace the old proof dependency. Another abandoned attempt preserves the original transaction/candidate evidence even though its new pending candidate can have different ciphertext and use a different key.

After successful positive-generation publication, the authority removes the resolved absence proof. The current authenticated envelope then supplies scope and its own source-key dependency. This does not authorize deleting an older key: retained historical artifacts, other absences or other systems may still require it. A key ID is a reference, not a key-material fingerprint, availability attestation or backup receipt.

Missing or changed source-key material, a malformed proof or a MAC mismatch fails the operation. There is no fallback to the configured write key, implicit key creation, scope inference or metadata repair. The authority remains bounded to 256 admitted profile hashes, including unpublished absences, with its existing counter and state-byte limits. Repeated attempts do not create a second slot for the same profile hash.

## Inspection and downstream boundaries

The inspector requires the exact expected namespace, holds both custody guards, rejects any pending decision across the namespace, authenticates every generation-zero proof with its referenced source key and rechecks the complete observed names, bytes and custody after awaits. It returns copied read-only absence projections: profile/scope hashes, project/environment, source-key ID and proof digest, plus sorted absence-key references. It does not return the full scope, raw profile ID, MAC, token, key material, root path or private proof.

Positive envelopes are counted for structural consistency; this inspector does not AES-authenticate them or provide their key dependencies. Its result is an absence-scope observation, not a complete recovery inventory or retained-key-backup assessment. See the [inspection guide](foundation-absence-scope-inspection.md) for exact flags, cancellation and failure behavior.

The existing v1 recovery [inventory](foundation-credential-recovery-inventory.md), [signed set](foundation-credential-recovery-set.md), [inactive staging](foundation-credential-recovery-staging.md), [durable scoped review](foundation-credential-recovery-review.md) and [set checkpoint](foundation-credential-recovery-checkpoints.md) formats remain unchanged. Legacy inventory and capture entry points explicitly refuse the new namespace format before source-key lookup. Revision 22 adds separate [scoped inventory, signed capture, retained-byte AES/HMAC rehearsal and combined key holds](foundation-scoped-recovery.md). Revision 23 implements separate [scoped destination staging](foundation-scoped-recovery-staging.md), [complete-scope human review](foundation-scoped-recovery-review.md) and [scoped checkpoint acceptance](foundation-scoped-recovery-checkpoints.md). Existing v1 unsupported/unscoped-absence refusals remain in place. No scope is backfilled into a legacy root.

No default HTTP, AHP, MCP or model tool is registered for this API. No directory application role, database schema, live vault adoption, credential export, key retirement or deployment authority is added.

## Local qualification

The [independent boundary tests](../tests/SelfService.Foundation.Tests/CredentialAbsenceScopeBoundaryTests.cs) exercise legacy byte compatibility, mode separation, real first-write interruption and explicit abandonment, original-proof retention, exact-scope retry, key rotation, independently recomputed MACs and public-checksum tampering, missing/malformed proofs, cancellation, native DPAPI custody and competing writers. The inspector has a separate native test cohort for guard retention, source-key lookup, cancellation, whole-observation failures and redaction.

Run the complete disconnected Foundation profile from the repository root with `./scripts/Test-Foundation.ps1 -DotNetPath <reviewed-dotnet-executable>`. Release evidence records the actual executed source, results and limitations in [validation history](validation.md). Synthetic local qualification does not establish independent key backup, clean-machine or power-loss recovery, production readiness, cloud acceptance or publication approval.
