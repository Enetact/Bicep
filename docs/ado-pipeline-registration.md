# Register the pipeline suite from Platform Studio

Implemented locally on 27 September 2026. **ADO setup** inventories all 18 root YAML entry points and can register the missing definitions in one reviewed batch. It does not queue runs. Existing definitions, GitHub source, service connections, permissions, environments and approval checks remain unchanged. Live authenticated ADO creation is still an acceptance step; local tests use synthetic responses.

## Use it

1. Commit/push the intended pipeline YAML to GitHub **main**. A local file or feature-branch commit is not enough. The portal compares each missing pipeline's published Git blob with its local/package copy, normalizing CRLF to the repository's LF format. It never pushes source for you.
2. Start or reload the updated portal. If rebuilding from source, run these commands from the repo root:

   ```powershell
   ./scripts/Stop-Portal.ps1
   ./scripts/Setup-Portal.ps1
   ./scripts/Start-Portal.ps1
   ```

3. Under **Connections**, connect Azure DevOps using an account with permission to view and create pipelines in the configured project. Azure subscription Contributor/Owner is not an ADO pipeline-creation permission. No PAT or GitHub token is needed by this adapter.
4. Open **ADO setup → Check registration**. It reads ADO definitions and anonymous public GitHub metadata. Choose an existing GitHub-connected pipeline for this same repository. Only its repository connection ID and default agent-queue ID are reused; variables, secret values, variable groups, triggers and unrelated settings are not cloned.
5. Review the statuses. **Missing** entries are selected by default; use **Select all missing** or deselect entries. **Existing** matches are preserved. **Conflict** means a name/path/repository/folder/branch differs or is duplicated; the tool will not rename/delete anything. **Source not published** means publish the exact YAML or update your local/package copy first.
6. Select **Review selected registrations**. Inspect the organization/project, commit, source connection/queue and each definition name/YAML. The optional details show the exact requests. No mutation has occurred yet.
7. Select **Register pipelines — no runs**. This creates only missing definitions, one at a time, and reads them back. Download the receipt; a local copy is retained under ignored `artifacts/pipeline-registration/<id>/receipt.json`.
8. Refresh registration. Complete the separately governed pipeline-resource authorizations and environment/approval setup before using **Discover**, **Preview** or **Deploy**. Registration does not qualify a deployment or enable any target.

One GitHub-linked pipeline must already exist in the project. If none is visible, a platform administrator must establish that initial repository connection through ADO. This tool does not create credentials, approve GitHub access or change an organization's GitHub App installation. `SC-AZ-A-Bicep` is the Azure deployment connection, not the GitHub repository connection reused here.

## Included pipelines

The seven products contribute their existing Discover/Deploy names and paths from `Catalog.Products` and `self-service/pipeline-settings.json`:

| Group | Entry points |
|---|---|
| Workload menus | Fourteen `azure-pipelines-<workload>-discover.yml` / `-deploy.yml` files for Blob copy, Event flow, Storage, Key Vault, Observability, HTTP API and Service Bus worker |
| Qualification | `azure-pipelines.yml` → **Qualify - Platform** |
| Generic Discover compatibility | `azure-pipelines-self-service.yml` → configured `discoveryPipelineName` (currently **Enetact.Bicep**) |
| Generic Deploy compatibility | `azure-pipelines-self-service-deploy.yml` → **BlobTransfer - Deploy** |
| Network allocation | `azure-pipelines-network.yml` → **Network - AVNM allocation** |

The four non-product entries and public repository identity are recorded in [pipeline-registration.json](../config/pipeline-registration.json). Every root `azure-pipelines*.yml` must appear exactly once, with unique names. Adding a new entry without a registration binding fails validation rather than silently leaving it out. Shared YAML templates under `pipelines/templates/` are consumed by the entry points and must **not** be registered independently.

Matching currently requires root-folder definitions and the exact names used by portal/deployment resource bindings. A differently named existing pipeline is a conflict even if its YAML is correct. Reconcile the naming/bindings deliberately; no automatic duplicate, rename or move is performed. Existing matched definitions are not certified for trigger policy, source freshness, queue availability or authorization by this registration check.

## What changes in ADO, and public-repo safety

