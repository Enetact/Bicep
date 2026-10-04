# Private ADLS Gen2 lake

Resource-group leaf module using StorageV2 `2025-01-01`. Inputs: name/location/tags, LRS or ZRS SKU and existing Log Analytics workspace. Outputs: account ID/name.

Creates HNS-enabled identity-only storage, landing/curated containers, private/default-deny network access, TLS 1.2/HTTPS, infrastructure encryption, 14-day blob/container deletion retention and Blob diagnostics. Blob versioning is intentionally absent for HNS compatibility. No keys, data, endpoints, role assignments or Unity Catalog objects are created here.

The [data platform](../../../workloads/private-data-platform/README.md) supplies Blob/DFS endpoints and container-scoped RBAC. Soft deletion is not a full backup/disaster-recovery strategy. Regional SKU support and actual data recovery must be qualified. This module is inventoried but not selectable by generic AI source drafts yet.
