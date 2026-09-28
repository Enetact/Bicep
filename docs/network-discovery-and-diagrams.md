# Network discovery, validated agent diagrams and AVNM allocation

[Wiki home](README.md) · [Platform overview](platform-overview.md) · [Status](completion-status.md)

Source implementation: 27 September 2026. This guide is the current delivery contract and manual test runbook for [the implementation plan](plans/network-diagram-delivery.md). Azure/Codex/ADO acceptance is separate from local tests. No production target or allocation profile was enabled by this implementation.

## What you can test now

1. **Network discovery** in Platform Studio: registered subscription, selected subscriptions, management-group descendants or all accessible subscriptions in the configured tenant.
2. **Observed configuration** diagrams and deterministic prefix, capacity-boundary, delegation, DNS-link, route and endpoint findings.
3. **Review this evidence with Codex**: the existing Microsoft visualizer skill receives this browser's recent frozen snapshot through the MCP evidence bridge.
4. **Agent interpretation** diagrams: supported Mermaid is parsed and matched to observed/reference IDs and configured relationships before rendering as image-only SVG. Review, Mermaid and receipt remain downloadable.
5. **AVNM IPAM pipeline** requests: review/send a Plan, Reconcile, Reserve or Reserve-and-create-network run. Pool configuration and protected ADO setup are required before this portion can run.

The four views remain distinct: **Observed configuration**, **Agent interpretation**, **Proposed workload**, **Azure Preview changes**. None implies effective reachability or deployment approval. Existing workload configuration and saved Preview diagrams retain their separate contracts.

## Scope and collection contract

`NetworkDiscovery.Scopes` reads subscriptions and management groups through the portal's existing delegated Azure identity. It accepts only enabled subscriptions whose reported `tenantId` equals the configured portal tenant. Foreign/delegated, disabled and unqualified entries are excluded and reported. Management groups must also report the matching tenant. Group descendants are reconciled with the visible subscription set; inaccessible descendants appear as issues.

`NetworkDiscovery.Discover` resolves scope server-side before calling the fixed ARM reader. Registered mode preserves existing catalog scope; broader modes do not add deployment targets. Set `Portal:AllowExpandedNetworkDiscovery=false` in local configuration to disable broader enumeration and scans. No configuration can enable a workload simply by discovering it.

The first collector uses **bounded direct ARM GETs**, not Resource Graph. This reuses the tested projection and explicitly collects successful-empty subscription results. Resource Graph remains a future scale optimization, not a hidden dependency. Enumerations allow 20 pages/10,000 entries; scans allow 64 subscriptions, ten minutes and 32 MiB aggregate evidence. Each subscription retains its existing two-minute budget, ten pages/5,000 resources per collection and 4 MiB per response. Limits and throttled/failed reads become Partial/Failed; no silent successful truncation. The UI shows elapsed time and offers cancellation, not a fabricated completion percentage. Broader scans are sequential to limit ARM pressure.

Network projection includes resource metadata, VNets with reported inline subnets/peerings, configured NSGs/routes, endpoints, private DNS zones and child VNet links for up to 50 zones per subscription. References do not cause automatic reads outside selected scope. Custom DNS/resolvers, effective routes, data-plane traffic, RBAC/join rights, AVNM security policies and external/on-premises address coverage are not collected. Successful reads prove visibility of the returned data, not completeness of the tenant.

Each scan saves `artifacts/portal-network/<id>/report.json`: requested/resolved scopes, tenant, timestamps, membership limitations, collection results and rule findings. A single current snapshot is retained per browser for up to 15 minutes of agent use. It cannot be selected by another browser or loaded by arbitrary filesystem path. Disconnect clears it. These reports are not ADO workload manifests.

## Evidence-bound Mermaid

`EvidenceDiagram.Build` produces stable aliases for canonical resource IDs and an allowlist of reported relationships. Referenced-but-unread resources retain **Referenced only** status. The graph is frozen with the evidence exposed by `platform_evidence`; `platform_skill` supplies the pinned Microsoft skill plus adapter limits. No browser token is passed to Codex.

The validator accepts exactly one fenced Mermaid block with `graph TB/LR` or `flowchart TB/LR`, individual node declarations and labeled `-->` edges. It rejects unknown/duplicate aliases, invented or reversed connections, changed relationship labels, multiple blocks, unsupported syntax, HTML, links, directives, styling and excessive output. This is an intentionally restricted Mermaid language, not every feature supported by Mermaid.js. A no-resource snapshot may produce an Empty result without a diagram.

The model's node labels are not authoritative: the visual uses canonical names/types from the evidence. Interpretation consists of the selected evidenced nodes/relationships and advisory prose; the UI reports how many nodes/edges were included. Prose remains untrusted and is displayed with `textContent`. Model confidence cannot upgrade an unknown fact.

