# Security and RBAC

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

Runtime uses its explicit UAMI. Local development/operator tooling uses AzureCliCredential. No production storage keys, SAS tokens or credential fallback chains are embedded. The emulator's well-known development key is used only by opt-in local tests.

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
