---
name: platform-discovery-audit
description: Review saved discovery manifests for Blob copy or Event flow, separating consistency, freshness and query coverage from authenticated ADO handoff. Use to diagnose discovery artifacts without Azure changes.
---

# Audit saved discovery

This skill uses the implemented offline analyzer; the proposed platform MCP tools are not available yet. Read [the analysis guide](../../../docs/self-service-analysis.md) and locate the user's selected `manifest.json` / `inventory.json` pair. Do not substitute another run or fetch the latest automatically.

Run `scripts/Export-SelfServiceAnalysis.ps1 -DiscoveryDirectory <selected directory> -OutputDirectory <new artifacts path>`, supplying the requested workload/environment when known. Use the current evaluation time for readiness questions; a historical `-EvaluatedUtc` is only for an explicitly retrospective analysis and must be identified as such.

Read `analysis.json` and the report. Distinguish reported successful-empty collections from denied/incomplete queries, stale evidence, unsupported schema, mismatched selection and ownership conflicts. Refer to finding IDs and exact missing evidence. Do not copy raw diagnostics, connection strings or artifact contents into public documentation.

The analyzer checks inventory bytes against their manifest, but does not authenticate that manifest or ADO run. Report `UnverifiedOffline` and keep deployment authority false. Do not call `Test-DiscoveryHandoff.ps1` as an incidental offline check: its authenticated pipeline path is a separate operation. Explain the existing deployment handoff if asked whether the run can deploy.

Return useful findings even for partial artifacts that produce a report. If the analyzer rejects malformed/mismatched input, stop that artifact review with its safe error; never bypass validation or rewrite the manifest hash.
