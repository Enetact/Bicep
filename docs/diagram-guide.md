# How external blob arrivals become reliable transfers

Source layout updated on 19 September 2026: the composition and environment files now live under `workloads/blob-transfer/`, with reusable resource modules under `modules/<category>/<resource>/`. The rendered HTML/SVG diagrams are historical illustrations and can show old paths; use the [current repository map](repository-structure.md) for source navigation.

This is the current implementation: an external system uploads normally, a polling BlobTrigger dispatcher sends a pointer to an explicit Storage Queue, and a QueueTrigger worker copies with deduplication and recovery. No Event Grid, MCP, custom uploader, or uploader metadata is required.

Start with diagrams 1, 4, 9 and 10 for the workflow. Engineers can continue through the Bicep, identity, and configuration diagrams. These diagrams describe code/templates; they are not evidence of a live Azure deployment.

## 1. What happens when a file lands

```mermaid
flowchart TD
    A[External system uploads normally] --> B[Solution storage container]
    B --> C[Polling BlobTrigger dispatcher]
    C --> D[Explicit Storage work queue]
    D --> E[QueueTrigger copy worker]
    E --> F[Existing data lake container]
    E --> G[Ledger container in solution storage]
    H[Reconciliation timer] --> B
    H --> D
```

The dispatcher is Function code. There is no storage setting that silently creates our queue message without a producer. The timer recovers missed dispatches.

## 2. From Bicep files to Azure resources

```mermaid
flowchart TD
    A[Environment bicepparam] --> B[main.bicep]
    B --> C[Reusable modules]
    C --> D[Bicep compiler]
    D --> E[ARM template and parameters]
    E --> F[WhatIf review]
    F --> G[Authorized deployment]
    H[C# Function project] --> I[Build release ZIP]
    I --> J[Private package blob]
    J --> K[Function runs package]
    G --> K
```

Bicep provisions resources and settings. Compiled C# provides trigger/worker behavior. Provisioning a Function App alone does not implement a copy job.

## 3. Symbolic name versus deployed name

```mermaid
flowchart TD
    A[main.bicep module declaration] --> B[Symbolic name: uploadStorage]
    A --> C[File: modules/storage/storage-account/main.bicep]
    A --> D[Deployment name: upload-storage-dev]
    C --> E[Actual storage account resource]
    E --> F[Azure account name derived from environment and suffix]
    B --> G[Reference outputs in main.bicep]
```

The symbolic name is a local Bicep identifier. The module path selects code; the module `name` identifies its ARM nested deployment; the storage resource has a separate Azure name.

## 4. One blueprint with four isolated environments

```mermaid
flowchart TD
    M[main.bicep and modules] --> D[Dev parameters]
    M --> Q[QA parameters]
    M --> U[UAT parameters]
    M --> P[Prod parameters]
    D --> DR[Dev resources and workspace]
    Q --> QR[QA resources and workspace]
    U --> UR[UAT resources and workspace]
    P --> PR[Prod resources and workspace]
```

Every environment has its own host account, solution account, identity and Function. Its ledger is a separate container inside that environment's solution account. Components share the workspace inside their own environment. Existing enterprise workspace reuse is not implemented.

## 5. Module responsibilities

```mermaid
flowchart LR
    A[main.bicep] --> B[storage: host account]
    A --> C[storage: solution incoming and ledger containers plus queues]
    A --> E[network: VNet and private DNS]
    A --> F[private-endpoint: service connections]
    A --> G[storage-access: runtime and operator roles]
    A --> H[destination-access: existing account scope]
    A --> I[monitoring: workspace and alerts]
    A --> J[function-app: plan and app settings]
```

The storage module is reused twice: host and solution. No separate ledger account is created. The existing destination remains owned by its current team.

## 6. Where environment settings come from

```mermaid
flowchart TD
    A[Per-environment parameters] --> B[main.bicep]
    C[Storage and identity outputs] --> B
    B --> D[function-app module]
    D --> E[Source and queue endpoints]
    D --> F[Ledger and destination endpoints]
    D --> G[Prefix to scope mapping]
    D --> H[Recovery schedule and attempt limits]
    D --> I[Managed identity and telemetry settings]
    E --> J[C# startup configuration]
    F --> J
    G --> J
    H --> J
    I --> J
```

Ledger and upload Blob settings share the same solution endpoint; their container names must differ. Prefix mappings come from trusted configuration, not blob metadata. Queue/dispatcher concurrency remains in `host.json`; schedules, scan limits, verification frequency and retry budgets are parameterized.

## 7. Deployment dependencies

