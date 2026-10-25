# Foundation feature configuration

Implemented source, 24 October 2026. The dedicated customer foundation has reviewed, persisted feature configuration and offline signed technical entitlements. HTTP, AHP and model tools share feature decisions through the workflow service. The disconnected worker rechecks availability inside its durable dispatch transaction. See [foundation service](foundation-service.md), [security and operations](foundation-security-operations.md) and the [commercial release plan](plans/platform-studio-commercial-release.md).

`LocalPreview` explicitly enables the existing local capabilities in a new scope when the trusted host permits development preview. A separately configured mandatory signed-mode policy can require a valid entitlement for those capabilities instead. The current foundation still needs commercial licensing decisions, issuer operations, customer onboarding, production operations and legal terms before a commercial release can be declared complete. Customers with control of source code, the host configuration or the database remain inside the trusted operator boundary; this increment provides no commercial anti-tamper guarantee.

## Features and authority

The immutable server policy recognizes these configurable features:

| Feature | Shared enforcement |
|---|---|
| `source.catalog` | Customer-scoped source catalog |
| `source.bicep` | Parameterized Bicep/AVM source generation |
| `model.review` | Model review start, refreshed tool callbacks and final result |
| `pipeline.plan` | Customer pipeline plan creation |
| `pipeline.mock-dispatch` | Enqueue and atomic disconnected-worker dispatch admission |

The server always retains authorized `administration.safe`, `audit.read` and `export.read` feature availability. Existing role, customer/cell and project/environment checks still apply. CustomerAdmin retains administrative duties; Auditor retains audit and export duties. Feature availability grants no role, Entra permission, credential, approval, customer access, GitHub/ADO right or Azure right. Both entitlement and feature decision objects report `externalExecutionAuthorized: false` and `identityGrantAuthorized: false`.

HTTP, AHP and MCP callers use the same workflow service. Models receive no configuration approval or license issuance tool. The dedicated host rejects a live transport, and the default pipeline transport denies external dispatch. A signed document, a configuration change, a successful mock receipt and a paid edition label each leave those execution boundaries in place.

## Mandatory signed-mode host policy

Protected API and independent-worker configuration can set `requireSignedEntitlement: true`. The default is false for existing local development flows. Configure this consistently on both processes in a customer cell; the API setting governs shared HTTP/AHP/model workflows, and the worker setting governs its atomic dispatch admission.

The trusted composition creates an immutable `EntitlementVerifier` with `requireSignedMode: true`. This setting belongs to host policy and has no field in the customer feature-change DTO. A scoped application administrator, reviewer or model cannot disable it with a feature change or rollback. LocalPreview availability, configuration preparation and configuration execution fail with `EntitlementSignedModeRequired` under this policy. Generation-zero metadata remains readable, but its preview features are unavailable until a valid Licensed configuration is reviewed and executed. Previously approved preview plans are also rechecked against this policy at execution.

With the requirement active, a valid signed document enables only its listed server-recognized features, subject to ordinary roles, rollout, compatibility and expiry. Expiry does not fall back to LocalPreview, and rollback to generation zero cannot restore unsigned access. Authorized safe administration, audit and export remain available for recovery and evidence. Trusted operators who can change the protected host configuration or application binaries remain outside this application-level restriction; OS/configuration custody and commercial anti-tamper controls require separate qualification.

## Read, prepare, approve and execute

All paths below use this customer scope prefix:

```text
/api/foundation/customers/{customerId}/projects/{projectId}/environments/{environmentId}
```

Use the existing signed HTTP identity flow with current scoped application membership. The commands below show JSON bodies, with IDs and digests taken from actual responses. Credentials never belong in these bodies.

1. `GET /administration/configuration` returns the current `generation`, `digest`, `administrativePlanId` and configuration. CustomerAdmin, Approver and Auditor can read this metadata, including during customer suspension or entitlement expiry. Generation zero is the explicit LocalPreview baseline.
2. A scoped CustomerAdmin calls `POST /administration/configuration/plan`. For example, this generation-zero development change retains catalog and source generation when mandatory signed mode is disabled:

   ```json
   {
     "configuration": {
       "mode": "LocalPreview",
       "enabledFeatures": ["source.catalog", "source.bicep"],
       "rolloutPercent": 100,
       "minimumRuntimeVersion": "1.0.0",
       "maximumRuntimeVersion": "1.0.0",
       "entitlement": null
     },
     "expectedGeneration": 0,
     "purpose": "Review development source features"
   }
   ```

