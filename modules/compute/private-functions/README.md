# Private Functions hosting

Owns a Linux B1 dedicated plan, .NET 10 isolated Functions site, diagnostics, publishing-credential restrictions and HTTP-mode Entra auth configuration. References an existing user identity, host/package storage, VNet integration subnet and monitoring workspace. The composition owns private endpoints and RBAC. Uses identity-based host storage and immutable Blob run-from-package, no storage keys. HTTP mode requires API registration and allowed client apps. Worker mode disables the HTTP health function. Both modes require private agents, DNS and approved egress. Outputs id/name.

See [module source](main.bicep) for the exact interface and [product onboarding](../../../docs/workload-onboarding.md) for composition, tests and acceptance. Local modules are compiled into versioned Template Specs; no registry is required. No module independently grants deployment authorization.
