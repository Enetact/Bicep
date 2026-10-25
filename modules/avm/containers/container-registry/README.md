# AVM private container registry

[main.bicep](main.bicep) wraps official `avm/res/container-registry/registry:0.13.1` from [pinned upstream source](https://github.com/Azure/bicep-registry-modules/tree/f1f4ba28698437d9a683d68bf181341ad3547e48/avm/res/container-registry/registry). Exact compiled ARM and the original MIT license are preserved in [the verified bundle](../../../../vendor/azure-verified-modules/bundle.json). The constrained wrapper is locally maintained, without a separate AVM certification claim.

| Input | Contract |
|---|---|
| `name`, `location`, `tags` | Required reviewed globally unique 5-50 character registry name, approved region and ownership tags. Validate alphanumeric naming and availability; drafts leave names unresolved. |
| `zoneRedundancy` | `Disabled` by default, or reviewed `Enabled` in a supported region. |
| `retentionInDays` | 7-90, default 30. Retention is enabled: untagged manifests become eligible for deletion after this period. |
| `replications` | Up to 10 sealed children, default `[]`; each requires explicit `name`, `location` and `zoneRedundancy`. The regional endpoint is enabled and tags inherit the parent. |

Owns a new Premium registry and optional geo-replicas. Outputs `id`, `name` and `loginServer`. Admin and anonymous authentication, public access, trusted-service bypass, ARM audience authentication and exports are disabled; the firewall defaults to deny with no IP exceptions. Telemetry is disabled. No webhooks, tasks, tokens, credential sets, arbitrary roles, private endpoints, DNS changes or image pushes are exposed.

Compose approved private endpoints and DNS for registry/data endpoints separately. Qualify Entra repository permissions, agent network reachability, image pull identities, replica uniqueness and regional/zone availability. The caller needs scoped registry/replication management permissions; this module grants none. Premium capacity, storage, geo-replication and transfer charges require owner review. Do not reuse an existing shared registry name to adopt it or assume an enabled login endpoint is reachable.

Review the [source-draft qualification path](../../../../docs/bicep-source-drafts.md#qualification-and-promotion) before registration and protected Preview/Deploy. Compilation is local evidence; Azure acceptance, private pull/push, recovery and retention behavior remain separate acceptance work.