The only write route is `POST .../_apis/build/definitions?api-version=7.1`. It creates an enabled YAML definition for manual runs, with project-scoped job authorization, the selected existing queue/connection, an empty trigger list, no variables and no variable groups. The referenced checked-in entry must have literal `trigger: none`, `pr: none`, disabled resource triggers and no schedules. No queue/run endpoint, update/delete operation, GitHub write API or permission API is called.

Public source remains readable by everyone who can access GitHub. That does not confer your ADO account or Azure service-connection permissions. Browser ADO tokens remain in the portal's existing session cache and are attached only to the configured ADO API requests. Anonymous GitHub requests receive no ADO token. Connection IDs, pipeline names and YAML paths are metadata, not credentials. Receipts contain definition metadata and outcomes, not tokens or copied secret variables. Credential/report ignores remain enforced.

New definitions inherit **the project's existing ADO permissions**. The tool does not tighten those permissions or audit all public-repository protections. A platform owner must maintain restricted pipeline edit/queue rights, protected main, explicit resource access, environments/approvals, and safe fork/PR settings. Existing “all pipelines” grants can apply to new definitions; this feature neither adds nor removes those grants. Future source edits can change triggers and behavior. This is a bounded registration-safety design, **not a full-history secret scan or blanket public-release clearance**.

## Methods and failure behavior

| Method | Contract |
|---|---|
| `LocalCatalog` / `GET /api/pipeline-setup/catalog` | Build all-entry registry, validate names/paths/literal manual-trigger contract, compute LF Git blob IDs and catalog digest. No authentication or cloud call. |
| `Inventory` / `GET /api/pipeline-setup/inventory` | Read paginated ADO definitions; read public GitHub repo, main commit and its root tree. Compare exact repository/name/YAML/branch bindings and source hashes. No writes. |
| `Review` / `POST /api/pipeline-setup/review` | Re-read evidence, allow only selected Missing entries, bind source connection/queue, catalog digest and main commit to a browser-owned, one-use, five-minute ticket. Return exact payloads. |
| `Apply` / `POST /api/pipeline-setup/apply/{ticket}` | Consume ticket, revalidate all evidence, serialize registration within this portal process, recheck each name/path, create and read back definitions. Discover entries precede Deploy entries. Save outcome after each step. |

HTTP responses are bounded to 8 MiB and requests to 45 seconds. ADO enumeration is capped at 500 definitions/ten continuation pages, rejecting repeated/incomplete pages. GitHub reads use the non-recursive root tree and reject truncation. Redirects are disabled. Only registered paths can become requests; arbitrary URLs or YAML paths from the browser cannot be registered.

Denied reads, missing consent, GitHub rate limits and incomplete inventory fail closed: they never mean “no pipelines.” A changed main commit, catalog, source connection or queue invalidates review. Matching definitions created meanwhile are reused. An uncertain POST or read-back stops the batch, preserves evidence, and is **never retried automatically**. A receipt can retain Pending if the process crashes before recording the response. Refresh inventory before creating another request; earlier successful definitions remain and no rollback/deletion is attempted. Cross-process administrators can still race; the immediate recheck and ADO name constraints are not a distributed transaction.

## Local and manual verification

`PipelineRegistrationTests` exercises the real service with synthetic ADO/GitHub HTTP responses: all 18 roots, create-all-missing, reuse, duplicate/path/repository conflict, incomplete paging, unpublished source, expired/drifted reviews, lost responses, partial batches, no secret/settings cloning and token isolation. The real unauthenticated HTTP smoke test confirms catalog access, identity enforcement and rejection of unissued tickets. See [dated verification](validation.md).

For live acceptance, first run **Check registration** with the intended ADO account. Compare results with the ADO UI. Review a selected missing definition or the full missing batch, then register and verify the definitions, zero new runs, unchanged GitHub commit and unchanged existing pipelines. Confirm resource access remains gated before attempting a Discover run. A service connection or private pool may need a separately approved authorization even after registration succeeds.

Sources: [ADO definition creation](https://learn.microsoft.com/en-us/rest/api/azure/devops/build/definitions/create?view=azure-devops-rest-7.1), [definition inventory](https://learn.microsoft.com/en-us/rest/api/azure/devops/build/definitions/list?view=azure-devops-rest-7.1), [Microsoft CLI creation implementation](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/pipelines/pipeline_create.py), [GitHub tree API](https://docs.github.com/en/rest/git/trees?apiVersion=2022-11-28#get-a-tree).
