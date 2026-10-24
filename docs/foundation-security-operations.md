# Foundation security and operations

24 October 2026. These controls are implemented for local qualification within the [commercial release plan](plans/platform-studio-commercial-release.md). P0/P1 are **In progress** and P2 breadth remains Planned behind the foundation gates; this guide does not declare production readiness, authorize a provider operation, or substitute for customer consent. Start with the [foundation service guide](foundation-service.md) for the host, workflow and API contracts, and [ADR 0001](adr/0001-foundation-dedicated-cell-sqlite.md) for the selected SQLite backend.

The dedicated customer host preserves the existing desktop portal. Its signed identities, durable state and bounded agent bridge are separate from an operator's existing browser, Azure CLI, GitHub CLI and Codex caches. A customer may own its GitHub repository/workflow or Azure DevOps organization/project/repository/pipeline; an application role does not grant access to any of them.

## Identity keys and discovery

[FoundationTokenService](../src/SelfService.Foundation.Host/FoundationTokenService.cs) selects one of two explicit modes:

| Mode | Implemented behavior | Operator responsibility |
|---|---|---|
| Pinned public keys, default | Validate the configured RSA signature, issuer, tenant, audience, lifetime, calling client, token kind, scope and role claims. Public cell configuration rejects private, weak or ambiguous signing keys. | Supply and maintain reviewed public keys and exact issuer/client policy. Loading configuration does not create an identity or acquire a token. |
| Explicit Entra discovery | `enableEntraDiscovery` selects the tenant-specific public Entra v2 metadata adapter. Metadata issuer and the configured tenant/common JWKS address must match exactly. Signing-key issuer constraints are checked before a key enters a tenant's snapshot. | Decide when public metadata network access is allowed and qualify the actual customer identity/consent path separately. Sovereign-cloud and claims-mapping/custom-app metadata addresses are not implemented by this adapter. |

[OidcIdentityValidator](../src/SelfService.Foundation/OidcIdentityValidator.cs) publishes complete per-tenant snapshots atomically. Defaults are a 24-hour key lifetime, refresh after 12 hours, a five-minute minimum refresh interval, one-minute failure backoff and ten-second document timeout. Unknown key IDs cannot create an unbounded refresh queue or cache. Concurrent initial requests share the same fetch. A failed refresh may use a still-valid known key; it cannot extend that snapshot's expiry. Expired discovery never falls back to static bootstrap keys. Successful replacement removes keys absent from the new snapshot.

[The HTTPS transport](../src/SelfService.Foundation/OidcDocumentTransport.cs) uses only the preconfigured official metadata/JWKS addresses, bounded JSON bodies, no redirects, cookies or proxy, and no credentials. Token-supplied `jku`/`x5u` addresses are rejected rather than fetched. Its default transport denies network access; the JSON host configuration constructs the HTTPS transport only when discovery is explicitly selected. This implements Microsoft's distinction between tenant/issuer validation and the issuer attached to a signing key. [Microsoft token and signing-key validation guidance](https://learn.microsoft.com/en-us/entra/identity-platform/access-tokens).

After authentication, the server fixes CustomerId, CellId, project and environment. Current persisted grants intersect human token roles. Workload clients require their admitted service-principal object ID/client and cannot approve requests. Two customers in one Entra directory remain different application customers. Discovery, token acquisition, app-role assignment, consent and downstream provider permissions are separate operations; a healthy metadata cache proves only the discovery path.

## Customer credential custody

[CredentialVault](../src/SelfService.Foundation/CredentialVault.cs) stores AES-GCM encrypted envelopes using a mandatory host-supplied `ICredentialKeyProvider`. There is no embedded master key or fallback to a desktop token cache. Encryption authenticates the key ID and profile metadata. Each profile binds customer/cell/project/environment, provider, connection ID/generation/digest, authProfileId, audience, required scopes, expiry and profile generation. A profile cannot be silently retargeted to another identity; rotation/revocation uses expected-generation checks. Resolution rechecks exact bindings, enabled state and expiry. Secret/key/lease types reject JSON serialization and redact their string representation. Managed buffers are cleared on disposal; this is not a claim that immutable strings or the entire process heap can be securely erased.

