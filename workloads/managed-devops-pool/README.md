# Private Managed DevOps Pool

This workload creates a Microsoft Managed DevOps Pool whose agents join an approved existing private VNet subnet. **Managed DevOps Pool** appears in Platform Studio alongside Fabric and the other workloads. Use the standard saved Discover → Preview → protected Apply → Verify flow; no model is required. Four environment targets ship disabled and all organization, image, identity and network bindings require platform review.

## Resources and ownership

The workload owns one `Microsoft.DevOpsInfrastructure/pools@2026-06-02` Azure resource and its workload resource group. The provider also provisions the declared Azure DevOps pool/project association. Existing Dev Center project, VNet, subnet, DNS, routing, monitoring and bootstrap agents remain platform-owned references outside the workload stack. This increment creates neither shared subnet delegation nor network role assignments.

The default profile is deliberately bounded:

| Setting | Reviewed profile |
|---|---|
| Agents | Stateless, service logon; no stateful cache or provisioning script |
| Image | One Windows Server 2022 Generation 2 Azure Pipelines image, with an explicitly reviewed numeric version; no rolling `latest` |
| VM sizes | `Standard_D2ds_v5` default or `Standard_D4ds_v5` |
| Concurrency | Two maximum agents by default; accepted range one to ten |
| Standby | No standby profile, schedule or predictions |
| Storage | Standard SSD OS disk, no extra data disks; platform-managed encryption |
| Azure job identity | No pool/agent managed identity; separately authorized WIF service connections |
| Azure DevOps | One organization, explicit project names, `openAccess: false`, `SpecificAccounts` administrator security-group email addresses |
| Resource name | Reviewed globally unique Azure name; ADO pool alias is a separate explicit input |

The stable API and module interface were checked with actual Bicep MCP schema/best-practice calls and Microsoft Learn MCP search/fetch. Bicep MCP also identified the official AVM pool module version 0.8.0. This implementation follows the repository's native, constrained product-adapter convention; it does not claim AVM certification or add the pool to generic AI source drafts. [ARM contract](https://learn.microsoft.com/en-us/azure/templates/microsoft.devopsinfrastructure/2026-06-02/pools) · [official AVM reference](https://github.com/Azure/bicep-registry-modules/tree/avm/res/dev-ops-infrastructure/pool/0.8.0/avm/res/dev-ops-infrastructure/pool).

## Bootstrap without a circular dependency

Register the generated definitions:

| Definition | Source |
|---|---|
| Discover - Managed DevOps Pool | `azure-pipelines-devopspool-discover.yml` |
| Deploy - Managed DevOps Pool | `azure-pipelines-devopspool-deploy.yml` |

The creation target uses an **existing** `platform-bootstrap` agent pool. Configure its tools, pipeline authorization, WIF service connection and protected `devopspool-<env>` deployment environment before enabling this product. The adapter rejects creation using the requested output pool alias/name as its own bootstrap runner. Initial Discover/Preview and shared qualification/publication stages retain their existing runner bindings; creating an MDP does not silently migrate every suite job into the VNet.

After the new pool passes acceptance, consumer workloads can use its exact ADO alias in reviewed target `agentPool` bindings and corresponding generated pipelines. Keep the independent bootstrap runner available for pool recovery and upgrades. Never replace all runner bindings at once before the new pool can accept and complete jobs.

## Required source inputs

Complete `environments/main.<env>.bicepparam` and `self-service/targets/devopspool.<env>.json` through source review:

| Input | Required meaning |
|---|---|
| Common target fields | Reviewed subscription, workload group, region, owner, cost center, WIF deployment principal, existing monitoring reference and existing bootstrap pool |
| `devCenterProjectResourceId` | Existing same-subscription, same-region `Microsoft.DevCenter/projects` resource; no automatic creation or adoption |
| `agentSubnetId` | Existing exclusive subnet in the approved same-region VNet, already delegated to `Microsoft.DevOpsInfrastructure/pools` |
| `devOpsInfrastructurePrincipalObjectId` | Observed service-principal object ID for Microsoft's DevOpsInfrastructure service in the intended tenant; used to qualify platform network pregrants, not to create grants |
| `adoOrganizationUrl` | Exact reviewed `https://dev.azure.com/<organization>` URL; no token, query string or arbitrary host |
| `adoPoolName` | Organization pool alias used by consumer `pool.name`; different from the bootstrap pool |
| `adoProjectNames` | Explicit existing project names where the pool is needed; no empty list meaning all projects |
| `poolAdministratorGroupEmails` | Reviewed security-group email addresses accepted by Azure DevOps; not object IDs or guessed addresses |
| `agentImageVersion` | An available, approved published `windows-2022-g2` image version, observed during onboarding |
| `agentVmSize`, `maximumConcurrency` | Reviewed capacity and quota bounds for the selected region |

