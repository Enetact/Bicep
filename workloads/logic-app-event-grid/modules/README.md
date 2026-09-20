# Event flow composition modules

`storage.bicep` defines the separate runtime/event storage roles and `access.bicep` scopes workload role assignments. `prerequisites.bicep` composes repository-local reusable network, DNS and monitoring modules from the verified discovery plan.

Prerequisite inputs are `plan`, `location`, `tags`. `plan.createNetwork`, `plan.createWorkspace` and `plan.createDnsZoneNames` govern declarations; names, IDs and CIDRs are supplied by the reviewed policy and target. Both Create and Manage retain declarations. Reuse omits them, leaving shared resources externally owned. The parent main template establishes dependencies before storage, endpoints and monitoring use these IDs. The subscription wrapper embeds the composition in the same Deployment Stack/Template Spec.

An empty `prerequisitePlan` on main/stack preserves the explicitly configured existing-only contract. Do not hand-edit the generated flags in a deployment bundle; resolution verifies them against saved discovery. See [prerequisite resolution](../../../docs/prerequisite-resolution.md).