The optional [Windows CurrentUser key provider](../src/SelfService.Foundation/CredentialWindowsUserKeyProvider.cs) creates a cell-bound key namespace and DPAPI-protected random encryption keys with customer/cell/key-specific entropy. Its directory ACL is restricted to the current Windows identity. DPAPI normally ties decryption to the protecting logon identity and computer, with platform exceptions such as roaming profiles; it does not isolate two cells running under the same trusted user from that user. Production cells need separately qualified OS/service identities and protected storage/key custody. [Windows DPAPI behavior](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata).

These components are trusted host composition APIs, **not** secret-import HTTP endpoints or model tools. No public command in this increment accepts a PAT, raw access token, private key or encryption key through an application request. Cell JSON contains public metadata and opaque references only. The default JSON-configured host does not register a live authenticated pipeline transport.

An operator's credential onboarding flow is therefore:

1. Select a dedicated customer cell and its separately protected key provider and credential directory. Keep signing keys, encryption keys and application domain data in their intended trust boundaries.
2. Admit a customer connection with its repository/pipeline identifiers, allowed refs/environments/inputs, source-revision contract and opaque authProfileId. Derive credential scope from that stored connection, not caller/model JSON.
3. Configure a trusted host callback for the reviewed GitHub App signing handle or external workload assertion. Registration, installation, federation trust and consent occur outside these library calls.
4. Qualify acquisition, exact repository/project access, token lifetime/rotation/revocation and provider rights using a separately authorized acceptance profile. Current tests use synthetic material and captured HTTP handlers.
5. Resolve a short-lived credential only after durable authority is rechecked. Never return the resolved secret to the API caller, model, audit record, artifact or source archive.

