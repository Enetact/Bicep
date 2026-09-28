# Authenticated Codex agent workflows

**Extension standard:** follow [the shared workflow standard](agent-workflow-standard.md), [blueprint](templates/agent-workflow-blueprint.md) and [workflow-builder skill](../.agents/skills/platform-workflow-builder/SKILL.md). Tagging implements validated structured recommendations and explicit advice-to-draft selection. The four earlier reviews remain advisory Markdown. AHP coordinates readiness; the typed API starts model execution.

Implemented locally on 27 September 2026. Platform Studio now connects a browser-owned agent host, Codex inference and an in-process MCP evidence server. **Live ChatGPT login, Astra inference, Azure collection and ADO Preview review remain unverified.** The native Codex startup/account/MCP handshake and local contract tests are verified; those checks made no model calls.

## Developer experience

1. Start the portal using the commands below and open **Agent workflows**.
2. Choose **Connect Codex in browser**, complete ChatGPT sign-in, and return. If the browser blocks the new tab, use **Continue Codex sign-in**. Cancel and retry are available.
3. For Azure workflows, connect Azure under **Connections**. For saved Preview review, connect Azure DevOps. These use the existing Entra desktop registration and remain separate from Codex authentication.
4. Choose a workflow and its scope. Visualization/network review require an explicit resource-group selection or the current browser-owned Network discovery snapshot (maximum age 15 minutes). Workload advice requires product/environment/region. Preview review additionally requires a matching saved run ID.
5. Read the scope and data-transfer description, then click **Collect evidence and run review**. Sign-in and readiness checks never start inference automatically.
6. Read the advisory Markdown, inspect the tool/evidence receipt, or download the review. A successful review is not an Azure deployment, approval or proof of connectivity.

| Workflow | Connections | Implemented evidence and limits |
|---|---|---|
| Azure resource visualizer | Codex + Azure | Executes the pinned Microsoft visualizer instructions against selected-group metadata and configured network relationships. Produces advisory Markdown and restricted Mermaid validated against the evidence before image-only rendering. Does not collect secret-bearing app settings, data-plane objects or identity/RBAC detail. |
| Private network evidence review | Codex + Azure | Uses the same visualizer guidance with a network-specific review scope. Covers configured VNet/subnet/peering/NSG/route/DNS/private-endpoint inventory. Broader visible scopes are available through Network discovery; IP occupancy, IPAM availability and effective traffic remain unknown. |
| Workload configuration advisor | Codex | Applies `platform-request-design` to the selected catalog product, target and reviewed topology. Covers all seven products. Does not write/resolve a typed request or queue ADO. |
| Tag governance review | Codex + Azure | Applies `platform-tagging-review` to 1–50 selected resources in saved tag evidence (maximum age 30 minutes, 256 KiB projection). Strict JSON/resource/finding validation before selectable suggestions. No model write tools. See [tagging](tag-governance.md). |
| Saved Preview change review | Codex + ADO | Applies `platform-change-review` to the existing bounded Preview projection. Source/run checks are deterministic. Full digests, stack ownership, application ZIP contents and approval outcomes are not proven by this projection. |
| Design Bicep source draft | Codex + Azure | Applies `platform-bicep-composition` to a browser-owned network snapshot, selected workload context and source-hashed local module contracts. Generates unqualified main/stack/parameter source and provenance. See [source drafts](bicep-source-drafts.md); compile and qualify before promotion. |

The other bundled Microsoft skills remain guidance plus their existing deterministic discovery views. Their complete upstream workflows, hooks and remediation commands are **not** enabled. The four original reviews add no pipeline definitions. Tag governance adds two separate governed entries, not a new deployment product. Microsoft Azure MCP Server (`azmcp`) is not launched or implicitly signed in; this implementation uses the platform's own MCP evidence bridge and existing Azure/ADO readers.

## Setup and dependencies

From the repository root, in PowerShell 7.4+:

```powershell
./scripts/Stop-Portal.ps1
# Optional installation if Codex CLI is missing. Uses the official npm package.
npm install --global @openai/codex@0.153.2
./scripts/Setup-Portal.ps1
./scripts/Start-Portal.ps1
```

