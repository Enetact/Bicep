# GitHub decisions

Provider id: `github`. The host uses its verified attached GitHub CLI identity against the fixed remote MCP endpoint with all toolsets requested. The Codex desktop connector and an ADO GitHub checkout connection are separate. New CLI browser login adds `workflow` to CLI defaults for workflow-file authoring; existing attached tokens retain their scopes. Organization/administrative and security features need their own grants and policy acceptance.

Use `get_me` for the account baseline, then explicit owner/repository and branch/ref selection. That identity read does not establish private repository access or write/admin rights. Discover schemas for repository/file/tree/commit/branch/tag/release operations; issues, labels, subissues, discussions and projects; PRs/reviews/comments/merges; Actions runs/jobs/logs; collaborators/teams/rulesets/custom properties; security alerts/advisories/scanning; gists, stars, notifications and Copilot-space/UI metadata. Only returned tools are available.

Resolve immutable commit/file identity before proposing code changes. Preserve unrelated content and concurrent updates; include expected SHA/ref where supported. Workflow-file changes require actual workflow authorization in addition to repository write access. Creating a PR, submitting a review, merging, changing rulesets and triggering Actions are distinct effects needing their own concrete plan steps.

Read Actions status/logs as execution evidence, not deployment proof. Security scanning can itself trigger work; inspect effect/schema rather than assuming a read. Sensitive alert output may be withheld; do not request secret values through another endpoint. For notifications, marking read, dismissing or changing subscriptions is a write even when the topic seems informational.

Bind searches and pagination to the requested repository/organization. Keep missing permissions distinct from no results. A successful GitHub operation does not authorize ADO pipeline checkout or Azure deployment.
