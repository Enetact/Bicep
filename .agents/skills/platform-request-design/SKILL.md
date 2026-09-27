---
name: platform-request-design
description: Explain a registered workload, environment and region selection, or validate a local typed request draft using repository tooling. Supports the seven-product catalog; does not queue or deploy.
---

# Design a supported request

This project skill supports the current local catalog. In the portal agent workflow, read `platform_skill` and `platform_evidence`: these MCP tools supply a frozen catalog selection and its coverage limits. Explain only that selection; do not claim a typed draft was created or resolved. File/script steps below apply only to a separately requested repository-authoring workflow. See [portal agent adapters](../../../docs/agent-workflows.md). Do not invent an Auto networking field or additional tools.

Read [the catalog](../../../docs/self-service-catalog.md), the selected `workloads/<type>/request.schema.json` and example, and matching files under `self-service/targets/`. Use the actual repository root, not a hard-coded drive. Match workload type, instance, environment and region together; do not infer target enablement from a display name.

Explain what the selected workload creates, what stays external, and which current prerequisites/cost assumptions matter. Ask for missing business choices only when the request and catalog do not determine them. If the requested product/profile is absent, describe the gap instead of registering or deploying a new one.

When a local draft is requested, write the typed request to a new path under `artifacts/requests/`, then run `scripts/Resolve-WorkloadRequest.ps1 -RequestPath <draft> -OutputPath <new resolved path>`. This command performs local validation, not Azure deployment. Preserve the original request and report any rejection; never alter requirements just to pass validation. No subscription IDs, service connections, arbitrary executable paths or secrets belong in the developer request.

Return the selection, resources, disabled/onboarding status and paths to the validated draft/result. A resolved request is not a deploy approval. Follow the normal protected ADO route only when separately requested. Read [the analysis guide](../../../docs/self-service-analysis.md) for saved-discovery reports; no report proves effective Azure permissions.
