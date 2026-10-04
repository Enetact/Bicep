# Manual ADF and Databricks recipes

[Workload guide](README.md) · [Catalog](../../docs/workload-catalog.md)

These source-owned starters reuse the Private Data Platform connections. Infrastructure publication creates **definitions only**. There are no schedules, file-arrival triggers, automatic data uploads or job runs. Private endpoint approvals, identities, Unity Catalog grants and a qualified runtime are still prerequisites. The portal displays this capability in the workload summary; execution is through ADF Studio or Databricks Run now, not a new portal run-data button.

## Included recipes

| Manual entry | What it does | Binding |
|---|---|---|
| ADF `run-reviewed-notebook` | Existing `SELECT 1` execution acceptance notebook. No lake writes. | Deployment's `notebookPath`, workspace and MSI connection. Import acceptance source separately. |
| ADF `stage-reviewed-file` | Copies one reviewed CSV from `landing/inbox` into `landing/staged/{ADF RunId}/batch.csv`. No source deletion or Databricks compute. | Deployment's `inputFileName` (default `sample.csv`), owned lake, private managed IR and factory identity. |
| ADF `stage-and-run-reviewed-job` | Stages that file, then invokes the reviewed Databricks job only after Copy succeeds. | Optional positive `databricksJobId` in reviewed Bicep parameters; **zero omits this definition and its linked service**. |
| Databricks `reviewed_batch` | Three dependent tasks on one shared job cluster: validate → curate → verify. Also runnable directly with an inbox filename. | Exported bundle binds workspace, lake, runtime, node/worker settings, existing compute policy and explicit job-runner identity. |