Existing Codex installations need not be replaced. The implementation was checked with **Codex CLI 0.153.2**, **.NET SDK 10.0.300** and **ModelContextProtocol 2.2.0**. NuGet lock files pin the MCP dependency. Codex runs natively on Windows ARM64 or x64 using the matching official CLI package. Portable portal packages still require Codex installed separately; they do not bundle another agent executable. Rebuild older portal packages to include these changes.

Native executable lookup checks PATH and the official npm package's architecture-specific vendor directory. If needed, set `Portal.CodexPath` in ignored `src/SelfService.Portal/portal.local.json` to the absolute path of the official `codex.exe`, then restart. This must be an executable path, not a shell command or `.cmd` wrapper. The example configuration leaves it empty for discovery. No API key or Azure model endpoint is required.

The provider selection is fixed in `AgentPolicy`: model **`gpt-6-astra`**, reasoning **`high`**, service tier **`default`** (Standard). Both thread creation and each turn specify the tier; the turn also sets `serviceTierForTurn`. Readiness checks the authenticated account's model catalog, including High and Standard support. Effective thread settings are verified before starting the turn. Unavailable or substituted settings block execution. There is no Fast/priority, alternative-model or API-key fallback. See [Codex app-server](https://developers.openai.com/codex/app-server); the exact field contracts were also generated from the installed CLI.

## Method and protocol flow

```mermaid
sequenceDiagram
    participant UI as Portal browser
    participant Host as Local agent host
    participant Reader as Azure / ADO readers
    participant MCP as In-process MCP server
    participant Codex as Codex app-server
    UI->>Host: AHP initialize / createSession / subscribe
    Host-->>UI: Connection and workflow readiness
    UI->>Host: Connect Codex
    Host->>Codex: account/login/start
    Codex-->>UI: Official browser sign-in URL
    Host->>Codex: account/read + model/list
    UI->>Host: Run selected typed workflow
    Host->>Reader: Collect selected read-only evidence
    Host->>MCP: Freeze evidence and pinned skill
    Host->>Codex: thread/start; verify settings; turn/start
    Codex->>Host: item/tool/call
    Host->>MCP: tools/call (evidence or skill only)
    MCP-->>Codex: Scoped snapshot via host callback
    Codex-->>Host: Completed advisory review
    Host-->>UI: Review, hashes and tool receipt
```

| Component | Responsibility |
|---|---|
| `AgentHostChannel.Handle` | Cookie/origin/CSRF-bound WebSocket coordination at `/api/agent/ahp`. Implements `initialize`, `createSession` and snapshot `subscribe`; channels cannot cross browser connections. |
| `AgentWorkflows.Status` | Checks Codex account/model readiness separately from Azure/ADO audience availability. GET/readiness creates no Codex process and performs no inference. |
| `AgentWorkflows.Connect` | Starts one isolated native Codex process for this browser session and requests the official ChatGPT login flow. |
| `AgentWorkflows.Run` | Rechecks readiness, validates catalog scope, collects fixed-route evidence, bounds/fixes the snapshot, invokes the agent and saves the result. One active operation per browser; five-minute run limit. |
| `AzureDiscovery.ResourceGroups` / `Discover` | Uses the portal MSAL Azure token for fixed ARM GET collections; missing/denied/partial coverage is distinguished. Selected-group resources cannot escape that group. |
| `AdoGateway.Preview` | Reuses the guarded run/artifact reader; verifies workload/environment/region matching before model review. |
| `AgentMcpBridge.Create` / `Call` | Official MCP SDK client/server over local pipes. Exposes only zero-argument `platform_evidence` and `platform_skill`, limited to 12 calls. Arguments, unknown tools/namespaces and revoked sessions stop the review. |
| `CodexAgentRuntime.Review` | Uses explicit skill input, disables inherited tools/features, rejects inherited MCP servers, starts a read-only/no-approval thread, checks settings, handles dynamic-tool callbacks and requires terminal completion. Both evidence tools must have been read. |

