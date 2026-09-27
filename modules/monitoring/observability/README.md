# Workbook and heartbeat alert

Owns a workbook and scheduled query alert only. Requires existing workspaceId and approved nonempty actionGroupIds; does not change shared workspace networking/retention, attach arbitrary diagnostic settings or invent notification recipients. Heartbeat must already be ingested. Detects formerly reporting computers, not never-seen inventory. Outputs workbookId/alertId. Synthetic alert delivery remains a separate live acceptance gate.

See [module source](main.bicep) for the exact interface and [product onboarding](../../../docs/workload-onboarding.md) for composition, tests and acceptance. Local modules are compiled into versioned Template Specs; no registry is required. No module independently grants deployment authorization.
