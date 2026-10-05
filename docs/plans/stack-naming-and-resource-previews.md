# Stack identity, resource naming and named previews

[Wiki](../README.md) · [Workload onboarding](../workload-onboarding.md) · [Preview](../deployment-preview.md) · [Discovery issues](../testing/README.md)

## Status and authorization

**4 October 2026: foundation and first integration group implemented; full rollout remains in progress.** The user authorized phased implementation across the application in subsequent prompts, necessary builds, and preservation of existing authentication. Azure deployments are explicitly excluded. This plan does not enable targets, grant access, register providers, queue Preview/Deploy, publish Template Specs, change ADO definitions or rename existing Azure resources.

The [implementation guide](../resource-naming.md) covers the shared resolver, non-executable draft registry, optional Bicep name maps for Private Storage/Key Vault/Observability, generated discovery artifacts and portal draft/table source integration. Latest checks: 99 Node naming cases, three schema checks, three stack compilations with 12 standard/legacy environment pairs, 38 discovery cases, 74 focused .NET tests and nine actual Release HTTP checks. The other six workload bindings, target/request compatibility and protected Preview/Deploy propagation remain open. The authenticated primary portal was not restarted; browser visual acceptance is pending. The [binding review](stack-naming-bindings.md) records all nine workloads.

Proceed through the phases below without asking for routine implementation approval. Keep each phase reviewable and record its actual checks. A later continuation should read this plan and the checkout, resume the first incomplete phase and preserve earlier work. This document does not schedule background execution or create an automation.

## Intended developer experience

1. Select workload and registered scope/environment/region.
2. Choose an existing registered stack instance or prepare a new disabled instance. Enter a descriptive stack name and short stack acronym; choose the permitted Internal/External classification. Workload type remains separate from instance identity.
3. See a live **Calculated resource names** table and diagram: exact proposed names where inputs are known, resource type, purpose, scope, naming rule/exception, planned ownership and name availability status. A missing prerequisite produces an explicit unresolved row.
4. Discover using this identity. Existing resources show their actual names; shared references are selected by approved IDs, never silently adopted by name similarity.
5. Review a Preview-only request. The saved naming plan, discovery selection and configuration version travel together to the protected pipeline.
6. When a saved Azure Preview is loaded, show **Azure What-If names and actions**, highlighting differences from locally calculated names, unresolved expressions and conditional resources. Names are searchable and copyable; full parent paths and ARM IDs disambiguate children.
7. After a separately authorized future deployment, show **Observed names** and verify them against the frozen plan. AI can explain differences; it cannot approve renames or invent resolved identities.

The existing conceptual topology remains available as an architecture overview. It must not masquerade as a complete list of resource instances.

## Naming decisions

The requested example uses hyphens, technically lowercase *kebab-case*, rather than underscore-based snake_case. Adopt the exact requested token order:

```text
{environment}-{resourceType}-{stackAcronym}-{exposure}-{regionCode}
```

Use lowercase ASCII. Resource-specific compact profiles concatenate the same ordered semantic tokens where separators are prohibited or a strict length limit requires compact formatting. Do not introduce underscores globally.

| Input | Proposed contract |
|---|---|
| `stackDisplayName` | Human-readable purpose, 1–80 characters; not inserted into Azure resource names or shell commands |
| `stackAcronym` | 2–4 lowercase ASCII letters/digits, starts with a letter; reject invalid input rather than silently deleting characters |
| `instanceId` | Immutable opaque registration identity; not the workload slug, a display name, or an Azure Deployment Stack name |
| `instanceOrdinal` | Stable reviewed local instance discriminator, initially `01`; never generated from run number/time |
| `environment` | Existing allowed `dev`, `qa`, `uat`, `prod` tokens |
| `exposure` | `internal` → `int`, `external` → `ext`; classification only, not an instruction to enable public access |
| `region` / `regionCode` | Actual approved location, initially `eastus2` → `eus2`; map reviewed in configuration, never accept a contradictory free-text alias |
| `namingProfile` | Explicit versioned `legacy-v1` or proposed `standard-v2`; existing targets retain legacy until explicitly migrated |
| `namespaceSalt` | Persisted non-secret collision namespace for the instance, used only when needed; never rotate implicitly |

