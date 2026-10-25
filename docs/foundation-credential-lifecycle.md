# Credential key lookup and dispatch admission

Implemented source, 24 October 2026. This increment separates explicit Windows key provisioning from ordinary lookup and orders credential revocation/rotation against authenticated dispatch admission. It extends the existing [credential custody and acquisition adapters](foundation-security-operations.md#customer-credential-custody). The dedicated API and independent worker remain disconnected: no live transport is enabled, no provider permission is granted and no real credential or sign-in is created by these changes.

For fresh vaults that need local replay detection, [opt-in rollback checks](foundation-credential-rollback.md) add a separately protected authority. They do not upgrade existing vaults or make DPAPI recovery automatic. Preserve the authority independently of vault/key backups; restoring all local authority together cannot prove freshness.

## Explicit provisioning and existing-only lookup

[Reviewed local maintenance](foundation-credential-maintenance.md) adds a separate store-backed path for exact revoke/rewrap intents, independent human approval and durable admission/outcome audit. It does not change the authority of the low-level owner APIs described below. Import, renewal, pending-write reconciliation, bulk rotation and key retirement are outside that new path; no existing credential or login cache is migrated.

`CredentialWindowsUserKeyProvider.GetKeyAsync(customerId, cellId, keyId, cancellationToken)` now resolves existing DPAPI-protected material only. A missing key returns `credential-user-key-not-found`; it never generates a replacement. `CredentialVault` continues to sanitize unavailable key-provider failures as `credential-key-unavailable`.

Trusted owner composition provisions a new encryption key explicitly:

```csharp
using var key = await provider.ProvisionKeyAsync(customerId, cellId, newKeyId, cancellationToken);
```

The provider uses the existing cell-bound DPAPI format, customer/cell/key-specific entropy and atomic new-file write. Provisioning an existing key ID returns `credential-user-key-already-provisioned` and preserves its bytes. Concurrent provision attempts admit one new key; other instances can subsequently resolve that same existing material. This operation is a C# custody API, with no HTTP, MCP, model or public JSON key-import endpoint.

Revision 10 supersedes the earlier constructor compatibility behavior: normal vault/key-provider constructors now validate existing [protected credential namespaces](foundation-credential-namespaces.md). They do not create/adopt directories, create missing guards or repair ACLs. Trusted owners must explicitly call `CredentialVault.Provision(...)` or `CredentialWindowsUserKeyProvider.ProvisionNamespace(...)` for a fresh namespace before separately provisioning key material. Legacy/unmarked, widened or incomplete custody is refused unchanged; no portal/desktop cache is discovered or migrated. Directory provisioning, key custody and runtime identities remain trusted operator responsibilities.

Callers that previously relied on the first `GetKeyAsync` or vault write to create a key must provision it first. Missing keys during recovery require restoring independently protected original key custody or undertaking separately reviewed re-onboarding. Generating a different random key under the same ID cannot decrypt an existing envelope and is no longer an implicit lookup behavior. No key from a real application cache is read or altered by the synthetic tests.

An envelope's plaintext key ID is untrusted until authenticated decryption succeeds. Editing that field to a nonexistent key can therefore cause a sanitized resolution failure, but cannot make lookup allocate a new key file. AES-GCM authentication, profile identity, connection generation/digest, audience and required-scope checks remain in place.

## Revocation and send admission

The authenticated transport obtains its initial exact durable permit, then asks the vault for an internal dispatch lease. That lease retains the vault's cross-instance file gate while the existing encrypted profile is read and authenticated. Final durable authorization, credential-expiry checking, permit consumption and the `HttpClient.SendAsync` invocation occur before the gate is released.

The ordering guarantee is:

- A profile revocation or replacement that commits first is observed by the later dispatch read. Revoked profiles cannot supply a credential; replacements supply their new generation/material.
- A dispatch that obtains the admission gate first prevents `RevokeAsync` and `PutAsync` from committing between that read and send admission. Those operations may wait or receive the existing bounded busy/cancellation outcome.
- Send admission is the `SendAsync` invocation. The gate is released as soon as that invocation returns its task, before waiting for response headers or body. Network latency does not retain the vault admission gate.

This orders local operations. It does not prove that a provider received the request, that a remote workflow completed, or that a revocation can cancel an already admitted request. A replacement/revocation may finish while an earlier admitted request is still in flight. External token revocation, uncertain-effect reconciliation and provider rights remain separate concerns. The adapter still sends only to its fixed HTTPS provider origins, follows no authorization-bearing redirects and performs no automatic POST retry.

The lease releases its guard on final denial, authorizer failure, cancellation, expired credentials, send-initiation failure and ordinary disposal. The final expiry check includes the existing 15-second safety margin; a slow final authorizer cannot extend credential life. Returned credential secret/key/lease objects retain their redaction and serialization guards.

`ICredentialDispatchPermitSource` is trusted host composition and must not reenter the same `CredentialVault` during final authorization. That check runs under the custody gate; reentrant vault access can wait until the existing busy limit or cancellation. The producer should inspect current durable application authority through the store and preserve activation, exact plan, role, connection and fence checks. No model-supplied callback becomes authoritative.

## One bounded operation deadline

`PipelineAuthenticatedTransport` now uses one linked operation deadline across permit authorization, custody/key resolution, final authorization, send invocation, response headers and response body. Its trusted constructor and `ForMockHandler` accept an optional `operationTimeout`, defaulting to 20 seconds. `CredentialTokenHttpClient` accepts the same option, defaulting to 15 seconds; acquisition starts that budget before the GitHub private-key or Entra assertion callback and carries it through token HTTP/body reading and vault persistence.

Both options are restricted to 50 milliseconds through 30 seconds. The small override supports deterministic synthetic fixtures. No request JSON, model argument or provider response can configure these deadlines. HTTP's separate header-only timeout is disabled inside these adapters because the linked operation token governs the complete operation, including body reads. Trusted key/authorizer/assertion callbacks and injected test streams must honor cancellation; this is cooperative cancellation, rather than forced termination of arbitrary callback code.

Caller cancellation remains a sanitized `OperationCanceledException` carrying the original caller token. A transport-owned deadline before send invocation returns `pipeline-credential-admission-timeout` and admits no POST. A deadline after invocation becomes a sanitized `TimeoutException`; the existing dispatcher classifies it as `OutcomeUnknown` and does not retry. Cancelling local I/O does not establish that an admitted remote request was cancelled or had no effect.

Acquisition-owned deadlines return `credential-token-acquisition-timeout`. Stalled key/assertion/response paths cannot persist a token profile, and cancellation during precommit key resolution releases the vault gate. Atomic profile commit remains the existing write boundary: cancellation racing an already completed commit does not erase that commit. Inspect the persisted generation before an owner decides whether to try again. A provider may also have minted a token before an interrupted response; no remote revocation or rollback is inferred.

## Local verification and remaining work

The later revision 9 continuation adds [explicit ciphertext rewrap](foundation-credential-rewrap.md) and [a store-backed permit source](foundation-durable-dispatch-authorization.md). Rewrap is one-profile maintenance with already-provisioned keys, rather than bulk rotation/retirement or rollback-resistant recovery. The permit source provides current durable checks for disconnected trusted composition; real provider onboarding and live host composition remain unqualified.

[Authenticated transport tests](../tests/SelfService.Foundation.Tests/PipelineAuthenticatedTransportTests.cs) pause final authorization, start a second vault instance's revocation or replacement, and verify that it cannot commit before send admission. They then hold the mock response open and verify that the credential change can finish while the request remains in flight. Subsequent sends reject revoked profiles or use replacement material. Denial, exception, cancellation, expiry and send-failure cases verify that subsequent revocation can acquire the gate.

[Windows custody tests](../tests/SelfService.Foundation.Tests/CredentialWindowsUserKeyProviderTests.cs) use new temporary native DPAPI namespaces, explicit provisioning, missing-key reads, altered envelope key IDs and competing provision attempts. [Vault tests](../tests/SelfService.Foundation.Tests/CredentialVaultTests.cs) retain encrypted-envelope, scope, expiry, generation and tamper coverage. Every credential, key and provider payload in this verification is synthetic; provider HTTP is captured by mock handlers.

[Operation deadline tests](../tests/SelfService.Foundation.Tests/CredentialOperationDeadlineTests.cs) stall response streams and key/assertion/final-authorizer callbacks, verify the whole-operation deadline and original caller cancellation, and check preadmission zero-POST/no-profile outcomes, postadmission unknown outcomes, released gates and successful later attempts. They make no network request.

This increment does not add bulk re-encryption/rewrap, encryption-key retirement, rollback-resistant credential recovery epochs, remote revocation, a live callback onboarding UI or production HSM/KMS custody. The existing model-auth callback remains explicitly supplied and separately customer/actor bound. Production acceptance still requires real customer consent/provider permissions, protected service/key/storage operations and a reviewed recovery process. No commercial or cloud-execution authority follows from these local checks.
