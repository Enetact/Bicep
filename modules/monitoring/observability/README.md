# Workbook and heartbeat alert

Owns a workbook and scheduled query alert only. Requires existing workspaceId and approved nonempty actionGroupIds; does not change shared workspace networking/retention, attach arbitrary diagnostic settings or invent notification recipients. Heartbeat must already be ingested. Detects formerly reporting computers, not never-seen inventory. Outputs workbookId/alertId. Synthetic alert delivery remains a separate live acceptance gate.

Optional `workbookName` and `alertName` accept explicit names from a reviewed composition. Defaults preserve the existing workbook GUID derived from resource-group ID/name and the `${name}-heartbeat` alert. The new standard-v2 composition passes the immutable instance UUID for its workbook and the calculated alert name; display-name edits do not change that workbook identity. This additive interface is locally compiled, not Azure-accepted or enabled in self-service Deploy yet.

See [module source](main.bicep) for the exact interface and [product onboarding](../../../docs/workload-onboarding.md) for composition, tests and acceptance. Local modules are compiled into versioned Template Specs; no registry is required. No module independently grants deployment authorization.
