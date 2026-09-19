# Event Grid queue subscription

Resource-group-scoped `main.bicep` inputs: `topicName`, `storageAccountId`, `queueName`, `deadLetterContainer`. Topic, queue/container, identity permissions and approved firewall exception are caller prerequisites. No credential outputs.

Creates system-identity queue delivery and blob dead lettering. Filters `Document.Received` and `/documents/`; TTL 1,440 minutes, maximum attempts 30, queue TTL seven days. This is Basic push, not namespace pull. Caller orders RBAC first; propagation still requires live acceptance. See [workload runbook](../../../workloads/logic-app-event-grid/README.md).
