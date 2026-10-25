# AVM network security group

[main.bicep](main.bicep) wraps official `avm/res/network/network-security-group:0.5.4` from [pinned upstream source](https://github.com/Azure/bicep-registry-modules/tree/99f7bd7e64b6067691330b63744a999269431d2c/avm/res/network/network-security-group). Exact compiled ARM and the original MIT license are preserved in [the verified bundle](../../../../vendor/azure-verified-modules/bundle.json). This constrained local wrapper is not independently AVM certified.

Required inputs are explicit `name`, approved `location` and ownership `tags`; draft names remain unresolved. Optional `securityRules` defaults to `[]`, with at most 100 sealed children. Each requires a name and sealed `properties`: priority 100-4096, `Allow`/`Deny`, `Inbound`/`Outbound`, supported protocol, source/destination address prefixes and port ranges. Description is optional. No arbitrary extra properties, application security groups, array address/range forms or role assignments are exposed.

Owns a new workload NSG and its custom rule collection; outputs `id` and `name`. It never attaches to or adopts subnets/NICs. Connection flushing and module telemetry are disabled. Empty rules retain Azure's default NSG rules, including default virtual-network allowances; empty does not mean deny-all. Caller permissions and approved subnet ownership/associations remain separate.

Qualify unique rule names/priorities, valid service tags/CIDRs/ports, least-privilege traffic, effective routes, return paths and management connectivity. Custom explicit rules can change exposure; parameter bounds do not prove a safe rule set. Existing shared NSGs require their authoritative writer, concurrency controls and drift review. Review associated flow logging and traffic/observability costs separately; this wrapper creates no monitoring destinations.

Use the [qualification and protected Preview/Deploy path](../../../../docs/bicep-source-drafts.md#qualification-and-promotion). Local compilation does not establish Azure rule acceptance, security review, attachment authorization or effective traffic behavior.
