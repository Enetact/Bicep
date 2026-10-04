# Nested discovery and IaC qualification

[Wiki](README.md) · [Shared IaC workflow](agentic-iac.md) · [Preview/Deploy](deployment-preview.md)

Implemented locally on 4 October 2026. Discovery now supplements generic MCP inventory with **fixed provider-specific ARM GET/list probes and a versioned nested-property profile**. The dedicated workload Preview/Deploy flow consumes this evidence; no new ADO definition or subscription setting is required. IDs remain derived from the selected target and returned resources.

## What this resolves—and what remains unknown

Live probes capture current properties that generic inventory or What-If may omit. They cannot determine the future result of an unresolved runtime expression. What-If can short-circuit a resource or whole nested deployment, and does not expand linked templates. The pipeline retains that distinction rather than copying current values into the prediction. [Microsoft What-If limitations](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-what-if).

For example, an observed subnet may have a delegation and route table that the What-If property projection omitted. Those facts now enter the evidence and approval fingerprint. If the desired subnet ID is still an unresolved `reference(...)` expression, it remains a blocker. Correct the owning source to use a reviewed explicit input, or split the dependency into separately qualified stages; rerun Preview. Codex can explain this evidence and suggest source changes, but cannot turn an observation into a proven desired value.

## Current profile

`scripts/iac/profiles.json` is the common source for Node collection/projection and the portal's embedded validator. Generic scalar security/SKU/reference fields remain supported. The additional profiles include:

| Resource family | Nested properties and additional reads |
|---|---|
| VNets/subnets | Address spaces, DNS servers, subnet prefixes, NSG/route references, delegations, service endpoints, NAT references and network policy flags. Fixed subnet list catches children omitted from inventory. |
| Private endpoints | Service/group/approval state, FQDN/IP configuration; child DNS zone-group configurations and zone IDs. |
| Storage | Network ACLs and selected blob retention, versioning and change-feed properties. Exact `blobServices/default` and `queueServices/default` GETs; queue data/CORS bodies are not exported. |
| App Service/Functions | Selected host security flags; exact `config/web` GET for TLS, FTPS, route-all and host flags. No app-settings/connection-string/key retrieval. |
| AKS | Selected version/network/API-server settings; agent-pool list with version, subnet, scaling and surge configuration. No Kubernetes data-plane or effective capacity claim. |
| ADF | Managed private endpoints under the approved `managedVirtualNetworks/default` child path; resource/group/approval/FQDN metadata. No linked-service credentials or activity payloads. |
| Key Vault / Databricks | Selected network ACLs and workspace networking parameter values. Secret values, tokens and arbitrary parameters are excluded. |

Provider contracts include [Subnet GET](https://learn.microsoft.com/en-us/rest/api/virtualnetwork/subnets/get), [Web App configuration GET](https://learn.microsoft.com/en-us/rest/api/appservice/web-apps/get-configuration?view=rest-appservice-2024-11-01) and [ADF managed private endpoint listing](https://learn.microsoft.com/en-us/rest/api/datafactory/managed-private-endpoints/list-by-factory?view=rest-datafactory-2018-06-01). Live API compatibility and permissions still require acceptance in the selected Azure estate.

## Evidence, display and recommendations

- `config` stores allowlisted scalar leaves, such as `properties.delegations.0.properties.serviceName`, plus array counts. Empty returned arrays differ from missing arrays. Scalar arrays are sorted; uniquely named/identified object arrays are sorted by that identity. Other arrays retain order. Index keys are presentation/comparison coordinates, not Azure resource identity.
- `propertyCoverage` records `Observed`, `NotReturned`, `UnresolvedExpression` or `LimitExceeded` per profile selector. An omitted property is not its default. A profile is deliberately narrower than a full resource configuration.
- Child probe receipts record parent/route, API version, GET method, timestamp, pages, count and status. Denied, missing child endpoints, malformed replies, unsafe continuation and budget exhaustion remain explicit; a child endpoint 404 is `NotAvailable`, not a successful empty list.
- `qualification` contains profile identity/hash, blockers and a before-state fingerprint. The portal displays this with expandable property/probe coverage and the existing before/What-If/after matrix and diagrams. Old artifacts are labeled as lacking nested qualification.
- The existing **AHP → Codex GPT-6.1 Sol / Ultra / Standard** IaC review reads the frozen projected artifact through `platform_evidence`. Its instructions require separating observed and desired facts and describing unknowns. No new model, shell, Azure credential or write tool is introduced.
- Bicep source-draft recommendations now retain the selected browser-owned network report's projected nested facts and coverage. Subnets are extracted from the discovery contract's top-level `subnets` array. This is a different source from the pipeline artifact; the two identities are not mixed. Module bindings still accept only reviewed settings, observed IDs, typed module outputs or unresolved inputs. Observed values never silently become parameter defaults.

## Preview, Deploy and verification

1. Before collection reads the primary resource group, then fixed children of observed parents in that group. Planned exact IDs can also be read in the selected subscription; shared groups are not recursively scanned.
2. Plan retains nested What-If projection, counts unresolved expressions without publishing expression contents, detects diagnostics and structurally inspects linked nested templates. Successful What-If status alone does not clear these blockers. Structural inspection does not prove every conditional module was expanded; missing Azure diagnostics remain a platform limitation.
3. Required planned-resource observations, changed projected properties and child probes must have qualified coverage. A known array count can prove that a new element was absent. A missing scalar cannot prove its default. No deployable preview is published if qualification fails; analysis/receipts are still retained.
4. The immutable Preview fingerprint now includes the observed configuration/profile/probe fingerprint. Deploy requires the new field and recomputes the evidence before Apply. Even unchanged What-If changes cannot bypass a changed nested observation. Older approvals must be regenerated.
5. After an attempted Apply, fresh observations are compared. Mismatch, unresolved comparison or missing expected projected values withhold readiness. Existing failure preservation and recovery controls remain in effect; no automatic retry/rollback is added.

A sequence of GETs is not an atomic snapshot. There remains a race between the final read and provider execution. The profile does not prove effective reachability, free subnet capacity, authorization of data-plane access or full application readiness.

## Limits and extension contract

Child probes allow 100 GETs per pass, up to ten pages per child route and a two-minute budget plus any in-flight 30-second request. Existing resource inventory has its separate budget. Each ARM response is bounded to 4 MiB; total resources remain capped at 500. Individual projected arrays allow 256 entries and each resource up to 4,096 retained leaves/counts. Exceeding a projection/probe limit blocks qualification. The existing portal artifact, model and diagram size limits still apply.

To add a provider, add reviewed paths and optional fixed child routes to `profiles.json`, pin its API version, and test nested missing/empty/denied/paginated results, sensitive-field exclusion, unresolved expressions and before/after drift. Never accept arbitrary paths from a model or recursively retain all properties. Data-plane probes require a separate scoped adapter. Profile changes require new Preview evidence and a portal rebuild to update its embedded validator.

## Manual acceptance

Run a new dedicated workload **Preview only** on main using a selected discovery artifact. Compare a small approved existing resource set against Azure: subnet delegations/route IDs, private endpoint DNS associations and one applicable child configuration. Verify property coverage and child receipts in `azure/iac/analysis.json`, the Summary and portal. Exercise a denied child read in a disposable test scope and confirm Deploy remains blocked. Reconnect Codex and explicitly review the artifact to validate the model receipt. Only then rehearse an authorized disposable deployment and validate fresh after-state evidence.

Local tests use synthetic resources and mocked Azure transports. No live Azure/ADO/Codex acceptance is claimed by this increment.
