---
name: platform-change-review
description: Explain saved Bicep Deployment Stack Preview evidence and material resource, ownership or application changes before review. Use for plan analysis; does not generate new Azure What-If or approve/apply changes.
---

# Review a saved deployment plan

Read [Preview semantics](../../../docs/deployment-preview.md), [stack ownership](../../../docs/deployment-stacks-upgrade.md) and the selected run's saved `deployment-preview/README.md`. Inspect the associated preview plan, effective parameters, compiled template and package metadata only to answer the requested review. Do not assume a saved report is authenticated or current.

Separate Create/Modify/NoChange from Delete/Detach, ownership/deny changes and unresolved/potential changes. External Reuse and stack-owned Manage are distinct; a managed resource changed into a Bicep `existing` reference can leave stack management. Explain application package changes separately because ARM What-If does not inspect ZIP contents.

Identify source/run identity, plan age, selected workload/environment, missing evidence and any changed digest. Use the existing deterministic gates as the deployment authority; do not reproduce their outcome by model confidence. If a complete gate result is absent, report unknown rather than approved.

This skill reads saved evidence. Running `Invoke-WorkloadPreview.ps1 -Action Preview` writes native stack What-If metadata and requires a separately requested pipeline operation. Do not regenerate hashes, rerun Azure commands, enable targets or queue/approve/apply a pipeline as part of a saved-plan review.

Return a concise change summary, material risks, evidence gaps and the next required validation. Avoid pasting raw before/after settings or credential-bearing errors; use redacted summaries and specific safe resource/property names. Proposed platform MCP change-assessment tools are not implemented yet.
