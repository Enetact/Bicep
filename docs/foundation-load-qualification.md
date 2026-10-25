# Local foundation load and recovery qualification

This profile measures bounded local regression scenarios using the real foundation store, RSA-signed API identities, two dedicated customer cells and the existing independent worker/loopback process fixtures. It does not establish a production SLO, production capacity, live provider acceptance or Azure readiness. All identities, plans, jobs and provider responses are synthetic; no cloud deployment, sign-in, model call or primary-host restart occurs.

Run from the repository root in a normal Windows context with .NET SDK 10 and PowerShell 7.4 or later:

```powershell
./scripts/Test-FoundationLoad.ps1
```

`-DotNetPath` selects the installed SDK executable. The script keeps locked restore and vulnerability auditing enabled, builds under `artifacts/foundation-load-check`, and selects exactly five test cases. It starts only TestServer and explicitly owned hidden worker processes with literal loopback fixtures. An app-container or restricted sandbox can deny SQLite file operations, atomic worker receipts or process ownership inspection; such a run is not acceptance.

## Targets defined before measurement

`platform-studio.local-foundation-targets.v1` is a conservative local regression contract. Both test assertions and the receipt validator fix these values before execution. The script writes `intent.json` before restoring/building/running. A failing result remains failed; changing a target requires an explicit new target version and review, rather than adjusting the result after measurement.

| Scenario | Workload and correctness requirement | Fixed local timing target |
|---|---|---|
| Concurrent queue admission | Four SQLite clients enqueue 160 distinct, separately approved plans; exactly 128 jobs commit and 32 receive `QueueCapacityExceeded`/429. Job/outbox/audit counts prove rejected admissions leave no partial commit. | Enqueue p95 ≤ 5,000 ms; measured flood phase ≤ 60,000 ms. |
| Customer isolation under pressure | Customer A holds one mock dispatch while four SQLite clients enqueue 159 additional approved plans, paced at 2 ms between each client's attempts. Exactly 127 commit and 32 receive backpressure. Customer B makes 40 RSA-signed API reads; all return 200, at least one overlaps A's flood, and every read occurs while A's provider is held. Foreign IDs/scopes fail, and B can enqueue independently after A fills its queue. | B API p95 ≤ 3,000 ms and maximum ≤ 10,000 ms; measured pressure/read phase ≤ 60,000 ms. |
| Competing worker processes | Reuses the existing two-process, six-plan loopback test; every plan is claimed/dispatched once. | Complete test duration ≤ 15,000 ms. |
| Pre-effect process recovery | Reuses the actual worker crash test, two-second synthetic pre-effect lease, stale heartbeat and recovery with a new fence. | Complete test duration ≤ 15,000 ms. |
| Post-effect process recovery | Reuses the actual dispatched-worker crash test; the disposable fixture's lease deadline is accelerated, the job becomes `OutcomeUnknown`, and no POST is replayed. | Complete test duration ≤ 10,000 ms. |

Plan creation, approval and initial fixture provisioning occur before the measured flood/read phases. These plans use the existing durable approval methods; the flood measures the trusted store's admission boundary, rather than asserting HTTP ingress throughput. Signed API read latency includes token creation, real signature validation, authorization, storage and response-body reading; deserialization assertions follow the measurement. The isolation provider is a deliberately held in-process mock behind the shared `FoundationJobRunner`; the separately reused process tests exercise actual loopback HTTP. The complete per-test TRX durations and profile wall time remain in the receipt so setup cost is visible separately.

The tests retain the existing limits, dispatch gates, three-attempt recovery cap and unknown-outcome rules. They do not increase capacity, shorten a production lease, change rate limits, disable audit or introduce another worker harness. CPU/disk contention on this developer machine is part of the observed local result. A single run and forty API samples do not establish a sustained service guarantee.

## Evidence and interpretation

Each invocation creates a new ignored directory at `artifacts/test-results/foundation-load/<run>/` containing:

- `intent.json`: the versioned targets and explicit local-only scope, saved before measurement.
- `foundation-load.trx`: actual outcomes/timings from the five expected test identities, including the two tests' machine-readable measurement lines in their own `StdOut`.
- `summary.json`: acceptance checks, workload counts, raw latency samples, nearest-rank p50/p95/p99, minimum/mean/maximum, per-test timings, restore/build/test/profile elapsed time, source hashes and aggregate machine/runtime facts.

The validator requires the exact five tests, all passing with no skips, exactly one admissible per-test measurement for each new scenario, valid finite samples and every fixed target. It does not scrape the runner's combined console stream, infer missing measurements or report a failed target as accepted. Process recovery timings are single scenario durations from TRX; the receipt does not manufacture a recovery latency distribution from one observation.

The summary contains OS/architecture, logical CPU count, optional aggregate physical memory/processor model, SDK/PowerShell versions and the measured test runtime. It excludes computer/user/domain names, hardware serials/UUIDs, process/customer/actor IDs, source/database/configuration paths, environment contents, credentials and payloads. Optional hardware facts remain null when unavailable. The standard raw TRX may contain framework-generated machine names and source paths; keep it private in the ignored evidence directory and review it before sharing. The derived summary is intentionally narrower.

An accepted summary means this fixed local workload met its regression targets on the reported machine at that time. Production requires representative sustained and burst traffic, realistic customer counts/artifacts, network/provider latency and outages, separate worker identities and resource limits, storage/backup operations, soak/restart testing, deployment topology, security review and explicit production SLO agreement. See [the foundation service](foundation-service.md), [independent workers](foundation-independent-worker.md) and [the customer-isolation architecture](plans/platform-studio-foundations-and-customer-isolation.md).

## Recorded local result — 24 October 2026

The first measured run, `20261024-203054-026de3d7dd5a40d794b20d2358ef20b7`, passed all five tests and every unchanged v1 target. On Windows Arm64, twelve logical processors, 15.61 GiB physical memory and .NET 10.0.8, the flood phase took 1.78 seconds with 161.08 ms enqueue p95. Customer B's forty signed reads all overlapped the other customer's flood and returned 200; API p95 was 10.18 ms and maximum was 10.94 ms. The isolation phase took 2.18 seconds. The competing-process, pre-effect recovery and post-effect unknown/no-replay tests took 3.24, 4.47 and 1.89 seconds respectively. Complete restore/build/test/aggregation time was 66.43 seconds; the TRX test run reported 25 seconds.

Evidence is under the corresponding ignored `artifacts/test-results/foundation-load/` run directory. These results describe one developer machine and one bounded synthetic workload; they establish no production throughput or availability guarantee.

The revision 7/schema-v5 rerun, `20261024-211348-cd3c074fe7ca439baa3503829146a8f4`, passed all five tests and unchanged v1 targets on the same reported Windows Arm64/.NET 10.0.8 profile. The 160-attempt flood accepted 128 and backpressured 32; enqueue p95 was 129.36 ms. All forty other-cell signed reads overlapped pressure and succeeded, with p95 19.47 ms and maximum 25.10 ms. Competing-worker, pre-effect recovery and post-effect no-replay cases took 2.48/3.39/1.62 seconds. Profile elapsed time was 60.75 seconds; these remain local regression measurements.