**AHP scope:** this is an experimental, bounded **coordination-only profile based on AHP 0.9.0**, not a fully conformant general-purpose AHP host. It supplies fresh snapshots when subscribed, not continuous multi-client action/replay synchronization. It does not advertise arbitrary chat dispatch, terminals, filesystem access or the MCP side-channel. Model execution deliberately uses the typed `/api/agent/run` adapter, then native Codex app-server stdio. AHP and Codex app-server are different protocols. No PowerMCP host or its fixed model settings were modified. See [Microsoft AHP](https://github.com/microsoft/agent-host-protocol).

## Identity, evidence and failure behavior

- Azure/ADO tokens stay in the existing per-browser MSAL cache. They are never passed to Codex, process arguments, MCP tool output or prompts. Selected infrastructure metadata does go to Codex on **Run**, so normal Codex usage limits apply.
- Each Codex process has a fresh ignored home under `.local/portal/agents/<id>`. Existing desktop credentials/configuration are not copied. Codex stores its own login cache there while connected. Disconnect removes that runtime's `auth.json`; the stop script verifies and stops registered child processes and removes their credential file. A crash or forced OS shutdown can leave this ignored local cache; protect this folder as credentials and remove stale session directories deliberately when no process uses them. No browser-session reconnect is promised after portal restart.
- API-key and Azure credential environment variables are removed from the child. Shell, web search, hooks, plugins, memories and delegated agents are disabled; read-only sandbox settings are checked before inference. Unknown Codex server requests are rejected. The trusted local CLI/OS account remains part of the trust boundary.
- Evidence is capped at 256 KiB before inference; oversized evidence is rejected without silent truncation. Azure collections retain their existing time/page/resource limits. A selected resource group's visibility must be verified before analysis.
- Each run writes `evidence.json` (after successful collection), `review.md` (only on completed review) and `receipt.json` beneath ignored `artifacts/portal-agents/<id>`. Receipts record requested/accepted model settings, skill/evidence hashes, tool calls and terminal state, but no tokens or raw provider errors. They are local evidence, not tamper-proof audit storage or proof of the server's billed service tier.
- Failure after inference starts, cancellation and timeout retain a failure receipt, stop the owned inference process and require reconnect. Input/collection failures before inference preserve the connection for correction. Disconnect revokes further evidence reads. No failed or partial collection is converted to creation approval.
- Model output is displayed with `textContent`. Markdown remains text-only. Accepted restricted Mermaid is converted to an evidence-validated graph and escaped image-only SVG; unknown relationships and executable/unsupported syntax are rejected. Existing observed/proposed/Preview views remain separately labeled. See [network diagram contract](network-discovery-and-diagrams.md). Semantic correctness of model prose is not guaranteed; no model output drives Azure changes.

## Verification and next integrations

For source/runtime separation and Git exclusions, run `./scripts/Test-RepositoryHygiene.ps1`; see [the repository boundary map](repository-structure.md#source-runtime-state-and-test-boundaries). Normal portal startup runs the application Release assembly. Neither `tests/portal/diagram-fixture-server.mjs` nor the native opt-in test creates the production connection; **Connect Codex** invokes `AgentWorkflows.Connect` in the application. Codex desktop sign-in is separate from this browser-owned portal session.

With the portal stopped, run backend/MCP tests, optionally including a native no-login/no-inference handshake:

```powershell
$env:PLATFORM_TEST_CODEX='1'
./scripts/Test-Portal.ps1
Remove-Item Env:PLATFORM_TEST_CODEX
./scripts/Start-Portal.ps1 -NoBrowser
node ./tests/portal/smoke.mjs
./scripts/Test-AgentHost.ps1
```

The native handshake test is explicitly skipped unless opted in. Before production use, complete browser consent/cancel/logout tests, one synthetic Astra review at High/Standard, one scoped Azure visualization and one ADO Preview review. Verify denied/partial coverage and cancellation against those real providers; inspect saved receipts. These remain acceptance gates.

Next additions should be read-only evidence adapters with their own scope/permission contracts: saved discovery coverage audit using the original manifest/inventory pair; policy/RBAC prerequisite explanation; cost review using dated price/billing evidence; and Resource Graph scale optimization for the now-implemented tenant/management-group coverage collector. Full AHP chat/action synchronization, general Azure MCP hosting, IPAM reservation and any write/remediation workflows remain separate enhancements. Authentication alone must never grant those capabilities.
