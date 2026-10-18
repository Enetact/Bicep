# Security and RBAC

**17 October 2026 — connected MCP authorization:** the browser-owned broker integrates complete Azure Bicep, Azure, ADO, GitHub and Learn catalogs. The explicit `mcp-orchestration` workflow adds catalog/call/plan/run tools; the other nine workflows retain only frozen evidence/skill tools. Non-read effects, sensitive tools and unclassified tools require human approval of an immutable exact plan. AHP can dispatch registered workflow/provider/plan actions, but neither AHP nor the model can approve a plan. This supersedes earlier universal frozen-tools-only descriptions. Live credential attachment, catalog/call acceptance and service grants remain separately qualified. See [credentials, plan bindings and acceptance](provider-mcp.md).

**Local GitHub authentication:** the portal has a separate GitHub CLI connection for private-source checks and GitHub MCP. Source checks use fixed HTTPS GET routes with redirects disabled. Explicit GitHub MCP connection sends its verified CLI token to the fixed `https://api.githubcopilot.com/mcp/` endpoint and requests all toolsets; it can run approved capabilities permitted by that account. New browser sign-in requests `workflow` alongside the CLI defaults for workflow-file authoring; existing attached tokens keep their existing grants, and administrative/organization features still need separate permissions. Tokens stay server-side; ADO tokens never cross to GitHub. GitHub CLI retains its normal local credential store (secure OS storage preferred, plaintext fallback possible). Portal disconnect detaches the browser and invalidates identity-bound plans without logging the terminal out. OAuth grants can exceed the selected task's needs. See [the GitHub authentication boundary](local-portal.md#github-browser-sign-in). ADO's repository connection must separately retain private-repository authorization.

**4 October 2026 — shared IaC evidence:** the dedicated workload Deploy path now captures live before/What-If/after state using pinned Microsoft Azure MCP and scoped ARM reads, renders projected comparisons in the portal and supports an explicit AHP/Codex visualizer review. See [the component contract, identity boundaries, reuse steps and manual acceptance](agentic-iac.md). This does not implement restore execution or establish live Azure acceptance.


**AKS/Helm increment — 4 October 2026:** AKS requires distinct control-plane, kubelet, application and pipeline identities. Shared network/DNS/identity/registry grants are validated, never automatically broadened. Helm uses temporary non-admin Entra kubeconfig, namespace-scoped authorization and reviewed application federation. It manages no Kubernetes Secrets or RBAC objects; Helm itself stores release metadata in namespace Secrets, so its delivery identity still requires that permission. See [the implementation and onboarding guide](../workloads/private-aks/README.md).

**Private Data Platform identity boundary:** factory managed identity, separate root/lake Databricks access connectors and deployment principal have distinct roles. Container-scoped Azure data RBAC does not grant Databricks workspace/compute permissions or Unity Catalog privileges. No PAT or blanket factory Contributor assignment is emitted. Endpoint approvals require explicit owner review. See [permissions and manual acceptance](../workloads/private-data-platform/README.md).

**Tag governance update (27 September 2026):** subscription evidence, deterministic checks, structured Codex advice, editable drafts, source proposals and two manual ADO pipelines are implemented locally. Apply is disabled until onboarding and live acceptance. See [the tagging guide](tag-governance.md) for current methods, source structure, permissions and limits. Earlier dated verification records remain historical.


**Pipeline registration boundary:** [ADO setup](ado-pipeline-registration.md) requires pre-existing ADO view/create permissions, reuses a GitHub repository connection reference and creates only definitions. It neither grants service-connection access nor configures approvals; new definitions inherit existing project permissions. Public source is not access to ADO/Azure.

