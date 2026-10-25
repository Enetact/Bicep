# Explicit credential ciphertext rewrap

`CredentialVault.RewrapAsync` is a trusted cell-owner library operation for moving an existing encrypted profile to a different, already-provisioned encryption key. It complements [credential custody and admission](foundation-credential-lifecycle.md). It has no HTTP, AHP, MCP, model or provider endpoint, and grants no permission to use a credential.

The operation does not acquire or renew a provider token. It preserves the authenticated customer/cell, project/environment, provider, connection identity/generation/digest, audience, required scopes, profile ID, token kind, expiry and enabled state. It increments the profile generation exactly once. An expired profile remains expired; a revoked profile remains disabled. Returned `CredentialProfile` values contain public metadata, never the token or encryption key.

## Trusted call contract

```csharp
Task<CredentialProfile> RewrapAsync(
    string profileId,
    long expectedGeneration,
    string expectedSourceKeyId,
    CancellationToken cancellationToken = default);
```

Construct the vault with its existing customer/cell/root binding and the intended destination `CredentialVaultOptions.EncryptionKeyId`. That configuration is supplied by the trusted host. Both key IDs must resolve through its existing `ICredentialKeyProvider`; lookup never provisions a missing key. For the Windows provider, provision a fresh destination ID through the separate explicit owner provisioning procedure before rewrap. Do not replace key material beneath an existing key ID.

Revision 10 requires that the [protected vault/key namespaces](foundation-credential-namespaces.md) already exist. Provision fresh custody once through the explicit APIs; normal constructors only validate it. The namespace marker deliberately excludes the active encryption-key ID, so reopening the same valid vault with its reviewed destination key remains supported. Legacy/unmarked custody is not automatically migrated for rewrap.

The caller supplies the exact current profile generation and expected source key ID from its independently reviewed maintenance record. A positive generation below `long.MaxValue` and a valid source key ID are mandatory. The source envelope's plaintext key ID and profile metadata are not accepted as authority until authenticated decryption succeeds. A generation mismatch returns `credential-generation-conflict`, a source-key mismatch returns `credential-rewrap-source-key-conflict`, and a source already using the configured destination returns `credential-rewrap-same-key`. Same-key requests are refused instead of silently succeeding.

The vault holds its existing cross-instance file gate while it authenticates the source, compares both expected values, resolves the destination key and atomically replaces the envelope. It uses the existing fresh temporary-file, durable flush and replacement path, with a new AES-GCM nonce and metadata binding. Failure or cancellation before replacement leaves the original envelope intact and releases the gate; temporary writes are cleaned up by the existing writer. Cancellation racing an already completed replacement does not undo that commit. Inspect the stored generation before retrying after an uncertain maintenance outcome.

The operation accepts caller cancellation throughout lock/key/file work. Trusted key providers must honor that token. The library method does not create an additional background job or claim forced termination of an uncooperative key provider.

## Ordering with dispatch and other writers

Rewrap shares the same gate as `PutAsync`, `RevokeAsync` and the transport's retained dispatch-admission lease. Two cooperating rewraps against the same generation cannot both commit. A concurrent replacement or revocation changes the generation and requires a fresh maintenance decision.

If dispatch holds the gate first, rewrap cannot commit between authenticated credential resolution and the final authorization/send invocation. After that invocation the transport releases the gate; rewrap can complete while the admitted mock/provider response remains in flight. It does not cancel an admitted request or establish a remote completion outcome. Revalidation against an older profile generation fails after a successful rewrap.

## Custody and recovery limits

This is one-profile maintenance, not a bulk rotation coordinator, encryption-key retirement or recovery system. Rewrap never deletes a source key or exports token/key material. Older encrypted profile backups still require their original keys even after every current profile has moved. Inventory, backup retention, protected maintenance authorization/audit, key retirement approval and recovery custody must be completed separately before an owner removes any key.

No durable application audit event is added by this library call. Its trusted owner composition must retain the reviewed public profile/key IDs, expected generation and result under the deployment's operational audit policy. Avoid recording tokens, key bytes or private provider payloads. Configuring one vault instance with a new destination does not automatically update other host configurations or prevent a trusted host still configured with an old write key from using that key later.

The generation check fences concurrent cooperating operations. Default vaults have no external high-water mark: restoring an older credential envelope and its usable keys is a separate recovery threat. Fresh vaults may explicitly select [local rollback checks](foundation-credential-rollback.md), which bind encrypted bytes and generations to a separately protected authority. That mode also fences rewrap and preserves pending writes for exact reconciliation. Restoring both stores or acting as their trusted OS owner remains outside that guarantee. Local qualification does not establish production HSM/KMS custody, an external revocation guarantee, customer consent, cloud rights or commercial readiness.

## Local verification

For customer-role authorization and durable public audit, use [the reviewed maintenance executor](foundation-credential-maintenance.md). It prepares the encrypted candidate under the vault gate, commits one-use admission before publication and retains an uncertain outcome if the local effect cannot be recorded. Calling `RewrapAsync` directly remains a trusted owner primitive with the limits above; this guide's historical checks do not establish the new composition's acceptance.

[CredentialRewrapTests](../tests/SelfService.Foundation.Tests/CredentialRewrapTests.cs) use synthetic GitHub and Azure DevOps profiles, temporary vaults and a captured HTTP handler. They exercise exact metadata preservation, use after the synthetic source key becomes unavailable, expired/revoked profiles, source-key/generation/type rejection, cross-customer/cell binding, authenticated-metadata tamper, missing source/destination keys, cancellation, competing vault instances and dispatch-admission ordering while a response is held open. A Windows fixture provisions two new temporary DPAPI keys, reopens the rewrapped profile and verifies that both original protected key files remain unchanged. No real credentials, network request, provider grant or primary application process is involved.

These checks qualify the local rewrap path. Existing production hosting, protected operational composition, key retirement/recovery and live-provider release gates remain open.

On 24 October 2026, the new 23 rewrap cases and existing vault, acquisition, authenticated-transport, operation-deadline and Windows key-provider regressions passed together: **136 passed, 0 failed, 0 skipped**. The portable .NET 10 SDK built into the separate `artifacts/foundation-credential-rewrap-check` directory; an exact copy of the original TRX is retained at `artifacts/foundation-credential-rewrap-check/results/credential-rewrap-focused.trx`. Native owner-only DPAPI checks ran in normal Windows with fresh temporary custody namespaces. No package dependency version changed in this increment.
