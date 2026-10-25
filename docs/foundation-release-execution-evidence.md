# Bind commercial qualification to Foundation execution evidence

`scripts/Test-CommercialRelease.ps1` optionally consumes the pinned completed
receipt from the fixed Foundation test runner. It verifies the receipt and exact
indexed Foundation TRX, compares the observed test inputs to the current checkout,
and rechecks that checkout at the end of qualification. It preserves the existing
capture-only index schema and behavior when these optional inputs are omitted.

This is stronger local evidence, not independent CI attestation, a complete
dependency/environment inventory, publication approval or a production gate.
The source and evidence owner remains trusted. A paid entitlement or a successful
qualification never grants customer, provider or cloud authority.

## Exact-source qualification

First run the fixed [Foundation test profile](foundation-test-execution-evidence.md)
after intended source edits and manifest regeneration. Keep its returned receipt
path and SHA256. Capture the ordinary index from the actual fresh TRX files:

```powershell
$run = .\scripts\Test-Foundation.ps1 -DotNetPath 'C:\IT\Package\dotnet\dotnet.exe'
$foundationTrx = Join-Path $run.resultsDirectory 'foundation.trx'
.\scripts\Test-CommercialRelease.ps1 `
    -CaptureTestEvidenceIndex 'artifacts/commercial-release/reviewed-index.json' `
    -TrxPath $foundationTrx

.\scripts\Test-CommercialRelease.ps1 `
    -TestEvidenceIndex 'artifacts/commercial-release/reviewed-index.json' `
    -ReceiptPath 'artifacts/commercial-release/qualification.json' `
    -FoundationExecutionReceipt $run.receipt `
    -ExpectedFoundationExecutionReceiptSha256 $run.receiptSha256
```

The explicit receipt/SHA pair selects the stronger check. It requires exactly one
indexed entry named `foundation`, whose path and digest equal that completed
run's `results/foundation.trx`. A copied TRX at another path, unrelated index
entry, failed/partial run, altered receipt, mismatched repository, stale/future
observation or unsupported claim fails closed. The ordinary evidence-age bound
also applies; its default is 24 hours, configurable from 1 through 168 hours.

The final report adds `foundationExecution` only when selected. With unchanged
inputs, `foundationExecutionReceiptVerified` and `exactFinalSourceMatch` are true.
The index's `captureDoesNotAttestTestedSource` remains true: consuming this local
Foundation observer does not independently attest execution or bind a separate
portal test run. The legacy capture-only report remains unchanged when the new
arguments are absent. Receipt/review arguments cannot be mixed into capture mode.

## Narrow review of post-test progress records

Recording accepted results after a test run necessarily changes some status
documents. The stronger check can admit **changed existing files only** from this
fixed allowlist:

- `docs/completion-status.md`
- `docs/plans/platform-studio-commercial-release.md`
- `docs/validation-results.json`
- `docs/validation.md`
- `MANIFEST.sha256`, derived from the current governed source digests

The same HEAD and exact source file set must remain. New/deleted/renamed files,
code, test, configuration, pipeline, vendor, instruction/skill or other guide
changes require a fresh execution. There is no arbitrary Markdown exception,
glob allowlist, source-path override or automatic classification of code changes.
Changing Git attributes is not a progress exception. The current manifest must
match the sorted governed source digests, excluding its own entry as the manifest
generator does; it is still included in the test/current source inventories.

After editing only those progress records and regenerating the manifest, generate
a proposed review with the trusted helper:

```powershell
. .\scripts\FoundationReleaseExecutionEvidence.ps1
$draft = New-FoundationReleaseProgressReview `
    -RepositoryRoot (Get-Location).Path `
    -ExecutionReceiptPath $run.receipt `
    -ExecutionReceiptSha256 $run.receiptSha256
