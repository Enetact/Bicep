# Resource naming foundation

[Wiki](README.md) · [Phased design](plans/stack-naming-and-resource-previews.md) · [Current status](completion-status.md)

**5 October 2026 — discovery and named Preview integration:** all nine workload compositions now accept typed standard-v2 name maps. Fresh discovery generates observed identities and matching source-draft candidates; the dedicated Deploy menus can freeze a reviewed instance into **Preview only**. Names shown for Azure changes come from returned What-If resource IDs. Existing evidence is never edited. All targets remain disabled, the source draft registry remains empty, and named Deploy is explicitly rejected. Local verification is recorded below; live ADO/Azure Preview and Codex inference acceptance remain outstanding.

## Use a reviewed instance in Preview

1. Calculate a draft in Configure workload or with the shared CLI. Review its intent, names and dependencies.
2. Add its intent as a draft entry in [the source registry](../self-service/instances/README.md), validate against the previous registry, then regenerate menus with `./scripts/Update-ServiceCatalog.ps1`. Commit/review through the normal source process; the portal does not write this registration.
3. Run fresh Discover on the reviewed source. Event flow collects separate stack-ownership/prerequisite evidence for matching named instances (bounded to 32 per scope). Failed collection cannot authorize Create.
4. In the workload-specific Deploy YAML, select `namingInstanceId` and **Preview only**, then select the successful discovery artifact. The portal offers only matching registered drafts in its Preview naming selector. With an empty registry, only **Existing target naming** is available.
5. Prepare verifies workload, subscription, environment and region, freezes `naming-draft.json`, concrete Bicep parameters, stack identity and hashes, and keeps the target disabled. Consumers regenerate the draft from current source and reject tampering or policy drift. The summary separates calculated candidates from Azure changes.
6. Read the fresh What-If summary and portal diagram. The artifact reader binds the named resource group back to the reviewed source before exposing IaC evidence. AHP/Codex receives names derived from the same validated IDs, retaining observed/predicted evidence labels. No model turn is started automatically.

Legacy is the default. Named Preview does not enable Deploy, publish a Template Spec or supply missing shared prerequisites. Global name availability, provider-generated children, conditional inclusion and live API compatibility still require Azure evidence. A calculated map is deliberately not a full deployment-resource inventory.


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
| [Workload adapter](../scripts/naming/workload.mjs) and [CLI](../scripts/naming/workload-cli.mjs) | Reviewed component descriptors for all nine workloads invoke the same resolver. Returns a non-deploying draft and partial parameter patch. Unknown principal-dependent role assignment names remain unresolved. |
| [Discovery names](../scripts/analysis/names.mjs) and [draft selection](../scripts/analysis/naming-drafts.mjs) | Derive observed identities from validated inventory IDs; select source registry drafts matching workload, environment, subscription and region. Hashes bind each new report's files. |
| [Portal adapter](../src/SelfService.Portal/NamingPlan.cs) | Bounded, timed local Node call behind CSRF protection; no cloud credentials, model calls, registration or ADO submission. |

Components explicitly choose convention, fixed exception, exact reference, provider-assigned or unresolved mode, plus included/excluded/unknown condition. Children bind through `parentKey` or an explicit validated scope. Parent cycles, wrong parent types, duplicate identities, malformed IDs, unsupported types/roles and conflicting fields fail. Unknown scope has no invented ARM ID. Cross-subscription references preserve their exact ID and casing; ownership/access remain unverified. Newly owned resources cannot silently change subscription.

Explicit exception names preserve provider requirements or application contracts. The resolver enforces selected hard rules (GUIDs, `default`, DNS syntax, Web child allowlists and storage container/queue syntax); other application-contract names still require their owning workload's validation. Existing names are not revalidated as new convention names or reformatted. Legacy-v1 is deliberately rejected by this new resolver because the unchanged legacy adapters remain authoritative.

Plan digests exclude clocks and include policy, normalized input and result content. Display-name edits change the request digest but not physical names. Changing profile, instance, role or salt is not a harmless rename: later integration must invalidate saved Preview and apply migration rules.

## Draft stack instances

[The empty registry and operator instructions](../self-service/instances/README.md) establish the N2 source contract without adding a deployable target. Two drafts for the same product/environment can reserve different ordinals: for example `dev-stack-pay-int-eus2-01` and `dev-stack-pay-int-eus2-02`. The validator calculates only the stack and resource-group identities, not the entire workload inventory.

