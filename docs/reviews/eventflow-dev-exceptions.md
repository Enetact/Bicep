# Event flow dev configuration and exception decision

Recorded: 19 September 2026 (America/Chicago). Scope: `eventflow/dev` in subscription `f4f2eafe-2512-4c2f-9b5b-c88f6767e778`, resource group `rg-eventflow-dev`, service connection `SC-AZ-A-Bicep`.

Authorization basis: the repository operator explicitly requested generation of missing dev settings, supplied the identity below, and authorized configuring both named exceptions in the current task. This record documents that dev authorization. Separate enterprise security-board approval and live Azure validation have not been established.

The HTTPS review references in `main.dev.bicepparam` point to the two sections of this checked-in document on GitHub `main`. Merge this document and the parameter changes together before queuing Preview from `main`; the links become available there after that merge. This is the review record itself, not an invented external ticket.

## Development settings

| Setting | Value | Provenance |
|---|---|---|
| Owner tag | `enetact-dev` | Generated dev ownership label under the operator's instruction; replace if a formal team name is assigned. |
| Cost-center tag | `dev-poc` | Generated dev allocation label, not a verified finance-system cost-center code. |
| Deployment principal object ID | `2b7a2791-e7d6-4181-9db3-1bee486236d0` | Supplied by the operator for the service connection's Entra service principal. Used by Bicep role assignments. |
| Application/client ID | `4749bb42-f74e-48fd-aa30-82bcec17a483` | Supplied for identity correlation only. Do not use as the role-assignment principal object ID. |

The IDs are identifiers, not credentials. Their tenant relationship and effective Azure permissions still require live preflight; this change does not grant access to the service connection. Generated tags do not create a spending cap or imply the resources are free.

## Trusted-service delivery

Decision: approved for this dev workload under the authorization above.

The event bridge storage account permits Event Grid delivery using the topic's system-assigned managed identity. Its public network endpoint is enabled with firewall default `Deny` and trusted-service bypass `AzureServices`; it has no IP or VNet allow rules. Shared-key access and anonymous blob access remain disabled. Queue delivery and dead-letter writes use scoped role assignments. Private endpoints serve the workflow and private agent.

This exception permits the trusted Azure service path, which is broader than access solely through this workload's private endpoints. It does not approve anonymous access or an allow-all firewall. The repository's change checks validate the exact event-storage Create shape; arbitrary public storage and security/topology modifications still fail their existing review gates. Microsoft documents the system-assigned identity and trusted-service requirement for firewalled destinations in [Event Grid managed-identity delivery](https://learn.microsoft.com/en-us/azure/event-grid/deliver-events-using-managed-identity).

## Runtime storage credentials

Decision: approved for this dev workload under the authorization above.

The current Windows Logic App Standard module uses runtime-storage connection strings for `AzureWebJobsStorage` and `WEBSITE_CONTENTAZUREFILECONNECTIONSTRING`. ARM resolves the storage key with `listKeys()` during deployment; no key is committed as a parameter. The separate runtime account permits shared-key authentication while disabling public network access, denying the firewall by default, and using no trusted-service bypass. Blob, Queue, Table and File use private endpoints. The app and SCM ingress are private, and the content share uses VNet access.

Account keys have broad account authority. Anyone able to read the relevant app settings or list storage keys must therefore be treated as privileged. Restrict those permissions, preserve evidence redaction, and coordinate key rotation with runtime connection-string updates and restart/revalidation. This decision is for the current module's credential model; identity-only alternatives require their own compatibility review. Microsoft describes the runtime settings in [Standard Logic Apps app and host settings](https://learn.microsoft.com/en-us/azure/logic-apps/edit-app-settings-host-settings).

## Boundaries and re-review

These decisions apply only to dev and synthetic test traffic. Review again before production, sensitive-data use, changing storage authentication/firewall rules, changing identity/scope, or expanding the trusted-service path. QA, UAT and production require their own values and authorization. Revoking either dev exception means setting its `approved` flag to `false`, updating this record and regenerating the source manifest; that blocks later validation and does not undo any prior deployment.

The target's deployment enablement, ADO environment checks, stack ownership, What-If/drift gates, private connectivity and runtime acceptance remain separate requirements. A successful local configuration check does not establish a successful Azure What-If or deployment.
