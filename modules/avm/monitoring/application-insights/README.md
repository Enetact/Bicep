# AVM private Application Insights

[main.bicep](main.bicep) wraps official `avm/res/insights/component:0.8.0` from [pinned upstream source](https://github.com/Azure/bicep-registry-modules/tree/4f750c70f333b2df6170e3b56ae90faa852361d2/avm/res/insights/component). Exact compiled ARM and the original MIT license are preserved in [the verified bundle](../../../../vendor/azure-verified-modules/bundle.json). The locally maintained wrapper applies a constrained profile and has no separate AVM certification claim.

| Input | Contract |
|---|---|
| `name`, `location`, `tags` | Required explicit component name, approved region and ownership tags. Drafts leave names unresolved. |
| `workspaceId` | Required approved existing or composition-owned Log Analytics workspace ID; an observed ID does not authorize reuse. |
| `applicationType` | `web` by default or `other`. |
| `retentionInDays` | One of 30, 60, 90, 120, 180, 270, 365, 550 or 730; default 90. Review effective workspace retention. |
| `samplingPercentage` | 1-100, default 100; instrumentation support must be verified. |

Owns only the workspace-based telemetry component. Local authentication and public ingestion/query are disabled; IP masking remains enabled, overriding upstream defaults. Telemetry from the module itself is disabled. Outputs `id`, `name` and `applicationId`; instrumentation key and connection string are deliberately not wrapper outputs.

The composition must qualify an Azure Monitor Private Link Scope and its scoped-resource association, private endpoints/DNS, approved workspace, Entra ingestion/query permissions and application instrumentation. This wrapper creates none of those dependencies. Missing Private Link prevents private telemetry from becoming operational. Management permission does not imply telemetry ingestion permission. Review data classification, sampling, workspace ingestion/retention charges, recovery and private query behavior.

See [source-draft qualification](../../../../docs/bicep-source-drafts.md#qualification-and-promotion). Compilation does not prove Azure Monitor connectivity, application authentication, telemetry delivery or runtime acceptance, and this module is not a newly registered deployable product.