**Local agent boundary:** [Codex workflows](agent-workflows.md#identity-evidence-and-failure-behavior) use isolated ChatGPT authentication plus the portal's separately attached service identities. Readiness grants no new Azure/ADO/GitHub rights. Scoped reviews expose frozen evidence/skill only; explicit orchestration can discover/call providers and prepare/run exact plans through the broker. Changing, sensitive or unknown effects need separate human approval. Tokens are supplied to provider transports, never to Codex. Protect ignored caches and private receipts; model metadata transfer happens only on explicit Run. A bounded public-only AHP/Codex/Bicep/Learn workflow passed; private resource operations, changes and each scoped cloud review need their own acceptance. See [the recorded boundary](provider-mcp.md#live-acceptance).

The final host's bounded authenticated baseline also passed GitHub `get_me` and an ordered Azure subscription/ADO project read plan. The user approved the exact latter plan in the UI because ADO lacked a read-only annotation; execution then returned successful read results and all five connections remained ready. These checks executed no changes and establish only the named calls/scopes. They do not qualify broader mutation/admin rights, protected deployments or the existing scoped cloud-review workflows.

The inference runtime disables apps/plugins, remote plugins, code-mode hosting, browser/computer/image tools and dependency tools at startup. Before each model turn it reads configured MCP server names, disables them in the isolated thread and checks every page of the full thread-scoped tools/resources/templates inventory. Disabled zero-capability registrations may remain visible; active or unverified capabilities block execution. This keeps provider calls behind the broker rather than account/plugin integrations. Configuration-preflight failure preserves a valid login; it is not evidence of inference or service execution.

Host callbacks are registered in the dedicated `platform_studio` namespace, with code mode disabled and direct calls restricted to that namespace. General JavaScript execution is not enabled to make the tools visible. A completed model turn with no required evidence callbacks is rejected; completed-output validation failure preserves authentication while an in-flight failure/cancellation stops the owned process. Azure `_meta.SecretHint` marks a tool sensitive even when its name appears read-only: exact approval is required and the result is withheld in every plan position. Wrapped JSON text is redacted recursively, and credential-shaped plain text is withheld before model/receipt exposure.

Provider plans bind the browser, ordered arguments, connection generation, identity epoch and catalog digest, expire after ten minutes and cannot be replayed after an attempt. Reconnection requires a fresh plan. Calls stop on failure or uncertain outcome; cancellation is not rollback. Bicep file arguments are confined to repository source and exclude credential/runtime/Git/artifact directories and linked paths. Receipts under `artifacts/portal-mcp/` remain private, redact recognized secrets, withhold sensitive results and bound provider output. Independent service read-back is required to verify a change. Direct provider operations use broker approval and service grants; they do not automatically inherit workload-target eligibility or domain Preview checks. Selected ADO pipelines retain controls enforced by their own definitions/environments. No new service permissions are granted by connecting a provider.

File-backed Bicep compiler/format/reference/snapshot calls also check their native file-reference dependency closure before and after the requested call, applying the same source-path policy to modules, auxiliary data and configuration. Missing, malformed, nonlocal, protected or over-budget references fail closed without returning raw diagnostics. A failure before the requested call prevents that call; scope failure after an attempted change withholds the output and records an uncertain outcome. JSON decompile retains direct-path checks. This guard is not an OS filesystem sandbox and does not establish protection against an adversary changing files concurrently. See [the exact Bicep bounds](provider-mcp.md#manual-tool-plans-and-execution).

**New product security:** the [onboarding guide](workload-onboarding.md) documents private-by-default Storage, RBAC/purge-protected Key Vault, Premium Service Bus with local auth disabled, and Entra-protected HTTP Functions. Object IDs refer to service principals, not application IDs. The existing stack gate now also rejects weakening local authentication, vault recovery/RBAC and API authentication settings. No role grants or cloud changes were executed by this local implementation.

## Local portal skill discovery

The [bundled skill discovery route](azure-skill-discovery.md) uses the signed-in user's delegated Azure Management identity, independently of ADO service connections. Read permission on the registered subscription/resources is sufficient for inventory; discovery does not require Owner. Only fixed ARM GET collections are allowed, with bounded same-collection pagination, disabled redirects and projected output fields. Authentication failures and denied/incomplete reads never become empty inventories. Tokens remain in server memory; saved local reports may contain private network names and addresses and are ignored by Git. Upstream skill text and supporting scripts are reference content, not executable instructions or authorization. The route cannot queue pipelines, allocate addresses or modify Azure.

The [local portal](local-portal.md#configure-microsoft-browser-sign-in) uses delegated user access through an isolated local Azure CLI login or a separate Entra public-client registration. MSAL tokens remain in process memory; CLI refresh credentials persist in a protected, ignored `.local/portal/azure-cli/<session>` directory until disconnect, session cleanup or the governed stop script. Both use loopback-only Host/session/Origin/CSRF controls and separate Azure/ADO token audiences. Browser responses and Codex/MCP prompts never receive tokens. Azure service-connection permissions and existing deployment guards remain independent. The portal has no credentials form, client secret or PAT configuration and is not supported as a shared remote web server. The current OS user remains trusted; process crashes can leave local credentials pending cleanup.

The data-path/RBAC details below primarily describe Blob copy. Event flow uses its [separate runtime and scoped exceptions](../workloads/logic-app-event-grid/README.md); dev authorization does not apply to higher environments. See [current status](completion-status.md) for verified versus external setup.

For the stack-based self-service pipeline, use the [publishing and deployment permissions runbook](deployment-stacks-upgrade.md#platform-setup-and-exact-local-checks). Template Spec publication, version reads, subscription stack/preview operations, RG creation and deny-setting management are additional to the workload permissions below. Restrict publishing writers and stack administrators; application developers only queue the protected workflow. External destination grants remain outside the stack's subscription deny boundary when they belong to another subscription.

## Discovery and selected-target permissions

Read-only discovery scopes resource enumeration to the chosen subscription; it does not assign roles, change networks or deploy. An Azure DevOps endpoint read requires project authorization separately from Azure RBAC. Contributor/Owner access does not refresh the already displayed Run Pipeline form. See [subscription discovery](subscription-discovery.md) for the catalog review and refresh flow.

The optional `deploymentPrincipalObjectId` adds pipeline acceptance roles at the selected package/source containers (Blob Data Contributor), ledger/destination containers (Blob Data Reader), and work queue (Storage Queue Data Reader). No subscription Owner/Contributor or role-assignment administrator grant is created by the template. The deploying identity must already be authorized to grant these scoped roles; Contributor alone is insufficient. Destination and shared-network/DNS owners must separately authorize their resource scopes.

Existing-network mode references approved subnet and private DNS IDs without redeploying the shared VNet, delegation or zones. New private endpoints and their DNS records still require subnet join and DNS permissions. Standardized names aid operations; exact resource IDs and protected catalog bindings determine authority.

## Runtime identity

One user-assigned identity per environment supports preauthorization and stable identity across Function recreation. Both managed identity types can be secure; roles, scopes and application boundaries determine access.

| Principal | Scope | Role |
|---|---|---|
| Function identity | Dedicated host account | Blob Data Owner, Queue Data Contributor, Storage Account Contributor |
| Function identity | Dedicated solution/source account | Blob Data Owner and Queue Data Contributor for polling BlobTrigger service/log/internal queue operations |
| Function identity | Ledger container in solution account | Covered by the existing solution-account Storage Blob Data Owner assignment; no redundant grant |
| Function identity | Existing destination container | Storage Blob Data Contributor |
| Function identity | Application Insights component | Monitoring Metrics Publisher |
| Optional external uploader group | Source container | Storage Blob Data Contributor; no work-queue send grant |
| Optional recovery operator group | Ledger container / work queue | Blob Data Contributor / Queue Data Message Sender |
| Optional package publisher | Host packages container | Blob Data Contributor |
| Optional ordinary operators | Function / workspace | Reader / Log Analytics Reader |

The BlobTrigger dispatcher's documented permissions are broader than a pure QueueTrigger worker would need. Both functions currently share one app/identity; narrower worker credentials would require a separate Function App identity boundary. Do not assume two identities attached to one app isolate code.

Existing Entra groups are referenced, not created. External uploaders keep their own integration/authentication method subject to this account's private networking and disabled shared keys. No external system is configured here. Deployment permissions are separate from runtime roles.

Azure runtime uses its explicit UAMI. Azure-connected development and the status/resume operator CLI use AzureCliCredential for application SDK clients; binding authentication is configured separately. The local run mode and opt-in emulator tests use the fixed Azurite development connection and require no Azure login. No production storage keys, SAS tokens or credential fallback chains are embedded. See the [Dispatcher README](dispatcher/README.md) for the mode-specific connection requirements.

## Network

Host and solution accounts disable public access, shared keys, anonymous blobs and cross-tenant replication; TLS and infrastructure encryption are enabled. Private endpoints cover host blob/queue/table, solution blob/queue (including the ledger container), destination blob/optional DFS and Function sites/SCM.

No Event Grid, trusted-service bypass, VPN, peering, resolver, runner VM, AMPLS or outbound firewall is added. External uploaders and operators need their own approved private network path. Monitor ingestion/query use authenticated public TLS endpoints. Existing destination security remains its owner's responsibility.

## Data and authority

Scope mappings are server configuration. Uploader-controlled metadata is ignored for authorization, scope and checksums. Filename prefixes alone do not establish customer entitlement. For hostile multi-tenant uploaders, use separately authorized containers/accounts or an upload gateway enforcing object-level access.

No source mutation/deletion occurs in application code, although the documented polling-trigger built-in roles permit broader operations. The destination built-in role permits delete even though code does not use it. Review custom roles or separate app boundaries if required.

Original filenames and version IDs are preserved in the private ledger and queue pointers; they can contain PII. Application messages primarily log opaque processing IDs and bounded codes. Protect queue access, ledger backups, diagnostic logs, model-free operational exports, and hash metadata accordingly. SDK/runtime logging also needs review.

Hash equality is exact byte equality, not malware detection, document authenticity, or claims adjudication.

## Controlled operator recovery

Recovery operators can mutate the ledger and send work. The tool checks reviewed ETags, requires explicit execution and a reference, and records prior state. The operator label and reference are annotations, not independently verified human approval. Azure Storage data-plane logs identify the actual authenticated caller. A dual-approver workflow is not implemented.

Ledger versioning and audit records aid recovery but are not tamper-proof against those write-authorized identities. Organization-controlled export, immutability, retention, and separation of duties remain required where applicable. Never delete ledger records merely to reset attempts.

Incremental ARM deployment does not revoke removed/changed historical roles. Host package privileges can also mutate executable deployment ZIPs; a separate package account/read-only deployment identity is a possible higher-assurance extension.

## Shared solution account boundary

The incoming and transfer-ledger containers are separate; the dispatcher and source reconciler watch only incoming. Startup rejects identical container names or a ledger endpoint outside the solution account. The optional uploader group is scoped only to the incoming container, while recovery operators retain a ledger-container grant. Account-wide Blob data grants include the ledger, so existing external uploader permissions must also be reviewed. Co-location shares account availability, throughput, retention configuration and administrator access; it does not provide account-level isolation. Keep incoming-only lifecycle rules from deleting ledger records. The account's blob private endpoint serves both containers.
