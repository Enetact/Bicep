# Tenant and management-group network discovery

Reviewed 27 September 2026. **Feasible, not implemented in the portal.** This review checks source, the pinned skill bundle and Microsoft Learn contracts. No broader discovery, MCP installation, access grant or tenant scan was performed.

## Verified current behavior

| Component | Current boundary |
|---|---|
| Portal menu | `wwwroot/index.html` exposes one Registered subscription selector. `Program.cs` derives choices from deployment targets. |
| Request and collector | `SkillDiscoveryRequest` in `AzureDiscovery.cs` accepts `skillId` and one `subscriptionId`. Unregistered subscriptions fail before HTTP. Fixed ARM GET collections read resources, VNets, private DNS, NSGs, routes and private endpoints. |
| Identity | `BrowserIdentity.cs` binds MSAL to the configured tenant. Its Azure token is not automatically an Azure MCP credential. |
| Skills | Bundled lookup and visualizer guidance describes cross-subscription Resource Graph queries. Enterprise infrastructure planning describes MCP insights. Displaying these instructions does not execute them. |
| MCP | No platform MCP runtime or Azure MCP client is wired to this route. Azure MCP tools were not callable in this review session. |
| ADO | `Export-DeploymentInventory.ps1` selects one subscription by ID, name or registered target/service connection. Its deployment manifest remains single-target evidence. |

All 17 existing `AzureDiscoveryTests` passed in this review with mocked HTTP/tokens, covering scope rejection, failed-versus-empty reads and continuation safety:

```powershell
dotnet test tests/SelfService.Portal.Tests/SelfService.Portal.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~AzureDiscoveryTests --logger 'trx;LogFileName=discovery-scope.trx' --results-directory artifacts/discovery-scope-audit
```

The assembly was built by the preceding full qualification run; discovery code is unchanged. Evidence: `artifacts/discovery-scope-audit/discovery-scope.trx`. This does not establish live tenant/MCP acceptance.

## Supported Azure mechanisms

