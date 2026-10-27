# Foundation test execution receipts

`scripts/Test-Foundation.ps1` now observes source inputs immediately before and
after its actual locked restore and test commands. It binds those observations,
the requested executable bytes and exact arguments, fresh TRX bytes and derived
outcomes into a completed local execution receipt. This improves PS-02 evidence
over capturing an unrelated source manifest after a test run.

The observer runs under the same trusted Windows owner as the source and tests.
It is **not independent attestation**, a production admission, a complete
dependency or execution-environment inventory, or a no-network attestation. A
privileged owner can rewrite the observer, tool, source and evidence. Protect a
reviewed receipt digest independently when using it later.

## Run and inspect

```powershell
$run = .\scripts\Test-Foundation.ps1 `
    -DotNetPath 'C:\IT\Package\dotnet\dotnet.exe'

. .\scripts\FoundationTestExecutionEvidence.ps1
Test-FTEExecutionReceipt -RunDirectory $run.runDirectory `
    -ExpectedReceiptSha256 $run.receiptSha256
```

An explicit executable path is preferable. A command name resolves to the first
native application found by PowerShell; the absolute selected path and SHA256 are
recorded. This identifies the requested file, not its vendor, signing trust, SDK
version or the complete code it loads. A retained original read handle denies
normal writes/replacement of that executable through restore/test completion.
Each child operation rechecks the same digest. No synthetic executor, test filter,
outcome injection or alternate project option is exposed by the production runner.

## Bounded process and output lifetime

The runner reuses `FoundationOwnedBuildNative.cs`: it creates each process suspended,
assigns it to an owned Windows Job Object before resuming, and captures only its
explicit standard streams. A successful operation requires the original process
to exit successfully, zero active job descendants and completed bounded captures.
Parent exit alone does not finish an operation while a child still holds a pipe.

The restore deadline remains **300 seconds**; the combined build/test command
remains **900 seconds**. Each output channel is bounded to **4 MiB**. Test operations
explicitly allow at most **64 active / 16,384 cumulative processes** to accommodate
serial native fixtures across the complete suite. The separate owned-build
launcher's defaults remain **16 active / 128 cumulative processes**. These are
local containment ceilings, not measured customer capacity or production limits.

On nonzero exit, timeout, cancellation or output overflow, the runner stops only
its held job and waits for bounded descendant/capture shutdown. Available bounded
output and a sanitized failure code are retained before rejecting the run. It
never searches for a PID to kill, enlarges the test deadline, or turns an incomplete
run into a successful result. Cleanup uncertainty remains a rejection.

Per-command `restore.process.json` and `test.process.json` sidecars record the
helper digest, original process creation time, observed process counts, configured
limits and stop/failure observations. They are supplemental local diagnostics;
the existing v1 completed-receipt contract and its false attestation/cleanup claims
remain unchanged. Older records do not acquire these observations retroactively.

Each invocation creates a fresh owner-only directory atomically under:

```text
artifacts/foundation-test-executions/<id>/
  started.json
  source-before.json
  restore.stdout.txt
  restore.stderr.txt
  restore.process.json
  test.stdout.txt
  test.stderr.txt
  test.process.json
  source-after.json
  build/
  results/foundation.trx
  results/summary.json
  completed.json               final successful commit marker
  # rejected.json              present on a handled rejection instead
```

The runner no longer overwrites `artifacts/foundation-build` or
`artifacts/test-results/foundation`. Historical receipts stay in place. Consumers
must use the returned `runDirectory`, `resultsDirectory`, `receipt` and
`receiptSha256`, rather than assuming the old fixed paths describe the latest
execution. An incomplete directory or `started.json` alone never means success.
Failure retains evidence and throws. Unknown/native errors use a bounded generic
rejection code; available raw process output remains in the protected run directory.
An interruption before observation/persistence can still leave incomplete artifacts
or no final receipt. Neither partial diagnostics nor a process sidecar establishes
a completed test verdict.

An observed nonzero build/test exit remains `TestExecutionNonzeroExit` even when
the command never creates a TRX. Valid failed-result metadata is retained when
available; missing or partial result files cannot mask that observed exit. Zero-exit
runs still require the complete strict TRX/source/summary checks before success.

The Foundation test assembly caps parallel collections at four to bound native
filesystem/crypto/in-process host contention. Framework ordering and the existing
nonparallel capacity collection stay unchanged; explicit races inside tests still
run concurrently. This is a local test resource policy, not an application service
limit or production capacity claim. The full profile still requires actual fresh
qualification within the same deadlines.

