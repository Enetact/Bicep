# Customer-cell operational metrics

The foundation API exposes `GET /api/foundation/customers/{customerId}/projects/{projectId}/environments/{environmentId}/operations/metrics` to explicitly assigned `PlatformOperator` callers. A valid signed token, current customer/cell binding and scoped operations membership are required. CustomerAdmin, Auditor, SupportDelegate and ordinary viewers receive no implicit metrics access. The endpoint stays available to eligible operators while normal features are disabled or the customer is suspended. It changes no identity, provider permission or execution policy.

The `platform-studio.cell-metrics.v1` response contains:

| Section | Actual scope |
|---|---|
| `process` | Started/completed/in-flight/peak requests, outcome counters, cumulative latency buckets, duration sum and process uptime. |
| `cell` | Current SQLite schema/lifecycle, job-state counts, outbox backlog, artifact count/bytes, oldest queued age and configured job limits. |
| `workers` | Aggregate registered/live/disabled/stopped/stale counts, most recent heartbeat, active leases and unknown outcomes. |
| `capacity` | Pipeline-plan count, audit-event count and logical export-metadata bytes with their existing admission limits. Physical disk quota enforcement remains false. |

Each API host owns its own bounded in-memory collector. It does not register a static/global collector, start an exporter, write diagnostic files or share a metric object with another host. It records no request path, route values, customer/actor identifiers, headers, credential material, request body, source text, model text, provider result or dynamic labels. The authenticated route identifies the cell; those identifiers are not echoed as metric labels.

Collection wraps application admission, so HTTP rate-limit rejections and handled failures are counted. Requests rejected by the network/server before application middleware are outside this collector. Request cancellation is classified as aborted; otherwise unhandled failures and HTTP 5xx are server failures, 4xx are client rejections, 2xx are successful HTTP responses and remaining status codes are other. An HTTP 200 tool-error envelope still counts as HTTP success; it does not prove a successful tool, pipeline or deployment.

In-flight and peak counters include requests waiting to be rejected by admission. They can therefore exceed the 16 admitted-request limit; they are not an admitted-slot gauge.

Latency buckets are cumulative at 0.01, 0.05, 0.1, 0.5, 1, 2, 5 and 10 seconds, followed by an unbounded bucket with a null upper bound. Durations use the process monotonic clock. A snapshot is internally consistent for process counters; database sections use separately authorized reads and are not one distributed point-in-time snapshot. The metrics request itself is in flight when its response is captured, and completes afterward.

Process counters reset on restart and are explicitly `durable: false`. Durable job/audit/capacity data remains in the customer cell. Metrics do not replace the append-only security audit. No dashboard-side filter is relied upon to enforce isolation. A future collector, exporter, retention service or alerting system needs its own protected cell-specific access and storage boundary.

Metrics and health currently share the host's global request budget. Saturating the 16 active slots can return HTTP 429 for an otherwise authorized monitoring request. There is no separately reserved operations listener or admission lane in this build. Database metric reads use deferred read transactions rather than reserving the SQLite writer lock.

This is local operational visibility. The fixed logical quotas do not enforce disk, WAL, backup, telemetry or runtime-home storage budgets. Request latency on a development machine is not a production SLO, and these counters establish no production capacity, availability, cloud reachability or live-provider acceptance. See [load qualification](foundation-load-qualification.md), [security and operations](foundation-security-operations.md) and [the release gates](plans/platform-studio-commercial-release.md).
