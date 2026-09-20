# Workload Log Analytics

Creates a PerGB2018 workspace with 30-day retention. Inputs: `name`, `location`, `tags`. Output: workspace `id`. The Event flow prerequisite composition calls it only for a missing standard workspace or one already managed by the workload stack. Existing selected workspaces are referenced without changing their settings.

Log ingestion and other applicable usage are billable. This module does not create alerts, action groups, a daily cap or RBAC assignments. See [prerequisite resolution](../../../docs/prerequisite-resolution.md).