3. Inspect the returned immutable administrative plan, including its entire payload and exact digest. Its kind is `config.update`; it freezes requester grant version, cell policy version, scope, expected generation, previous configuration digest, new configuration and purpose. The administrative plan expires after 15 minutes.
4. A different current scoped Approver calls `POST /administration/plans/{planId}/approve` with `{"digest":"<returned exact plan digest>"}`. Self approval is denied. A conversation, model response or feature document supplies no approval.
5. A scoped CustomerAdmin calls `POST /administration/plans/{planId}/execute` with that same digest. Execution rechecks approval bindings, current grants and versions, plan expiry, customer state, configuration generation, runtime compatibility and the current entitlement. Only then does it persist the next executed generation and audit receipt.

Preparation and execution require an active customer cell. Entitlement expiry does not prevent a properly authorized administrator and separate reviewer from preparing and executing a recovery configuration. Customer suspension still governs changes; existing safe lifecycle and urgent revocation paths retain their own authorization requirements.

Each successful change increments the cell policy fence. Existing immutable pipeline plans and queued jobs using the previous policy cannot proceed. A concurrent prepared configuration change using the previous fence or generation also fails and needs a new review. Configuration history uses the existing administrative-plan and immutable approval ledgers; it survives process restart without a browser session or an in-memory feature cache.

## Progressive rollout and compatibility

`enabledFeatures` is an explicit subset of the server's five configurable features. Unknown, duplicate or differently cased feature identifiers fail validation. An empty set disables all five while retaining authorized safe administration, audit and export access.

`rolloutPercent` is an integer from 0 through 100. The stable cohort hashes customer, cell, project, environment and feature. A requester, reviewer and worker in that same scope receive a consistent feature cohort. Their roles remain independently enforced. Zero disables the configurable features; 100 admits every listed feature whose other requirements pass. Changes to this percentage require the same exact reviewed plan.

`minimumRuntimeVersion` and `maximumRuntimeVersion` are inclusive three-component numeric versions. The current workflow runtime identifies itself as `1.0.0`. Invalid windows fail preparation. A running version outside the approved window cannot use configurable features. Administrative metadata and authorized recovery/evidence access remain available. Configuration payload schema version 1 is validated separately; database migrations retain their existing explicit schema compatibility controls.

The current ledger implementation bounds executed configuration history to 2,048 changes per customer cell, across all scopes. It fails closed at capacity rather than pruning approval or audit evidence. A larger-scale history/storage migration remains an operational design decision.

## Offline signed entitlements

`Licensed` mode requires a `SignedEntitlement` envelope containing `document` and Base64 `signature`. The document fields are:

| Field | Meaning |
|---|---|
| `schema` | Exact `platform-studio.entitlement.v1` |
| `issuer`, `keyId` | Exact configured issuer and signing-key identifiers |
| `licenseId`, `sequence` | Stable technical document identity and positive increasing sequence |
| `customerId`, `cellId` | Exact dedicated customer/cell binding |
| `edition` | Technical metadata; it confers no permissions |
| `features` | Explicit recognized configurable feature IDs |
| `notBefore`, `expiresUtc` | UTC timestamps with millisecond precision; expiry is exclusive |

Use `EntitlementVerifier.CanonicalDocument` as the canonical byte contract. It writes the documented fields in fixed order, sorts feature identifiers and represents timestamps as Unix milliseconds. RSA-PSS with SHA-256 signs those bytes. Verification also computes a SHA-256 canonical document digest. PKCS#1 v1.5 signatures, altered claims, wrong keys, untrusted issuers, wrong customer/cell bindings, unknown/duplicate features, inactive validity windows and noncanonical signature encoding are rejected. The raw JSON parser rejects unknown, missing, duplicate and case-ambiguous properties.

