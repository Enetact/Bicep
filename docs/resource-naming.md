# Resource naming foundation

[Wiki](README.md) · [Phased design](plans/stack-naming-and-resource-previews.md) · [Current status](completion-status.md)

**4 October 2026 — offline implementation started.** The resolver, versioned policy, input/output schemas, PowerShell adapters, source inventory and draft-instance registry validator are implemented and locally tested. They are not yet connected to the portal forms, workload Bicep parameters or ADO pipelines. Existing resource names and targets are unchanged. N1 and N2 remain in progress; full workload bindings and target/request integration are not implemented yet.

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

Components explicitly choose convention, fixed exception, exact reference, provider-assigned or unresolved mode, plus included/excluded/unknown condition. Children bind through `parentKey` or an explicit validated scope. Parent cycles, wrong parent types, duplicate identities, malformed IDs, unsupported types/roles and conflicting fields fail. Unknown scope has no invented ARM ID. Cross-subscription references preserve their exact ID and casing; ownership/access remain unverified. Newly owned resources cannot silently change subscription.

Explicit exception names preserve provider requirements or application contracts. The resolver enforces selected hard rules (GUIDs, `default`, DNS syntax, Web child allowlists and storage container/queue syntax); other application-contract names still require their owning workload's validation. Existing names are not revalidated as new convention names or reformatted. Legacy-v1 is deliberately rejected by this new resolver because the unchanged legacy adapters remain authoritative.

Plan digests exclude clocks and include policy, normalized input and result content. Display-name edits change the request digest but not physical names. Changing profile, instance, role or salt is not a harmless rename: later integration must invalidate saved Preview and apply migration rules.

## Draft stack instances

[The empty registry and operator instructions](../self-service/instances/README.md) establish the N2 source contract without adding a deployable target. Two drafts for the same product/environment can reserve different ordinals: for example `dev-stack-pay-int-eus2-01` and `dev-stack-pay-int-eus2-02`. The validator calculates only the stack and resource-group identities, not the entire workload inventory.

Always supply the trusted previous registry to check a proposed update. Without it, the receipt says `transitionChecked: false`. With it, changing UUID, workload type, subscription or naming identity fields, or removing an existing entry, is rejected as requiring retirement/migration. Display-name edits are allowed but change the registry digest. Cross-product acronym/ordinal collisions are rejected. Source review must reconcile concurrent additions; this is not a transactional allocation service or protected CI gate yet. No approvals are created or invalidated in the running application by these offline checks.

The [nine-workload binding review](plans/stack-naming-bindings.md) records repeated endpoint roles, derived hosting/managed-group names, GUID exceptions and runtime consumers that must be wired together before Bicep integration. The source inventory now correctly distinguishes AKS's explicitly named node resource group from Azure-assigned resources inside it.

## Verification and remaining work

```powershell
./scripts/Test-Naming.ps1
```

The latest run passed **74 core/CLI cases** (46 naming + 28 registry), naming/registry PowerShell golden and rejection checks, and **three JSON schema checks**. Evidence: `artifacts/naming-tests/receipt.json`, `sample-plan.json`, `instance-receipt.json` and `source-inventory.json`. The 37 existing platform-contract cases also passed, and all 36 unchanged legacy targets loaded/resolved with zero enabled. The inventory covers 47 explicit ARM types plus reviewed supplemental fields; it is not full expanded resource/role coverage for all workload settings.

Next: finish N1's executable workload role/exception bindings and N2's target/request compatibility, then integrate names into Bicep and frozen pipeline inputs (N3/N4) before enabling portal submission. No application build/restart was needed for this offline increment. No authentication state, Azure resource or pipeline definition was changed. See the [implementation phase table](plans/stack-naming-and-resource-previews.md#phased-delivery-and-acceptance-gates).
