# AI-assisted Bicep source drafts

[Wiki home](README.md) · [Platform overview](platform-overview.md) · [Status](completion-status.md)

## Current behavior

Platform Studio now has **Design Bicep source draft** under **Agent workflows**. It uses the existing AHP coordination, GPT-6.1 Sol / Ultra / Standard runtime and scoped MCP evidence bridge. The model proposes a structured module composition; deterministic C# generates Bicep, parameter files, a subscription wrapper, copied local modules and receipts. **All generated output is an unqualified source draft.** This feature cannot queue ADO, publish Template Specs, enable targets, create resources or reserve addresses.

The existing nine workloads still deploy their registered, fixed compositions. `Resolve-PlatformRequest` accepts the same four intent fields. Generated drafts do not replace those files or become a new catalog entry automatically. This implements the user's selected **reviewable source drafts first** route.

**17 October 2026 — AVM source building blocks:** six pinned AVM-backed wrappers add user-assigned identity/federation, private Container Registry/replicas, App Configuration/non-secret key values/replicas, private Application Insights, NSG rules and route-table routes. The combined inventory has 23 contracts and 18 selectable modules; its 17 native contracts remain separate from the six AVM contracts. The updated portal's **Agent workflows** module details and `GET /api/bicep/modules` expose the reviewed interfaces and provenance. See [exact parameters, prerequisites and maintenance](avm-module-library.md). Activation in an already-running primary portal requires a governed binary restart; a source or candidate-host change alone does not update its loaded application code.

## Use the workflow

1. Start the updated portal with `./scripts/Start-Portal.ps1` after building with `./scripts/Setup-Portal.ps1`. If already running, stop it with `./scripts/Stop-Portal.ps1` before rebuilding; a restart clears browser-owned authentication and evidence.
2. Connect Azure and Codex. Run **Network discovery** for a scope that includes the selected workload's subscription. Select its **Review with agent** action.
3. In **Agent workflows**, choose **Design Bicep source draft**. Select the workload context, environment and approved region. Describe the desired infrastructure in at most 2,000 characters.
4. Click **Collect evidence and run review**. This is the explicit model invocation and consumes Codex usage. The discovery snapshot must belong to this browser and be at most 15 minutes old. Only the selected subscription's resource IDs/types and status are projected; other subscriptions are not sent. A 256 KiB evidence limit still applies.
5. Inspect the summary, missing inputs and source. Download **source, modules, parameters and receipt**. Selected AVM wrappers include their pinned local compiled artifact, OCI manifest, bundle provenance and license. Copies also remain under `artifacts/portal-agents/<run>/source-draft/`; artifacts are ignored by Git.
6. Extract into a local review directory. Fill required parameters, review dependencies, then compile:

   ```powershell
   az bicep build --file main.bicep
   az bicep build-params --file main.bicepparam
   az bicep build --file stack.bicep
   az bicep build-params --file stack.bicepparam
   ```

Parameter compilation intentionally fails until all required values are supplied. `stack.bicepparam` additionally requires reviewed `workloadResourceGroupName`, `deploymentLocation` and `workloadTags`. No Azure deployment command is included in a draft.

The workload selection supplies context for a new design; it does not grant ownership of that workload's existing resources. The model can combine different selectable modules. Missing capability is reported as a gap rather than invented module code. A partial discovery can support an advisory draft, but cannot establish absence, Create permission or deployment eligibility.

## Source and parameter contract

| File / method | Responsibility |
|---|---|
| `scripts/Update-BicepModuleContracts.ps1` | Locally compiles the 17 native modules and invokes `Update-AvmModuleContracts.ps1` for six reviewed wrappers. Records actual parameters, constraints, defaults, outputs, resource types, normalized wrapper hashes and exact AVM dependency hashes. `-Check` rejects catalog drift. No Azure collection. |
| `scripts/Verify-AvmSources.ps1` | Offline verification of exact official OCI manifests, compiled ARM layers, inline nested templates, file inventory, immutable pins and MIT license. Called by the AVM contract updater. |
| `config/bicep-module-contracts.json` / `config/avm-module-contracts.json` | Separate source-owned inventories: 17 native contracts with 12 selectable, and six AVM contracts with six selectable. The application validates and merges them into 23 contracts/18 selectable. Neither is an authorization catalog. |
| `BicepDrafts.Context` | Validates browser-owned snapshot, selected subscription/target/region and goal; projects known resources and allowlisted target overrides; computes evidence/catalog digests. |
| `platform-bicep-composition` skill | Requests `platform.bicep-proposal/v1`: module IDs, rationales, typed bindings and gaps. No raw generated Bicep is executed. |
| `BicepDrafts.Generate` / `BicepParameterSchema` | Validate schema, exact module/source catalog and dependency closures, references, nested parameter types/constraints and acyclic dependencies. Emit fixed Bicep syntax, bounded user-defined types and literal local module paths. |
| `BicepAvmSources` | Verifies each allowlisted wrapper's exact local source closure; copies the selected compiled dependency, manifest, provenance and license into the draft. Registry fetching and arbitrary module paths are unavailable. |
| `AgentWorkflows.Run` | Runs the existing isolated model/MCP workflow, validates the proposal, saves files and returns a downloadable archive. Still requires both scoped MCP reads. |
| `scripts/Test-BicepDrafts.ps1` | Checks both module catalogs, runs draft tests with isolated output under `artifacts/bicep-draft-build/`, and compiles emitted main/stack fixtures. Called by project qualification. See dated validation for the exact test coverage achieved. |