Always supply the trusted previous registry to check a proposed update. Without it, the receipt says `transitionChecked: false`. With it, changing UUID, workload type, subscription or naming identity fields, or removing an existing entry, is rejected as requiring retirement/migration. Display-name edits are allowed but change the registry digest. Cross-product acronym/ordinal collisions are rejected. Source review must reconcile concurrent additions; this is not a transactional allocation service or protected CI gate yet. No approvals are created or invalidated in the running application by these offline checks.

The [nine-workload binding review](plans/stack-naming-bindings.md) records repeated endpoint roles, derived hosting/managed-group names, GUID exceptions and runtime consumers. All nine compositions have additive bindings. Blob copy and Event flow also bind owned VNet, NSG, subnet and DNS-link names; shared IDs and fixed DNS domains remain exact references/contracts. The source inventory distinguishes AKS's explicitly named node resource group from Azure-assigned resources inside it.

## Generated discovery and proposed names

Names are generated from evidence and selected settings, never manually inserted into saved What-If results or portal labels. A new Discover run invokes the existing shared analysis step and publishes these files under `subscription-discovery/analysis/`:

- `observed-resource-names.json`: identities found in the validated saved inventory, with full child names, type, parent scope and evidence references. Coverage remains that of the collector; an empty or partial result does not prove absence. Names do not establish workload ownership.
- `proposed-resource-names.json`: calculations for matching entries in the reviewed source registry, including registry/policy/catalog digests. All nine workload bindings are supported. The checked-in registry is empty, so it produces an explicit no-matching-draft result rather than invented resources.
- `README.md` and `report-manifest.json`: generated tables and file hashes. The analysis command requires a new output directory and never rewrites earlier evidence.

After a new portal build is activated, uploading a discovery pair displays its generated observed names separately from matching calculated drafts. Explicit reanalysis of an old pair creates a **new** report against current source policy/registry; it does not change the old artifact or claim a fresh Azure observation. Fresh Azure What-If is still required for create/modify/delete evidence. This increment does not replace or backfill saved What-If views.

The local editor under Configure workload can calculate a new stack draft for all nine workloads. It takes the selected environment/region and editable draft identity, clears stale results on edits, and downloads the full draft plus its partial parameter patch. It cannot register a target. After source review, a matching registered draft can be selected separately for ADO Preview. A bootstrap capability flag hides it when served against an older backend.

```powershell
. ./scripts/naming-common.ps1
$example = Get-Content ./tests/naming/request.example.json -Raw | ConvertFrom-Json -AsHashtable
$draft = New-WorkloadNamingDraft -Request @{ workloadType = 'private-storage'; intent = $example.intent }
$draft.plan.resources | Select-Object componentKey, fullName, resourceId, status
$draft.parameterPatch | ConvertTo-Json -Depth 20
```

The patch includes concrete `resourceNames`, resource-group name, environment and location only. Shared subnet/DNS/workspace/action-group IDs, principals and other required settings must come from reviewed configuration. This is not a complete deployment parameter file. The nine compositions' optional sealed name maps preserve legacy expressions when absent. Observability uses the immutable instance UUID for its new workbook name; display-name edits do not rename it. The Preview adapter validates the normal workload inputs before adding this map, then binds the complete map to its source registration and frozen receipt. Deploy rejects named drafts.

## Verification and remaining work

```powershell
./scripts/Test-Naming.ps1
./scripts/Test-NamingBicep.ps1 -BicepPath '<path-to-standalone-bicep.exe>'
```

The current local checks cover 123 Node naming cases, PowerShell adapters, three schemas, nine stack templates and 36 standard/legacy environment pairs. The portal suite passed 306 tests with one optional native Codex test skipped; 80 discovery, 27 prerequisite, 30 Preview and 171 pipeline contracts passed. These are local checks, not Azure name availability or server-side What-If acceptance. See [dated evidence](validation.md).

The actual Release app built with zero warnings/errors and passed 15 HTTP checks on an isolated port. Browser checks exercised a Blob copy draft, edit invalidation, and valid → tampered → valid discovery analysis. No primary-portal restart or authentication transfer was attempted. Review [the remaining gates](plans/stack-naming-and-resource-previews.md#phased-delivery-and-acceptance-gates) before claiming rollout or deployment readiness.
