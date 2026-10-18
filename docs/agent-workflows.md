# Authenticated Codex agent workflows

[Wiki home](README.md) · [Platform overview](platform-overview.md) · [Status](completion-status.md)

**17 October 2026 — current integration:** ten typed workflows share the host: nine scoped reviews/drafts and `mcp-orchestration`. All five provider catalogs are connected on the final host with 657 tools. AHP advertises bounded typed workflow/MCP dispatch; exact human approval remains a separate HTTP action. This supersedes earlier universal frozen-evidence-only and coordination-only descriptions. Public-only orchestration completed through AHP/Codex with required evidence reads and successful Bicep/Learn calls. Separate GitHub identity and human-approved Azure subscription/ADO project reads also passed. ADO's missing read-only annotation correctly required approval for that intended read. The [provider guide](provider-mcp.md#live-acceptance) records exact results and scopes. No changes were executed; broader mutation/admin rights, protected deployments and the remaining scoped workflows' live acceptance remain separate.

**Extension standard:** follow [the shared workflow standard](agent-workflow-standard.md), [blueprint](templates/agent-workflow-blueprint.md) and [workflow-builder skill](../.agents/skills/platform-workflow-builder/SKILL.md). Tagging implements validated structured recommendations and explicit advice-to-draft selection. The four earlier reviews remain advisory Markdown. Both typed HTTP requests and bounded AHP dispatch use the same workflow implementation and authorization checks.

Implemented locally on 27 September 2026. Platform Studio connects a browser-owned agent host, Codex inference and an in-process MCP evidence server. **On 4 October, the existing portal ChatGPT sign-in and live GPT-6.1 Sol/Ultra catalog availability were verified with Codex 0.160.0.** Model inference, Azure collection and ADO Preview review remain separate acceptance steps. See the [dated validation evidence](validation.md).

## Developer experience

1. Start the portal using the commands below and open **Agent workflows**.
2. Choose **Connect Codex in browser**, complete ChatGPT sign-in, and return. If the browser blocks the new tab, use **Continue Codex sign-in**. Cancel and retry are available.
3. For Azure workflows, connect Azure under **Connections**. For saved Preview review, connect Azure DevOps. Choose isolated local CLI authentication or the registered-app method; Microsoft and Codex identities remain separate. For MCP orchestration, install/connect the desired providers as described in [the provider guide](provider-mcp.md); Azure MCP requires local CLI mode and GitHub uses its verified CLI attachment.
4. Choose a workflow and its scope. Visualization/network review require an explicit resource-group selection or the current browser-owned Network discovery snapshot (maximum age 15 minutes). Workload advice requires product/environment/region. Preview review additionally requires a matching saved run ID.
5. Read the scope and data-transfer description, then click **Collect evidence and run review**, or **Run orchestration task** for `mcp-orchestration`. Sign-in and readiness checks never start inference automatically.
6. Read the advisory Markdown, inspect the tool/evidence receipt, or download the review. A successful review is not an Azure deployment, approval or proof of connectivity.

