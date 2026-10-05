# Resource naming foundation

[Wiki](README.md) · [Phased design](plans/stack-naming-and-resource-previews.md) · [Current status](completion-status.md)

**4 October 2026 — first integration group implemented and locally tested.** Private Storage, Key Vault and Observability accept optional typed standard-v2 name maps while retaining their legacy defaults. Fresh discovery analysis generates observed-name and matching source-draft artifacts. Portal source includes a local draft editor and generated name tables; the actual Release candidate passed isolated HTTP checks. The authenticated primary portal has not been restarted. All-nine-workload qualification, protected Preview/Deploy transport, browser acceptance and instance registration remain open. No targets were enabled or Azure resources changed.

## Calculate names locally

From the repository root with Node.js 22+ and PowerShell 7.4+:

```powershell
. ./scripts/naming-common.ps1
$request = Get-Content ./tests/naming/request.example.json -Raw | ConvertFrom-Json -AsHashtable
$plan = Resolve-ResourceNames -Request $request
$plan.resources | Select-Object componentKey, name, resourceId, status, availability
```

The checked-in synthetic example produces `dev-rg-pay-int-eus2-01`, storage account `devstpayinteus2hcsr5ju`, the account's `default` blob service, and its `incoming` container. These are **calculated examples**, not Azure resources. The globally scoped account has a deterministic suffix; availability remains Unknown.

The requested token order is environment → resource abbreviation → stack acronym → internal/external → region. Scoped convention names append a stable ordinal and, where applicable, a role. Global namespace profiles encode instance/ordinal/component/role identity in the deterministic suffix and use compact role tokens. Storage always uses compact formatting; Key Vault uses the approved compact fallback when separators would exceed its length budget. No token is silently truncated.

`external` is a naming classification only. The generic resolver accepts it, but the new instance validator permits only `internal` for the current private products. Neither qualifies public access. Do not use a calculated name as an ownership, availability, networking or deployment decision.

## Contracts and methods

| Source | Responsibility |
|---|---|
| [Naming policy](../config/naming-policy.json) | Versioned region/environment/exposure mapping, explicit type classifications, convention roles, formats and length limits. Conservative naming subset; not an exhaustive reserved-word/availability checker. |
| [Request schema](../schemas/naming/intent.schema.json) | Intent and component descriptors. Schema validation is structural; the resolver additionally checks mode/role/parent/scope relationships. |
| [Plan schema](../schemas/naming/plan.schema.json) | Name/ID/status rows and policy/input/plan digests; execution authorization is always false. |
| [Core resolver](../scripts/naming/core.mjs) | `resolveNames` validates and resolves; `parseArmId` retains parent and nested type/name context; `canonical`/`digest` bind deterministic receipts. No I/O, credentials, network, shell or model execution. |
| [CLI](../scripts/naming/cli.mjs) | One JSON request on stdin, one plan on stdout. Fixed repository policy, no path/command overrides, 1 MiB input cap. Errors produce a nonzero exit and no partial plan. |
| [PowerShell adapter](../scripts/naming-common.ps1) | `Resolve-ResourceNames` invokes the same Node resolver, applies input/response limits and a 15-second process timeout, and returns a hashtable. It does not duplicate naming algorithms. |
| [Source inventory](../scripts/naming/inventory.mjs) | Classifies declared ARM types and records reviewed supplemental inline/service fields. Fails on a newly declared unclassified type; does not evaluate Bicep conditions or expand modules. |
| [Instance validator](../scripts/naming/instances.mjs) and [schema](../schemas/naming/instances.schema.json) | `validateInstances` pins policy/catalog receipts, checks registered product types and duplicate RG/stack identities, and compares an optional previous registry for immutable-identity changes. Current entries are draft only. |
| [Instance CLI](../scripts/naming/instances-cli.mjs) | Bounded stdin envelope `{registry, previousRegistry?}`; fixed local policy/catalog; no file/path/command input or cloud call. PowerShell `Test-StackInstanceRegistry` invokes it through the shared bounded adapter. |
| [Workload adapter](../scripts/naming/workload.mjs) and [CLI](../scripts/naming/workload-cli.mjs) | Reviewed component descriptors for the first three workloads invoke the same resolver. Returns a non-deploying draft and partial parameter patch. Unknown principal-dependent role assignment names remain unresolved. |
| [Discovery names](../scripts/analysis/names.mjs) and [draft selection](../scripts/analysis/naming-drafts.mjs) | Derive observed identities from validated inventory IDs; select source registry drafts matching workload, environment, subscription and region. Hashes bind each new report's files. |
| [Portal adapter](../src/SelfService.Portal/NamingPlan.cs) | Bounded, timed local Node call behind CSRF protection; no cloud credentials, model calls, registration or ADO submission. |

Components explicitly choose convention, fixed exception, exact reference, provider-assigned or unresolved mode, plus included/excluded/unknown condition. Children bind through `parentKey` or an explicit validated scope. Parent cycles, wrong parent types, duplicate identities, malformed IDs, unsupported types/roles and conflicting fields fail. Unknown scope has no invented ARM ID. Cross-subscription references preserve their exact ID and casing; ownership/access remain unverified. Newly owned resources cannot silently change subscription.

