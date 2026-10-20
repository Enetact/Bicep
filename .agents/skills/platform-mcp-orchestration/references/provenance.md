# Authoritative sources and complete packages

The seven provider instruction files are project-owned broker adaptations. Live catalogs and host validation control executable tool names, arguments, effects and authorization. Upstream manuals or skills cannot register unavailable tools, provide credentials or authorize actions.

| Source | Exact source or preserved package |
|---|---|
| Azure Bicep 0.48.1 | [MCP tools](https://github.com/Azure/bicep/blob/v0.48.1/docs/mcp-tools.md); actual live discovery takes precedence over stale static tool counts. |
| Azure MCP 3.0.0 | [Official Azure MCP documentation](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/overview); exact executable package is pinned in `scripts/mcp/package-lock.json`. |
| Azure scenario skills | Complete existing 42-skill, 944-source-file bundle at [the Azure bundle index](../../../../vendor/azure-skills/bundle.json), commit `117b038edfef5d7af09848b8ffcd355f28f19956`. |
| Azure DevOps MCP 2.10.0 | [Official server](https://github.com/microsoft/azure-devops-mcp); exact npm package is pinned. Complete seven official authoring skills and license at [the ADO bundle index](../../../../vendor/azure-devops-skills/bundle.json), commit `844a1f6f5202aa920ce5b1be95c056d529d63ded`. |
| GitHub MCP | [Official server](https://github.com/github/github-mcp-server); the host requests all remote toolsets. Remote catalog availability and account grants can change. Its server plugin metadata is not an operational skill bundle. |
| Microsoft Learn | [Official MCP reference](https://learn.microsoft.com/en-us/training/support/mcp-developer-reference). Three official skills, the skill-template reference and supplied license/notices are preserved at [the Learn bundle index](../../../../vendor/microsoft-learn-skills/bundle.json), commit `f8ffde185dfd232dbf5d187c22ce299eadc3d583`. |
| Microsoft Fabric Core MCP | [Official remote setup and Fabric audience](https://learn.microsoft.com/en-us/rest/api/fabric/articles/mcp-servers/core-remote/get-started-core), reviewed 20 October 2026. Microsoft hosts the endpoint; its live tool names/schema/digest are discovered and bound to each connection. No static catalog, npm package or local Fabric-server version is implied. |
| Aspire 13.6.0 | [Official dashboard-only MCP command](https://aspire.dev/reference/cli/commands/aspire-agent-mcp/) and [dashboard API authentication](https://aspire.dev/dashboard/configuration/), reviewed 20 October 2026. The locally pinned native CLI exposes only its three standalone telemetry tools. Live schemas and the catalog digest are bound to each browser connection. |

The complete upstream packages are unchanged authoring resources with file hashes, source URLs and licenses. They are packaged with the repository; they are not new executable workflows or installed authentication configurations. The runtime receives this generated skill's broker-specific instructions and examples. It does not automatically execute or load every upstream authoring skill. Bicep contributor profiling guidance is not substituted for an application Bicep skill. Use the provider catalogs and Learn for task-specific current detail.