## Clean old compiler intermediates after success

GitHub commits, reviewed tags and Releases define revision history. These local
test records and cleanup logs are evidence, not a second product version system.
After successful full execution, the runner now attempts conservative compiler
housekeeping. `-SkipBuildCleanup` opts out. Cleanup occurs after the test verdict
is committed; a refused or failed cleanup does not turn a successful test into a
rejected run. The returned `buildCleanup` describes the attempt, and a completed
attempt writes `build-cleanup.json` inside that same protected run.

For a separate review, preview is the default; `-Apply` enables deletion and
`-WhatIf` previews it even with `-Apply`:

```powershell
.\scripts\Remove-OldBuildIntermediates.ps1 `
    -RunDirectory $run.runDirectory -CompletedSha256 $run.receiptSha256
```

The helper requires an accepted full, unfiltered execution from this repository
within the past 24 hours and an exact current source/HEAD match. It only considers
ordinary-check `artifacts/<check>/build/obj` or `.build/obj`, optionally under a
32-hex run directory. For legacy unregistered output it retains at least the newest
two eligible build trees, all activity within the past hour, explicit
`-KeepBuildDirectories`, and any build referenced by the current Windows process
inventory. Unavailable process inspection still refuses cleanup of these trees.
It rechecks qualification, processes and the tree immediately before deletion.

[Prospective owned builds](foundation-owned-build-cleanup.md), started by the fixed
`Build-FoundationOwned.ps1` launcher, have a separate policy. Completion requires
an actual Windows Job Object with zero remaining descendants and a successful
original process, unchanged source inputs, pinned output and a private exclusive
generation guard. After current full-test qualification, cleanup can remove older
proven-completed owned `obj` trees without a one-hour delay, retaining the newest
two owned builds separately. It holds the existing guard and captured original
file handles through deletion. No global process-visibility bypass is granted to
legacy trees and no earlier tree can be adopted into this protocol.

All `bin` outputs, complete test-execution directories, packages, commercial and
release evidence, published/installed/preview generations, runtime/cache/storage,
credential/key/backup families and `.local` remain outside the deletion scope.
Only recognized .NET SDK project layouts and a bounded generated-file allowlist
qualify; unexpected files or layouts are preserved. Linked paths, foreign-owned
files, protected data/key/evidence names inside the candidate, operational custody
markers in the tree or its ancestors, oversized trees and changed trees are refused. Unrelated sibling check-family names do not establish ancestor custody. Both final evidence and
rejected runs remain. Except for the known Idle/System kernel identities, every
process must expose executable and command-line metadata for legacy cleanup; an
opaque record preserves legacy output. Absolute and root-relative build references
are retained when visibility is available. Owned cleanup never falls back to
recursive path deletion if its file leases, captured names or completion bindings
fail; registered build consumers must hold the generation guard throughout use.
An inactive compiler intermediate is rebuildable; a database or key is not treated
as an intermediate. The process observations and metadata comparisons are local
cooperating-process checks, not continuous fencing against a concurrent owner or
administrator. An OS deletion failure can leave a partially removed `obj` tree;
the result records `RemovalUncertain`, counts only completely removed trees, and
does not claim physical disk-space reclamation.

## Captured source identity

The helper inventories all paths reported by local
`git ls-files --cached --others --exclude-standard --eol -z`, including docs,
skills/instructions, vendor content, project and lock files, and
**`MANIFEST.sha256`**. The latter is an input to model skill pinning, so it is
included even though the separate source-manifest generator excludes itself.
The evidence files live in ignored artifacts and create no source self-reference.
Ignored files, Git internals/global configuration, external imports, package
caches, SDK/runtime closure, environment values and files outside this checkout
are not inventoried.

Every source record contains its relative path, exact byte count and SHA256,
effective Git attributes, governed byte count/SHA256 and normalization flag. The
governed digest uses exactly the manifest generator's rule: when Git reports
`w/crlf` or `w/mixed`, an `eol=lf` rule and no `attr/-text`, replace only each
CRLF byte pair with LF. Latin1 is used as a lossless byte mapping; no encoding,
BOM, Unicode, lone-CR or trailing-newline normalization occurs. Binary/`-text`
files and other EOL rules retain exact bytes. Both raw and governed identities
are preserved. A CRLF-only edit can keep the governed digest while changing the
raw digest; that change still rejects this execution's stability claim.

Paths are sorted ordinally and checked for Windows aliases, case collisions,
traversal, device segments and reparse points. Both inventories also record HEAD.
Successful completion requires exact equality of the serialized before/after
inventories. `sourceInputsUnchanged` therefore means **matching observations at
those two boundaries**. It cannot detect a transient edit restored between
observations, prove all compilation reads saw one atomic snapshot, or prove a
tool consumed every captured input. This is deliberately broader than a project
dependency graph, while remaining incomplete as an execution closure.

## Commands and result admission

The fixed profile restores
`tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj` with
locked mode and the fresh build directory, then runs its Release tests without
restore, with one build node, shared compilation disabled and the fixed
`foundation.trx` logger in the fresh results directory. Command observations
record executable/digest, exact argument array, original process ID, start/end,
exit code and output artifact hashes. The verifier binds `started.json`, stage
order, same tool, exact project/no-filter arguments, output paths and summaries.

TRX XML is read with DTDs prohibited and no resolver. It must have the expected
namespace, one summary/times/results/definitions/entries contract, bounded counts,
valid semantically unique test/execution GUIDs, linked definitions/entries and
recognized outcomes. Summary counts are cross-checked against individual
outcomes. Skips are derived from `NotExecuted` results even when a TRX writer
incorrectly leaves its summary skip counter at zero. Times must fit the observed
test process, allowing two seconds at each boundary for timestamp precision.

Nonzero restore/test exits, failed tests, skipped tests, absent/stale/invalid TRX,
source set/content/attribute changes or contradictory claims reject completion.
`Test-FTEExecutionReceipt` requires the reviewed completed-receipt hash and
rechecks bound source/output/TRX/summary bytes and the fixed execution contract.
It rejects a rejected or partial run and prevents a filtered or re-pinned
contradictory receipt from being treated as this complete profile. Its hash
validation does not make the underlying observer independent.

## Bounds and remaining assurance

Current bounds are 30,000 source entries, 16 MiB per source file, 128 MiB cumulative
raw source, 1,024-character relative source paths, 16 MiB JSON/log artifacts,
32 MiB TRX, 100,000 test results and 64 MiB requested executable files. Native
stdout/stderr are each capped at 4,194,304 characters. Git observations have a
30-second deadline, restore five minutes and tests fifteen minutes. A failed or
expired observation terminates the original process handle and waits at most
five further seconds. The observer does not reopen a process by PID to kill it.

Descendant-process cleanup is not attested: tests that create their own workers
remain responsible for their existing cleanup. Parent environment settings are
inherited by the build as before; their values are not captured. Locked restore
and its vulnerability audit can contact configured feeds. The intended test
profile uses mocked provider/model traffic; this observer does not enforce a
network sandbox. The receipt therefore keeps `independentAttestation`,
`completeDependencyClosureVerified`, `environmentAttested`, `noNetworkAttested`,
`descendantProcessCleanupVerified`, `signed`, `productionReady` and
`publicationAuthorized` false.

## Focused qualification

```powershell
.\tests\release\foundation-test-execution.tests.ps1 `
    -DotNetPath 'C:\IT\Package\dotnet\dotnet.exe' `
    -SdkPath 'C:\IT\Package\dotnet\sdk\10.0.300'
```

The fixtures compile a constant stand-in using existing .NET 10.0.8 reference
and current-architecture apphost packs, without restore. They execute the actual
fixed PowerShell runner in owned temporary Git checkouts, proving complete and
rejected paths, source/manifest add/change/remove detection, old TRX preservation,
real failed/skipped/nonzero outcomes, stale and case-aliased duplicate result
rejection, exact command/tool/claim bindings, pinned TRX tamper detection, LF/raw
identity rules, incomplete receipts and bounded native interruption/output cleanup.
The retained fixture summary explicitly states that it did not execute the real
Foundation suite. A later actual execution is separate evidence and should occur
after all intended source edits and source-manifest regeneration are complete.

On 24 October 2026 the focused profile passed **77 checks**. Its retained receipt
is `artifacts/foundation-test-execution-tests/4984d2ad26dd4adcad66a5ff719b26b8/summary.json`.
That receipt explicitly has `actualFoundationSuiteExecuted: false` and
`restorePerformed: false`; its passing stand-in results must not be counted as
application regression acceptance.