Accepted graph data uses the existing escaped SVG renderer as an `<img>` Blob, with no inline SVG/HTML evaluation or external renderer/CDN. Search, 40-node pages, full resource IDs and page SVG export are retained. Only edges whose endpoints are on the displayed page are drawn; cross-page links remain in the details. Unsupported Mermaid remains in the original downloadable review, with a visible rejection explanation and no accepted visual.

Agent evidence is capped at 256 KiB; diagram graphs at 200 nodes/500 edges; review parsing at 128 KiB and diagram source at 64 KiB. Reduce scope if needed. Larger network scans remain useful deterministically even when too large for one model review.

Saved files under `artifacts/portal-agents/<id>/` are `evidence.json`, `review.md`, `receipt.json`, `diagram.json` and, only when validated, `diagram.mmd`. The receipt records the Mermaid hash, validation status and tool calls. A completed model review and a validated diagram are separate statuses. Hashes support comparison; these local files are not immutable audit storage.

## Deterministic network findings

`NetworkAssessment.Evaluate` checks canonical IPv4 prefixes, VNet containment, sibling overlap, the current /26 Web-host integration baseline, reported delegation, configured DNS links, custom DNS, NVA routes and private-endpoint approval state. It distinguishes Known, Unknown, Unsupported and Conflict.

Capacity reports total IPv4 addresses minus Azure's five reserved addresses. It **never calls that free capacity**: occupancy, platform reservations and scale headroom remain unknown. The Web-host size rule is role-specific and is not a requirement for every private-endpoint subnet. DNS links and endpoint approval do not prove FQDN resolution. An NVA route does not prove appliance policy or the return path. Automatic workload placement remains Blocked until the wider qualified profile/probe/ownership evidence exists.

## AVNM IPAM setup and delivery

Register **Network - AVNM allocation** from [azure-pipelines-network.yml](../azure-pipelines-network.yml). The pipeline uses `SC-AZ-A-Bicep`; its subscription scope must contain the configured pool and network stack. A broader discovery token does not broaden this service connection.

Before a reservation run, the platform owner must:

1. Create or select an existing AVNM IPv4 pool under connectivity ownership. This delivery does not invent a pool, root CIDR or routing domain.
2. Reconcile existing Azure, on-premises and other relevant prefixes into the pool's authoritative operating model. Supply real values in [config/network-allocation.json](../config/network-allocation.json): `poolId`, `tenantId`, `subscriptionId`, `routingDomain`, `owner`, `costCenter`, `location` and an HTTPS review reference. The pool ID must match the subscription.
3. Configure ADO environment **platform-network-allocation** with explicit approvals and an **exclusive lock shared by every writer to this pool**. Use protected main/definition permissions, authorize the service connection and prevent out-of-band writers. `lockBehavior: sequential` consumes an existing check; YAML does not create it.
4. Grant the service connection the reviewed read/write rights for the pool/static CIDRs, deployments, resource groups, VNets and subscription Deployment Stacks. Validate exact rights in the test subscription; do not assume Contributor covers every platform policy/deny condition.
5. Set `exclusiveLockReviewed`, `externalPrefixesReconciled` and `enabled` only after those reviews. These are operator attestations, not proof that ADO/Azure checks exist. Leave all workload targets independently disabled until their own onboarding is complete.

| Mode/stage | Actual operation |
|---|---|
| Plan only | Reads pool and stable allocation identity, writes intent/receipt locally in the agent, publishes `network-plan`. Does not consume space. |
| Reconcile | Reads that same ID, returns Reserved, Absent or Quarantined evidence. Never deletes, renews or retries allocation automatically. |
| Reserve | After protected approval, compares the same-run Plan intent, requests 256 addresses using the static CIDR Bicep module and rereads provider state. Publishes `network-reservation`. |
| PreviewNetwork | Uses the retained provider-assigned /24, deriving an integration /26 and endpoint /27. Performs subscription Bicep What-If with these exact prefixes and publishes `network-preview` plus its summary. |
| ApplyNetwork | After a separate protected approval, rereads the allocation and reruns What-If. Hash drift blocks apply. Creates a separate connectivity-owned Deployment Stack/RG/VNet and two subnets; publishes `network-binding`. |

`Reserve and create network` selects all four stages. Exact prefixes cannot be approved before the provider assigns them, so the first approval authorizes reservation, and the later approval reviews the actual network What-If. The Plan stage does not claim a free prefix.

Stable identity is tenant/routing-domain/profile/workload/environment, independent of ADO run ID. Provider description binds the reviewed intent hash. Existing allocations must have the exact ID, description/hash, Succeeded state and one canonical /24. Other outcomes are quarantined. A configuration change cannot silently overwrite an allocation.

