# AVM source generation through AHP and Codex

[Wiki home](README.md) · [Module library](avm-module-library.md) · [Source drafting](bicep-source-drafts.md) · [Provider tools](provider-mcp.md)

On **17 October 2026** (18 October UTC), the rebuilt primary portal completed real AHP → Codex → scoped MCP research and source-generation runs. The accepted composition contains six pinned AVM wrappers, a Log Analytics workspace and all seven requested typed child collections. The actual generated source passed offline compilation and negative-input checks. **No Azure deployment, resource change, pipeline submission, target enablement or new permission grant was performed.** This qualifies the source-generation path, not the resulting infrastructure for deployment.

## Verified flow and evidence

| Stage | Actual result | Saved evidence, relative to the repository |
|---|---|---|
| Guarded activation | Primary PID 27724 → 23784; fresh Release assembly `e9058847442f382a4896b81232280ca622132c222314fa57376dfdd8cbe1eb14` | `artifacts/avm-agent-e2e/activation-fixed.json` |
| Existing sign-ins | Microsoft Azure/ADO, GitHub and Codex restored; all five catalogs ready, 657 tools; one-time checkpoint consumed | `artifacts/avm-agent-e2e/resume-fixed.json` |
| Fresh network evidence | Browser-owned snapshot `c1f62f1dfd5b436bb2393eafa1c4869f`, Partial; Private DNS failed with HTTP 400 and remained unknown | `artifacts/portal-network/c1f62f1dfd5b436bb2393eafa1c4869f/report.json` |
| Real provider research | Run `0a456becc7a44789bb9e01dfe53a66c3`, Completed over AHP; 14 successful callbacks; ten exact read plans, each with one successful step and no approval | `artifacts/avm-agent-e2e/research-fixed-audit.json` and the run's `receipt.json` |
| Real composition | Run `19966c45ec7a499a98e5fb206de8f58a`, Completed over AHP; ten successful callbacks, complete seven-contract coverage and the complete single network page | `artifacts/portal-agents/19966c45ec7a499a98e5fb206de8f58a/receipt.json` |
| Actual emitted directory | All seven nodes, seven child collections, workspace output edge and 31-file dependency/license/provenance closure verified | `artifacts/avm-composition-acceptance/91d2dbcbbfb44dc8a5e67c3ec87affcf/results.json` |
| Offline review archive | Exact bytes of all 31 emitted files repackaged and verified independently in ZIP mode | `artifacts/avm-agent-e2e/complete-avm-review.zip`, its `.provenance.json`, and `artifacts/avm-composition-acceptance/776208497dbe4d1ea3ebf572d069ae0d/results.json` |

Both model runs used native Codex 0.163.0, `gpt-6.1-sol`, Ultra reasoning and the configured default/Standard service tier. Receipts establish requested/accepted settings, not independent billing evidence. The research applied the complete generated provider guidance and adapter instructions; the separate composition applied `platform-bicep-composition`. Skill and frozen-evidence hashes were checked against the saved receipts.

The provider research performed Bicep best-practice guidance, six exact `get_file_references` calls, two Learn searches and one official Learn page fetch. It made no Azure, ADO or GitHub tool calls. Catalog readiness for those three providers does not establish acceptance of all their operations or additional permissions. Bicep's native reference results listed only each wrapper and the root compiler configuration; they did not establish the local ARM dependency closure. The host's source preflight protects those omitted inputs, and the source verifier independently checks the packaged closure.

The first resume request restored Microsoft but did not finish the remaining connections. Reopening the protected handoff in the retained browser completed all checks without a new OAuth sign-in. Its final sanitized result has no errors. No credential values, browser cookies or resume capability belong in the test artifacts.

## Parameterized composition

| Node | Module/version | Explicit typed child inputs |
|---|---|---|
| `identity` | User-assigned identity, AVM 0.6.0 | `federatedIdentityCredentials` |
| `registry` | Container Registry, AVM 0.13.1 | `replications` |
| `config` | App Configuration, AVM 0.10.1 | `keyValues`, `replicaLocations` |
| `telemetry` | Application Insights, AVM 0.8.0 | None; `workspaceId` references `workspace.id` |
| `nsg` | Network security group, AVM 0.5.4 | `securityRules` |
| `routes` | Route table, AVM 0.5.0 | `routes`, `applianceRoutes` |
| `workspace` | Reviewed native Log Analytics module | None |

Every location binds to the approved `location` setting. Names, tags and the seven child collections remain required `Input` bindings; other optional parameters retain contract defaults. The generated files expose **21 unresolved main inputs plus three subscription-stack settings**. The verifier checks actual compiled parameter forwarding, the workspace reference and dependency, unconditional same-resource-group module execution, and the stack's explicit resource-group ownership.

