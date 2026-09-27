# Bundled Azure Skills and browser-authenticated discovery

Implemented 27 September 2026. The portal includes **42 upstream skill definitions**: 28 top-level skill directories, 10 nested workflows and four cost skills from the repository's bundled cost plugin. It also retains our five project-local skills. These are separate from the two registered workload products and their four ADO definitions.

Source: [Microsoft Azure Skills at commit 117b038e](https://github.com/microsoft/azure-skills/tree/117b038edfef5d7af09848b8ffcd355f28f19956). Exact downloaded source bytes and licenses are under `vendor/azure-skills`; `bundle.json` records each definition, discovery profile and SHA-256 for **944 source files**. The three onboarding sub-workflows without front matter have explicit wrapper metadata in the catalog; their source is unchanged. No runtime downloads, telemetry hooks or plugin installation are activated by the portal.

## Developer experience

1. Open **Skills library** at `http://localhost:5087`.
2. Search/filter by source or discovery view. Every Microsoft card states **No pipeline associated yet**. Read the complete bundled skill text or open its pinned upstream source.
3. Choose **Discover in Azure**. The panel describes the exact collection and its limits; select a registered subscription. No CIDR, subnet ID or resource name is needed for inventory.
4. In **Connections**, sign in to Azure using the [portal's separate Entra registration](local-portal.md#configure-microsoft-browser-sign-in). ADO sign-in is not required for this action. The signed-in user needs read access to the relevant resources; do not grant Owner just for discovery.
5. Click **Discover visible resources**. The local host uses an Azure Management token to issue fixed ARM GET requests. It does not run upstream scripts, execute an AI/MCP agent or queue a pipeline.
6. Inspect per-collection status and projected resource facts. Download the report or retain `artifacts/portal-discovery/<id>/report.json` locally. These reports may contain private network names/IP ranges; they remain ignored by Git.

Live consent and tenant calls remain unverified until registration is configured. Local mocked Azure and real localhost tests do not establish Azure acceptance. Skill guidance is available immediately in this portal and repository; the bundle is not installed into the user's global Codex skill directory or advertised as an active agent toolset.

## What each discovery view does

| Profile | Azure reads | Important boundary |
|---|---|---|
| Network | Resource metadata, VNets with inline subnets/peerings, private DNS zones, configured NSG rules, route tables and private endpoints. | No effective routes/rules, DNS records/links/resolution, probes, IP occupancy or IPAM allocation. Missing inline facts remain Unknown. |
| Compute | Resource list filtered to Microsoft.Compute and Microsoft.Web. | No VM login, commands or application settings. |
| Kubernetes | ContainerService, Kubernetes and KubernetesConfiguration resource metadata. | No kubeconfig, cluster credentials or workload API calls. |
| Storage | Storage resource metadata. | No keys, connection strings, blob contents or data-plane actions. |
| AI | CognitiveServices, MachineLearningServices, BotService and ApiManagement metadata. | No model calls, capacity qualification, secret retrieval or agent execution. |
| Messaging | EventGrid, EventHub and ServiceBus metadata. | No messages consumed or sent. |
| Monitoring | Insights and OperationalInsights metadata. | No telemetry or log-query contents. |
| Data | Kusto resource metadata. | No database queries or records. |
| General inventory | Visible resource metadata across the selected subscription. Used for cross-cutting skills, cost and identity guidance. | Does not read bills, estimate costs, enumerate Entra directory objects or execute specialist assessments. |

Profiles are defined deterministically in `scripts/Update-AzureSkillBundle.py`, recorded in the bundle index and allowlisted by `AzureDiscovery.cs`. A discovery button on a deployment, cost or identity skill means **supporting Azure resource inventory**, not implementation of that skill's complete upstream workflow. Our local skill cards keep their existing local-guidance behavior.

## Networking-plan alignment

This implements a bounded, subscription-scoped inventory entry point for the [networking expansion plan](plans/private-networking-self-service.md). Developers can inspect existing topology without entering an address range. It is not the enterprise planner, subnet recommender or authoritative allocator proposed there.

- Facts are visible only within the registered subscription and the user's read permissions. Successful pagination does not prove complete tenant, hub, on-premises or cross-cloud coverage.
- `SucceededEmpty` means the query completed without matching visible resources. HTTP 403/404, invalid JSON/schema, timeouts and incomplete pagination are Failed/Partial with unknown remaining coverage. They never become an implicit instruction to create resources.
- Inline subnet and peering data are marked as reported configuration; missing inline arrays remain Unknown. Remote VNet references do not grant permission to follow them outside the approved scope.
- IP occupancy, reservations, external overlaps, ownership, join permission and effective connectivity are unverified. No CIDR is guessed, no subnet is ranked/reserved and no resource is changed.
- `deploymentAuthorized` and `allocationAuthorized` are always false. This is a `portal-skill-discovery` report, **not** the ADO workload `manifest.json`/`inventory.json` contract. Deploy cannot consume it as approved handoff evidence.

## Security and implementation

`POST /api/skill-discovery` accepts only `{skillId, subscriptionId}`. The existing browser session, Origin and CSRF checks apply. The subscription must match a registered target; client-supplied URLs, resource queries, shell commands and write methods are not accepted. Azure tokens remain in server memory. There are no ADO calls on this route.

`AzureDiscovery.Discover` selects the approved profile, obtains the Azure token and calls `Collect`. Collection follows at most 10 pages and 5,000 records per route, with a 4 MiB response limit and two-minute overall timeout. A continuation must remain on HTTPS management.azure.com and the exact original collection path. Redirects are disabled. Repeated links or scope changes return Partial without forwarding credentials. Resource IDs outside the selected subscription are rejected. Returned resource fields are explicitly projected; tags, arbitrary settings and raw errors are omitted. Limits/failures retain already observed facts but do not assert complete coverage.

The UI renders reports and skill instructions as text. Network reports include observed configuration, not generated executable code. Saved reports are local evidence snapshots; subsequent Azure changes make them stale. No AI decision or approval is inferred from the collection.

Official API contracts: [ARM resource list](https://learn.microsoft.com/en-us/rest/api/resources/resources/list?view=rest-resources-2021-04-01), [VNet list](https://learn.microsoft.com/en-us/rest/api/virtualnetwork/virtual-networks/list-all?view=rest-virtualnetwork-2025-09-01), [route table list](https://learn.microsoft.com/en-us/rest/api/virtualnetwork/route-tables/list-all?view=rest-virtualnetwork-2025-09-01). Resource metadata uses API 2021-04-01, network reads 2025-09-01 and private DNS 2024-06-01. Unsupported API/provider responses remain unknown rather than falling back to write operations.

## Updating and packaging the bundle

The upstream snapshot was installed with the Codex skill-installer helper using the explicit commit and project-local destination. It is vendored as reference content outside `.agents/skills` so upstream instructions do not silently become this repository's operating policy. Only skill directories/supporting files and their licenses are included; plugin hooks and MCP startup configuration are not activated.

For an intentional update, review a new upstream revision, import its 28 skill directories and bundled cost skill directories into a new staging directory, review the diff, then replace the vendor snapshot. Update the pinned revision and profile mappings in `scripts/Update-AzureSkillBundle.py`. Regenerate `bundle.json` using Python with PyYAML 6.0.3 available, then run:

```powershell
node tests/portal/verify-skills.mjs
./scripts/Test-Portal.ps1
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
./scripts/Publish-Portal.ps1
```

The bundle-count assertions intentionally require review if upstream adds or removes skills. Git preserves vendor bytes using `.gitattributes`; do not normalize or edit vendored files just to make a test pass. Hash checks detect local drift; they are not an independent publisher signature. Both Windows packages include the same full `vendor` snapshot. Old packages do not update themselves; rebuild and redistribute after reviewed changes.
