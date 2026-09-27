# Connected discovery, workload and Preview diagrams

**Agent diagram update, 27 September 2026:** generated Mermaid is now parsed as a restricted flowchart grammar and matched against frozen resource/relationship evidence before image-only SVG rendering. Agent interpretation remains separate from observed configuration, proposal and Azure Preview. Rejected output stays text-only; source and receipt downloads remain available. See [diagram validation and manual tests](network-discovery-and-diagrams.md).

Implemented locally on 27 September 2026. The connected diagram views render deterministic diagrams from evidence and reviewed component definitions. Separately, [Agent workflows](agent-workflows.md) can invoke Codex with the pinned Microsoft visualizer and a scoped MCP evidence snapshot; its validated Mermaid graph is rendered as Agent interpretation, with advisory source retained. Rejected output remains text-only. The deterministic views do not call a model. Neither diagram path enables deployment targets or authorizes resource creation.

## Use the connected views

1. Start Platform Studio with `./scripts/Start-Portal.ps1` after [source setup](local-portal.md#run-from-source-in-order).
2. In **Skills library**, choose a discovery profile, connect Azure, and select **Discover visible resources**. The result now includes an **Existing resources** diagram alongside collection coverage and JSON. The network profile adds inline subnets, configured peerings, NSG/route-table references and private-endpoint relationships. DNS links and effective connectivity are not inferred.
3. Choose **Configure a workload**, then a product card. **Proposed workload** appears immediately for that product, environment and region. All seven products have reviewed component diagrams. These are conceptual groups, not evaluated Bicep resource counts or final resource names. Conditional/reused dependencies are described, not assumed to be new resources.
4. When the latest Azure inventory in this page session matches the target subscription, expand **Compare with the latest visible-resource discovery for this subscription**. It shows the observed diagram separately from the proposal. Inventory can be filtered, partial and broader than the workload; proximity or a similar name is not a reuse decision. Azure Skills JSON remains distinct from the ADO manifest required for Preview.
5. Queue the workload's **Discover** pipeline, choose its successful saved run, and request **Preview changes** through the existing review/confirmation flow. Configuring or viewing diagrams does not queue anything.
6. In **Pipeline activity**, choose **View Preview diagram** after the `deployment-preview` artifact is published. This also works while the run waits for Deploy approval. For a previous run, enter its positive ADO Preview/Deploy run ID in the workload form and select **Load Azure Preview diagram**. ADO browser sign-in is required. The run must match the registered Deploy definition, YAML path, repository and main branch; its artifact must match workload, environment, subscription, run and commit. The UI also checks environment and region against the selected form.
7. **Azure Preview changes** displays Create, Modify, Delete, Detach, NoChange and uncertain actions on resource nodes. Potential/unknown certainty appears on the node and in the details. Missing changes are unknown. Blocked Preview results remain visibly blocked. Use ADO's README and JSON for property-level changes, diagnostics and approval decisions. A completed matching run submitted in this page session automatically attempts to load its Preview once. Missing or oversized artifacts show an explanation and retain the proposed view; they never become a successful zero-change plan.

Each diagram supports search, 40-node pages, a full-name resource/relationship list and SVG export of the current page. Edges only draw between nodes on that page; the detail list includes cross-page relationships. Export metadata includes evidence context and the current filter/page. Reports and diagrams can reveal infrastructure names and network ranges; store/share them with the same care as discovery artifacts.

Changing workload/environment/region/action/discovery selection clears saved Preview evidence. Switching selections while an artifact loads cannot attach the old response to the new configuration. The current discovery comparison and run list are session-only; refreshing clears them. Existing saved-manifest **Discovery analysis** remains available independently.

## Methods and contracts

| Path | Responsibilities |
|---|---|
| `AzureDiscovery.Discover` → `observedTopology` | Existing bounded ARM reader; case-insensitive resource-ID deduplication, scope/group nodes and observed/configured relationships. References outside collected coverage are labelled **Referenced only** and are not followed. Failed/partial queries remain unknown. |
| `Catalog` / bootstrap → `config/portal-topologies.json` → `proposedTopology` | Seven source-backed component definitions. Each entry identifies its composition and module/resource symbols. Tests require complete product coverage and existing source symbols. No template evaluation or exact resource matching occurs in the browser. |
| `GET /api/preview/{product}/{runId}` → `AdoGateway.Preview` | Validate registered pipeline/run provenance, obtain the named artifact with the delegated ADO token, then download from an allowlisted Azure artifact-storage host without forwarding that token. Redirects are disabled. |
| `PreviewDiagram.Read` → `previewTopology` | Read the ZIP in memory without extraction. Project resource IDs, action/uncertainty and run context from `target.json`, `preview-inputs.json`, `status.json`, `effective.parameters.json` and `azure/stack-what-if.json`. No template parameters, property values, raw errors, or signed URLs are returned to the browser. This is a display projection, not the pipeline's full bundle/hash/policy approval validator. |
| `renderTopology` / `graphSvg` | Shared paginated renderer, text-only DOM labels, XML escaping, image-only SVG and full-name details. No external diagram library, executable SVG input or remote rendering service. |

Artifact retrieval uses Microsoft's [Pipelines Artifacts Get API with signed content](https://learn.microsoft.com/en-us/rest/api/azure/devops/pipelines/artifacts/get?view=azure-devops-rest-7.1). Downloads are limited to 32 MiB compressed, 1,000 archive entries, 4 MiB per selected file and 10,000 resource changes. Only HTTPS port 443 hosts under `.vsblob.vsassets.io` and `.blob.core.windows.net` are accepted; unsupported hosts fail with guidance to open ADO. ZIP paths must be exact or have one `deployment-preview/` root. Duplicate required entries fail closed. ADO permissions still determine artifact visibility.

No packages were added: the portal retains .NET 10/MSAL and browser-native SVG. The diagram definitions under `config/` and the frontend module are included by the existing portable packaging path. Packages produced before this change must be rebuilt.

## Verification and limits

Run locally, in order (stop the owned portal before rebuilding):

```powershell
./scripts/Stop-Portal.ps1
./scripts/Test-Portal.ps1
./scripts/Start-Portal.ps1 -NoBrowser
node tests/portal/smoke.mjs
```

Optional browser fixture, with the real portal serving bootstrap on 5087:

```powershell
node tests/portal/diagram-fixture-server.mjs
# Open http://127.0.0.1:5098 — visibly labelled LOCAL TEST FIXTURE.
# Skills library > search azure-resource-visualizer > Discover in Azure > Discover visible resources.
# Configure a workload > Private Storage Workspace > load Preview run 44.
# Stop this fixture with Ctrl+C; it cannot queue pipelines or contact Azure/ADO.
```

Local automated checks cover topology semantics/source links, large inventories, SVG injection encoding, blocked/invalid archives, run/target mismatch, download hosts, bounded streams and token separation. Browser checks use the real configuration view plus synthetic discovery/Preview responses. Live Entra consent, ARM inventory, ADO artifact download host/ZIP compatibility and real What-If acceptance remain unverified. A rendered diagram is not proof of private DNS, routing, reachability, workload deployment or Azure readiness.