Using pinned Bicep **0.48.1** with `--no-restore`, each directory/ZIP verification produced four successful compilations: `main.bicep`, `stack.bicep`, and separate filled main/stack parameter fixtures. Both original unresolved parameter files failed as required, as did two malformed child-parameter fixtures. The filled values are **synthetic test data**, including nonempty values for every child collection. They are separate copies, not model-provided values, discovered defaults or deployable configuration. Original model output and all application-emitted files remain unchanged.

The portal's ZIP button was exercised, but this browser session did not yield a new inspectable downloaded file. The supplied review ZIP is explicitly an offline repackaging of the verified application files; fresh browser-download acceptance is not claimed. Its SHA-256 is `1234ad0a0b945c0deaac9c35645625f9243bde4bf2e5b92672b358906c1a41d5`.

## Issues reproduced and fixed

1. **Large evidence truncated module interfaces.** The original real run `595d263db25f4bba900848fec5cfa297` returned only four of seven requested nodes because the native tool result was truncated. That draft and its downloaded ZIP were preserved and correctly rejected by the new verifier: `artifacts/avm-composition-actual-partial/8f40e4ccc671489ab282c5560c912fc8/results.json`. Composition now reads a compact index and complete bounded module/setting/page lookups, each at most 12,000 UTF-8 bytes. Successful delivery is committed in the receipt. The host rejects unread selected contracts and unobserved setting/resource bindings before emission. The complete frozen audit file remains capped at 256 KiB. The explicit native output budget is checked at 16,384 tokens before inference; this does not make arbitrary provider responses unlimited.
2. **Native reference discovery omitted local ARM inputs.** The host now inspects literal Bicep modules/imports/parameter references and content loaders before native probing, recursively validates local Bicep and self-contained ARM inputs, hashes inspected source/template/configuration inputs, checks auxiliary paths and metadata, and rejects inspected-source drift, external templates, unsupported declarations/readers and unsafe compiler profiles. See [provider scope rules](provider-mcp.md) and [security boundaries](security-and-rbac.md).
3. **Incomplete drafts could pass structural generation.** The reusable acceptance verifier now requires this task's exact seven-node/child-binding profile and inspects compiled wiring and ownership. It rejects the real partial draft and copied malformed sources/archives. Sixteen guard cases passed: `artifacts/avm-composition-guards/157f091c49da42bba1b9c29ea1ddbf8a/results.json`.

Azure diagnostics now project only bounded, recognized canonical `error.code` values, never raw service messages or bodies. The fresh Private DNS failure did not yield a projected canonical code; HTTP 400 remains an unresolved service/access condition. No provider registration, API downgrade or permission change was attempted.

## Regression checks and repeatable verification

- **650 application tests passed, zero failures/skips**, including installed native Codex/Bicep checks, source/configuration guards, scoped MCP delivery/coverage and protected credential tests: `artifacts/test-results/avm-live-e2e/full-live-avm.trx`.
- **16 UI/guidance tests**, pinned five-provider guidance/source checks, **six HTTP boundary checks**, and **nine AHP session/policy checks** passed. HTTP evidence: `artifacts/test-results/avm-live-e2e/http-final/http.json`. These independent sessions performed no login, inference or provider calls.
- Generated provider guidance retains 16 sources, 26,773 bytes and bundle-identity SHA-256 `1e3bd8ac8ed23dde2de17369c9704620bea9354d662311cec085e159a5c435a4`. Its generated file's SHA-256 is `dd699aa6ea9cc689fd527b017a53a7e8e8cad7f62d98241372b06021dd8f33a0`, matching the research receipt's `skillHash`. The compiler lock deliberately distinguishes bundle identity from generated output bytes. The runtime appends adapter guidance after computing the selected file's receipt hash.

From the repository root, these commands inspect copies and compile locally; they do not connect to Azure:

```powershell
./scripts/Test-AvmComposition.ps1 -DraftDirectory ./artifacts/portal-agents/19966c45ec7a499a98e5fb206de8f58a/source-draft
./scripts/Test-AvmComposition.ps1 -ArchivePath ./artifacts/avm-agent-e2e/complete-avm-review.zip
./scripts/Test-AvmCompositionGuards.ps1 -DraftDirectory ./artifacts/portal-agents/19966c45ec7a499a98e5fb206de8f58a/source-draft
```

These scripts preserve their inputs and create new ignored result folders. Native Bicep MCP build/format operations retain their existing effect classification and exact-human-approval boundary; the local compiler tests do not claim those MCP operations were approved or invoked.

Private endpoints/DNS, Entra and data-plane RBAC, federation trust, Azure Monitor Private Link, names/ownership, regional/SKU support, quotas, costs and effective network behavior remain qualification requirements. The source workflow neither registers a workload nor authorizes protected Preview/Deploy. See [workload onboarding](workload-onboarding.md).