The environment examples contain placeholders and intentionally fail deployment qualification. Azure role assignments do not establish Azure DevOps project membership, pool-creation rights, self-hosted parallel-job entitlement or permission to use a WIF connection. The [Managed DevOps Pools prerequisites](https://learn.microsoft.com/en-us/azure/devops/managed-devops-pools/prerequisites?view=azure-devops) describe the current separate grants: initial pool creation requires organization/project membership and the relevant project-level Agent Pools Creator or Administrator rights; updating an existing organization pool needs its Administrator permission. Use the service connection identity intended to execute the pipeline, rather than relying on an interactive administrator's access.

`SpecificAccounts` declares administrator groups, and Microsoft also includes the creator by default. Review resulting ADO security after creation. `permissionProfile` and open-access settings have creation-time semantics; do not assume that an ARM update changes existing ADO permissions. The adapter conservatively blocks ownership, identity, organization, project, administrator or networking rebinding through the routine update path. Handle migrations and permission changes through a separately reviewed platform procedure. [Pool security](https://learn.microsoft.com/en-us/azure/devops/managed-devops-pools/configure-security?view=azure-devops).

## Private network prerequisites

The platform must prepare the network before this workload runs:

- Delegate a dedicated subnet exclusively to `Microsoft.DevOpsInfrastructure/pools`. Do not use an application integration or private-endpoint subnet. Pool and VNet must share a region; size addresses for maximum agents, Azure reservations and operational headroom.
- Resolve the actual DevOpsInfrastructure service principal in the intended tenant and grant the documented VNet read/join/service-association permissions at the approved scope. The initial adapter qualifies the reviewed built-in Reader/Network Contributor pregrant profile. Custom-role, conditional, PIM and deny-assignment equivalence requires separate qualification.
- Keep the `172.17.0.0/16` service range out of the VNet and connected routing domains. Review connected networks as well as the local subnet; local inventory cannot prove every remote prefix or available address.
- Provide controlled outbound connectivity for Managed DevOps Pools, Azure DevOps, Entra, artifacts and the repositories/tool downloads used by the selected jobs. Private agents still need those service endpoints; subnet injection is not an offline execution environment.
- Link private DNS zones or qualify the platform's resolver path so agents resolve dependencies to their intended private addresses. NSGs, firewall, route tables, peering and DNS remain external platform ownership.

These follow [Microsoft's network requirements](https://learn.microsoft.com/en-us/azure/devops/managed-devops-pools/configure-networking?view=azure-devops). Discovery/preflight checks establish configured prerequisites only. Effective routing, firewall behavior and private application access require real agent tests.

## Standard operator sequence

1. Prepare Dev Center, subnet delegation/network pregrants, Azure providers, regional MDP quota and ADO memberships. Configure the existing bootstrap runner and external deployment-environment approvals.
2. Register the two generated roots through **ADO setup**. Complete reviewed source inputs, image version and target settings; keep `enabled: false` until the platform accepts onboarding.
3. Run **Discover - Managed DevOps Pool**. Preserve its authenticated subscription inventory and manifest, including partial/denied coverage. Missing or failed evidence blocks qualification.
4. Run **Deploy - Managed DevOps Pool → Preview only**, selecting the matching successful discovery run. Inspect actual Azure What-If, source parameters, costs, ownership and conservative change gates. The API resource name and ADO alias must both be correct.
5. After reviewed enablement, run **Preview and deploy** and approve the protected environment. Existing publication, Template Spec, Deployment Stack, fresh prerequisite/drift and preservation controls still apply.
6. Inspect the Azure pool read-back receipt. It verifies declared configuration; it does not prove ADO scheduling, agent startup, image/tool correctness or application authorization.
7. Qualify the new ADO pool with the separately reviewed canary below, record real runtime evidence, then bind individual consumer workflows through source review.

Compilation, discovery, ARM success and a created pool card are separate from runtime readiness. Failed or uncertain deployments require external-state reconciliation before retry. No agent-produced advice, old Preview or restore assumption can authorize new infrastructure changes.

## Agent image and consumer qualification

Gen1 Azure Pipelines images are no longer updated; this workload uses Gen2. A version pin improves repeatability but requires a patch/requalification process before its image is deprecated. The v5 profiles avoid the currently documented Azure Pipelines-image restrictions on v6/v7 sizes. Region availability, actual image publication and quota are live acceptance inputs. [Images and version syntax](https://learn.microsoft.com/en-us/azure/devops/managed-devops-pools/configure-images?view=azure-devops).

The sample [private-agent-canary.yml](examples/private-agent-canary.yml) is a separate manual consumer pipeline, not an automatically queued step or a registered root. Replace its pool alias and private dependency hostname in source, then register and explicitly authorize that sample for the new pool. It checks Windows/PowerShell 7, Node 22+, the repository's .NET SDK feature band, Azure CLI, Bicep 0.48.1, Git, private IPv4 DNS and TCP 443 connectivity. It does not sign in, read data, grant permissions or claim successful application access. Qualify each workload's other tools through the existing pinned setup and product checks.

Consumer YAML uses the created pool alias and, when needed, a reviewed image demand:

```yaml
pool:
  name: REPLACE_CREATED_ADO_POOL_NAME
  demands:
    - ImageOverride -equals platform-windows
```

Per-pipeline pool permission remains explicit because `openAccess` is disabled. WIF service connections, environments and repository checkout permissions remain separate controls. Agents have no ambient Azure managed identity; jobs receive only their authorized connections. Keep separate pools/trust boundaries for untrusted PR builds and production deployment identities.

## Fabric and other private workloads

The pool can host qualified jobs that reach private endpoints in the VNet or connected networks. It does not remove Fabric service limitations: native Fabric deployment pipelines still cannot promote into deny-public workspaces. The existing Fabric API executor uses the global endpoint; workspace-specific private deployment remains a separate implementation/acceptance profile. [Fabric release/network choices](../fabric/README.md#private-networking-choose-the-release-profile-first).

Changing a workload's target pool does not change shared hosted `windows-latest` stages. Review each workflow's actual setup, Preview, publication, Apply and verification placement before claiming the entire suite runs privately.

## Costs, observability and recovery

No complete reviewed cost snapshot is supplied. Agent VMs/disks, networking, logging and Azure DevOps self-hosted parallel jobs can incur charges. No standby schedule is configured; cold job startup can take several minutes. Measure queue/startup time before approving standby capacity or larger SKUs. Concurrency is a ceiling, not a guarantee of quota, available VM capacity or licensed parallel jobs. [Cost/performance guidance](https://learn.microsoft.com/en-us/azure/devops/managed-devops-pools/manage-costs?view=azure-devops).

The Log Analytics value is an external monitoring reference in standard delivery evidence. This module does not silently add diagnostics, job-log exports, alerts or tenant audit retention. Qualify queue duration, provisioning failures, agent image/version, job failures, outbound denies and cost signals before production use. Keep job artifacts and receipts in restricted retention policies; stateless agents are not durable stores.

Recovery keeps the bootstrap pool and shared foundations independent. Drain and review active jobs before upgrades, image changes, pool replacement or deletion. The conservative profile blocks unsafe rebinding and has no automatic rollback of Azure/ADO side effects. Reconcile both the Azure resource and ADO pool if a request partially succeeds. Preserve external subnet/DNS/project resources and business data.

## Acceptance evidence

Local source/contract validation is recorded in [validation](../../docs/validation.md); it does not establish live Azure/ADO readiness. Before production, record real What-If/Apply, caller permission denial, exact org/project association, unauthorized pipeline rejection, agent startup/cleanup, image/tool versions, private DNS/TCP/application smoke tests, concurrent-job behavior, outbound controls, telemetry/cost coverage and recovery drills. No Azure creation, ADO pool permission change, target enablement or pipeline run is performed merely by authoring this capability.