```

The returned fixed-schema object has `reviewState: Pending`. It binds the
execution receipt, original source-inventory digest, current inventory digest,
HEAD and every changed file's full tested/final record: path, exact byte count and
SHA256, governed byte count/SHA256, attributes and normalization flag. It contains
no source contents or credentials. Save it in a new reviewed artifact location,
inspect every change, then explicitly set `reviewState` to
`ReviewedForLocalEvidence`. Preserve `limits` unchanged. Pin the exact reviewed
file SHA256 independently of the qualification call.

```powershell
.\scripts\Test-CommercialRelease.ps1 `
    -TestEvidenceIndex 'artifacts/commercial-release/reviewed-index.json' `
    -ReceiptPath 'artifacts/commercial-release/qualification.json' `
    -FoundationExecutionReceipt $run.receipt `
    -ExpectedFoundationExecutionReceiptSha256 $run.receiptSha256 `
    -ProgressSourceReview 'artifacts/commercial-release/reviewed-progress.json' `
    -ExpectedProgressSourceReviewSha256 '<reviewed-lowercase-sha256>'
```

Recapture the ordinary index after final progress/manifest edits so its existing
source binding matches the final checkout. Generating a proposal does not approve
it; qualification requires the separately supplied reviewed file and pin. A
pending, omitted, partial, duplicate, stale or rebound review is refused. Any
further progress edit changes the final inventory and requires renewed review.

With accepted deltas, `exactFinalSourceMatch` stays **false** and
`reviewedProgressChanges` lists them. The report does not say those document bytes
were tested. `independentReviewVerified` stays false: a local review-state field
and hash are not authenticated proof of an independent human reviewer.

## Assurance and bounds

`FoundationReleaseExecutionEvidence.ps1` reuses the existing pinned execution
verifier. It additionally validates every source record's schema/types/limits,
canonical ordering, semantic path uniqueness, manifest presence, total bytes and
exact file set. Comparisons include raw and governed identities; CRLF-only
changes to an executable input cannot be hidden by LF normalization. Both source
snapshots and the completed receipt remain pinned. A final source/manifest check
rejects drift during qualification. The bound final HEAD, file count and manifest
must also match the outer qualification/index identity, preventing a reviewed
progress change in the intervening window from producing inconsistent bindings.

Matching observations do not detect transient same-owner edit-and-revert or prove
what every compilation read consumed. Ignored files, external imports, SDK/cache
contents and environment/network behavior are not a complete execution closure.
All limits remain false for independent review/attestation, complete dependency
closure, environment attestation, no-network attestation, portal execution binding,
production readiness and publication authorization. Existing commercial, identity,
provider, capacity, signing and clean-machine gates remain open.

The source limits remain 30,000 records, 16 MiB per file, 128 MiB total raw bytes,
1,024-character relative paths and 16 MiB JSON artifacts. Progress reviews are
capped at 64 KiB and five changes. Unknown fields, invalid types, duplicate JSON
keys and unsafe/linked paths are rejected. Evidence and reviews must stay under
the selected checkout's ignored `artifacts/` root.

## Focused verification

```powershell
.\tests\release\foundation-release-execution.tests.ps1
.\tests\release\commercial-release.tests.ps1
```

The new fixtures compile the existing constant owned stand-in offline, then use
the actual fixed runner in a temporary Git checkout. They exercise exact-source
and reviewed-delta qualification, the real CLI pairs, pinned receipt/TRX/review
binding, pending/contradictory reviews, all protected input classes, file-set and
HEAD changes, final-check drift, invalid derived manifests and typed snapshot
tampering. Their summary explicitly excludes actual Foundation regression or
cloud acceptance. A fresh actual Foundation capture is required after integrating
this new helper, test and guide; the older revision 9 execution cannot qualify
these new non-progress source files.

On 24 October 2026 the new focused profile passed **42 checks**, with the retained
receipt at `artifacts/foundation-release-execution-tests/1b68804bc697449a87e02c6fca3f7324/summary.json`.
The unchanged legacy cohort passed **28 contracts** at
`artifacts/commercial-release-contract-tests/abf5e7f28f6541b7a26a1654a6d0333c/results.json`.
These are synthetic contract results, not a new application regression result.
