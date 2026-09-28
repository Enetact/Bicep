# Platform recovery rules

[Wiki home](README.md) · [Platform overview](platform-overview.md) · [Status](completion-status.md)

**Implemented locally, 27 September 2026:** fourteen policies, a deterministic offline assessor, workload/operations policy views and pipeline evidence annotations. This is our platform's recovery policy layer. It uses no Azure resource-group rollback-on-error mechanism.

**Execution boundary:** there is no restore executor or recovery pipeline yet. Offline checks consume caller-supplied assertions and cannot verify Azure state, historical artifacts, approval or ownership. Even `ChecksSatisfiedUnverified` always has `canExecute: false`. Do not use this command's exit code or a diagram as an Apply gate. Run-bound before/after collection, retained-release verification and the protected restore path remain [planned](plans/deployment-state-and-recovery.md).

## View the rules

Rebuild/restart Platform Studio using [the existing local setup commands](local-portal.md#run-from-source-in-order) when upgrading an already running portal.

1. Choose **Configure workload**, then expand **Recovery rules for this workload**. Only that product's policy, exclusions and required evidence appear.
2. Open **Pipeline activity → Recovery policies** to select any of the fourteen product/operations policies. This works without Azure, ADO or Codex sign-in and never queues a run.
3. In a newly prepared workload Preview, read **Recovery rules** in its ADO summary and `deployment-preview/recovery-policy.json`. It explicitly says eligibility is not assessed.
4. A new deployment coordinator receipt includes `recovery.policyId`, `mode`, `catalogSha256`, `assessmentStatus`, `canExecute` and `executorRegistered`. A Ready deployment does not imply recoverability. If bundle/target validation fails before the policy is resolved, the failure receipt may have no recovery annotation.

Existing artifacts are not rewritten. The policy snapshot is informational and has a catalog hash; it is outside the existing nine-file deployment input/approval contract. Editing this snapshot cannot authorize execution. Future recovery must revalidate the active rules and the independently retained candidate, rather than trust a historical policy snapshot.

## Modes and rules

| Mode | Workflows | Rule |
|---|---|---|
| ConfigurationRestore | Private Storage Workspace, Key Vault, Observability | Only qualified mutable settings from a previously verified release. Preserve data, credentials, ownership and new resources. |
| ApplicationRestore | Blob copy, Event flow, HTTP Functions API, Service Bus worker | Configuration rules plus exact prior package compatibility and processing/trigger impact review. |
| TagCompensation | External-resource tags | Plan restoration only for owned keys whose current values match the recorded applied values. Unrelated tags stay unchanged. Removal of a newly introduced key is blocked until separately qualified. |
| SourceRevert | Source-owned tags | Return `SourceChangeRequired`; revert reviewed source settings through the normal workload path. |
| ManualRecovery | Network/IPAM, ADO registration | Return `ManualRecoveryRequired`; reconcile durable allocation/resource/definition receipts with the platform owner. |
| NotApplicable | Discovery/diagrams, agent reviews, Bicep drafts | Return `NotApplicable`; no cloud change was made by these workflows. |

The executable assessor requires fourteen common boolean checks for action-capable modes: observed-before capture, qualified baseline, retained artifacts, integrity, same target, complete coverage, ownership, current policy, compatible changes, current credentials, fresh Preview, no drift, protected approval and a verification plan. Application and tag modes each add two domain checks. Missing values, strings such as `"true"` and false values fail the corresponding check.

`Succeeded` and `FailedReconciled` are the only accepted current-outcome assertions. Pending, failed without reconciliation, cancelled and unknown outcomes block another mutation. Every resource needs an explicit definite change within the requested resource group. Duplicate IDs, scope mismatches, missing/empty changes and uncertain impact block assessment. Configuration candidates allow `Modify`/`NoChange` with `MutableConfiguration`; tag candidates allow `RestoreTag`/`NoChange` with `OwnedTagValues`. These declarations still require independent provider/property qualification; this assessor does not inspect ARM property diffs or generate inverse code.

Creation, deletion, detach, resource replacement, allocation release, secret/data restoration and event replay are not generic recovery operations. First deployment lacks a qualified baseline. Adding a stateful resource does not make its automatic deletion an acceptable rollback. A prior release that omits owned resources needs a preservation or separately reviewed retirement plan.

## Run an offline assessment

Requires a source checkout and PowerShell 7.4 or newer; no Azure login or model is needed. The shipped example deliberately lacks evidence and must be Blocked:

```powershell
# From the Bicep repository root. Use a new output folder for every assessment.
pwsh -NoProfile -File ./scripts/Test-RecoveryCandidate.ps1 `
  -CandidatePath ./tests/recovery/candidate.example.json `
  -OutputDirectory ./artifacts/recovery/example-001
# Expected process exit code: 2. Read artifacts/recovery/example-001/recovery-assessment.json.

# Run deterministic regression checks:
./scripts/Test-RecoveryRules.ps1
```

Copy the example into ignored local artifacts when preparing a real review. Set `workflow` to a policy ID from `config/recovery-capabilities.json`, provide the exact owned resource-group scope, current outcome and explicit changes, and record only checks supported by your evidence. Supported check IDs/descriptions are in the catalog; required sets are fixed in `Get-RecoveryCheckIds`. Tag changes are summarized once per resource; the future tag adapter must verify every affected key and construct any compensating patch.

Exit 2 means Blocked; 0 means the report was produced for another assessment status, **not permission to execute**. Invalid input raises an error. Inputs are bounded to 4 MiB/30 JSON levels and 10,000 changes. The receipt contains input/catalog SHA-256 hashes and generated time, not raw secrets or template parameters. Existing receipt files are preserved; choose another output folder instead of overwriting them.

## Source and extension points

| File/method | Responsibility |
|---|---|
| `config/recovery-capabilities.json` | Versioned policy text, exclusions and check descriptions for all fourteen workflows. `executionEnabled` is false. |
| `scripts/recovery-common.ps1` | `Read-RecoveryCatalog` validates fixed workflow/mode mapping; `Get-RecoveryCheckIds` defines mandatory checks; `Get-RecoveryPolicy` creates a policy snapshot; `Get-RecoveryAssessment` returns a non-authorizing rules report. No cloud, model or process execution. |
| `scripts/Test-RecoveryCandidate.ps1` | Bounded local input, consistent candidate hash and create-only report output. |
| `scripts/workload-preview-common.ps1` | Adds Preview policy/summary and deployment receipt annotation. Does not alter existing forward deployment authorization. |
| `Catalog.RecoveryPolicies` / `GET /api/recovery/policies` | Public local display metadata with exact workflow coverage and execution-disabled validation. No user secrets or deployment artifact reads. |
| `wwwroot/recovery.mjs` | Text-only policy rendering and selected-workflow check filtering. No write controls or model dependency. |

The existing package process copies `config/` and web assets, so newly rebuilt Windows packages include policy views. The offline assessment command is a source-checkout tool. No new NuGet/npm dependencies or distribution packages were added in this increment. Existing portal processes/packages must be rebuilt to load the new code.

To add another policy, extend the registered code mapping, catalog, UI coverage and tests together. To enable recovery execution, implement a trusted retained-evidence adapter, fresh Preview, protected executor and post-recovery verification; a config change cannot do that. See R1–R4 in [the recovery plan](plans/deployment-state-and-recovery.md).