```mermaid
flowchart TD
    A[Identity] --> B[Monitoring]
    B --> C[Storage accounts and queues]
    C --> D[Storage role assignments]
    E[VNet and DNS] --> F[Private endpoints]
    C --> F
    G[Existing destination] --> H[Destination RBAC and endpoints]
    D --> I[Function App release]
    F --> I
    H --> I
    J[Published package] --> I
```

This groups important dependencies, rather than drawing every ARM edge. Identity/RBAC propagation may outlast deployment completion.

## 8. Bootstrap and release

```mermaid
sequenceDiagram
    participant O as Operator
    participant B as Bicep deployment
    participant S as Private storage
    participant F as Function App
    O->>B: Bootstrap with deployFunctionApp false
    B->>S: Provision storage queues ledger and access
    O->>O: Establish private connectivity and validate RBAC
    O->>S: Publish immutable Function ZIP
    O->>B: Release with deployFunctionApp true
    B->>F: Configure app and package identity
    F->>S: Read package with managed identity
    O->>F: Synchronize and validate triggers
```

Incremental bootstrap is not an app shutdown/deletion switch. CI validates/builds; deployments require the deliberate release commands.

## 9. Source arrival and content deduplication

```mermaid
sequenceDiagram
    participant U as External uploader
    participant S as Solution blobs
    participant D as BlobTrigger dispatcher
    participant Q as Work queue
    participant W as Queue worker
    participant L as Ledger container in solution
    participant T as Data lake
    U->>S: Upload ordinary blob
    D->>S: Detect blob and read revision
    D->>Q: Send source pointer
    Q->>W: Invoke copy job
    W->>L: Acquire request lease
    W->>S: Read exact revision and hash bytes
    W->>L: Acquire scope and content lease
    W->>T: Conditionally create if absent
    W->>T: Verify actual destination bytes
    W->>L: Record completion
```

Different filenames or source revisions with identical bytes share the same destination within a scope. Existing destination content is never silently overwritten.

## 10. Recovery and review decisions

```mermaid
flowchart TD
    A[Source or retained version discovered] --> B[Stable request record]
    B --> C{Completed and destination valid}
    C -->|Yes| D[Record verification]
    C -->|No| E{Attempts remain}
    E -->|No| F[Quarantine and alert]
    E -->|Yes| G[Conditional copy and hash verification]
    G -->|Success| H[Completed]
    G -->|Temporary failure| I[Retry and scheduled reconciliation]
    G -->|Content conflict| F
    I --> B
    F --> J[Operator investigation]
    J --> K[Reviewed recovery with exact ETags]
    K --> B
```

A missing destination can be rebuilt from retained source. A corrupt existing destination requires review. The tool cannot recreate a permanently deleted source revision.

## 11. Identity and permission boundaries

```mermaid
flowchart LR
    A[Function user-assigned identity] --> B[Host runtime and package access]
    A --> C[Source polling and queue permissions]
    A --> D[Solution ledger container writes and leases]
    A --> E[Destination container read and create]
    A --> F[Telemetry publishing]
    G[External uploader identity] --> H[Source blob writes]
    I[Recovery operator identity] --> D
    I --> J[Work queue send]
    K[CI deployment identity] --> L[Resource deployment and package publishing]
```

Polling BlobTrigger requires broader host/source roles than a pure queue reader. Dispatcher and worker share one identity/app. Deployment privileges are separate. A recovery operator is privileged; the CLI is not a dual-approval system.

## 12. Private paths and external prerequisites

```mermaid
flowchart TD
    A[External uploader with private connectivity] --> B[Solution blob endpoint: incoming and ledger]
    C[Integrated Function App] --> B
    C --> D[Solution queue private endpoint]
    C --> F[Host storage private endpoints]
    C --> G[Existing destination private endpoints]
    H[Private DNS zones] --> C
    I[Approved deployment runner] --> J[Function and SCM private endpoint]
    C --> K[Authenticated public Azure Monitor endpoints]
```

No Event Grid or trusted-service firewall exception is configured. The project does not provision the uploader network path, VPN, peering, DNS resolver or runner. Private account access still requires those prerequisites.

## Glossary

| Term | Plain meaning |
|---|---|
| Blob | Stored file/object |
| ETag | Storage revision identifier, not a content checksum |
| Version ID | Address of a retained source revision |
| QueueTrigger | Function runs because a queue message is available |
| BlobTrigger | Function detects a blob through its configured listener |
| Ledger | Durable records of discovered work, attempts and outcomes |
| Idempotent processing | Repeating work converges on the same intended result |
| Scope | Business boundary inside which matching content is treated as duplicate |
| Quarantine | Processing stopped for investigation; no source movement implied |
| Managed identity | Azure-issued application identity used without stored passwords |

See [architecture](architecture.md), [security](security-and-rbac.md), [operations](operations.md), and [validation](validation.md) for limits and evidence.
