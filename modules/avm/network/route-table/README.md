# AVM route table

[main.bicep](main.bicep) wraps official `avm/res/network/route-table:0.5.0` from [pinned upstream source](https://github.com/Azure/bicep-registry-modules/tree/38e14b5cc5a386be8620c26c25d368e8aeaa0bf2/avm/res/network/route-table). Exact compiled ARM and the original MIT license are preserved in [the verified bundle](../../../../vendor/azure-verified-modules/bundle.json). This constrained local wrapper is not independently AVM certified.

| Input | Contract |
|---|---|
| `name`, `location`, `tags` | Required explicit new table name, approved region and ownership tags. Draft names remain unresolved. |
| `routes` | At most 100 sealed children, default `[]`. Each has `name` and sealed `properties` with explicit `addressPrefix` and next hop `VnetLocal`, `VirtualNetworkGateway` or `None`. |
| `applianceRoutes` | At most 100 sealed children, default `[]`. Each has `name` and sealed `properties` with explicit `addressPrefix` and required `nextHopIpAddress`; wrapper sets `VirtualAppliance`. |
| `disableBgpRoutePropagation` | Default `false`; only a reviewed routing profile should disable propagation. |

Owns a new table and its route collection; outputs `id` and `name`. Empty collections add no user routes. No Internet next-hop profile, role grants, subnet writer, gateway or appliance creation is exposed. Module telemetry is disabled. Do not pass an existing shared table name to adopt it, or create multiple writers for the same route collection.

Network review must verify names unique across both arrays, valid destination prefixes, appliance IP syntax/reachability, forwarding, gateway compatibility, propagated/effective routes and return paths. String bounds do not establish IP validity or non-overlap. Existing subnet association requires its approved authoritative writer and drift/concurrency protection. Review gateway/appliance, transit/egress and observability costs separately; this module supplies none of those dependencies.

Follow [qualification and protected Preview/Deploy](../../../../docs/bicep-source-drafts.md#qualification-and-promotion). Local compilation does not prove Azure route acceptance, subnet ownership, reachability or runtime traffic behavior; no registered product or enabled target is created by this source addition.
