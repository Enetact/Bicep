# Workload VNet

Creates one VNet, an integration NSG and two inline subnets. Inputs are `name`, `location`, `tags`, `addressPrefix`, `integrationSubnetPrefix` and `privateEndpointSubnetPrefix`; output `id` is the VNet resource ID. The integration subnet is delegated to Microsoft.Web/serverFarms. The nondelegated endpoint subnet disables private endpoint network policies. The NSG has Azure default rules and no custom rules. This module does not create peering, routes, agents or DNS resolvers.

Call only for new or already stack-owned dedicated networking. Existing shared VNets are referenced by their selected subnet IDs instead. The Event flow resolver validates CIDRs and determines lifecycle before this module is included. See [prerequisite resolution](../../../docs/prerequisite-resolution.md).
