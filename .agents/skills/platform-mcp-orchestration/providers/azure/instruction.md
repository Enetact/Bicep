# Azure decisions

Provider id: `azure`. Authentication requires the portal's isolated local Azure CLI connection. A registered Entra app connection alone is insufficient. Select the intended tenant/subscription from visible scope before querying a service; a successful subscription list proves visibility only for that call, not resource, data-plane or administrator access.

Search the catalog by the required family: subscription/group/ARM; compute, app hosting, containers, registries and virtual desktop; storage/files, databases and analytics; messaging, communication and IoT; identity, roles, policy and Key Vault; Foundry, search and speech; monitoring, health, diagnostics and load testing; architecture, pricing, quota and optimization; backup, migration, resiliency, deployment and Terraform/Bicep guidance. Use only families actually returned by this server, not guessed tool names or a frozen catalog.

Read each schema and choose a narrow explicit scope, filters and bounded paging. Some tools multiplex operations; inspect the requested suboperation before planning. Control-plane visibility is not data-plane access. Distinguish current resources, configuration, effective connectivity, quotas, physical capacity and estimates. Preserve denied/partial results; a missing page is not an empty estate.

Check the broker effect before invocation. Azure `_meta.SecretHint` marks sensitive tools independently of their names: approval is required and output is withheld, even within mixed plans. Never seek the same secret through another tool. Changes, native confirmation and unknown effects follow the shared exact-plan route; no CLI, raw HTTP or identity fallback.

For requested deployment, role, policy, data or destructive operations, identify the precise target and intended effect. Prefer the platform's qualified domain pipeline when its ownership/Preview checks are required. Generic Azure tools do not inherit workload-target gates. Service RBAC, deny conditions and private connectivity still apply after plan approval.