Bindings are `Input` (unresolved), `Setting` (a known context key), `Resource` (a compatible observed ID), or `Output` (a known output of another proposed module). There are no literal, expression, arbitrary path or arbitrary URL binding kinds. Values are data in `resolved-values.json`, not interpolated Bicep source. Unknown module inputs are rejected. Omitted optional inputs retain their module default; omitted required inputs become explicit top-level parameters.

Bounded arrays and sealed object children are supported through reviewed compiled schemas and generated Bicep user-defined types. The adapter resolves only local type definitions; unsupported advanced/secure schemas, cycles and unknown child properties fail. Selecting an optional child array as `Input` makes it a required typed parameter for the human to complete; omitting it leaves the wrapper's empty-array default. Structured output bindings are excluded until separately qualified. Follow the [child parameter contract](avm-module-library.md#parameterized-children-and-bounds) when editing the downloaded `.bicepparam` files; source/rendered values must contain no secrets.

The draft parameter names are `p_<node>_<input>`. They are an intermediate source interface, **not a change to production request fields**. Intent supplies workload/environment/region; allowed source target overrides can supply approved owner/cost center, specific identity IDs and shared-resource bindings. **This version does not evaluate environment `.bicepparam` defaults or apply the production prerequisite resolver.** Values found only there remain required inputs. This avoids pretending that the portal's discovery projection is the qualified ADO discovery manifest.

`parameter-provenance.json` attributes each input. `draft-receipt.json` binds module/source hashes, evidence/catalog digest, selected context and output file hashes; AVM selections also retain origin and exact source-closure hashes. The enclosing agent receipt records model/skill/tool evidence. Receipt hashes detect changes; they are not signatures or approval. Keep receipts with the source and retain relative dependency paths. The runtime does not invoke the compiler on each downloaded draft: its compilation state is **Required**, even though locally generated test fixtures compile.

## Qualification and promotion

```mermaid
flowchart LR
  A[Saved discovery and selected context] --> B[Scoped Codex module proposal]
  B --> C[Contract and dependency validation]
  C --> D[Source draft and parameter provenance]
  D --> E[Human source and ownership review]
  E --> F[Compile, test and register workload]
  F --> G[Qualified discovery and effective parameters]
  G --> H[ADO Preview and exact changes]
  H --> I[Protected Template Spec and Stack deployment]
  I --> J[Verify]
```

For promotion, follow [workload onboarding](workload-onboarding.md). Move reviewed source into `workloads/<type>/`, replace copied module references with repository-relative references, establish a stable parameter/naming interface, create environment files and disabled target profiles, and implement the typed adapter/package/verification contract. Update `config/workloads.json`, its code allowlists, menu generation, topology, pipeline registration, documentation and tests together. Keep shared dependencies outside workload stack ownership. Preview and deployment must bind to the same qualified commit and immutable bundle.

Generated source cannot bypass the existing allowlists by supplying a path or uploading its archive as a release. The current ADO path performs real stack What-If, validates provenance, rechecks drift and requires protected resources. A local compilation does not prove permissions, region/SKU availability, quotas, private connectivity, resource ownership or successful execution. Stack What-If itself can report limitations; review them as blockers where coverage is insufficient. [Microsoft stack What-If documentation](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deployment-stacks-what-if).

## Known limits and manual acceptance

- Names, IP ranges, recipients, tenant consent and missing identity IDs are not invented. Names remain reviewable required parameters until a versioned naming policy is qualified. No secret values should be entered into a draft; advanced/secure input contracts need a separate secure-binding adapter.
- The emitter validates primitive output compatibility, not every service dependency. A string resource ID can still be semantically inappropriate. Existing modules embed diagnostics, roles, runtime assumptions and some fixed settings; use the [module audit](plans/bicep-composition-and-module-audit.md).
- The main composition supports reviewed native leaf modules and the six exact self-contained AVM closures in the same resource group. The subscription wrapper creates its reviewed workload resource group. Cross-subscription/shared-scope changes, arbitrary nested modules and external template links require a separate reviewed adapter; no arbitrary scope expressions are accepted.
- AVM wrappers expose a constrained subset. Private registry/configuration access, App Configuration data-plane authorization and ARM Private Link, Application Insights AMPLS/workspace networking, federation trust and NSG/route ownership remain explicit qualification gaps until supplied and verified. Their source defaults do not create these dependencies automatically. See [service prerequisites](avm-module-library.md#networking-identity-cost-and-ownership).
- Discovery supplies observed IDs/types, not capacity, routing, role assignment evidence or Create/Reuse/Manage decisions. No automatic adoption or generic “missing means create” behavior exists.
- Source drafts do not create application code or package/runtime configuration beyond what the selected module already declares. Function and Logic App deployment lifecycles still need qualification.
- Windows package generation includes source modules and contracts. Previously produced packages do not contain this increment; rebuild packages before distributing it.

Manual acceptance: use a fresh network snapshot and connected Codex, request a storage-plus-monitoring draft, verify both MCP reads, inspect module selections/missing endpoints and permissions, download/extract, complete inputs, compile both templates/parameter files, and confirm no pipeline was queued. Repeat with partial evidence, missing module requirements, expired evidence and sign-out. **Live Azure/Codex draft acceptance remains outstanding.** See [validation history](validation.md) for local results.

The three Private Data Platform modules are inventoried but not draft-selectable. Its fixed registered composition is available through the governed workload path; independently generated data-platform compositions need additional network/identity/data-plane qualification.