The current network creation profile is intentionally **create-only**: only Create/NoChange What-If results are admitted; an existing matching RG or stack blocks apply for reconciliation. Resource names use `rg-net-<workload>-<environment>-<stable hash>` and the analogous `vnet-` prefix. The stack uses detach-on-unmanage with no automatic teardown. The durable static CIDR remains held for the network lifetime; the VNet uses explicit prefixes and does not request a second native allocation. No reservation TTL, automatic release, resize or renumbering is implemented.

NetworkCreated is **not WorkloadReady**. The new spoke has no automatic hub peering, route tables, NSGs, private DNS zones/links, resolver integration or workload resources. Review the returned binding, configure/validate the required connectivity and then onboard the workload's existing subnet/DNS references through its normal Discover → Preview → Deploy path. Automated cross-stack binding/admission and production connectivity profiles remain later work; this release never bypasses those checks.

If a network apply partially succeeds, retain the allocation and inspect the stack/operations before repair. Never delete a reservation because a run failed. A future release operation must prove no resource or in-flight operation still owns the space and qualify the provider's supported cleanup semantics.

## Manual tests, in order

From the source checkout:

```powershell
./scripts/Stop-Portal.ps1
./scripts/Setup-Portal.ps1
./scripts/Start-Portal.ps1
```

If the updated portal is already running, simply reload it. A backend restart requires reconnecting Azure/ADO/Codex in that browser.

1. **No cloud changes:** open Network discovery without login. A scan must request Azure connection; allocation must report missing pool configuration. The workload targets remain disabled.
2. **Single scope:** connect Azure, scan the registered subscription and inspect successful-empty versus Partial/Failed collections. Download JSON and SVG. Compare a known VNet/subnet/DNS link against Azure Portal.
3. **Broader scopes:** refresh accessible scopes, select subscriptions, then a management group and tenant mode. Compare resolved subscriptions to expected caller visibility. Test denied hierarchy/resource reads; they must not become empty success. Cancel a scan; no allocation or deployment should appear.
4. **Network findings:** compare reported prefix math, overlap/containment, delegation, DNS and endpoint state against known test configuration. Check that free capacity and effective routing remain Unknown.
5. **Real agent:** connect Codex, choose Review this evidence with Codex, inspect the scope disclosure and explicitly Run. A supported completed visual must be labeled Agent interpretation and reference the discovered IDs. Download review, Mermaid, receipt and SVG. Inspect `diagramStatus` and hash in the saved receipt. No automatic Azure write or ADO run should occur.
6. **Adversarial/limits:** use the automated malicious-output cases below rather than changing real Azure metadata solely to inject prompts. Verify a large real scan requires narrower scope instead of silently cutting model evidence.
7. **ADO Plan:** after configuring a test pool, register the pipeline and connect ADO. Review/send Plan only from the portal. Compare the visible request with the real ADO run; inspect `network-plan`. Pool usage must remain unchanged.
8. **Reservation (real Azure write):** only in the approved test pool, enable the reviewed profile and choose Reserve only. Approve in ADO. Verify one stable allocation and Reserved receipt. Reconcile/rerun the same request: it must not consume a second prefix. Test a conflicting intent through a reviewed test configuration and confirm it blocks.
9. **Create spoke (real Azure write):** choose Reserve and create network. Review exact prefix What-If before the second approval. Verify network-binding IDs and subnet prefixes, retained IPAM allocation and independent stack ownership. A subsequent Apply must block existing-resource adoption. Do not enable a workload until its DNS/routing/product acceptance is complete.

Local automated checks:

```powershell
./scripts/Stop-Portal.ps1
./scripts/Test-Portal.ps1
node ./tests/infrastructure/verify.mjs
./scripts/Update-ServiceCatalog.ps1 -Check
./scripts/Update-Manifest.ps1 -Check
./scripts/Start-Portal.ps1
```

Tests use production code with synthetic Azure evidence; they do not log in, invoke a model, create a pool or reserve addresses. For the recorded results and package/platform limits, see [validation](validation.md).

Microsoft contracts: [Azure Resource Visualizer](https://learn.microsoft.com/en-us/azure/developer/azure-skills/skills/azure-resource-visualizer), [subscriptions](https://learn.microsoft.com/en-us/rest/api/resources/subscriptions/list?view=rest-resources-2022-12-01), [management-group descendants](https://learn.microsoft.com/en-us/rest/api/managementgroups/management-groups/get-descendants?view=rest-managementgroups-2020-05-01), [AVNM IPAM](https://learn.microsoft.com/en-us/azure/virtual-network-manager/concept-ip-address-management), [static CIDRs](https://learn.microsoft.com/en-us/azure/templates/microsoft.network/networkmanagers/ipampools/staticcidrs).