| Workflow | Connections | Implemented evidence and limits |
|---|---|---|
| Private DNS analysis review | Codex + Azure | Browser-owned DNS assessment through the shared MCP bridge. Validated advice can be explicitly copied into an editable local proposal; no migration executor. |
| DNS drift and desired-change review | Codex + Azure | Explicit reference/observed/desired DNS property matrix. Local reference authority and missing accepted baseline stay visible; no remediation executor. |
| Azure resource visualizer | Codex + Azure | Executes the pinned Microsoft visualizer instructions against selected-group metadata and configured network relationships. Produces advisory Markdown and restricted Mermaid validated against the evidence before image-only rendering. Does not collect secret-bearing app settings, data-plane objects or identity/RBAC detail. |
| Private network evidence review | Codex + Azure | Uses the same visualizer guidance with a network-specific review scope. Covers configured VNet/subnet/peering/NSG/route/DNS/private-endpoint inventory. Broader visible scopes are available through Network discovery; IP occupancy, IPAM availability and effective traffic remain unknown. |
| Workload configuration advisor | Codex | Applies `platform-request-design` to the selected catalog product, target and reviewed topology. Covers all nine products. Does not write/resolve a typed request or queue ADO. |
| Tag governance review | Codex + Azure | Applies `platform-tagging-review` to 1–50 selected resources in saved tag evidence (maximum age 30 minutes, 256 KiB projection). Strict JSON/resource/finding validation before selectable suggestions. No model write tools. See [tagging](tag-governance.md). |
| Before / What-If / after IaC review | Codex + ADO | Applies the pinned Microsoft visualizer to the exact saved Preview or Deploy artifact and selected phase. Live collection occurs in the pipeline through Microsoft Azure MCP plus scoped ARM reads. Hash/provenance validation, projected properties and evidence-validated Mermaid; no write tools. See [agentic IaC](agentic-iac.md). |
| Saved Preview change review | Codex + ADO | Applies `platform-change-review` to the existing bounded Preview projection. Source/run checks are deterministic. Full digests, stack ownership, application ZIP contents and approval outcomes are not proven by this projection. |
| Design Bicep source draft | Codex + Azure | Applies `platform-bicep-composition` to a browser-owned network snapshot, selected workload context and source-hashed local module contracts. Reads a compact index and complete selected contracts through the same two evidence/skill tools, with a 32-call budget and a successful-delivery gate. Generates unqualified main/stack/parameter source and provenance. See [source drafts](bicep-source-drafts.md); compile and qualify before promotion. |
| MCP orchestration | Codex + at least one connected MCP provider | Applies `platform-mcp-orchestration`. Adds `platform_mcp_catalog`, `platform_mcp_call`, `platform_mcp_plan` and `platform_mcp_run` alongside the two evidence tools. Accepted reads and already-approved exact plans can execute. The model has no approval tool; other effects await separate human approval. Up to 48 bridge calls per turn; provider execution has independent limits and receipts. [Guide](provider-mcp.md). |

The other bundled Microsoft skills remain guidance plus their existing deterministic discovery views. Their complete upstream hooks and remediation scripts are not enabled automatically. Explicit provider connection now launches interactive Azure MCP 3.0.0 and the other configured servers; it does not sign them in implicitly. The shared IaC pipeline collector retains Azure MCP 2.0.5 under its separate service connection; the scoped IaC review reads retained evidence through the platform MCP bridge. Existing network/tagging collectors retain their browser-identity paths. Provider orchestration adds no pipeline definitions or deployment targets.

## Complete orchestration guidance