Explicit exception names preserve provider requirements or application contracts. The resolver enforces selected hard rules (GUIDs, `default`, DNS syntax, Web child allowlists and storage container/queue syntax); other application-contract names still require their owning workload's validation. Existing names are not revalidated as new convention names or reformatted. Legacy-v1 is deliberately rejected by this new resolver because the unchanged legacy adapters remain authoritative.

Plan digests exclude clocks and include policy, normalized input and result content. Display-name edits change the request digest but not physical names. Changing profile, instance, role or salt is not a harmless rename: later integration must invalidate saved Preview and apply migration rules.

## Draft stack instances

[The empty registry and operator instructions](../self-service/instances/README.md) establish the N2 source contract without adding a deployable target. Two drafts for the same product/environment can reserve different ordinals: for example `dev-stack-pay-int-eus2-01` and `dev-stack-pay-int-eus2-02`. The validator calculates only the stack and resource-group identities, not the entire workload inventory.

Always supply the trusted previous registry to check a proposed update. Without it, the receipt says `transitionChecked: false`. With it, changing UUID, workload type, subscription or naming identity fields, or removing an existing entry, is rejected as requiring retirement/migration. Display-name edits are allowed but change the registry digest. Cross-product acronym/ordinal collisions are rejected. Source review must reconcile concurrent additions; this is not a transactional allocation service or protected CI gate yet. No approvals are created or invalidated in the running application by these offline checks.

The [nine-workload binding review](plans/stack-naming-bindings.md) records repeated endpoint roles, derived hosting/managed-group names, GUID exceptions and runtime consumers. The first three compositions now have additive bindings; the other six retain legacy naming. The source inventory distinguishes AKS's explicitly named node resource group from Azure-assigned resources inside it.

## Generated discovery and proposed names

Names are generated from evidence and selected settings, never manually inserted into saved What-If results or portal labels. A new Discover run invokes the existing shared analysis step and publishes these files under `subscription-discovery/analysis/`:

- `observed-resource-names.json`: identities found in the validated saved inventory, with full child names, type, parent scope and evidence references. Coverage remains that of the collector; an empty or partial result does not prove absence. Names do not establish workload ownership.
- `proposed-resource-names.json`: calculations for matching entries in the reviewed source registry, including registry/policy/catalog digests. Only the first three workload bindings are supported. The checked-in registry is empty, so it produces an explicit no-matching-draft result rather than invented resources.
- `README.md` and `report-manifest.json`: generated tables and file hashes. The analysis command requires a new output directory and never rewrites earlier evidence.

After a new portal build is activated, uploading a discovery pair displays its generated observed names separately from matching calculated drafts. Explicit reanalysis of an old pair creates a **new** report against current source policy/registry; it does not change the old artifact or claim a fresh Azure observation. Fresh Azure What-If is still required for create/modify/delete evidence. This increment does not replace or backfill saved What-If views.

The local editor under Configure workload can calculate a new stack draft for Private Storage, Key Vault or Observability. It takes the selected environment/region and editable draft identity, clears stale results on edits, and downloads the full draft plus its partial parameter patch. It cannot register a target or submit standard-v2 parameters to ADO. A bootstrap capability flag hides it when served against an older backend.

```powershell
. ./scripts/naming-common.ps1
$example = Get-Content ./tests/naming/request.example.json -Raw | ConvertFrom-Json -AsHashtable
$draft = New-WorkloadNamingDraft -Request @{ workloadType = 'private-storage'; intent = $example.intent }
$draft.plan.resources | Select-Object componentKey, fullName, resourceId, status
$draft.parameterPatch | ConvertTo-Json -Depth 20
```

The patch includes concrete `resourceNames`, resource-group name, environment and location only. Shared subnet/DNS/workspace/action-group IDs, principals and other required settings must come from reviewed configuration. This is not a complete deployment parameter file. The three compositions' optional sealed name maps preserve legacy expressions when absent. Observability uses the immutable instance UUID for its new workbook name; display-name edits do not rename it. Existing pipeline product validators still reject the new override until frozen Preview/Deploy binding is implemented.

## Verification and remaining work

```powershell
./scripts/Test-Naming.ps1
./scripts/Test-NamingBicep.ps1 -BicepPath '<path-to-standalone-bicep.exe>'
```

The latest run passed **99 Node cases** (46 core + 28 registry + 16 workload + 9 discovery-draft), PowerShell adapters and three JSON schema checks. The standalone compiler built three stack templates and 12 standard-v2/legacy environment parameter pairs. Also passed: 38 offline discovery cases, 74 focused .NET tests, 169 infrastructure contract checks and 42 YAML parses. These are local checks, not Azure name availability or server-side What-If acceptance. See [dated evidence](validation.md).

The actual Release app built with zero warnings/errors and passed nine HTTP checks on isolated port 5099 using a separate cookie session; that candidate was stopped afterward. Evidence is under `artifacts/naming-integration/` (build, `http-receipt.json`, `bicep/receipt.json`, `naming-portal.trx`). The authenticated port-5087 portal and its ownership receipt were preserved. Browser visual acceptance and authentication-preserving activation remain pending. Next: remaining workload groups, target/request compatibility and frozen naming inputs through Preview/Deploy before enabling submission. See the [phase table](plans/stack-naming-and-resource-previews.md#phased-delivery-and-acceptance-gates).
