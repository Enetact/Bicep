# Retain and rehearse restricted credential recovery

This source-only recovery protocol preserves `FoundationBoundDispatchOnly` for a freshly provisioned [restricted namespace](foundation-bound-pipeline-consumer.md#restrict-a-fresh-credential-namespace). It captures a signed private artifact and authenticates its retained encrypted bytes with existing keys. It does not stage, adopt or activate a vault, release a restored-cell execution hold, authorize a provider operation or establish independent key backups.

## Separate signed format and policy

`CredentialRestrictedRecoverySets` uses a separate artifact schema and version-3 signature/content domains. The fixed access profile is required in signed signature, content and canonical inventory metadata. Retained vault and rollback markers must have the actual restricted kinds, with both binding copies and namespace commitments consistent. Omitting the policy, changing it to General, relabeling a legacy artifact or replacing the restricted markers fails validation.

Existing V1 and General scoped-V2 bytes, public types and validation remain distinct. Their exporters continue to refuse restricted namespaces before keys/signers. Their staging, checkpoint and review methods cannot consume the new restricted set type. No caller-supplied profile option broadens an existing parser or supplies authorization.

The artifact retains the complete positive-profile and authenticated-absence dependency union, including original source key IDs, full scope commitments, original absence proofs and current rollback state. Expired and revoked profiles remain dependencies. Missing keys, pending writes, mixed/duplicate entries or inconsistent inventory fail closed; a successful subset is never returned as complete.

## Explicit trusted-owner capture

Use the existing restricted vault and an independently reviewed exact namespace identity:

```csharp
var inventory = await vault.InspectRestrictedRecoveryDependenciesAsync(
    reviewedNamespace, cancellationToken);

using var set = await vault.CaptureRestrictedSignedRecoverySetAsync(
    reviewedNamespace, trustedSigner, signerKeyId, generation,
    notBeforeUtc, expiresUtc, cancellationToken);
set.WritePrivateArtifact(ownerPrivateOutputStream);
```

Capture holds both actual source guards through source authentication, signing, final content/identity checks and completion. It freezes one existing key per actual key ID for consistent AES/HMAC authentication. Cancellation does not detach an uncooperative callback or release its guards while that callback still runs. Source changes, unsafe custody and invalid final lifetime cannot produce a successful set.

This is an explicit custody-library API. No HTTP, AHP, MCP, model, worker or default host registration starts it. A trusted signer is supplied; this increment creates no signing-key store or automatic key provisioning. The private output contains encrypted profile material and readable custody/scope metadata, including source paths and owner identifiers. Protect the complete artifact and destination stream. A signature supplies integrity under the pinned key; it does not encrypt the artifact or approve restoration.

## Retained verification and read-only rehearsal

Parse bounded private bytes, then verify against independently selected exact artifact/customer/cell/namespace and signer pins:

```csharp
using var selected = CredentialRestrictedRecoverySets.Parse(retainedBytes);
var verified = CredentialRestrictedRecoverySets.Verify(
    selected, reviewedAuthority, pinnedPublicKey, reviewedNamespace, clock);

var rehearsal = new CredentialRestrictedRecoveryRehearsal(
    reviewedNamespace, existingKeyProvider, clock);
var result = await rehearsal.RehearseAsync(
    selected, reviewedAuthority, pinnedPublicKey, cancellationToken);
```

Do not derive approval by accepting the authority advertised by an incoming artifact. The exact authority selection must come from trusted owner review. Structural/signature verification validates retained metadata and its signature; it does not claim AES/HMAC authentication. Rehearsal separately authenticates every retained positive envelope and original absence proof with supplied existing source keys. It consults no live source directories and returns only bounded dependency/result metadata. Artifact disposal cannot invalidate the private snapshot already owned by an awaited operation.

No plaintext token, encryption key or retained envelope bytes are returned in the result or persisted. Available keys prove authentication at rehearsal time; they do not prove independently recoverable key custody, greatest-ever/latest state, current provider permissions or remote token revocation. The underlying encrypted envelope format remains unchanged, so this policy does not make ciphertext cryptographically non-portable to an actor holding its customer key.

## Remaining recovery gates

The separate [restricted inactive staging protocol](foundation-restricted-recovery-staging.md) preserves this policy through destination encryption and keyed completed-stage verification. [Restricted checkpoints](foundation-restricted-recovery-checkpoints.md) add accepted-set custody and a concrete current-head lease through staging. Restricted complete-scope human review and later joint execution-time custody remain required before changing recovery. Older General protocols remain unavailable for restricted artifacts. Later recovery release/adoption requires its own exact approval, one-use admission, supported-consumer coverage and coordinated credential/database activation. Independent latest-set/key backups, supported service/storage behavior and production acceptance remain open in the [commercial roadmap](plans/platform-studio-commercial-release.md#next-implementation-order).

Local qualification uses disposable identities, keys and namespaces with synthetic provider payloads. Actual executed counts and failure evidence are recorded in [validation](validation.md); source implementation does not establish live provider or production recovery acceptance.
