# Private Logic App Standard host

Optional `planName` independently names the hosting plan; its default remains `asp-${name}`. Compositions pass a concrete standard-v2 name without altering the runtime storage/workflow contracts.

Resource-group-scoped `main.bicep` requires `name`, `location`, `tags`, `runtimeStorageName`, `eventStorageName`, `topicId`, `integrationSubnetId`, `instrumentationConnectionString`, `releaseActivated`. Optional `skuName`: WS1 default, WS2 or WS3. Outputs: site `id`, system `principalId`.

Creates Windows Workflow Standard plan/site and disables SCM/FTP basic publishing. Caller supplies private runtime account and workflows file share, delegation, endpoints/DNS, RBAC and monitoring. ARM resolves storage keys into runtime/content app settings; caller must enforce the credential exception. No keys are output. Private SCM uses Entra bearer authentication.

`releaseActivated` controls `Workflows.process-event.FlowState`, not workflow installation. Content is a separately hashed ZIP. Defaults: Functions ~4, Node ~22, private content over VNet. Compilation is covered by `Test-Project.ps1`; Azure mount/startup/indexing acceptance remains required. See [workload runbook](../../../workloads/logic-app-event-grid/README.md).
