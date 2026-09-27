# Enhanced self-service implementation progress

Started 26 September 2026 after authorization to review the documentation/structure and proceed through the plan. This record tracks actual increments separately from the [target design](enhanced-self-service-validation.md). It is not permission to skip a required technical acceptance gate.

## Documentation and structure review

**Follow-up: bundled skills and Azure inventory, 27 September 2026.** The [Azure Skills discovery guide](../azure-skill-discovery.md) records the next delivered increment: 42 pinned Microsoft definitions plus supporting files, no pipeline associations, and a fixed read-only ARM collector using the portal's Azure browser identity. Network discovery reports observed configuration and unknown coverage without CIDR allocation or creation decisions. Local verification includes 41 backend tests, bundle hashes and real localhost HTTP checks. Live Azure acceptance and the remaining V2–V4 gates are still pending.

**Follow-up: local portal, 26 September 2026.** User-requested [Platform Studio](../local-portal.md) is implemented under `src/SelfService.Portal`, with ARM64/x64 publishing, existing catalog/skill/analysis integration and guarded ADO request/authentication code. This advances the local request experience independently of V2–V4 network allocation. Entra registration, real ADO calls and cloud acceptance remain outstanding; no AI/MCP runtime or address authority was added.

Inventoried all 47 pre-implementation Markdown documents, including project skills, module interfaces, workload guides, runbooks, evidence and plans. Checked navigation, current-versus-historical scope, code entrypoints and the implementation-plan dependencies. Detailed source tracing focused on discovery, workload dispatch, prerequisite resolution, Preview, stack lifecycle and pipeline qualification because they define this increment's compatibility boundary. This was not a fresh line-by-line audit of the application runtime or a repetition of its historical smoke tests.

| Documentation family | Disposition |
|---|---|
| Root README, catalog, completion, self-service, pipeline/discovery/preview guides | Active authority; add the new analysis path without claiming new deployment menus or Azure acceptance. |
| Repository/module/workload interface guides | Keep Bicep resource modules and workload composition in place; add analysis code, schema and fixture locations explicitly. No source moves or resource ownership changes. |
| Runtime, dispatcher, local setup/recovery and security guides | Existing workload-scoped contracts remain applicable; do not make them describe a generic MCP platform or Event flow emulator. |
| Original ADO / Microsoft Learn assessments and dated validation | Historical evidence remains dated; do not overwrite past execution results. |
| Expansion/networking/skills/readiness plans | Preserve V0–V6 dependency gates; update delivered subset and link to this progress record. Skill guidance and future MCP interfaces remain distinct. |
| Cost snapshots and development exception record | Existing assumptions/authorizations retained; this reporting work grants no production exception or updated price claim. |

Structure decisions: shared pure operations and rendering in `scripts/analysis/`; thin local wrapper in `scripts/`; versioned contracts in `schemas/analysis/`; synthetic fixtures in `tests/analysis/`; ignored execution evidence in `artifacts/`; project skill instructions in `.agents/skills/`. `platform/` remains independently owned Azure infrastructure, not an undifferentiated application/service folder. Future MCP hosting will get its own `src/` project after SDK/hosting selection; no empty server scaffolding is created now.

## Increment status

| Increment | Delivered / remaining |
|---|---|
| V0 baseline/contracts | Added offline evidence output schema, explicit old-manifest readers, rejection/ownership fixtures and structural placement. Full future product registry, binding and release schema migration is not complete. |
| V1 offline reports | Implemented both workload readers, coverage/findings, observed/proposed containment, Markdown/Mermaid/static SVG, integrity receipt and qualification integration. Shared Discover integration authored; actual ADO execution/rendering remains a live gate. |
| First four project skills | Local request design, discovery audit, topology reporting and saved change review implemented as repository guidance. They use actual local commands/evidence and explicitly reject nonexistent MCP tool assumptions. |
| V2 discovery/product contracts | Enterprise coverage collectors, generalized typed registry and network profile rules remain planned; existing collectors and deployment resolver remain authoritative. |
| V3 allocation | Requires backend/pool/profile decision and an authorized sandbox; no allocator or Azure write path is shipped by this increment. |
| V4 pilot | Additional protected stages and live network/workload acceptance remain gated on V3 and platform setup. |
| V5/V6 | New products, immutable promotion, lifecycle operations, runtime MCP and model assistance remain subsequent increments. |

The next live input is the address authority and approved connectivity profile for the allocation spike. Absence of that input does not make offline analysis fail or relax existing deployment checks. All current targets remain disabled. See [the analysis guide](../self-service-analysis.md) for commands and exact reporting limits.

## Verification for this increment

`Test-Project.ps1` passed with 36 new analysis cases, 5 output-schema validations, all existing local platform suites, 46 pipeline/infrastructure contracts, both workloads' Bicep compilations and 18 application tests (15 emulator cases skipped). The operator build and Function dependency advisory check passed. Four project skills passed frontmatter/scaffold validation; the request-design command was exercised; a synthetic SVG was rasterized and visually inspected; four emitted Mermaid diagrams parsed with Mermaid 12.0.0. These checks qualify the local increment only. The outstanding ADO renderer, server expansion and live pipeline/identity checks are not replaced by local expression expansion.