The trusted host supplies `EntitlementTrustKey` entries with `issuer`, `keyId` and `subjectPublicKeyInfoPem`; the JSON host configuration exposes these as `entitlementTrustKeys`. Only public SubjectPublicKeyInfo PEM keys are admitted, with RSA sizes 2,048 through 8,192 bits and at most 32 trusted issuer/key pairs. Private signing keys belong to a separately governed issuer. There is no application endpoint for importing them or minting a real entitlement. The verifier snapshots public keys immutably; changing host trust requires composing a new verifier through the governed host lifecycle.

`Licensed` configuration may enable only features present in the verified document. Verification occurs during preparation, execution and feature use. The worker also verifies inside dispatch admission, so expiry alone blocks a queued mock run without requiring a policy update. Removing a public key from the host's trust removes acceptance of documents using that key when the replacement verifier is active.

The cell retains a customer-wide high-water mark through executed configuration history, including retired generations and other project/environment scopes. A lower sequence for the same issuer/license ID is rejected. Reusing the same sequence requires the same canonical document digest; a different randomized PSS signature on the identical document is acceptable. Changing claims or rotating the document key requires a higher sequence. Switching through LocalPreview, restarting or selecting another scope does not reset this history. Applying a higher sequence in one scope also makes older sequences unavailable in other scopes; coordinate newly reviewed configurations across those scopes during issuer rotation or renewal.

Verification is offline and uses the trusted host clock. No online issuer, revocation-list retrieval, grace period or commercial issuance service is implemented. These operational controls need an explicit release design and acceptance evidence before production licensing claims are made.

## Rollback and recovery

A scoped CustomerAdmin prepares `POST /administration/configuration/rollback/plan` with:

```json
{
  "targetGeneration": 1,
  "expectedGeneration": 3,
  "purpose": "Restore the earlier reviewed feature selection"
}
```

The target must be an earlier generation in the same project/environment; zero selects the original LocalPreview baseline when the trusted host permits preview. Review and execute the returned `config.update` plan through the same two-person flow. Rollback creates generation four in this example. It retains all intervening history, creates a new digest and policy fence, and does not restore an old role version, approval or entitlement sequence. An older expired or replayed entitlement cannot be revived by rollback. When mandatory signed mode is disabled, an administrator and separate reviewer can deliberately restore LocalPreview without a valid document; its configured local features then work without a signature, while the signed-document high-water mark remains retained for later Licensed mode. Prepare a compatible newly reviewed Licensed configuration when mandatory signed mode is active.

`FoundationJobRunner` uses `StartMockDispatch` with an explicit verifier. The store checks current authority, configuration, feature availability, runtime window and entitlement in the same SQLite write transaction, before recording `DispatchIntent`. Feature denial leaves the job `Blocked` with reason `DispatchFeatureUnavailable`, emits no intent and sends no provider POST. Existing authority failures retain their own denial reasons. Existing post-intent uncertainty remains conservative and still requires reconciliation; it is never relabeled as a known no-effect failure.

Safe feature availability bypasses entitlement/config parsing and runtime-window checks while retaining customer and role checks. This keeps authorized administration, audit and export access usable after expiry or ordinary feature disablement. Configuration corruption still fails closed for ordinary features; recover retained data through the separately qualified backup/restore process. A feature decision supplies no database repair authority.

## Local evidence

The focused suite in [EntitlementPolicyTests](../tests/SelfService.Foundation.Tests/EntitlementPolicyTests.cs) and [FoundationConfigurationTests](../tests/SelfService.Foundation.Tests/FoundationConfigurationTests.cs) uses ephemeral real RSA-PSS signatures, controlled clocks and real temporary SQLite databases. It exercises tampering, strict parsing, customer/cell isolation, exact review, generations, rollout, compatibility, rollback, replay, expiry, mandatory signed mode and safe access. Its worker test uses a provider-shaped GitHub payload and captured mock transport; the unexpired control dispatches once and the expired case emits no POST. Test keys and payloads are synthetic. These checks establish local contract behavior, without Azure deployment, customer pipeline acceptance, real issuer operation or completed commercial licensing.
