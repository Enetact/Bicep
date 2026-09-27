# Private RBAC Key Vault

Owns a Standard vault, Secrets User service-principal grant and audit settings. References the supplied monitoring workspace. Private endpoint ownership belongs to the composition. Purge protection is enabled with 90-day retention; no secret values, keys or credentials are outputs. Requires name/location/tags/workspaceId/readerPrincipalId; outputs id/name.

See [module source](main.bicep) for the exact interface and [product onboarding](../../../docs/workload-onboarding.md) for composition, tests and acceptance. Local modules are compiled into versioned Template Specs; no registry is required. No module independently grants deployment authorization.
