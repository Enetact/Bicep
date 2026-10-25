# AVM user-assigned identity

[main.bicep](main.bicep) wraps official `avm/res/managed-identity/user-assigned-identity:0.6.0` from [pinned upstream source](https://github.com/Azure/bicep-registry-modules/tree/a9370ad48e31b2de5a679dd630da28612ad95471/avm/res/managed-identity/user-assigned-identity). The local compiled ARM artifact and its original MIT license are preserved under [the verified bundle](../../../../vendor/azure-verified-modules/bundle.json). This repository's constrained wrapper is locally maintained; it does not claim independent AVM certification.

| Input | Contract |
|---|---|
| `name`, `location`, `tags` | Required reviewed identity name, approved region and ownership tags. Names remain unresolved in drafts until supplied; no identity is adopted. |
| `federatedIdentityCredentials` | Optional sealed collection, at most 20; default `[]`. Each child requires explicit `name`, `issuer`, `subject` and a single `api://AzureADTokenExchange` audience. |

Owns the identity and explicitly requested federation children in the workload resource group. Regional isolation is mandatory; telemetry is disabled. No role assignments, identity attachment, app registration, tenant consent or secret generation occur. Outputs `id`, `name`, `principalId` (object ID) and `clientId` (application ID) have distinct meanings.

Federation grants trust to an external issuer and subject. Review issuer HTTPS/tenant, subject syntax, audiences, unique child names, token exchange and regional identity compatibility before deployment. The caller requires scoped identity/federation write permissions; this module grants none. Identity creation does not authorize Azure resource access. Review identity operational limits and the costs of consuming services separately.

Locally compiled source is a draft capability. The existing [qualification and protected Preview/Deploy path](../../../../docs/bicep-source-drafts.md#qualification-and-promotion) still applies. Azure deployment, federation acceptance, workload identity behavior and operational qualification are separate and remain required.
