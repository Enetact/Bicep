# Current durable authorization before credential admission

`FoundationCredentialDispatchPermitSource` supplies the authenticated transport's existing permit callback from the current Foundation database. It closes a trusted-composition gap: a callback no longer needs to reconstruct authority from a previously captured `FoundationDispatch` record. Each callback reads the actual job, plan, approval, memberships, connection and feature policy again. The dedicated host and independent worker still use disconnected transports; this increment adds no live configuration, HTTP/MCP/AHP route, provider permission or cloud execution.

The public C# constructor is for trusted cell/worker composition:

```csharp
var permits = new FoundationCredentialDispatchPermitSource(
    store, jobId, workerId, fence, credentialScope,
    expectedPlanDigest, expectedSourceRevision, expectedCatalogDigest,
    entitlementVerifier, runtimeVersion);
```

All inputs come from the reviewed host/worker context, never model arguments or caller-selected request fields. The scope is validated and detached, including its required-scopes array. Customer/cell must match the supplied store. The constructor pins that store's current database instance plus the exact job/worker/fence, approved plan digest, source revision and catalog digest. Empty catalog digests remain supported for existing pipeline plans without catalog evidence; this does not create such evidence. The entitlement verifier and runtime version are fixed trusted composition values. There is no default trust key or unsigned-mode override inside this adapter.

These supplied pins restrict which durable state can be used. They do not grant authority, independently authenticate a machine, prove a repository's current contents or make a passed `FoundationDispatch` authoritative. Trusted composition must refresh the expected source/catalog pins when admitting a separately reviewed runtime context. This adapter performs no repository fetch, catalog discovery, directory registration or credential acquisition.

## One current store snapshot per callback

`AuthorizeAsync` uses a new deferred SQLite read transaction and reuses the store's existing validation methods. It does not claim, dispatch, complete or reconcile a job, write an audit event, bootstrap a user or migrate a schema.

| Check | Required invariant |
|---|---|
| Store and activation | Current schema/customer/cell, the pinned database-instance identity and any configured activation guard remain valid. A disposed/switched activation lease fails closed. |
| Durable job | The exact job is already `Dispatched`, with the same worker ID, positive fence and unexpired lease. Queued, cancelled, terminal and restored unknown-outcome jobs cannot provide a permit. |
| Current plan | Existing `ValidatePlanAuthority` checks active cell, plan lifetime, policy version, current requester/approver roles and versions, current connection generation/scope, plan digest and exact approval evidence. The queued operator's current role version must also match. |
| Trusted pins | Current approved plan digest, source revision and catalog digest equal the composition's pinned expectations. Pins cannot replace a missing or altered stored approval. |
| Feature/runtime policy | The existing `PipelineMockDispatch` gate is re-evaluated inside the same transaction using the trusted entitlement verifier/runtime. Disabled rollout/features, signed-mode restrictions, expired entitlement and invalid/incompatible runtime fail closed. This remains the disconnected feature vocabulary; no live feature is introduced. |
| Exact transport request | `CredentialDispatchPermit.FromDurableDispatch` rebuilds the current pipeline connection/request/approval and compares the complete wire commitment, including method, URI, headers, body and binding. Credential profile, audience, required scopes and connection digest remain bound. |

The existing authenticated transport calls the source twice: before resolving custody and again after potentially asynchronous key lookup. Both calls perform new database reads. Revocation, cancellation, suspension, changed policy/connection/approval or lease loss committed during key lookup is therefore observed before a POST can be admitted. Each successful call returns a fresh nontransferable in-process permit with the existing plan/lease/one-minute expiry restriction. The original permit's lifetime is not extended by a later callback. Store/SQLite failures are sanitized as credential authority/storage failure codes; caller cancellation retains its cancellation token.

## Ordering and remaining boundaries

Each transaction establishes a moment-in-time database snapshot. It ends before the callback returns. The vault admission lock separately orders credential replacement/revocation against the transport's authenticated read and `SendAsync` invocation, as described in [credential lifecycle](foundation-credential-lifecycle.md). It does not lock the database, remote repository or provider policy after the transaction. This increment does not claim cross-resource atomic revocation, remote cancellation, external exactly-once delivery or prevention of changes made after the final database check.

The final authorizer does not reenter the vault: it reads only its bound store and trusted in-memory policy. No SQLite transaction or vault admission guard is held while awaiting the provider response. Trusted callbacks remain subject to the transport's cooperative cancellation contract. Existing uncertain-outcome handling and no-automatic-POST-retry rules remain necessary. A new permit does not replay a completed/unknown job or clear recovery holds.

When activation authority is required, trusted composition must supply the already guarded store and retain its valid activation lease for the runtime lifetime. The source rechecks that guard; it does not provision an authority or hold a separate machine lease. Dedicated OS/service identities, physical storage/key isolation, multi-host fencing, live consent/provider permissions and production recovery remain separate acceptance work.

## Local qualification

`FoundationCredentialDispatchPermitTests` uses real temporary SQLite databases and the actual vault/authenticated transport with `ForMockHandler`. It verifies current authorization on both callbacks for synthetic GitHub and Azure DevOps requests; detached scope arrays; invalid customer/cell/job/worker/fence/plan/source/catalog/scope/wire bindings; cancellation; real local activation leases; restored work held unknown; current feature/runtime restrictions; and signed-entitlement expiry without a policy change.

The asynchronous custody tests pause the key provider, change durable requester/approver/operator authority, approval, policy, source/catalog, connection, cancellation/suspension, instance identity or lease, then resume. They require zero mock POSTs and verify that denial releases the vault guard for subsequent revocation. Signing keys, credentials, identities, stores and provider responses are synthetic. No external network request, real sign-in, Azure deployment, primary portal restart or provider permission change is part of this qualification.

On 24 October 2026 the focused cohort passed **35/35**, with no skips, under isolated `artifacts/foundation-durable-dispatch-check`. The actual receipt is `artifacts/foundation-durable-dispatch-check/results/durable-dispatch-final.trx`. Locked restore retained normal NuGet auditing. This is focused local acceptance for the new adapter; it does not replace the full release cohort or establish live identity/provider acceptance.
