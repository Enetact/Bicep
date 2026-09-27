# Security and RBAC

## Local portal skill discovery

The [bundled skill discovery route](azure-skill-discovery.md) uses the signed-in user's delegated Azure Management identity, independently of ADO service connections. Read permission on the registered subscription/resources is sufficient for inventory; discovery does not require Owner. Only fixed ARM GET collections are allowed, with bounded same-collection pagination, disabled redirects and projected output fields. Authentication failures and denied/incomplete reads never become empty inventories. Tokens remain in server memory; saved local reports may contain private network names and addresses and are ignored by Git. Upstream skill text and supporting scripts are reference content, not executable instructions or authorization. The route cannot queue pipelines, allocate addresses or modify Azure.

The [local portal](local-portal.md#configure-microsoft-browser-sign-in) uses a separate Entra public-client registration and delegated user access for ADO. Tokens remain in process memory behind loopback-only, session/origin/CSRF controls. Azure service-connection permissions and existing deployment guards remain independent. The portal has no credentials form, client secret or PAT configuration and is not supported as a shared remote web server.

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
