# Reconcile uncertain work during customer offboarding

Implemented locally, 24 October 2026. A human with the current scoped **Operator** grant can inspect bounded uncertain-job metadata and record reconciliation while a cell is **Suspended** or **Offboarding**. This closes the dependency between uncertain effects and completing offboarding without requiring customer reactivation. It extends the existing [durable jobs and lifecycle](foundation-security-operations.md); it does not resume a job, invoke a provider or verify an external effect.

## Authority and state

The signed API uses a separate recovery membership resolver that retains only the current Operator role and intersects it with the human token's Operator role. The customer/cell is fixed by host configuration; project/environment comes from the authorized route. Viewer, Requester, Approver, CustomerAdmin, Auditor, PlatformOperator, SupportDelegate and workload identities receive no recovery authority from their roles. A paid entitlement, model result or caller-selected customer cannot replace this check.

Recovery is restricted to Suspended and Offboarding cells. Active cells keep their ordinary job APIs. Completed offboarding removes operational roles, ending recovery access. Urgent access revocation is rechecked at each operation. The store repeats human, role, lifecycle and exact scope checks in the same SQLite transaction as the read or reconciliation write. Ordinary evidence, plan, enqueue and AHP/model routes remain blocked while inactive.

## API contract

All suffixes below use the existing `/api/foundation/customers/{customerId}/projects/{projectId}/environments/{environmentId}` prefix.

| Suffix | Operation |
|---|---|
| `/recovery/jobs` | GET: up to 100 current OutcomeUnknown cases, oldest first, with `hasMore`. Resolve cases and read again to drain a larger queue. |
| `/recovery/jobs/{id}` | GET: one uncertain or already reconciled case within this exact scope. |
| `/recovery/jobs/{id}/reconcile` | POST: require current `caseSha256`, outcome and nonempty evidence. |

The projection contains job/customer/cell/scope/plan identifiers, the stored plan digest, state, fence, update time, reason code and case digest. It excludes actor identities, plan payloads, connection configuration, provider receipts and credentials. The case digest binds this projection for an optimistic concurrency check; it is not a signature, human approval or proof of the provider's result.

Example POST body, with the case digest copied from the immediately reviewed response:

```json
{
  "caseSha256": "<current-case-sha256>",
  "outcome": "FailedNoEffect",
  "evidence": {
    "reviewedRun": "synthetic-123",
    "finding": "Operator recorded the independently reviewed result"
  }
}
```

Only `Succeeded` and `FailedNoEffect` are accepted, producing `ReconciledSucceeded` or `ReconciledNoEffect`. Unknown body fields, empty/non-object evidence, ambiguous JSON and the existing credential-shaped content rules are rejected. Store evidence is bounded by the existing 64 KiB JSON limit. Changed cases fail the digest check; a repeated submission after reconciliation cannot rewrite the recorded result. Successful reads and reconciliation are audited. The evidence, job state and required audit event commit together; an audit failure rolls back the job change.

This records the authorized operator's evidence assertion. It does not independently confirm a remote run or deployment. Reconciliation does not enqueue a replacement, enable a connection, restore grants, switch activation or change the cell lifecycle. Completing offboarding still requires its separately reviewed administrative plan and refuses any remaining uncertain job.

## Verification and limits

[Recovery tests](../tests/SelfService.Foundation.Tests/FoundationRecoveryReconciliationTests.cs) exercise genuine SQLite transactions and signed synthetic HTTP identities. They cover offboarding completion without reactivation, suspension, current-role revocation, workloads and role-only tokens, foreign scope/customer references, case changes/replay, bounded metadata projection, rejected evidence and audit-fault rollback. Ordinary HTTP/AHP access stays blocked; mock transports receive no request.

No customer UI or model recovery tool is added. The ordinary active-cell reconciliation contract remains separate. Real provider investigation, evidence quality, production custody and administrator tamper resistance require operational acceptance. Local database administrators remain trusted; these digests do not defend against an administrator rewriting the store and audit. No Azure operation, identity grant or live pipeline is performed by this increment.