[Acquisition adapters](../src/SelfService.Foundation/CredentialAcquisition.cs) implement two provider protocols. The GitHub App adapter signs an app assertion, requests a repository-restricted installation token, and verifies the returned repository and requested permission projection before storage. GitHub installation access still depends on the installed app's actual repository access and permissions. [GitHub installation authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation). The Entra adapter obtains an assertion from a host callback and exchanges it at the tenant's v2 token endpoint for the Azure DevOps resource scope. The external assertion requires a separately configured federation trust; obtaining a token does not establish ADO organization membership or permission to queue a pipeline. [Microsoft workload identity federation](https://learn.microsoft.com/en-us/entra/workload-id/workload-identity-federation).

Both token-exchange networking and [authenticated dispatch](../src/SelfService.Foundation/PipelineAuthenticatedTransport.cs) are disabled by default. Trusted code must opt in explicitly; request JSON cannot enable them. The dispatch adapter requires a one-use, short-deadline permit bound to the exact durable dispatch/wire request, validates the credential profile, and rechecks current durable authority after asynchronous key resolution before sending. It sends credentials only to fixed HTTPS provider origins, follows no redirects and returns a restricted response projection. A correlated accepted run is not a verified deployment. Acquisition results retain `ProviderPermissionsVerified=false`; mock-handler results remain marked as mock.

## Reviewed administration and support

[Schema migrations](../src/SelfService.Foundation/FoundationMigrationStore.cs) now support schema version 3. The store verifies database/cell binding and uses transactional v1→v2→v3 upgrades with checksummed immutable migration history, SQLite integrity/foreign-key checks and audit-chain validation. Unknown versions or altered migration history fail closed. This is actual SQLite migration code, not SQL Server migration qualification. Back up and independently protect evidence before a production upgrade; no destructive downgrade or cross-backend migration is supplied.

[Administrative routes](../src/SelfService.Foundation.Host/FoundationAdministration.cs) use the same signed principal and exact project/environment context. CustomerAdmin prepares a scoped immutable administrative plan; a separate Approver reviews its exact digest before execution. Reviewers cannot approve their own request or approve an access/onboarding plan for themselves. Current grant versions, target versions, policy, scope and plan state are checked again before the local transaction. The final administrator cannot be removed through these operations.

Under the common API prefix, the main operator sequence is:

| Step | Route suffix | Evidence or effect |
|---|---|---|
| Inspect | GET `/administration/state`, `/administration/scopes` | Current cell lifecycle and admitted scopes, subject to administrative roles. |
| Prepare | POST `/administration/scopes/plan`, `/administration/memberships/plan`, `/administration/support/plan`, or `/administration/lifecycle/plan` | Purpose-bound immutable plan and digest; no change from preparation alone. |
| Review | GET `/administration/plans/{id}`; another eligible human POSTs `/administration/plans/{id}/approve` | Exact separate approval. |
| Execute locally | POST `/administration/plans/{id}/execute` with the same digest | Revalidated administrative transaction and audit event. |
| Urgently deny | POST `/administration/access/revoke` | Deny-only scoped revocation; no grant or elevation bypass. |
| Export/retain | POST `/administration/exports` or `/administration/retention/plan` | Scoped metadata export, or an exact reviewed retention candidate set. |

The common prefix is `/api/foundation/customers/{customerId}/projects/{projectId}/environments/{environmentId}`. Bodies contain stable actor IDs, scopes, purpose and exact digests, never private credentials. See the source records for field names. The simpler membership route rejects new privileged standing-role grants; those use the reviewed plan path. A separately configured initial human approver can bootstrap the review path without giving the administrator implicit operational rights.

[Support grants](../src/SelfService.Foundation/FoundationAccessStore.cs) are human-only, purpose-bound, scoped and limited to at most 24 hours. They permit catalog/evidence reads, not changes, approval, dispatch or credential retrieval. Their approval/version/expiry is rechecked and support access is audited. The grant cannot coexist with standing operational privilege in that scope. Expiry or revocation removes support authority; SupportDelegate is not an unrestricted standing administrator role.

[Lifecycle transitions](../src/SelfService.Foundation/FoundationLifecycleStore.cs) cover Active, Suspended, Offboarding and Offboarded. Disabling a cell cancels undispatched work and holds dispatched effects as OutcomeUnknown. Offboarding disables/increments connection generations; completion refuses unresolved unknown effects. Safe reviewed administration and metadata exports remain available to eligible roles while normal operation is disabled. This does not delete Azure resources or revoke externally issued tokens. External revocation and final data disposition remain separate operator duties.

Exports contain scoped metadata/digests and audit records, not artifact bytes, connection configuration, plan payloads or provider receipts. Retention requires at least 30 days, a bounded reviewed candidate set and a quiescent scope with no unexpired plans or pending/uncertain jobs. It removes current artifact content while retaining tombstones and audit. It does not securely erase SQLite WAL/free pages, snapshots, backups or storage media. [Retention implementation](../src/SelfService.Foundation/FoundationRetentionStore.cs).

## Agent and model isolation

The customer bridge and HTTP workflow operations share [FoundationWorkflowService](../src/SelfService.Foundation.Host/FoundationWorkflowService.cs). [The AHP channel](../src/SelfService.Foundation.Host/FoundationAgentChannel.cs) refreshes token validation and current authorization rather than accepting model-supplied acting identities. It exposes typed foundation operations with no model approval tool. Provider descriptions, repository content and model advice cannot grant a capability.

[FoundationModelRuntime](../src/SelfService.Foundation.Host/FoundationModelRuntime.cs) is disabled unless a trusted model client is configured. Its native Codex adapter creates customer/cell/actor-bound runtime roots with ownership markers and link rejection. Authentication comes only from an explicitly supplied callback whose customer/cell/actor binding must match the invocation; an absent callback fails rather than copying an operator's desktop login. The runtime uses the existing isolated Codex process and bounded MCP bridge. It checks the pinned skill hash, revalidates actor/scope during tool use, and stores advice as a scoped artifact. Advice reports no execution or deployment authority. Current synthetic-model tests do not establish real ChatGPT/Codex account authorization, customer entitlement or successful live inference.

## Signed audit anchors and recovery custody

Local append-only SQL interfaces and hash chaining detect ordinary corruption but do not defeat an administrator who can rewrite every local row and hash. [Signed audit anchors](../src/SelfService.Foundation/FoundationAuditAnchorStore.cs) add an independently verifiable commitment to an audit prefix. A trusted host supplies an RSA signing handle; creation requires a current Auditor grant. RSA-PSS/SHA256 binds anchor/schema/type, customer/cell/project/environment, signer/role version, sequence/hash, UTC time and pinned key identity/fingerprint. Verification permits legitimate subsequent append and detects tested full-chain rewrites or truncation through the committed prefix.

Host composition uses `CreateAuditAnchor(...)`, `FoundationAuditAnchorFiles.WriteNew(...)`, `ReadVerified(...)` and `VerifyAuditAnchor(...)`. These are C# APIs, not a private-key configuration command or HTTP/model signing endpoint. Supply the signing handle from independently controlled custody; provide only a pinned public key to verifiers. The file interface uses bounded strict JSON, rejects links/traversal and creates new files atomically without overwrite. Historical signatures do not automatically expire; a verifier may impose an explicit maximum age.

Keep the latest trusted anchor, public-key pin and signing-key custody outside the database administrator's ability to replace them together. Local anchor files are not WORM storage, and a privileged owner controlling both signer and evidence can replace both. An anchor commits only through its stated sequence; it does not attest later unanchored events. Signed audit prefixes are not digital signatures over backup database bytes. The existing backup hash/length manifest still requires independent protection.

The [backup/restore implementation](../src/SelfService.Foundation/FoundationStoreBackup.cs) uses SQLite online snapshots, verifies binding/integrity and holds recovered pending work while advancing policy. Fence the original host before activating a restored copy. Recovery must account for the separately protected credential keys, external token revocation, anchor custody, backups and retained copies; a database restore alone cannot prevent two hosts from running or satisfy production RPO/RTO.

## Operational limits and release evidence

[Independent workers](foundation-independent-worker.md) share the local cell queue through exact leased/fenced operations, with current-schema/no-create/no-migration store opens, an explicit inherited-handle/environment allowlist and original-process-handle cleanup. `/operations/workers` exposes only PlatformOperator aggregates. [Frozen exports](foundation-paged-exports.md) enforce owner/version/policy/expiry and bounded storage; their canonical commitment envelopes permit offline digest verification. [Configuration and technical entitlements](foundation-feature-configuration.md) use separate human approval, monotonic generations and scope cohorts. A trusted-host signed-mode requirement prevents API policy changes from restoring unsigned development features; authorized recovery/audit/export stays available. These controls do not grant provider rights or commercial clearance.

[Logical limits and status](../src/SelfService.Foundation/FoundationOperationsStore.cs) include 128 active jobs, three predispatch claim attempts, 10,000 plans, 1,000 current artifacts, 256 MiB of current artifact bytes and 100,000 audit events per cell. The host additionally admits at most 16 concurrent requests with no waiting queue. These are local admission controls, not distributed rate limits, physical disk quotas or production capacity commitments. SQLite pages/WAL, tombstones, exports, backup files, runtime homes and telemetry can consume additional storage. Per-host/cell isolation does not prove a shared-host or multi-replica noisy-neighbor SLO.

Anonymous `/health` reports bounded storage readiness and disabled external dispatch. The authenticated `/operations/health` path requires PlatformOperator and returns aggregate lifecycle, queue/outbox and artifact totals without customer payloads or credentials. Its separate operational membership resolution preserves health access while the cell is inactive; it does not grant evidence or execution access. Production monitoring, capacity targets, alerting and host/disk enforcement still need qualification.

Use actual results from the [validation record](validation.md), not a copied test count:

```powershell
./scripts/Test-Foundation.ps1
./tests/release/commercial-release.tests.ps1

# Freeze hashes from actual completed TRX files after the final source manifest check.
./scripts/Test-CommercialRelease.ps1 `
  -CaptureTestEvidenceIndex artifacts/commercial-release/test-index.json `
  -TrxPath @('artifacts/test-results/foundation/foundation.trx',
             'artifacts/test-results/foundation/foundation-portal-regression.trx')

./scripts/Test-CommercialRelease.ps1 `
  -TestEvidenceIndex artifacts/commercial-release/test-index.json
```

The [mechanical release profile](../scripts/Test-CommercialRelease.ps1) verifies the existing source manifest without regenerating it, hashes actual TRX files, derives passed/skipped outcomes from individual results, rejects missing/stale/tampered/unknown evidence, and checks disabled deployment targets and classified source artifacts. Capture explicitly does not independently attest which source was executed. Protect the index separately. Convention-named test/fixture content exclusions and scanner limitations are disclosed; passing this profile is not a full Git-history/final-archive secret scan, vulnerability review, SBOM/license review or legal clearance. The receipt always reports `productionReady=false` and no publication/external-execution authority.

The optional [isolated AHP receipt test](../tests/release/agenthost-receipt.tests.ps1) uses an already built portal on an ephemeral local port, preserves the primary process and stops only its owned PID/start-time/path. Package AHP counts now derive from named successful checks and the actual provider catalog. Neither that test nor a local Release build establishes a signed ARM64/x64 clean-machine install/update/repair/uninstall qualification.

Production admission still requires live identity/consent and provider permission acceptance, supported TLS/hosting and OS/service isolation, protected encryption/key/backup/anchor custody, physical quotas and measured operational recovery/SLOs, final-package signing and scans, ownership/license/privacy/support review, and explicit approval for external execution or publication. No Azure deployment, live pipeline submission or new permission grant follows from any command shown here.
