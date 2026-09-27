# Key Vault

Implemented source offering; all four targets remain disabled pending platform onboarding and live Azure acceptance.

1 RBAC Key Vault with soft delete and purge protection, 1 private endpoint, a scoped reader grant and audit diagnostics. No secret values are created.

- Discover: `azure-pipelines-keyvault-discover.yml` (**Discover - Key Vault**).
- Deploy: `azure-pipelines-keyvault-deploy.yml` (**Deploy - Key Vault**), default **Preview only**.
- Portal: **Workloads → Key Vault**.
- Request: [schema](request.schema.json), [example](request.example.json).
- Composition: [main.bicep](main.bicep), subscription wrapper: [stack.bicep](stack.bicep).
- Environment files: `environments/main.{dev,qa,uat,prod}.bicepparam`.
- Package kind: `infrastructure`. Infrastructure-only products do not contain an application ZIP.

## Onboarding and ownership

Platform-reviewed existing network/DNS and monitoring bindings, owner, cost center and deployment identity are required. No automatic address allocation. Vault operations, one private endpoint, audit logs and transfer.

Empty fields and REPLACE values are deliberate onboarding blockers. Complete owner/cost-center, principal and shared-dependency bindings through platform review; do not guess IDs or address ranges. This product owns only its dedicated resource group and resources within it. Shared networking, DNS, monitoring and action groups are referenced and retain their own owner. Missing shared resources do not become an automatic Create action. Existing unrelated resource groups are not adopted.

Read the [complete product onboarding, method and acceptance guide](../../docs/workload-onboarding.md) before enabling a target. It covers identity, permissions, private-agent/DNS requirements, price qualification, runtime behavior and failure/recovery boundaries.

## Cost and validation

Estimate unavailable until complete reviewed pricing is configured; this does not mean zero cost. Vault operations, one private endpoint, audit logs and transfer.

Run `./scripts/Test-Products.ps1` from the repository root for local compilation and synthetic discovery/contract tests. The [validation record](../../docs/validation.md) distinguishes local evidence from live Azure acceptance. Infrastructure readiness is not proof of consumer authorization, alert delivery or recovery; runtime offerings also require their implemented smoke tests and separate production acceptance drills.