Use Microsoft abbreviations as a starting catalog, with deliberate platform aliases where the same ARM type has different roles: Functions and Logic App Standard are both `Microsoft.Web/sites`, but need distinguishable `func` and `logic` labels. Profile rules are keyed by ARM type **and component role**, not by portal display text. See [CAF abbreviations](https://learn.microsoft.com/en-us/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations).

Example stem for stack acronym `pay`, dev, internal, East US 2:

```text
dev-rg-pay-int-eus2
dev-vnet-pay-int-eus2
dev-func-pay-int-eus2
```

These illustrate token order, not globally qualified final names. Append stable role/instance disambiguators for repeated resources and a deterministic uniqueness suffix for global namespace profiles. For example, host and event storage accounts must not collapse to the same name:

```text
devstpayinteus2h<hash6>   # storage host role, compact form
devstpayinteus2e<hash6>   # event storage role, compact form
```

`<hash6>` is explanatory notation, never a literal resource name. The resolver uses a versioned lowercase base32 SHA-256-derived suffix over canonical subscription/instance/type/role/region/salt inputs. Six characters is a proposed baseline with explicit collision handling, not a guarantee of global availability. No model generates suffixes. Tests pin serialization and vectors; changing the algorithm requires a new naming profile.

Length resolution is deterministic: reserve required suffix/role space, try the documented separated representation, then the resource's approved compact representation. If still invalid, reject with the exact input budget. Never blindly truncate, drop environment/exposure/region tokens, or silently change the acronym. Show which representation was selected and why. Collision detection blocks and requests a revised persisted identity/salt; it never chooses a random new name during Deploy. Name-availability results expire and do not reserve a name.

Names describe intent, not effective security. Existing private-only products continue rejecting public configuration even if an external classification is proposed; the catalog must disclose unsupported classifications until a qualified external product exists. Names use the deployment's region context; the preview also displays the resource's actual location, including global services.

## Resource exceptions and ownership

Azure naming rules differ by type, scope and child resource; a universal punctuation rule is insufficient. Storage accounts require 3–24 lowercase alphanumeric characters; role assignments use GUIDs; Web apps and Functions share name restrictions. Validate provider-specific lengths, reserved words, first/last characters and uniqueness scopes. Sources: [Azure resource name rules](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-name-rules), [Bicep name generation](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/patterns-name-generation).

Classify every resource as one of:

- **Convention-owned:** new RG, VNet, NSG, endpoints, storage accounts, identities, hosting plans, apps, data services, monitoring resources and workload Deployment Stack. Use the frozen naming plan.
- **Provider-mandated:** private DNS zone FQDNs, GUID resource identifiers (including role assignments/workbooks where required), service children such as `default`, Web configuration names. Preserve the required name, show the exception and use supported tags/display labels for stack context.
- **Application-contract names:** queues, containers, file shares, ADF pipeline/dataset/linked-service names, notebook references, AKS node pools, Kubernetes namespaces/service accounts and Helm releases. Change only with coordinated runtime/configuration updates; some remain fixed short aliases. Label the choice in the plan.
- **Reused/shared:** exact selected ARM IDs and existing names, including shared DNS/Log Analytics/AVNM/IPAM, service connections, ADO environments and repository definitions. No implicit rename or adoption.
- **Provider-generated:** managed RGs, NICs, AKS/Databricks-managed child resources and similar service outputs. Show explicit configuration when supported; otherwise mark service-assigned until observed. Do not fabricate final names.

The audit found **47 distinct explicitly declared ARM types** under `modules/` and `workloads/`. This excludes inline children and provider-generated resources: phase N1 must enumerate those too. The nine compositions are Blob copy, Event flow, Private Storage, Key Vault, Observability, HTTP API, Service Bus worker, Private Data Platform and Private AKS. Helm and AVNM require separate naming/ownership treatment, not an automatic reformatting pass.

Existing resources are not renamed in place by changing a parameter. A new name can imply another resource, resource group or stack ownership identity. Retain exact legacy output names; show legacy→standard diffs as **Migration required**. Block Apply on identity changes to an established instance unless a separate migration workflow qualifies them. `detachAll` is not rename safety or recovery. This follows Microsoft's recommendation to design durable naming conventions: [CAF naming guidance](https://learn.microsoft.com/en-us/azure/cloud-adoption-framework/ready/azure-best-practices/resource-naming).

## Current gaps and source map

| Current source | Change required |
|---|---|
| `Catalog.cs` `RunRequest(Product, Environment, Region, Operation, DiscoveryRunId)` and `SingleOrDefault` per environment | Add registered instance and typed naming context; support multiple instances of one product/environment without ambiguity |
| `config/portal-topologies.json`, `wwwroot/topology.mjs` `proposedTopology` | Component-group labels today; retain overview and add per-resource resolved plan rows |
| `PreviewDiagram.cs`, `wwwroot/iac.mjs`, `wwwroot/topology.mjs` | Project explicit resource name/type/parent/scope from qualified IDs and evidence; don't reduce all children to an ambiguous final ID segment |
| `Get-ServiceStem`, `New-ServiceTarget.ps1`, workload-specific PowerShell helpers | Centralize standard-v2 naming; preserve byte-for-byte legacy behavior and migration guards |
| `workloads/*/main.bicep`, `stack.bicep`, environment files | Currently mix stems, `uniqueString` and inline names. Pass the approved name map for standard-v2 rather than reimplementing it |
| `modules/**/main.bicep` and workload-local modules | Many already take `name`; audit internal child names/outputs and accept explicit typed names where useful |
| `New-StackContract` in `stack-service-common.ps1` | Current `stack-{workload}-{environment}` collides for multiple instances. Version the contract and derive instance-specific standard-v2 identities; preserve existing stacks |
| `Resolve-WorkloadRequest.ps1`, bundle/Preview/Deploy scripts and manifest validators | Carry and hash the same naming intent, plan and policy version through all stages |
| `Update-ServiceCatalog.ps1`, root YAML and shared templates | Generate supported fields from the contract; no manual divergence between portal and native ADO |
| `BicepDrafts.cs`, module contracts, agent evidence | Drafts use the same resolver and show unresolved names; AI recommendations cannot mint trusted names or mutable deployment identities |
| `Start/Stop-Portal.ps1`, `BrowserIdentity.cs`, CLI/GitHub/Codex session owners | Current sessions are process-local; stop deletes the CLI cache. Auth-preserving rebuilds need a qualified handoff before restart |

## Shared contracts and implementation structure

Implemented foundation layout (workload adapters and source views currently cover the first three products; full deployment integration remains planned):

```text
config/naming-policy.json                 # versioned token/region/type/role rules
schemas/naming/intent.schema.json
schemas/naming/plan.schema.json
scripts/naming/core.mjs                   # one pure deterministic resolver
scripts/naming/cli.mjs                    # bounded stdin/stdout adapter
scripts/naming-common.ps1                # invoke/validate/freeze; no second algorithm
src/SelfService.Portal/NamingPlan.cs       # bounded typed adapter, not a separate name generator
tests/naming/                            # rule coverage, stable vectors, collision/length tests
docs/resource-naming.md                  # operator/developer contract after implementation
```

Use the existing installed Node runtime; avoid a module registry or new cloud service. Bicep resource modules accept concrete names. Workload composition receives a typed name map for standard-v2 and passes its relevant names to modules. The Node resolver is authoritative for the new convention; .NET, PowerShell and browser do not independently reproduce hashing or truncation. Direct local Bicep users run a name-plan generation command first, producing reviewable `.bicepparam`/parameter JSON with concrete names. Compile-time Bicep decorators/types and deterministic preflight validate the map.

Each plan row needs `componentKey`, ARM type, parent key, name/full child path, expected scope/ID where known, role, condition, ownership, rule/version, naming certainty and exception reason. Keep an opaque UI graph key separate from a valid ARM ID. Preserve nested deployment/module context and extension-resource scope. Module deployment names are bookkeeping, not product resource names.

Plan-level fields include schema/profile version, immutable instance identity, normalized intent, rule/catalog digest, parameter/input digest, stable scope and row digest. Hash excludes timestamps; receipt records creation time separately. Resolve optional resources from explicit settings, not a hard-coded node count. Unresolved expressions stay unresolved—never label a guessed value as exact.

Bundle/Preview/Deploy bind these digests to source commit, artifact run, target, compiled template and effective parameters. Deploy consumes the reviewed frozen names and rejects drift; it must not regenerate different names after approval. A naming-only change invalidates previous discovery assumptions and Preview artifacts. Discovery scope evidence may be reusable only through explicit freshness/scope rules, while create/reuse decisions must be recomputed for the selected instance.

Shared Template Specs represent reusable workload types and content-hash versions, not individual customer stack names. Keep their existing publication identity unless separately migrated. Deployment Stacks/RGs are per instance. Never rename `SC-AZ-A-Bicep` or the ADO pipeline definition as a consequence of resource naming.

## Preview and agent presentation

- Show columns: action, resource name, type, purpose, parent/scope, naming status, ownership, source phase. Group by RG/type, allow search/copy and JSON/CSV download. Escape untrusted strings and preserve full IDs in accessible detail views.
- Graph labels show name first and friendly resource type second. Distinguish configured proposal, observed configuration, Azure What-If and agent interpretation visually and textually. Conditional resources indicate included/excluded/unresolved; provider-managed resources do not get invented labels.
- Parse ARM IDs with nested provider/type/name segments and subscription/RG/extension scopes; test multiple same-named children. What-If Ignore/Unsupported/unresolved changes remain explicitly incomplete. Do not substitute the local name plan into missing Azure evidence.
- Add `resource-names.json` and a named-resource table to pipeline summary/Preview artifacts; retain raw What-If and before/after evidence. Azure-reported drift from the frozen names blocks qualification rather than merely changing the diagram.
- AHP/MCP receives the frozen naming plan, evidence references and typed exceptions. Existing skills may explain naming/ownership differences or propose a draft context; recommendations must pass the same resolver. No automatic model call on typing, sign-in or preview-name calculation.
- Tagging, DNS, drift, network diagnostics, recovery policy views and release promotion use actual names plus `instanceId` metadata; do not identify ownership solely from parsing names. Preserve fixed DNS domains and runtime references.

## Phased delivery and acceptance gates

| Phase | Scope | Required completion evidence | State |
|---|---|---|---|
| N0 | Checkout audit, whole-system design and implementation order | This plan, current-source gaps, Azure naming sources, explicit auth/deployment boundaries | Complete — design only |
| N1 | Naming policy, resource/child/role inventory, schemas and deterministic resolver | Coverage of all nine workloads plus exceptions; cross-entry golden vectors; compact/max-length/collision/invalid input tests; no cloud calls | In progress — offline foundation tested; 47 explicit types classified; workload role/inline/runtime qualification remains |
| N2 | Registered stack-instance model, request/target schema compatibility, disabled new-instance draft path | Two instances of same workload/environment resolve distinctly; existing targets load unchanged; workload type != instance != Azure stack identity; naming edits invalidate approvals | In progress — offline draft registry/schema, duplicate identity and checked-transition validation tested; target/request binding and approval integration pending |
| N3 | Concrete module/composition parameters and adapters for all nine workloads | Legacy names unchanged; standard-v2 compile/parameter checks across every environment; nested runtime references and outputs agree; module-contract/Bicep-draft qualification updated | In progress — group (a) optional maps, three stacks and 12 standard/legacy pairs compile; remaining groups and runtime qualification pending |
| N4 | Discover/Preview/Deploy artifact and YAML propagation | Portal/native ADO fields agree; frozen plan hashes carried/rechecked; mismatches and renames blocked; offline pipeline contract tests pass; no ADO execution required | In progress — new Discover analyses generate/hash observed and matching proposed-name artifacts via existing shared step; protected Preview/Deploy transport remains pending |
| N5 | Portal forms, named tables/diagrams, saved What-If presentation | Browser tests of new input, all products, conditional names, unknown names, duplicate child labels, stale results, accessibility and downloads; source-only predictions labeled | In progress — local draft editor and generated analysis tables implemented; isolated actual Release HTTP checks passed; browser and full rollout pending |
| N6 | Agent/skills, networking/tagging/DNS/drift/recovery/data/Helm documentation and evidence alignment | Existing workflow tests; no inferred ownership or approval; no fixed-provider/application names accidentally rewritten; packaging includes required resolver/config | Pending |
| N7 | Auth-preserving lifecycle qualification, actual Release rebuild and local acceptance | See restart gate below; actual running app readiness + browser tests after supported restart; Windows ARM64 tested and x64 build status reported separately | In progress — actual Release candidate rebuilt/tested separately on Windows ARM64; primary portal preserved; handoff/browser/x64 acceptance pending |
| N8 | Documentation/catalog/source manifest and final audit | Main README/wiki/module/workload/operator docs updated; checks and unmet live acceptance recorded; no cloud resource changes | In progress — first-group documentation and evidence updated; final all-phase audit remains pending |

N1/N2 precede activating new Bicep naming. Additive optional maps may compile earlier while existing defaults remain legacy. N3/N4 must be complete before enabling new portal requests; N5 may ship a read-only display earlier, but must not send unsupported deployment parameters. N7's lifecycle investigation starts before any phase requiring a restart. The new default for freshly registered instances can be standard-v2 only once the whole request-to-bundle path is qualified; existing instance defaults remain legacy-v1.

Generated-evidence requirement: never manually insert names into saved What-If summaries or portal views. Fresh discovery derives observed names from its inventory and separately calculates matching source drafts. Explicit saved-pair reanalysis produces a new dated report, not a fresh Azure observation. New Azure What-If must establish actual planned changes; local draft calculations cannot replace it. Preserve prior report files and their hashes.

Review clarifications adopted with the first implementation: append the ordinal to scoped convention names (the earlier five-token examples are stems); global names carry ordinal/instance/role in their suffix. Require explicit parent bindings for child IDs. Preserve unknown name availability, even when validation passes. Application-contract validators and reserved-word/service checks are not replaced by the generic resolver. New instance registration must enforce ordinal uniqueness across existing registrations, beyond the resolver's within-plan collision check.

For N3, migrate in reviewable groups: (a) Private Storage/Key Vault/Observability, (b) HTTP API/Service Bus worker, (c) Event flow/Blob copy, (d) Data Platform/AKS and separate Helm/AVNM exceptions. Do not declare N3 complete until all groups and retained legacy behavior pass. Disabled targets are not evidence that names can safely change in another environment.

## Authentication-preserving rebuild gate

The current browser session is stored in memory; ordinary Stop disposes sessions and clears owned Azure CLI credentials. No supported checkpoint endpoint exists in the running build. **Do not invoke ordinary Stop or replace the live process during initial phases.** Source/static checks can proceed without disrupting it. Builds can use a staging output for validation, but final acceptance must run the actual normal Release app, not only test packages.

Design an explicit governed restart handoff before activating rebuilt backend code: same Windows user, same local browser session, short expiry, single-use ownership transfer and encrypted/ACL-protected local state. Reuse the CLI/GitHub/Codex providers' supported caches; never copy bearer tokens into source, logs, request JSON or browser storage. Preserve original expiry and account/tenant/model checks. Restore no pending approvals, queued actions, agent turns or stale evidence authorization. An absent/disconnected Codex session remains disconnected.

Default Stop/sign-out must still remove credentials. Only a qualified restart preserves an existing connection; startup must reverify identity/audience/readiness before reporting Connected. Test expired/revoked credentials, changed OS user/account, replayed/tampered handoff, failed new build, rollback to previous binary and cleanup after timeout. No hidden login/MFA automation or security bypass is authorized.

The **first migration from the currently running build** cannot be assumed to support that handoff. Investigate a supported provider-cache reattachment without altering the live session. If the current build cannot hand credentials over safely, finish independent source work and retain the running app; report the precise restart limitation before stopping it. Do not promise that every identity can survive the first upgrade. Future human-only consent/MFA remains a possible external dependency even with a qualified restart mechanism.

## Regression dependencies and release boundaries

Carry forward known discovery issues from [the issue log](../testing/README.md): fix the missing test-fixture helpers when those tests are required; qualify artifact-host handling before claiming live Preview loading; clear stale preview/analysis state so names cannot belong to a prior request. Keep each fix tied to its own evidence. Portal DNS fallback and provider registration are separate from naming; no provider registration is part of this implementation.

Test standard-v2 against long inputs, all environments, both declared exposure tokens under allowed policy, role/instance collisions, region mismatches, reserved/fixed names, duplicate child names, malicious display strings, non-ASCII inputs, unknown type/role, unresolved IDs, plan mutation, reused resources, legacy artifacts, and existing-stack rename attempts. Include exact expected name snapshots, Bicep compilation and generated menu/schema checks for every product. Keep nested What-If caveats and protected ownership checks intact.

Live Azure name availability, server-side What-If, Deployment Stack update and runtime acceptance are future separately authorized checks. Local compile/test success does not establish Azure name availability. No claim that an external classification changes network security, and no claim that a naming preview creates infrastructure.

## Continuation instructions

At each subsequent implementation prompt: read this phase table and current diff; execute the next pending phase(s), update their state and evidence paths, and run relevant checks. Continue through already authorized source/refactor/docs/test work without routine reconfirmation. Preserve unfinished unrelated changes and current credentials. Never commit/push, queue deployment/Preview, modify Azure/ADO access or enable targets merely to complete this plan. Record blockers precisely; do not label a partial phase complete.
