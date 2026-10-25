# Entra customer-cell identity proposals

`EntraCellOnboarding` generates a deterministic public setup proposal for one dedicated customer cell and one Entra directory. It reuses `FoundationRoles`, the signed-token validator and current customer membership boundary. The result is an application-definition fragment plus compatible public host identity metadata, with a SHA-256. It contains no Graph request executor, identity registration, consent, role assignment, federation, signing key or downstream Azure/GitHub/ADO permission. Every authorization/acceptance flag remains false.

The [example input](../config/foundation-entra-onboarding.example.json) contains synthetic placeholders. Replace them with reviewed customer/cell identifiers, the actual API application/client ID, tenant ID, separately registered human client IDs, and each worker's exact client ID/service-principal object-ID pair. An application/client ID and an application's directory object ID are different identifiers; this input uses the client ID for the expected v2 audience, and the worker service-principal object ID for `oid` admission. [Microsoft claim-validation guidance](https://learn.microsoft.com/en-us/entra/identity-platform/claims-validation). Use a dedicated API registration per cell. Two customers sharing one directory still need independently admitted cell configuration and scoped memberships.

Supply all nine stable role-definition IDs and the delegated-scope definition ID explicitly. For an existing application, obtain and preserve its reviewed IDs; replacing them can invalidate existing associations. For new definitions, allocate nonzero unique GUIDs through the trusted owner process and review them before registration. The example IDs are not evidence of real directory objects. Duplicate IDs, extra/missing/case-mismatched roles, duplicate clients, overlapping human/worker clients, the API acting as its own caller, unknown JSON fields and credential fields are rejected. Input JSON is limited to 64 KiB and depth eight.

## Generated contract

| Fragment | Proposed behavior |
|---|---|
| `applicationDefinitionFragment` | Single-directory API (`AzureADMyOrg`), `api://<api-client-id>` identifier, v2 access-token format, one administrator-consent delegated scope, role definitions and access-token `idtyp` optional claim. This fragment omits an `appId` write and is not an application-create or PATCH request. |
| `appRoles` | The eight existing human roles allow only `User`; `Worker.Execute` allows only `Application`. Definitions create no assignments. |
| `proposedClientPermissions` | Human callers request only this API's `platform.access` delegated scope. Workers request only this API's `Worker.Execute` role. There is no Graph/Azure/repository permission grant. |
| `servicePrincipalRequirements` | Proposes `appRoleAssignmentRequired=true`; it is unobserved and unapplied. |
| `hostIdentity` | Public customer/cell, API audience, tenant-specific v2 issuer and exact admitted callers. These fields can be merged into a reviewed cell configuration, preserving its database, bootstrap identities, public keys, custody and other controls. This fragment is not a complete runnable host configuration. |
| `claimRequirements` | Documents the existing validator's delegated scope, worker role and app-only `idtyp` requirement. These informational fields are outside `hostIdentity`; they are not extra host JSON settings. |

Microsoft Graph distinguishes user/application role membership through `allowedMemberTypes`, with the role value appearing in token claims. [App-role contract](https://learn.microsoft.com/en-us/graph/api/resources/approle?view=graph-rest-1.0). `api.requestedAccessTokenVersion=2` selects the API's token format; an administrator-consent scope uses `type=Admin`. [API settings](https://learn.microsoft.com/en-us/graph/api/resources/apiapplication?view=graph-rest-1.0), [delegated-scope contract](https://learn.microsoft.com/en-us/graph/api/resources/permissionscope?view=graph-rest-1.0). The optional `idtyp` claim identifies app-only access tokens. [Optional claims](https://learn.microsoft.com/en-us/entra/identity-platform/optional-claims-reference). These contracts guide source generation; they do not establish that a real tenant issues accepted tokens.

## Review and use

The trusted-owner CLI's `identity.propose` operation wraps this input as `identityContract`, with matching outer customer/cell IDs. It writes the proposal and hash only to a fresh private result file. See [owner commands](foundation-owner-cli.md). The C# API is also available to trusted composition:

```csharp
var proposal = EntraCellOnboarding.Parse(publicInputJson);
var reviewedMetadata = proposal.CanonicalJson;
var reviewDigest = proposal.Sha256;
```

Before applying anything externally, collect and review the current application/service-principal state, preserve unrelated definitions/associations and stable IDs, and construct a separately authorized merge. This tool emits no PATCH URL/body pair and cannot approve that change. Actual directory ownership, consent policy, client authentication/PKCE or federation setup, user/app assignments, discovery/JWKS and token validation require their own acceptance. It provisions no client secrets and does not copy an operator's login cache.

App roles are ceilings on current scoped in-app membership. A role definition or assignment alone cannot add membership, approve a request, grant support access or acquire downstream credentials. SupportDelegate still requires a reviewed purpose-bound grant limited to one day; workload tokens cannot perform privileged human approval. Independent requester/approver separation remains enforced. [Security and operations](foundation-security-operations.md).

`EntraCellOnboardingTests` verifies definition restrictions, stable detached output, malformed/overbroad inputs, real host-configuration merging without state creation, genuine RSA-signed synthetic user/worker tokens, wrong worker subject/audience, two-customer separation and denial without membership. No Graph call, Entra consent, real sign-in, Azure deployment or live pipeline is involved. Production identity acceptance remains open.
