# Azure DevOps decisions

Provider id: `ado`. Use the separately attached ADO audience from the portal's local CLI or registered-app Microsoft connection. Azure RBAC is not ADO organization/project permission. The broker targets the configured organization; select the intended project/team/repository explicitly rather than inferring it from an Azure subscription or GitHub repository.

The full supported business surface covers nine domains: core projects/teams/identities; work planning/iterations/capacity; work items/queries/backlogs/links/comments/attachments; code/wiki/work-item search; test plans/suites/cases/results; repositories/branches/files/commits/pull requests/reviews; wiki pages; pipeline definitions/builds/runs/logs/artifacts; advanced-security alerts. The experimental MCP App ping helper is not a business operation.

Discover current schemas: consolidated tools can multiplex read and write suboperations. Supply the exact operation and required IDs from observed evidence; do not reuse pre-consolidation tool names. All 40 tools in the reviewed server lack a read-only annotation, so even intended reads require an exact approved plan. `ChangeOrUnknown` is a conservative effect gate, not a failed login or unavailable tool. Do not bypass it with REST or a differently named operation.

Use small project-name filters/top limits for baseline checks, then page only the requested scope. A matching project establishes that read, not permission to create pipelines, modify code or access security findings. Queueing a run is a separate change from reading a definition. Protected environments, service connections, repository checkout grants and locks remain independently enforced by ADO; a browser token does not confer the pipeline's Azure identity.

For work-item/wiki/source updates, retain prior content and exact revision/branch context where the schema supports it. For run/PR changes, report the actual run/PR identifier and terminal receipt; submission alone is not success.