ADF handles orchestration and movement; the native [Databricks Job activity](https://learn.microsoft.com/en-us/azure/data-factory/transform-data-databricks-job) invokes a job whose task graph/compute are governed in the workspace. We use classic job compute for this VNet-injected profile; serverless networking is not qualified here. [Databricks describes classic cluster reuse](https://www.databricks.com/blog/best-practices-kicking-databricks-workflows-natively-azure-data-factory) for this integration. Verify actual MSI/managed-IR behavior in your Azure acceptance run.

## Data contract and behavior

The starter CSV has the exact header `record_id,event_time,amount`: a bounded alphanumeric ID, a UTC timestamp such as `2026-01-01T00:00:00Z`, and a decimal fitting `(18,4)`. A synthetic [sample](databricks/sample.csv) is included. This is an intentionally small, reviewable recipe: **10 MiB and 10,000 input rows maximum**. It is not a general-purpose production ingestion engine.

The validator rejects invalid headers, empty input, malformed values, excessive size/rows and conflicting duplicate IDs. Identical duplicates collapse within a batch. The job pins the source byte hash at validation and rechecks it in subsequent tasks. It writes a Delta dataset under `curated/batches/{batch_id}` with batch/hash/contract metadata, then compares actual rows with the expected complete result. No records or secrets are printed in receipts.

An existing batch is never overwritten: an identical replay is verified, and differing source or output causes failure while preserving data. This protects a **single batch ID**; a fresh ADF RunId creates a new batch and does not deduplicate across business batches. Keep staged files immutable during processing. Reconcile ADF/Databricks run IDs after a timeout or unknown outcome before submitting again. ADF and Databricks cancellation are not a transaction and cannot undo a committed Delta write.

ADF copy and job launch use no automatic retries. Copy has a 30-minute limit; the job activity has 75 minutes. The Databricks job has a one-hour limit, 20 minutes per task, one concurrent run, disabled queuing and zero automatic retries. Its approved policy and 1–4-worker bound constrain compute; the driver is additional and these limits are not a spending cap. Failed validation fails the run rather than emitting a success-path receipt. Production alert destinations, quarantine and retention/cleanup are platform decisions, not invented by the sample.

## Quick setup

1. Follow [infrastructure onboarding](README.md#manual-azure-acceptance). Keep `databricksJobId = 0` for the initial infrastructure run. The connection check and file-staging definitions are provisioned by the existing ADO Preview/Deploy path. ARM linked services bind the workspace ID/URL, lake account and private IR automatically; there are no PATs, storage keys or manually entered connection URLs.
2. Register the factory managed identity and a dedicated job-runner service principal in Databricks. Record their **application/client IDs**, not Entra object IDs. The factory needs `CAN_MANAGE_RUN` on this job; the runner needs access to the notebook files, approved job compute and Unity Catalog external locations. The bundle deployer needs permission to use the selected run-as identity. See [run-as identity guidance](https://learn.microsoft.com/en-us/azure/databricks/dev-tools/bundles/run-as).
3. Configure an approved Unity Catalog metastore, storage credential using the lake access connector and external locations. Grant the runner `READ FILES` on the landing location and `READ FILES`/`WRITE FILES` on the curated location. Restrict locations/identity scope to the approved paths; Azure container RBAC alone is insufficient. The recipe writes external Delta paths, not catalog tables. Managed tables, volumes, external table registration and business-specific grants are separate extensions.
4. Create/reuse a reviewed **standard-access classic job compute policy**, fixing approved runtime/node type, worker bounds, required tags and security settings. Record its policy ID. No policy bypass or unrestricted-cluster fallback is implemented.
5. Save the matching stack's `properties.outputs` as a JSON object and use the reviewed **compiled ARM parameter JSON** from the deployment bundle. This exporter reads local files only and validates scope/shape; it cannot attest that downloaded outputs, application IDs or policy IDs match live Azure.

From the repository root:

```powershell
# Replace paths and IDs with reviewed values. Output files contain ARM {value: ...} objects.
./scripts/Export-DataPlatformRecipes.ps1 `
  -ParametersFile ./artifacts/reviewed/parameters.json `
  -OutputsFile ./artifacts/reviewed/outputs.json `
  -AdfApplicationId '<factory-client-id>' `
  -RunAsApplicationId '<runner-client-id>' `
  -ComputePolicyId '<approved-policy-id>' `
  -ExportId dataplat-dev-reviewed
```

The exporter creates `artifacts/data-platform/recipes/dataplat-dev-reviewed/` with a ready-to-review `databricks.yml` (JSON is valid YAML), source/helper files, sample CSV and a hash receipt. It copies the source-owned bundle template and fills defaults from deployment parameters/outputs. Existing export directories are refused. Exports and `.databricks` deployment state are ignored by Git; templates remain source-controlled. The receipt is a provenance aid, **not a deployment authorization**.

6. Install a current platform-qualified Databricks CLI, connect from the approved private network and use its browser/OAuth login. Review the generated files before these **explicit workspace-mutating** commands:

```powershell
Set-Location ./artifacts/data-platform/recipes/dataplat-dev-reviewed
databricks auth login --host https://<your-workspace-host> --profile dataplat-dev
databricks bundle validate -t manual --profile dataplat-dev
databricks bundle deploy -t manual --profile dataplat-dev
databricks bundle summary -t manual --profile dataplat-dev
```

Deployment uploads source and creates/updates the job; it does not run it. The production-mode bundle has no schedule or trigger. Stable workload/environment root paths preserve resource identity; use the same reviewed bundle/state for later updates. Bundles own workspace job/source objects, while Bicep/Deployment Stacks own Azure resources. Never use `bundle destroy` as an infrastructure rollback. Workspace publication is currently an explicit operator step; a governed ADO bundle-publishing adapter is future work.

7. Record the published job ID, put it in the matching environment's `databricksJobId`, then review and run the existing ADO Preview/Deploy flow. This creates the optional launcher. Changes to an existing dataset, linked service or pipeline are deliberately blocked by the platform's migration review gate; do not disable that protection to redirect an established workload. Use a reviewed migration for later job/source rebinding.
8. Upload the synthetic sample through an approved identity into `landing/inbox/sample.csv`. If you changed `inputFileName`, upload to that reviewed filename. In ADF Studio choose **Trigger now** on `stage-and-run-reviewed-job`; no runtime connection fields are needed. ADF passes its RunId as `batch_id` and the isolated staged path. In Databricks, the same job's **Run now** defaults to the reviewed inbox filename and its own run ID; use an explicit prior batch ID only for a reviewed replay.

## Acceptance and troubleshooting

Verify both linked services and all private endpoint approvals before the first data run. Capture ADF run, Databricks job/run/task IDs, source hash, resulting row count, Delta version, audit logs and compute termination. Exercise invalid schema, conflicting duplicates, empty input, changed input between tasks, identical replay, changed replay, denied UC grants and a missing file. Local tests do not exercise Spark, Delta storage commits, workspace imports or actual native ADF Job execution.

If Copy fails, downstream job launch must not run. If a job invocation times out, inspect the actual Databricks run before retrying. If curated output exists but verification fails, preserve it and reconcile; the code performs no delete/overwrite compensation. Use the workload's **ManualRecovery** policy.

Additional source references: [ADF ADLS connector](https://learn.microsoft.com/en-us/azure/data-factory/connector-azure-data-lake-storage), [bundle task dependencies and values](https://learn.microsoft.com/en-us/azure/databricks/dev-tools/bundles/job-task-types). A reusable starter is not universal best practice for every dataset; business schemas, incremental checkpoints, cross-batch deduplication and large-volume pipelines require separate designs.

## Local qualification

PowerShell 7.4+, Python 3.10+ (standard library only), Node and compiled workload artifacts are required. The main `Test-Products.ps1`/qualification path now includes the recipe checks. A focused rerun is:

```powershell
./scripts/Test-DataPlatformRecipes.ps1
```

This checks deterministic data/path contracts, exporter scope/hash/no-overwrite behavior and compiled manual pipeline wiring. Native `databricks bundle validate`, Azure execution and data-plane acceptance remain separate checks on an authenticated private connection.
