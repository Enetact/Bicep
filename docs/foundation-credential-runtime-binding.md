# Bind credential use to an active cell instance

Schema 12 adds an explicit credential-runtime binding for Foundation-mediated pipeline dispatch. It pins the current database instance and location commitment, the complete vault/rollback namespace identity, and the actual activation authority's custody identity and selected generation. A concrete per-operation activation lease is retained through the owned HTTP request, response body and disposal.

This is a prerequisite for supported recovery and source-use fencing. It does not adopt a staged namespace, release a restored-cell execution hold, revoke an issued provider token or fence generic direct vault consumers, independently composed permit sources, another host or a malicious OS owner. No default HTTP, AHP, MCP, model or worker registration enables a live provider operation.

## Provision explicitly, then require the binding

Trusted owner composition establishes the binding once for a fresh, Active, never-restored cell. Provisioning uses the actual activation lease and guarded namespace metadata; a request DTO, boolean or caller-supplied status cannot stand in for those guards. It accesses no credential key or secret. Repeated provisioning, startup repair and rebinding are refused.

The binding includes a commitment to the actual canonical authority root, its owner and marker. Equal IDs copied to another directory do not establish shared lock custody. Public binding metadata contains commitments rather than private paths, owner SIDs or credential material.

Never-bound cells retain their existing disconnected legacy permit behavior. Once a binding or its durable provision history exists, the ordinary Foundation store permit path requires a concrete bound session. Removing runtime configuration, selecting a legacy transport, deleting the current binding or using a new restored instance cannot silently restore that fallback. The explicit bound transport always requires its exact current persisted binding, including on a never-bound cell.

Trusted composition starts with actual objects and independently reviewed identities, not values from an incoming job:

```csharp
var binding = FoundationCredentialBoundTransport.BindCurrent(
    store, vault, configuredActivationAuthority,
    expectedVaultNamespace, expectedActivationCustodyIdentity,
    expectedDatabaseInstanceId);
```

This is an explicit cutover call. Constructing a transport or starting a host never invokes it automatically. After cutover, `FoundationCredentialBoundTransport` takes the exact binding digest and a `FoundationCredentialBoundDispatch` containing the job/worker/fence/scope/plan/source/catalog pins. `ForMockHandler` exercises that same authorization path with a trusted synthetic handler; the external-dispatch constructor defaults to disabled. Binding does not enable the Foundation live-dispatch feature.

Provisioning also requires a quiescent changing-work boundary: queued, claimed or dispatched jobs and active worker registrations prevent cutover. The owner must stop never-bound or direct consumers outside this database boundary separately. Work admitted before the policy cannot be presented as safely fenced by it; this does not establish complete consumer closure.

An uncertain binding commit reports `CredentialRuntimeBindingUncertain`. Reread protected binding status and its exact digest rather than automatically retrying, repairing or rebinding. A missing acknowledgement does not prove that the policy was not committed.

## Hold real authority through the operation

The bound path acquires its own activation lease before store/key access, checks the actual configured custody and complete namespace against the persisted binding, and reuses the current durable job/connection/grant/source/entitlement checks. The namespace and current authority are checked again after asynchronous key lookup. Vault admission custody remains held until HTTP invocation, preserving existing revocation/rotation ordering, then releases before network waits.

The activation lease remains held while awaiting headers, reading the bounded response body and disposing the owned request/response. A cooperating activation switch remains Busy during that interval. Cancellation or transport disposal must not release the lease while an uncooperative provider is still using the operation. Failures after HTTP invocation retain uncertain-effect semantics; a lost response or later authorization failure cannot prove that nothing happened remotely.

Redirects, provider error bodies, token reflection, response-size bounds and the default-disabled external dispatch contract retain the existing transport protections. Binding does not replace human approval, current membership, connection generations, lease/fence checks or cloud-side permissions.

## Restore, history and remaining recovery work

`credential_runtime_bindings` retains immutable history under additive schema 12; earlier migrations and checksums remain fixed. Read-only backup admission validates retained structure and commitments without opening original private paths or repairing the incoming database. A restored instance retains historical bindings but cannot use them as current authority. The existing [restore execution hold](foundation-recovery-execution-hold.md) remains independent and unreleased.

Changing activation generation or namespace does not authorize an automatic rebind. Later recovery release/adoption needs separate changing-operation approval, exact current checkpoint/stage/binding/instance pins, one-use admission, supported-consumer closure and coordinated credential/database activation. Existing schema-9/10 read-only recovery verification cannot grant that authority. Independent latest/key custody, multi-host behavior, live provider acceptance and production qualification remain open in the [commercial roadmap](plans/platform-studio-commercial-release.md).
