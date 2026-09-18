# Versions, assumptions, and references

Documentation and package metadata were checked while preparing this bundle on 2026-09-16/17. Versions are pinned rather than floating. Update deliberately through pull requests and regenerate/review lockfiles; do not silently downgrade when a regional/runtime constraint appears.

| Component | Selected/verified version | Notes |
|---|---|---|
| Bicep compiler | 0.47.16 | Official Azure/bicep release; all four parameter files compiled |
| Local .NET SDK | 10.0.300 | Installed SDK; global.json rolls forward within stable .NET 10 |
| Function runtime | `~4` | Platform servicing stays current within v4 |
| Local Functions Core Tools | 4.14.0 | Real local host verified on Windows ARM64; portable minimal distribution. Installed 4.12 failed with an Options 10 assembly mismatch. |
| Worker runtime | `DOTNET-ISOLATED\|10.0` | Dedicated Linux; verify target region's supported runtimes |
| Functions Worker | 2.52.0 | Current stable NuGet result at preparation |
| Functions Worker SDK | 2.1.0 | Generates metadata and binding extension bundle |
| Storage Blobs extension | 6.8.2 | Identity connections and SDK BlobClient binding |
| Storage Queues extension | 5.5.5 | Explicit QueueTrigger and identity connection |
| Timer extension | 4.3.1 | Persistent recovery and monitoring schedules |
| Azure.Storage.Blobs | 12.29.2 | Leases, conditional writes and version-addressed reads |
| Azurite | 3.37.0 | Isolated local integration tests; not deployed |
| Azure.Identity | 1.21.0 | ManagedIdentityCredential / AzureCliCredential |
| Microsoft.Extensions.Hosting | 10.0.12 | Host startup |
| xUnit | 2.9.3 | Policy tests |
| xUnit VS runner | 4.0.0 | Verified with chosen test SDK |
| Microsoft.NET.Test.Sdk | 18.10.1 | Test execution |

Worker builds generate a net8.0 WorkerExtensions helper project; that is the extension build mechanism, not a downgrade of the net10.0 isolated application. Top-level application/test dependencies have committed NuGet lockfiles. The generated helper project's transitive restore is controlled by the worker SDK and deserves review during upgrades. EF Core, Semantic Kernel, and frontend packages are not used: this request is a storage-trigger infrastructure solution, not a claims UI or LLM pipeline.

Stable resource API versions are pinned in each module. Diagnostic settings use `2021-05-01-preview`, the established API for that resource. Template syntax/type validation does not prove that a subscription policy accepts an API or that a region has capacity.

## Primary documentation

- [Dedicated Azure Functions hosting](https://learn.microsoft.com/en-us/azure/azure-functions/dedicated-plan): Always On and dedicated capacity.
- [Functions local development](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local), [Core Tools 4.14 release](https://github.com/Azure/azure-functions-core-tools/releases/tag/4.14.0), and [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite): tools for the separate local emulator workflow.
- [Blob trigger](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-blob-trigger): polling versus event-based implementations, HNS restrictions, retry behavior, identity connections.
- [Blob binding extension](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-blob): isolated worker packages and host.json concurrency options.
- [Queue trigger](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-queue-trigger): explicit queue invocation, retries and poison handling.
- [Queue binding configuration](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-queue): host.json encoding, concurrency and retry settings.
- [Blob event overview](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blob-event-overview): storage-side push subscriptions use Event Grid, which is not included here.
- [Storage Actions operations](https://learn.microsoft.com/en-us/azure/storage-actions/storage-tasks/storage-task-operations): supported operations do not include queue-message publishing.
- [Blob versioning](https://learn.microsoft.com/en-us/azure/storage/blobs/versioning-overview): retained source revisions and retention implications.
- [Blob concurrency](https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage): ETags and leases.
- [Connection and role matrix](https://learn.microsoft.com/en-us/azure/azure-functions/manage-connections): required host/trigger identity roles.
- [Run from a deployment package](https://learn.microsoft.com/en-us/azure/azure-functions/run-functions-from-deployment-package): private package URL, managed identity, manual trigger synchronization.
- [Functions storage considerations](https://learn.microsoft.com/en-us/azure/azure-functions/storage-considerations): host storage versus Azure Files, removing Azure Files dependency.
- [Isolated .NET worker guide](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide): .NET 10 support and build/runtime requirements.
- [Built-in storage RBAC roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/storage): role IDs and actual permissions.
- [Blob data role assignment](https://learn.microsoft.com/en-us/azure/storage/blobs/assign-azure-role-data-access): container scopes and management/data-plane distinctions.
- [Monitoring configuration](https://learn.microsoft.com/en-us/azure/azure-functions/configure-monitoring): Entra telemetry authentication and host log controls.
- [VNet routing](https://learn.microsoft.com/en-us/azure/app-service/configure-vnet-integration-routing): application versus configuration routing.
- [Bicep releases](https://github.com/Azure/bicep/releases): official compiler distribution.
- [Worker package](https://www.nuget.org/packages/Microsoft.Azure.Functions.Worker), [Worker SDK](https://www.nuget.org/packages/Microsoft.Azure.Functions.Worker.Sdk), [Blob extension](https://www.nuget.org/packages/Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs), [Azure.Identity](https://www.nuget.org/packages/Azure.Identity): exact package metadata; direct NuGet flat-container version lists were also queried.

The Bicep module symbol (`hostStorage`, for example) is local to its parent file. The module `name` is the nested deployment's name. The storage module's `params.name` is the actual Azure account name. These are intentionally different identifiers.