- **Azure MCP Insights:** documented `insights get` supports `subscription` and `tenant`; tenant means accessible subscriptions. It provides aggregated insights and requires MCP sampling. The documented scope enum does not include management groups. Do not treat its generated insights as complete inventory. [Insights contract](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/tools/azure-insights)
- **Resource Graph:** supports subscription lists or management-group IDs, not both in one request. Unscoped queries may include Lighthouse delegation. Management-group queries have a documented first-10,000-subscriptions boundary; larger hierarchies require partitioning and reconciliation. [Query scope](https://learn.microsoft.com/en-us/azure/governance/resource-graph/concepts/query-language#query-scope)
- **API and coverage:** Resource Graph uses a read-only POST query operation with scope and paging fields. Results are eventually consistent and can omit inaccessible subscriptions without indicating partial permissions. A successful query cannot prove tenant completeness. [REST contract](https://learn.microsoft.com/en-us/rest/api/azureresourcegraph/resourcegraph/resources/resources?view=rest-azureresourcegraph-resourcegraph-2024-04-01), [permissions and freshness](https://learn.microsoft.com/en-us/azure/governance/resource-graph/overview)
- **MCP identity:** qualify the actual version, advertised schemas, credential context and read-only/tool filters. Do not assume the browser account equals an ambient CLI/server identity. [MCP security](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/security), [tool configuration](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/tools/), [subscription listing](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/tools/subscription)

## Proposed scope menu

| Choice | Behavior |
|---|---|
| Registered subscription | Preserve today's default. |
| Selected subscriptions | Multi-select readable, platform-permitted subscriptions in the connected tenant. |
| Management group | Select an allowed group by display name; retain its ID and include descendants. Show hierarchy-read status. |
| All accessible subscriptions in this tenant | Show connected tenant and resolved permitted subscription count. Do not imply visibility of every subscription. |

Show subscriptions checked, successful-empty collections, denied/failed scopes, limits and unresolved links. When Azure does not reveal subscriptions to the caller, mark tenant coverage unknown rather than inventing a complete list of inaccessible subscriptions. Show policy exclusions explicitly. Discovery requires no developer-entered CIDR, subnet ID or resource name.

Use the signed-in Azure identity with read rights across the intended scope. An approved management-group Reader assignment can provide inherited resource read access; qualify additional hierarchy permissions. Owner is unnecessary. The subscription-scoped `SC-AZ-A-Bicep` cannot gain broader visibility from this option; any future ADO enterprise inventory job needs an independently approved read identity and scope.

## Implementation design

1. Separate **discovery policy** from deployable target registrations. Resolve permitted tenant/group/subscription IDs server-side. Preserve the current configured-tenant boundary initially; later tenant switching needs a separate authenticated context. Never trust client/model authorization lists.
2. Enumerate subscriptions before resources, including empty subscriptions. Compare expected hierarchy where readable. Verify resource-tenant membership, not a guest account's home tenant. Exclude Lighthouse/delegated external subscriptions unless separately permitted.
3. Use reviewed Resource Graph templates for network inventory, explicit scope batches, stable IDs, continuation tokens, truncation/count checks and deduplication. Allow only this fixed read-only POST operation; no arbitrary KQL, shell commands or generalized ARM POST access.
4. Enrich selected configuration through bounded ARM reads. Record per-subscription/collection status, timestamps and query versions. Handle throttling, cancellation and partial evidence. Replace the current two-minute whole-scan budget with a bounded enterprise job, not an unlimited loop.
5. Map full resource IDs across VNets/subnets, peering, endpoints, NIC/IP configurations, NSGs, routes, DNS zones/links and resolver dependencies. Qualify additional collectors separately. Out-of-scope remote references remain unresolved; discovery must not silently expand scope to follow them. Inventory does not prove IP availability, effective routing or connectivity.
6. Version the enterprise report separately, recording requested/resolved scope, actor/tenant, membership coverage, collection results, limits and observation times. Preserve existing portal/ADO artifact compatibility. Keep deployment/allocation authorization false; broad inventory never enables targets or becomes an approved Deploy manifest automatically.
7. Let lookup/visualizer skills guide approved queries and explain evidence. Add optional MCP behind a pinned capability map and verified matching identity. If tenant Insights would exceed policy scope, use a supported narrower operation or the deterministic collector. Do not invent an Insights management-group parameter.
8. Make sampling/AI opt-in with disclosed data/model use. Store generated explanations separately from authoritative findings. Discovery must work without an LLM or MCP session. Browser tokens stay in the portal; any MCP authentication bridge requires a supported, tested design, not token copying into process arguments, environment or logs.

## Delivery and acceptance

| Increment | Proposed files | Required evidence |
|---|---|---|
| Scope policy/contracts | `config/discovery-scopes.json`, scope types beside `AzureDiscovery.cs`, report schema under `schemas/` | Tenant membership, scope authorization, nested groups, empty subscriptions and old-mode compatibility. |
| Collector | Separate Resource Graph adapter and shared network projections | Paging/truncation, throttling, cancellation, permission gaps, cross-subscription joins and no mutations. |
| UI | `Program.cs`, `wwwroot/index.html`, `wwwroot/app.js` | Scope selection, progress, coverage and unauthorized-request handling. |
| Skills/MCP | Shared deterministic service contracts first, separate adapter as needed | Pinned tools/schemas, matching credentials, scope enforcement and optional sampling. Preserve vendored source bytes. |
| Live pilot | Approved test tenant/management group | Compare expected hierarchy and ARM samples; demonstrate partial RBAC, inaccessible scopes and cross-tenant rejection. |

Also test duplicate group names, resource-level RBAC, expired tokens, denied hierarchy reads, missing peers, Lighthouse exclusion and scale limits. MCP tests must cover unavailable server, mismatched browser/server accounts, changed schemas, malicious resource text and missing sampling support. Existing single-subscription tests do not qualify these new modes.

This is the next discovery increment in the [networking plan](private-networking-self-service.md) and [platform MCP design](platform-mcp-skills.md). Automatic subnet selection, IPAM allocation and deployment retain separate gates.
