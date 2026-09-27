# Current implementation and verification status

Reviewed from local source on **26 September 2026**. This is the current summary; [validation evidence](validation.md) preserves dated results. A source/documentation review does not refresh cloud acceptance or rerun historical runtime tests.

## Available now

Two workload types (`blob-transfer`, `logic-app-event-grid`), two example instances (`blobcopy`, `eventflow`), four environments each and **eight disabled targets**. Four dedicated ADO menus share discovery/deployment implementation. The generic pair remains for compatibility. See the [catalog and method map](self-service-catalog.md).

| Capability | Implementation | Verification and remaining boundary |
|---|---|---|
| Blob dispatcher, queue worker, ledger and recovery timers | Implemented | Recorded real Functions/Azurite smoke and recovery from 17 September. No new runtime run in this audit. Azure runtime/network/identity acceptance remains outstanding in reviewed evidence. |
| Local install/run/test/stop/reset | Implemented for Blob copy | Local lifecycle evidence exists; not every clean-machine installation branch or architecture is verified. Event flow has no equivalent local end-to-end runtime. |
| Workload-specific menus | Implemented | Generated YAML and local routing tests; supplied ADO discovery and Preview-preparation logs. Every live menu/authorization branch has not been certified. |
| Scoped discovery and saved-run handoff | Implemented | Supplied Preview run 23 verified discovery run 21. Local coverage includes DNS fallback, empty/failed queries, hashes, provenance and freshness. This does not prove deploy permissions. |
| Event flow prerequisites | Implemented | Create / Reuse / Manage / Blocked, names, CIDR checks, selected shared IDs, ownership and live-recheck hooks. Latest targeted evidence: 26 cases on 19 September. Actual Azure creation/reuse not established. |
| Hosted Preview stage | Implemented | Disabled targets can consume discovery and preview complete inputs. Run 23 stopped at five onboarding settings before Azure What-If. Later local dev settings resolve them; successful subsequent Azure What-If has not been supplied. |
| Protected Deploy stage | Implemented | BuildBundle → PublishTemplateSpec → ApplyStack, with frozen inputs, drift checks and Foundation/Release. All targets disabled; no successful live Ready receipt established. |
| Template Specs / Deployment Stacks | Implemented | Local compilation and mocked lifecycle tests. Live publication, deny permissions and stack acceptance still needed. |
| Event flow runtime and package | Implemented | Package/schema/workflow contracts and Bicep compile locally. Hosted execution, Event Grid delivery, SCM indexing, RBAC propagation and smoke need Azure acceptance. |
| Event flow dev tags, identity and exceptions | Configured, dev only | Operator identity/generated labels and [authorization record](reviews/eventflow-dev-exceptions.md). Local onboarding passes with resolved discovery; effective permissions and higher-environment approvals are separate. |
| Reference costs and frozen reports | Implemented | Dated September price snapshots, freshness checks, exclusions and owned DNS charges. No live billing integration, enforced budget or reactive menu total. |
| Private agents, routing and ADO checks | External platform setup | Supplied runs used hosted agents and authenticated discovery. Private routes, production rights, checks and agent availability are not established by that evidence. |
| Shared workspace / hub resolver reuse | Supported Blob copy configuration | Azure acceptance pending. Event flow requires its supported same-region VNet/Azure-provided DNS topology; do not assume the adapters share resolver capabilities. |
| Same application artifact promoted across environments | Not implemented | Identical infrastructure may reuse a Template Spec version. Each environment run currently builds its application again. |
| More workload types and generic adapters | Planned | Two adapter IDs are explicitly allowlisted. JSON alone cannot register arbitrary applications. See the [expansion plan](self-service-expansion-plan.md). |
| Enterprise network analyzer, dynamic IPAM allocation and AI assistance | Planned | Current discovery and fixed-CIDR prerequisite checks are implemented; coverage-aware topology analysis, allocation transactions and AI assistance are not. See the [networking design](plans/private-networking-self-service.md). |
| Drift/TTL/adoption/deletion menus and automatic rollback | Not implemented | Existing preview and operator recovery safeguards are not a general operations catalog. Failures can leave resources. |
| Policy assignments, module registry, AVM migration | Partial/deferred | Separate Policy-definition/registry templates exist; workload flow uses local modules. Assignments/publication/migration are not delivered by it. |

## Evidence levels

- **Implemented:** entrypoints/contracts exist and were traced.
- **Locally verified:** identified tests/compilations passed on their recorded dates, often with Azure mocks.
- **Observed in ADO:** supplied logs establish only steps actually reached.
- **Azure accepted:** requires retained real preview/apply/readiness evidence; a full successful deployment is not established here.
- **Planned:** proposal only, not a shipped menu or module.

The last full project run recorded on 19 September passed 333 PowerShell contracts, 46 pipeline/infrastructure checks across 20 YAML files and 18 application tests, with 15 opt-in emulator cases skipped. After dev configuration changed, the targeted run passed 26 prerequisite, 50 Logic App and 22 preview cases. These are different runs; do not combine them into a new full-suite claim. See [validation](validation.md) for receipts and tooling dates.

## Next acceptance sequence

1. Review target prerequisites, costs and authorization. Dev's generated cost-center label is not a verified finance-system code. Blob copy needs its real destination/topology.
2. Confirm ADO permissions, identity scope, providers and quotas. Run matching Discover on `main`; repeat after target/policy selection changes, including Event flow enablement changes.
3. Run dedicated Deploy in **Preview only** and retain successful Azure validation/What-If. Resolve blockers without treating missing evidence as zero changes.
4. Establish private agents/routes and protected environments/checks. Enable only the chosen dev target, regenerate catalog/manifest, rerun discovery and queue **Preview and deploy**.
5. Retain a Ready receipt and independently verify recovery/alerts/runtime behavior. Qualify higher environments separately. Add immutable application promotion before release policy requires it.

Implementation, local verification, onboarding and production acceptance are distinct milestones. Historical artifacts are local or ADO evidence, not automatically present in a clone. See the [documentation index](README.md).