The [guidance file map](provider-mcp.md#guidance-files-and-updates) documents the five nested provider `SKILL.md`/`instruction.md` pairs, shared lowercase `instruction.md`, workflow references, concrete JSON examples and OpenAI metadata under `.agents/skills/platform-mcp-orchestration/`. A deterministic compiler embeds all **16 normative sources** into the top-level runtime `SKILL.md`; `guidance.lock.json` records source/resource hashes and the generated bundle identity. The next workflow reads that generated content from disk, serves it through `platform_skill` and records its skill hash. Guidance-only changes require regeneration, with no portal restart or sign-in loss. The separately stored companion files are not assumed to load themselves into the isolated model runtime.

```powershell
./scripts/Update-McpGuidance.ps1
./scripts/Update-McpGuidance.ps1 -Check
node ./scripts/mcp/verify-skill-sources.mjs
```

The catalog stays at **53 application entrypoints**. Complete official authoring packages preserve **52 skills** with support files, licenses and pins: 42 existing Azure, seven ADO and three Learn. The new official packages and nested provider pairs do not create executable workflows, implicit tool grants or extra runtime catalog entries. Publication verifies the compiled guidance and official ADO/Learn file inventories. The prior live workflow acceptance remains tied to its original receipt/hash. The new [AVM research and composition acceptance](avm-end-to-end-validation.md) verifies the current provider bundle and the complete composition skill separately.

## Setup and dependencies

From the repository root, in PowerShell 7.4+:

```powershell
./scripts/Stop-Portal.ps1
# Optional installation if Codex CLI is missing. Uses the official npm package.
npm install --global @openai/codex@latest
./scripts/Setup-Portal.ps1
# For the full-provider orchestration workflow:
./scripts/Setup-McpProviders.ps1
./scripts/Start-Portal.ps1
```

The current GPT-6.1 Sol/Ultra connection was checked with **Codex CLI 0.160.0**, **.NET SDK 10.0.300** and **ModelContextProtocol 2.2.0**. An older installed CLI 0.153.2 returned a catalog without GPT-6.1 Sol despite successful sign-in; the earlier protocol-only checks did not establish model availability. NuGet lock files pin the MCP dependency. Codex runs natively on Windows ARM64 or x64 using the matching official CLI package. Portable portal packages require Codex installed separately; they do not bundle another agent executable. Rebuild older portal packages to include these changes.

Native executable lookup first checks all PATH entries for `codex.exe`, in PATH order. Only if no native executable is found does it check the official npm package's architecture-specific vendor directories. This prevents an earlier npm shim directory from shadowing a newer native executable later on PATH. An explicit `Portal.CodexPath` overrides discovery; it must be an existing absolute `codex.exe` path, not a shell command or `.cmd` wrapper. Set it in ignored `src/SelfService.Portal/portal.local.json` and restart when necessary. No API key or Azure model endpoint is required.

### Sign-in and model readiness troubleshooting

The connection panel reports the selected runtime version separately from the requested model settings. `account/read` establishes ChatGPT sign-in; `model/list` must then advertise the exact model and Ultra effort. The status response exposes `authenticated` and `ready` separately. **Signed in to ChatGPT · Model ready** enables eligible workflows; it does not claim a model turn has completed or Azure/ADO is connected. Standard speed is requested explicitly and checked on the effective thread when a review starts.

If sign-in succeeds but the catalog is missing the model, update the selected Codex installation or configure `Portal.CodexPath`, then disconnect/reconnect (restart after configuration changes). Check account availability if the current runtime still lacks the model. Refresh readiness retries catalog verification without another sign-in. The portal never substitutes another model or reasoning level. Rebuilding through `Stop-Portal.ps1` clears that portal session's credentials, so reconnect afterward; desktop credentials are not copied.

**Model policy updated 4 October 2026.** The provider selection is fixed in `AgentPolicy`: model **`gpt-6.1-sol`**, reasoning **`ultra`**, service tier **`default`** (Standard). The isolated CLI configuration, AHP model advertisement, status API and UI use this shared policy. Both thread creation and each turn specify the tier; the turn also sets `serviceTierForTurn`. Readiness checks the authenticated account's model catalog for the exact model and Ultra effort. Optional catalog speed tiers need not list Standard: the explicit `default` setting must still be accepted in the effective thread before inference. Unavailable or substituted settings block execution. There is no Fast/priority, alternative-model, lower-reasoning or API-key fallback.

**17 October 2026 — complete tool-result delivery:** isolated startup and thread configuration set `tool_output_token_limit = 16384`. The host verifies the effective configuration before inference; a missing or different value blocks the workflow. This token budget is separate from application byte limits and does not prove that every large provider result was fully read. Bicep composition instead serves complete frozen lookups of at most 12,000 UTF-8 bytes and requires delivery of every selected contract. Binary/policy changes require governed restart; changing guidance alone cannot activate them. See [the dated end-to-end evidence](avm-end-to-end-validation.md).

The historical Codex 0.160.0 inspection accepted advertised reasoning strings and listed Ultra for GPT-6.1 Sol. This is a Codex app-server integration, not a claim that the Responses API accepts `ultra`. The model's local filesystem sandbox and disabled delegation remain in place. Scoped reviews retain read-only evidence tools; the explicit orchestration workflow uses the host broker for accepted reads and approved plans. See [Codex app-server](https://developers.openai.com/codex/app-server). Live authenticated model acceptance remains a manual check; historical validation receipts describe the policy tested at their recorded date.

After rebuilding or repackaging, restart the portal to create an isolated agent session with the new policy. The source checkout's [fresh-restart script](portal-fresh-restart.md) can revalidate its protected Codex authentication handoff; ordinary restart requires reconnecting. Existing processes and previously built distribution ZIPs do not update themselves. Verify that the agent page and saved review receipt show **GPT-6.1 Sol / Ultra / Standard**.

## Method and protocol flow

The following sequence describes the nine scoped reviews/drafts. The [provider orchestration sequence](provider-mcp.md#agent-and-ahp-orchestration) adds catalog discovery and exact plan execution through the same host.

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
    UI->>Host: AHP dispatchAction for selected typed workflow
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
| `AgentHostChannel.Handle` | Cookie/origin/CSRF-bound `/api/agent/ahp` WebSocket. Implements `initialize`, `createSession`, snapshot `subscribe` and bounded `dispatchAction` for typed workflow/provider/plan execution; channels cannot cross browser connections. |
| `AgentWorkflows.Status` | Checks Codex account/model readiness separately from Azure/ADO audience availability. GET/readiness creates no Codex process and performs no inference. |
| `AgentWorkflows.Connect` | Starts one isolated native Codex process for this browser session and requests the official ChatGPT login flow. |
| `AgentWorkflows.Run` | Rechecks readiness and registered workflow scope; freezes selected evidence or MCP task/provider metadata, invokes the agent and saves the result. Provider tools are attached only to `mcp-orchestration`. One active workflow per browser; five-minute run limit. |
| `AzureDiscovery.ResourceGroups` / `Discover` | Uses the attached Azure identity for fixed ARM GET collections; missing/denied/partial coverage is distinguished. Selected-group resources cannot escape that group. Unsuccessful responses expose only an allowlisted canonical ARM error code, when safely readable, alongside the HTTP failure. |
| `AdoGateway.Preview` | Reuses the guarded run/artifact reader; verifies workload/environment/region matching before model review. |
| `AgentMcpBridge.Create` / `Call` | Official MCP SDK client/server over local pipes supplies `platform_evidence` and `platform_skill`. Other scoped reviews use zero-argument reads and a 12-call budget. Composition retains the same two tool names with frozen evidence selectors and 32 calls. `EnableProviders` adds catalog/call/plan/run callbacks only for orchestration, with a 48-call budget. Unknown tools/namespaces and revoked sessions stop execution. |
| `BicepEvidenceView` | Freezes a compact context index, complete module contracts, approved settings and observed resource/network pages. Only successful MCP deliveries count toward proposal coverage; unread selected contracts, settings or resource IDs block emission. |
| `McpProviderBroker` / provider transports | Own session-bound provider credentials/catalogs, classify effects, bind immutable plans to identity/catalog/connection, require separate human approval for non-read effects, execute ordered calls and retain private receipts. |
| `CodexAgentRuntime.Review` | Uses explicit skill input, disables inherited features and actual configured MCP entries, then checks the complete paginated thread capability inventory. Only disabled registrations with zero tools/resources/templates are permitted. Starts a read-only/no-approval thread, checks settings, handles broker callbacks and requires terminal completion. Both evidence tools must have been read. |

**AHP scope:** the bounded `workflow-orchestration` profile is based on AHP 0.9.0; full general-purpose host conformance is not claimed. Root and session snapshots include provider readiness; top-level session `customizations` includes `mcpServer` records with connection metadata and state. Session-bound `dispatchAction` supports `platform/workflowRun`, `platform/mcpConnect`, `platform/mcpDisconnect`, `platform/mcpPlan` and `platform/mcpRun`, calling the same typed application handlers. The UI dispatches selected workflows through AHP; HTTP fallback is used only when no AHP channel was available before dispatch. Failure or timeout after dispatch never triggers automatic replay. Human approval is available only through `POST /api/mcp/plans/{id}/approve`, with the browser session and exact digest. Delegated sign-in remains in portal Connections, with no generic OAuth forwarding. There is no AHP/model approval action, arbitrary chat dispatch, terminal/filesystem exposure, generic MCP App side-channel or continuous multi-client replay synchronization. Codex inference uses native app-server stdio. AHP and Codex app-server are different protocols. No PowerMCP host or its fixed model settings were modified. See [Microsoft AHP](https://github.com/microsoft/agent-host-protocol).

## Identity, evidence and failure behavior

- Azure/ADO authentication uses the per-browser MSAL cache or the isolated local Azure CLI cache described in [portal authentication](local-portal.md#configure-microsoft-browser-sign-in). Azure MCP specifically requires isolated CLI mode. GitHub MCP resolves credentials from the verified attached CLI identity. Provider credentials stay in the host or dedicated provider environment, never Codex, process arguments or prompts. **Disconnect Microsoft** preserves the separate Codex connection while invalidating Microsoft evidence/review tickets and identity-bound provider plans. Selected metadata goes to Codex only on explicit Run, so normal Codex usage limits apply.
- Each Codex process has a fresh ignored home under `.local/portal/agents/<id>`, restricted to the current Windows user and SYSTEM. Existing desktop credentials/configuration are not copied. Codex stores its login cache there while connected. Disconnect removes that runtime's `auth.json`; ordinary stop verifies owned processes and clears it. The explicit [fresh-restart script](portal-fresh-restart.md) can transfer only the portal-owned authentication file through a protected, one-browser handoff, then revalidate account/model readiness in a newly configured home. Evidence and approvals always start fresh. A crash or forced OS shutdown can leave ignored local files; protect this folder as credentials and remove stale session directories deliberately when no process uses them.
- API-key and Azure credential environment variables are removed from the child. Shell, web search, hooks, plugins, memories and delegated agents are disabled; read-only sandbox settings are checked before inference. Unknown Codex server requests are rejected. The trusted local CLI/OS account remains part of the trust boundary.
- The full frozen context is capped at 256 KiB before inference; oversized input is rejected. Composition retains that full context in `evidence.json` while delivering a bounded index, contracts, settings and pages through `platform_evidence`; it does not send the entire module catalog in one model tool result. Unread network pages remain unknown. Azure collections retain their existing time/page/resource limits. A selected resource group's visibility must be verified before analysis.
- Each run writes `evidence.json` (after successful collection), `review.md` (only on completed review) and `receipt.json` beneath ignored `artifacts/portal-agents/<id>`. Receipts record requested/accepted model settings, skill/evidence hashes, tool calls and terminal state, but no tokens or raw provider errors. Composition also records `compositionEvidence`: index delivery, complete contract/setting hashes, hashed observed resource IDs and network page coverage. They are local evidence, not tamper-proof audit storage or proof of the server's billed service tier.
- An in-flight inference failure, cancellation or timeout retains a failure receipt, stops the owned inference process and requires reconnect. Input/collection failures, configuration preflight failures and completed model output rejected by validation preserve valid authentication for correction. Disabled zero-capability MCP registration metadata does not itself fail preflight; active or unverified tools/resources/templates do. A completed turn without both required evidence/skill callbacks is rejected. Disconnect revokes further evidence reads. No failed or partial collection is converted to creation approval.
- Model output is displayed with `textContent`. Markdown remains text-only. Accepted restricted Mermaid is converted to an evidence-validated graph and escaped image-only SVG; unknown relationships and executable/unsupported syntax are rejected. Existing observed/proposed/Preview views remain separately labeled. See [network diagram contract](network-discovery-and-diagrams.md). Semantic correctness of model prose is not guaranteed. Orchestration proposals can execute only through broker validation and exact approval where required; prose itself is not authorization.

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

The native handshake test is explicitly skipped unless opted in. Before production use, complete browser consent/cancel/logout tests, one synthetic GPT-6.1 Sol review at Ultra/Standard, one scoped Azure visualization and one ADO Preview review. Verify denied/partial coverage and cancellation against those real providers; inspect saved receipts. These remain acceptance gates.

Additional domain adapters still need their own scope, ownership, permission and verification contracts: saved discovery coverage, policy/RBAC explanation, dated cost review and Resource Graph scale optimization are examples. Full AHP chat/action synchronization and qualified domain-specific remediation remain separate enhancements. The connected-provider broker supplies general tools behind explicit connection and exact plan gates; direct calls do not automatically use the workload target registry or domain Preview checks. Authentication grants no new service roles. Complete [provider live acceptance](provider-mcp.md#live-acceptance) separately from the historical review checks above.
