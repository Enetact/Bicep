# Saved discovery analysis and topology reports

**Implemented locally, 26 September 2026. ADO integration is authored but not yet verified in a live run.** This is the first reporting increment of the [enhancement plan](plans/enhanced-self-service-validation.md). It uses no Azure calls, MCP server, model, credentials or new npm packages.

## Run against a downloaded artifact

From the repository root, install/use Node.js 22+ and PowerShell 7.4+. Extract a selected `subscription-discovery` artifact into a local directory containing `manifest.json` and `inventory.json`. Use a new output directory on every run:

```powershell
./scripts/Export-SelfServiceAnalysis.ps1 `
  -DiscoveryDirectory ./artifacts/downloaded-discovery `
  -OutputDirectory ./artifacts/analysis/my-review `
  -Workload eventflow -EnvironmentName dev
```

For Blob copy use `-Workload blobcopy`; both values must match the selected artifact. The workload type is obtained from the supported manifest contract, never an executable path. No login is needed. `-EvaluatedUtc` is available for explicitly retrospective/reproducible reviews; omit it for current freshness evaluation and disclose it when used.

The underlying dependency-free Node entry is equivalent:

```powershell
node ./scripts/analysis/cli.mjs --input ./artifacts/downloaded-discovery --output ./artifacts/analysis/another-review --workload eventflow --environment dev
```

Paths in these commands are example local locations, not files shipped with the repository. To exercise synthetic examples without a subscription:

```powershell
./scripts/Test-SelfServiceAnalysis.ps1
$latest = Get-ChildItem ./artifacts/analysis-tests -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
Get-Content (Join-Path $latest.FullName cli-output/README.md)
```

## Files produced

| File | Meaning |
|---|---|
| `analysis.json` | Versioned evidence context, coverage, findings and normalized resource graph. Contract: [offline-analysis.schema.json](../schemas/analysis/offline-analysis.schema.json). |
| `README.md` | Selected workload, collection status, reported prerequisite actions, diagrams, findings and limits. |
| `topology-N.mmd` | Mermaid source, at most 30 nodes per page. |
| `topology-N.svg` | Static, escaped SVG fallback with no scripts, links, embedded images or provider property values. |
| `report-manifest.json` | SHA-256 hashes of emitted report files; integrity evidence, not authentication or approval. |

No diagram page is generated when there are no resource nodes. An empty graph does not prove an empty environment: read collection status. Graphs use observed/proposed containment only; they deliberately make no claims about DNS resolution, traffic routes or runtime reachability. Cross-page containment remains recorded in JSON. Large labels are shortened in SVG; complete projected identifiers remain in JSON/Markdown.

Reports include network/DNS inventory that may be shared and selected Event flow prerequisites. Unrelated subscription ARM catalog resources are excluded. This is not the entire deployed application inventory: Blob copy discovery has no complete prerequisite plan, and neither discovery schema captures every runtime relationship. Use deployment Preview for the full compiled infrastructure release.

## Status and trust

All outputs have `deploymentAuthorized: false` and `provenance: UnverifiedOffline`. `partial` means a report was produced with required live authentication/coverage unknowns; `blocked` additionally records an explicit saved-evidence blocker. There is intentionally no offline Ready or Approved state. Generating a blocked/partial report exits successfully; malformed, mismatched or unsupported input exits with an error.

The reader accepts Blob copy manifest v1 / inventory v1 and Event flow manifest v2 / inventory v1. It checks exact inventory bytes against the manifest hash, subscription/selection, timestamps, array bounds, supported actions, resource-ID scope, duplicate identities and parentage. Unknown major versions fail. A hash supplied in the same artifact cannot establish trustworthy origin: the existing deployment handoff still authenticates the actual ADO run and target policy.

Query status is **ReportedComplete** or **Unknown**. The first means the saved producer reported success without explicit truncation, continuation or count mismatch; it is not independent proof of complete paging. Failed queries never become absence. Event flow Create/Reuse/Manage actions are copied only as informational reported actions when saved required collections, plan and ownership are consistent. Conflicting ownership/existence or incomplete evidence becomes Blocked. Current policy/target compatibility still belongs to the existing deterministic deployment resolver.

Input JSON is limited to 16 MiB per file; collections and normalized nodes are limited to 5,000. The local wrapper accepts explicit local paths; the future shared MCP facade must replace them with authorized opaque artifact references. Reports intentionally project only identifiers, types, query outcomes and fixed explanations. Raw errors, tags, settings, credentials and free-text reasons are not copied. Resource identifiers remain sensitive operational metadata; retain output in ignored artifacts or access-controlled ADO artifacts, not public examples.

## Pipeline integration

The shared `self-service-discover.yml` now runs the offline renderer after inventory, including when discovery saved partial evidence. It installs the same Node 22 major used by qualification, uploads the Markdown summary, and retains reports under `subscription-discovery/analysis/`. It does not alter `manifest.json`, `inventory.json` or deployment selection. Missing evidence after authentication failure produces an explanatory log entry; the failed inventory task remains failed.

The current ADO renderer's SVG/Mermaid behavior has not been accepted live. Download the static SVG artifact if the Summary view does not render the image. Actual ADO server expansion, run authorization and rendering must be checked before marking V1's live pilot complete. Existing Preview/Deploy stages and disabled targets remain unchanged.

## Method and file placement

| Source | Responsibility |
|---|---|
| `scripts/Export-SelfServiceAnalysis.ps1` | Thin PowerShell entry, Node requirement and argument forwarding. |
| `scripts/analysis/cli.mjs` | Bounded fixed-name file reads, supported options, exclusive new output directory and report-file hashes. |
| `scripts/analysis/core.mjs` | Pure `analyzeDiscovery`, stable hashing/canonical serialization, compatibility checks and normalized graph/findings. No filesystem, processes or network access. |
| `scripts/analysis/render.mjs` | Pure `renderAnalysis` returning safe Markdown, Mermaid and SVG text. |
| `schemas/analysis/` | Versioned output contract; distinct from workload intent/message schemas. |
| `tests/analysis/` | Synthetic fixture builders and regression tests, no tenant data. |
| `scripts/Test-SelfServiceAnalysis.ps1` | Runs cases and validates emitted JSON against the schema; included by `Test-Project.ps1`. |

Node.js already exists in qualification; this component adds no npm dependency. Inspection found local Node 24.16.0, PowerShell 7+ and .NET SDK 10.0.300; this feature uses Node 22+ built-ins and the existing PowerShell requirement, not a new .NET service or package stack.

Four project skills now support local request drafting, discovery auditing, topology reporting and saved change review. Their [design and supported modes](plans/platform-mcp-skills.md) distinguish these real offline workflows from future MCP tools. Skills never make a report into an approval.
