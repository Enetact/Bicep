# Rebuild the portal while preserving sign-ins

From the repository root in PowerShell 7.4+, run:

```powershell
./scripts/Restart-PortalFresh.ps1
```

This command first verifies the registered main portal and its owned processes, then builds current source in separate outputs while the running app remains available. A failed preflight leaves that app running. It checks source drift before stopping, captures the latest private authentication files after their writers stop, verifies an encrypted checkpoint, rebuilds normal Release outputs, starts the new portal and opens a private resume link. It checks the listener and bootstrap readiness. It manages the main source portal; separate review hosts and desktop applications keep their own processes.

The browser verifies restored Microsoft and Codex accounts, attaches the current verified GitHub CLI account and reconnects the five MCP catalogs. This does not execute inference, provider tools, workflow plans, pipelines or Azure changes. Review Connections and Agent workflows afterward: cached files alone do not establish authentication, service permissions or model readiness.

## What survives

| State | Handling |
|---|---|
| Portal-owned Azure CLI login | Only `azureProfile.json`, `msal_token_cache.bin` and optional `msal_http_cache.bin` enter the checkpoint. Azure and ADO acquire and validate separate audience tokens in a new private CLI home. |
| Portal-owned Codex ChatGPT login | Only `auth.json` enters the checkpoint. A new private home receives freshly generated policy/configuration; the native app-server must verify the ChatGPT account and required model availability. |
| GitHub login | Its normal CLI credential store stays in place. The currently active CLI account is checked against GitHub before attachment; no GitHub token store is copied. |
| Registered-app Microsoft login | The old host stores MSAL tokens only in memory. This restart cannot recover that mode; reconnect or use the isolated CLI mode for cache-preserving restarts. |
| Workflow evidence, CSRF, pending requests, plans and approvals | Fresh browser-session state. Existing report files remain on disk, but old approvals cannot authorize a new call. |
| Desktop Codex, ambient CLI configuration, plugins, hooks, process receipts and old MCP clients | Not imported. Provider clients/catalogs are created again after identity checks. |

This is an explicit, script-driven handoff. Ordinary `Manage-Portal.ps1 -Action Restart`, `Stop-Portal.ps1`, session expiry and explicit Disconnect retain their cleanup behavior; they do not create an authentication checkpoint. Run the new script whenever credentials should survive a fresh rebuild. Neither approach changes OAuth grants or bypasses expiry, consent, MFA, tenant rules or organization SSO.

## Protected checkpoint and recovery

The ignored `.local/portal/credential-cache/<port>/` directory and its key directory allow only the current Windows user and SYSTEM. ASP.NET Core Data Protection encrypts the bounded checkpoint and protects its key ring with Windows DPAPI CurrentUser. Application purpose includes the canonical repository and port. There is no plaintext credential backup fallback. See [Microsoft's DPAPI key protection documentation](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-encryption-at-rest?view=aspnetcore-10.0#windows-dpapi) and [official Codex authentication/cache behavior](https://learn.chatgpt.com/docs/auth).

The script creates a restricted `Resume-Portal.url` containing a random, one-time resume capability and no OAuth tokens. Treat it as private access to the checkpoint. Its URL fragment is removed before restoration and is not sent in a page request. The POST requires the new browser's CSRF token and exact loopback origin. The first valid claim binds the checkpoint to that browser session; an exclusive lease prevents parallel claims or refresh-token cloning. Each successfully restored provider is removed from the protected handoff. Explicit Disconnect also removes that browser's remaining checkpoint entry. The checkpoint expires after 24 hours.

The private `resume-status.json` receipt records the new host's PID/start time, verification time, connection readiness, tool counts and sanitized errors. It contains no accounts, cookies, resume capabilities, tokens or tool outputs. It supports local restart verification when the default browser cannot be inspected directly; a receipt is a dated observation, not continuous authorization.

Multiple eligible old browser profiles block migration because directory names cannot safely establish browser ownership. Unknown ownership, reused PIDs, foreign listeners, linked/escaped paths or incomplete caches also block the operation. A checkpoint failure retains the original authentication homes; a later build/start failure retains the protected checkpoint and private resume link. Do not manually delete those files while diagnosing a failed restart.

Use `-WhatIf` for a read-only description. Use `-NoBrowser` to keep the private resume shortcut for manual opening. If a checkpoint remains after a failed rebuild/start, use:

```powershell
./scripts/Restart-PortalFresh.ps1 -ResumePending
```

Recovery requires a stopped main portal and a free configured port. It does not overwrite a pending checkpoint or take over a running browser. After successful resume the shortcut has no remaining access; remove it when finished. Failed identity validation may require reconnection in Connections. No cache promises indefinite access.

A partially restored checkpoint stays bound to the claiming browser session. Reopen its private shortcut in that same browser to retry remaining providers; successful providers and the GitHub attachment are not replayed. Explicit Disconnect in that session forgets the corresponding pending entry. If that browser session is lost, the checkpoint cannot be transferred to another browser; deliberately remove the expired protected checkpoint after its 24-hour retention period before creating another. An unclaimed checkpoint from a build/start failure remains recoverable through `-ResumePending`.

## Verification boundary

Regression coverage uses synthetic lifecycle adapters and credentials, native Windows protection/ACL round trips, tampering, repository/port binding, browser claims, expiry, disconnect and deterministic Azure/Codex/GitHub validation. Real provider status after an authorized restart is separate acceptance evidence. Local tests do not establish Azure deployment or unrestricted service authorization.
