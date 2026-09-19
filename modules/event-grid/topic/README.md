# Private Event Grid Basic topic

Resource-group-scoped `main.bicep` inputs: `name`, `location`, `tags`. Creates a custom topic with system identity, EventGridSchema, TLS 1.2, public access disabled and local authentication disabled. Outputs: resource ID, endpoint, principal ID; no credentials.

Caller supplies private endpoint/DNS, publisher RBAC, diagnostics and event subscription. This module alone does not establish connectivity/delivery. Compile/contracts run in `Test-Project.ps1`; Azure delivery acceptance remains required. See [workload runbook](../../../workloads/logic-app-event-grid/README.md).
