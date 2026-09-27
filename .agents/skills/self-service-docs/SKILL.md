---
name: self-service-docs
description: Maintain this Bicep self-service repository's documentation when adding, changing, removing, or verifying workloads, pipeline menus, infrastructure, runtime features, or developer operations. Trace implementation and evidence, update affected guides and capability status, and reconcile the expansion plan. Also use for documentation audits; do not implement roadmap items merely to document them.
---

# Maintain self-service documentation

Keep the developer-facing explanation aligned with the actual checkout and the evidence available for it. Apply this workflow alongside feature work, or independently for a requested documentation audit.

## Establish scope from the checkout

- Locate the Git root containing `config/workloads.json`, `workloads/`, `pipelines/` and `docs/`. Resolve paths from that root; do not depend on a particular drive or developer directory. If the layout has changed, locate its replacements before editing.
- Read applicable repository instructions and inspect working-tree changes. Use the current task's change list or a verified comparison ref; do not assume a branch name, discard unrelated changes, or claim another contributor's tests as your own.
- Start with `docs/README.md`, `docs/self-service-catalog.md` and `docs/completion-status.md`. Read only the implementation and additional guides relevant to the change. A documentation audit may require broader coverage.
- Trace the capability from its entry point through validation, configuration, adapter/composition, dependencies, outputs and readiness. A Bicep module, roadmap row or catalog entry alone does not establish a selectable, deployable product.
- Recompute changing facts from source: registered types/instances, target counts and enabled state, menu fields/defaults, stage/job names, package kinds, resource ownership and supported operations. Do not carry forward today's counts as permanent facts.

## Choose the affected documents

Paths in this table are relative to the repository root. Update affected sections, not every file for every small change.

| Change | Documents to reconcile |
|---|---|
| Developer-visible capability, new/removed offering, onboarding | `README.md`, `docs/self-service-catalog.md`, `docs/completion-status.md`, the workload README; add navigation to `docs/README.md` for new guides. |
| Menu fields, YAML roots, jobs or artifact handoff | `docs/self-service.md`, `docs/pipeline-flow.md`, `docs/deployment-preview.md`; dedicated and generic routes must be described separately when behavior differs. |
| Inventory, naming, create/reuse or ownership | `docs/subscription-discovery.md`, `docs/prerequisite-resolution.md`, the affected workload README. |
| Template Spec publication or stack lifecycle | `docs/deployment-stacks-upgrade.md`, `docs/pipeline-flow.md`, `docs/enterprise-platform.md`. |
| Runtime methods, triggers, dependencies, recovery | Workload README, relevant `docs/dispatcher/` guides, `docs/architecture.md`, `docs/operations.md`, `docs/local-workflow.md`, `docs/local-development.md`. Clearly scope Blob copy versus Event flow or a new product. |
| Resource/module interface or source placement | Affected module README, `modules/README.md`, `docs/repository-structure.md`; update resource diagrams when behavior changes. |
| Permissions, credentials, networking or exceptions | `docs/security-and-rbac.md`, workload onboarding/runbooks and affected review records. Preserve who approved what and for which environment. |
| Price assumptions, optional resources or cost reporting | `docs/self-service-costs.md` and affected menu/catalog explanations; identify price date, assumptions and exclusions. |
| New verification or changed completion state | `docs/completion-status.md`, dated entries in `docs/validation.md`, and applicable fields in `docs/validation-results.json`. |
| Delivered or changed roadmap scope | `docs/self-service-expansion-plan.md` and affected implementation plans; keep remaining work explicitly proposed. |

Search for other current statements of changed identifiers, paths and behavior. Fix contradictions in active guides. Preserve historical assessments/logs with dates and a supersession link; do not rewrite them as if the new capability existed then. Label retained static diagrams as historical if they cannot be accurately refreshed within scope.

## Explain the developer's experience and implementation

For a new or materially changed capability, document what developers select, the exact YAML entry, required inputs and defaults, what is created/reused/managed, external dependencies and permissions, costs, and the evidence they receive. Explain failure/blocking behavior and any required platform setup.

Where methods change, record the actual entry script/function, downstream calls, input/output contract and readiness check. Use diagrams for substantial flow changes, not for trivial edits. Keep exact ordered commands runnable from the repository root and mark placeholders explicitly. Keep credentials and sensitive artifact contents out of examples.

Inspect `config/`, `self-service/targets/`, `self-service/pipeline-settings.json`, root YAML, shared templates and the selected adapter as needed. Generated menus are governed by `scripts/Update-ServiceCatalog.ps1`; document actual generated behavior and do not hand-edit YAML to make a prose claim true. Native ADO queue-time fields and saved discovery artifacts are different mechanisms; do not promise mid-run dynamic dropdowns without an implemented UI supporting them.

## Keep status and evidence honest

- Separate **implemented**, **locally verified**, **observed in ADO**, **Azure accepted**, and **planned**. Disabled targets or missing onboarding are deployment blockers even when code and tests exist.
- Record verification date, source/revision context where available, command/run/artifact, pass/failure/skip counts and scope. Distinguish real services from mocks and new results from older evidence. A partially completed run establishes only the steps reached.
- Do not promote discovery success, compilation, publication, or a successful Foundation phase into full runtime readiness. Require the selected product's actual acceptance evidence for that claim.
- Preserve dated validation entries. Update `currentDocumentationReview` only when that review was actually refreshed; leave older evidence intact and explain its scope. Do not record a new full-suite result for a targeted test or documentation-only check.
- Mark roadmap work delivered only for the portion implemented; keep remaining Azure acceptance, operations, migration or promotion gaps visible. Do not turn proposed offerings into registered products in documentation alone.
- Use current official sources when adding Azure/ADO platform behavior or pricing assertions. Link them near the claim; citations explain platform behavior, not proof of this deployment.

## Verify and finish

1. Check changed Markdown links/anchors, source paths, examples, menu names and JSON syntax. Review the diff for stale counts, contradictory status, leaked secrets and accidental unrelated edits.
2. Reuse meaningful verification already completed for the task. For catalog/menu changes, run `./scripts/Update-ServiceCatalog.ps1 -Check`; for pipeline behavior, run `node ./tests/infrastructure/verify.mjs` using the repository's prepared dependencies. Use the relevant runtime/Bicep/contract checks for implementation changes. Documentation-only edits do not require rerunning the entire application suite or starting services.
3. After the final intended file edits, run `./scripts/Update-Manifest.ps1`, review its diff, then run `./scripts/Update-Manifest.ps1 -Check` and `git diff --check`. Do not regenerate the manifest in CI to hide source drift. If a check cannot run, report that limitation without claiming a pass.
4. Summarize the capability/docs changed, checks actually run, and remaining blockers. Link the relevant local documents. Do not deploy Azure resources, enable targets, grant permissions, invent approvals, publish, or commit solely because this documentation workflow mentions those actions; follow the user's task scope and authorization.

Example requests: "Document the new Service Bus worker and its remaining onboarding", "Update the guides after this Preview artifact change", or "Audit what is working and reconcile the expansion plan." A request to implement a feature still requires implementing that feature; this skill covers its documentation, not a substitute deliverable.
