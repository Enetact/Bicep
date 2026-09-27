# Bicep Preview and Deploy

The [connected portal diagram](portal-diagrams.md) can now load this pipeline's published `deployment-preview` artifact by run ID, including while Deploy waits for approval. It projects resource actions and retains blocked/uncertain status; ADO's full README, pipeline guards and approval checks remain authoritative. This adds no pipeline stage or automatic deployment.

**Product coverage:** Preview also supports Private Storage Workspace, Key Vault, Observability, HTTP Functions API and Service Bus worker. It freezes typed inputs and checks existing shared dependencies before Azure What-If. Missing shared network/DNS/monitoring remains a blocker. Infrastructure-only deployment applies Release once; app products use Foundation/package/Release. See [onboarding](workload-onboarding.md).

Use the workload-specific **Deploy - Blob copy** (`/azure-pipelines-blobcopy-deploy.yml`) or **Deploy - Event flow** (`/azure-pipelines-eventflow-deploy.yml`). Both extend the protected `pipelines/deploy-entry.yml` and route to `pipelines/templates/self-service-two-stage.yml`. The legacy generic Deploy file keeps its older six-stage/setup flow.

## Run only the preview

1. Merge the reviewed changes to the GitHub branch used by ADO (`main` for these deployment menus).
2. Complete the matching workload's Discover pipeline on `main`. Its `subscription-discovery` manifest must be complete, match the instance/environment/subscription/service connection, and be no older than seven days.
3. Open the matching Deploy definition, **Run pipeline**, and select `main`, instance, environment and region.
4. Under **Resources > discovery**, choose that successful Discover run. ADO supplies both pipeline and run IDs.
5. Leave **Run stages = Preview only** and run. Preview executes; Deploy is skipped and contains no private-agent/environment bindings.
6. Open the completed run's **Summary / Extensions** and the uploaded **Bicep deployment preview** Markdown. The same file is under **Artifacts > deployment-preview > README.md**. The report is retained on failures when the agent reached evidence preparation.

No custom Azure DevOps extension needs to be installed. The pipeline uses the built-in [UploadSummary logging command](https://learn.microsoft.com/en-us/azure/devops/pipelines/scripts/logging-commands?view=azure-devops#uploadsummary-add-some-markdown-content-to-the-build-summary). The exact tab placement can vary with ADO's run UI; the artifact is the fallback.

### If source-manifest validation fails

The run stops before discovery download or Azure What-If. Its README reports the named changed/missing/unlisted files; it is not a resource-change plan. Run `./scripts/Update-Manifest.ps1` locally after reviewing the source changes, commit the updated `MANIFEST.sha256` with those changes, and queue a fresh run from the updated main commit. CI only checks the manifest and never repairs it automatically. Hashes follow Git's LF checkout rule for text, so local CRLF/mixed endings cannot produce a manifest that passes locally but fails solely because Git normalizes that text. Binary content remains hashed exactly.

### If Event flow reports incomplete onboarding

The dev profile now contains operator-authorized generated tags, the supplied service-principal object ID and two scoped exception decisions. See the [dev review record](reviews/eventflow-dev-exceptions.md). Merge that record with the parameters so their GitHub review links resolve. QA, UAT and prod still require independent onboarding. Dev still needs a matching complete discovery artifact to resolve resource-ID placeholders.

A successful Discover run proves its inventory was collected and verified. For Event flow, new discovery artifacts also resolve approved standard resources into Reuse / Create / Manage decisions; they do not grant platform approvals. Preview reports the missing fields together in **Required environment settings**, identifies the repository parameter file, and retains `onboarding-requirements.json` alongside the compiled declarations. No Azure resource-change result is claimed while these settings are incomplete.

Update the selected `workloads/logic-app-event-grid/environments/main.<environment>.bicepparam`: owner, cost center and deployment identity object ID. A fresh prerequisite plan supplies the two subnet IDs, workspace ID and six DNS zone IDs from reviewed discovery policy; select shared resources through target `parameterOverrides`. The two platform exception objects must also contain actual approvals and their HTTPS review references. Never turn approvals on merely to pass validation. Reviewed target overrides apply before the check.

Review the [prerequisite plan](prerequisite-resolution.md): compatible selected resources are reused; absent standard resources are declared in the workload stack and created during Deploy. Failed listings or missing explicit shared IDs block deployment. Discovery itself creates nothing. Rerun Discover to produce this plan; older artifacts keep the existing-only path. Keep `enabled: false` while obtaining a preview; enablement is required only for actual Deploy. After filling the real settings, regenerate the manifest, merge, and rerun Preview with matching discovery that remains within its freshness limit.

## What the report means

The full runtime-enabled Bicep stack is evaluated: `deployFunctionApp=true` for Blob copy and `releaseActivated=true` for Event flow. You can inspect the planned application host and supporting infrastructure before Foundation is deployed. The report lists every resource returned by Azure, action counts, resource IDs, certainty, nested property before/after differences, unchanged resources, management/deny changes and diagnostics. Create, Modify, Delete and Detach results remain visible even when policy rejects the plan.

The preview covers the selected workload's template and existing stack ownership, not all subscription resources. A separate table lists locally declared types, clearly distinguished from expanded Azure instances. If compilation, input validation, authentication or Azure evaluation fails, the report cannot claim a successful zero-change result. Placeholder environment settings must be replaced before a real What-If is possible.

Azure What-If has [documented evaluation limitations](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-what-if). Runtime values, unresolved expressions or incomplete evaluation may block this repository's strict gate. A successful preview is not proof of private-network reachability, package indexing or application readiness. Function/Logic App ZIP content changes are outside ARM What-If; application qualification and runtime verification happen in Deploy. Workflow content should also be reviewed in source/package evidence.

Cost estimates are frozen alongside the preview. They are retail estimates, exclude documented usage charges and are not spending caps. Sensitive property names and known account-key strings are redacted from the Markdown; existing Logic App evidence redaction also protects runtime storage keys.

## Method and command map

| Stage/job | Calls and effects | Evidence |
|---|---|---|
| Preview / PreviewInfrastructure | Prepare an initial README before downloads/sign-in; install pinned Bicep; verify source manifest; download exact selected discovery artifact. | Initial report even if a subsequent step fails. |
| Preview / PreviewInfrastructure | `Test-DiscoveryHandoff.ps1 -AllowDisabled`: verify inventory hash, typed scope/freshness and successful originating main run through ADO Build API. | Discovery source metadata; failed checks stop Azure evaluation. |
| Preview / PreviewInfrastructure | `Invoke-WorkloadPreview.ps1 -Action Prepare` → `New-WorkloadPreviewInputs`: compile composition, subscription wrapper and environment parameters; overlay approved profile; validate parameters; freeze full-release inputs and costs. | `preview-inputs.json`, target, main/stack templates, base/effective parameters, stack contract, costs and discovery. |
| Preview / PreviewInfrastructure | `Invoke-WorkloadPreview.ps1 -Action Preview` → `Read-WorkloadPreviewInputs` → `Invoke-WorkloadInfrastructurePreview` → `Get-WorkloadStackState` → `New-StackPreview -UseLocalTemplate`. Uses `az stack sub validate --template-file ... --validation-level Provider`, then `az stack-whatif sub create --template-file ... --stack-id ...`. | `azure/arm-validation.json`, `azure/stack-what-if.json`; successful policy-valid evaluation additionally creates `azure/preview-plan.json`. |
| Preview / PreviewInfrastructure | `Write-WorkloadPreviewReadme` renders raw Azure results, even after governance rejection. Upload summary and publish artifact run on success/failure. | `deployment-preview/README.md`, `status.json`, frozen inputs and Azure evidence. |
| Deploy / BuildBundle | Existing workload qualifier tests/packages the same checkout. `New-SelfServiceBundle.ps1` freezes the release using Preview's saved discovery; `Invoke-WorkloadPreview.ps1 -Action VerifyBundle` → `Assert-PreviewMatchesBundle` compares target, source, release, templates, parameters, stack contract and discovery. | `self-service-bundle`, `self-service-tests`. |
| Deploy / PublishTemplateSpec | Protected `Publish-WorkloadTemplate.ps1` publishes/reuses the exact content-hashed Template Spec version. | `template-publication`. |
| Deploy / ApplyStack | `Deploy-PreviewedWorkload.ps1` → `Invoke-PreviewedWorkloadDeployment`: guard main/manual/protected bindings; verify the successful current-run preview; rerun the full preview and compare fingerprints before workload writes. | `deployment-result/recheck/`. |
| Deploy / ApplyStack | `New-ServicePlan` then `Invoke-ServiceApply`, first Foundation then Release. Existing phase-specific governance/rechecks remain. Actual Bicep infrastructure apply uses `az stack sub create --template-spec ...`; each workload keeps its package deployment, private connectivity and smoke checks. | `deployment-result/plan-Foundation`, `result-Foundation`, `plan-Release`, `result-Release`, final `receipt.json`. |

`preview-inputs.json` hashes nine frozen files. Preview and deployment must belong to the same ADO run/commit/release, and the plan must be no older than 24 hours. Changed inputs, stale plans, unowned resource groups, uncertain results, destructive removals and disallowed security changes fail closed. A failed rerun clears old successful preview files before evaluation. Phase-specific safeguards still reject state drift during actual apply.

## Azure effects and prerequisites

Preview does **not** publish a Template Spec, create the workload resource group, apply a stack, upload an application or run smoke traffic. It is not strictly read-only: native [stack What-If](https://learn.microsoft.com/en-us/cli/azure/stack-whatif/sub?view=azure-cli-latest) writes a temporary preview metadata resource, requests one-day retention and deletes that invocation's metadata in `finally`. A cleanup failure fails the action; interruption may leave temporary metadata until retention expiry. Discovery remains the separate read-only operation.

Hosted Preview needs PowerShell 7.4+, Azure CLI 2.89.1+ with native stack What-If and the pinned Bicep compiler, outbound tooling/ARM access, authorized service connection, ADO build/artifact read rights and valid environment configuration. Provider-level validation checks deployment permissions; an inventory-only Reader identity is insufficient. The connection must be authorized for stack validation, What-If metadata creation/deletion and relevant resource/deployment checks. No new subscription permission grant is made by this pipeline. Preview deliberately avoids private storage/SCM data-plane tests.

Targets may stay disabled while Preview is used, but target syntax and real parameter prerequisites remain enforced. All checked-in targets are currently disabled and contain onboarding values. Platform setup still includes approved topology, identities, provider registration, quotas and existing resources referenced by the workload. Successful empty Event flow discovery can plan dedicated networking, six DNS zones/links and a workspace under the reviewed prerequisite policy. It does not invent existing shared IDs. Blob copy retains its reviewed network-profile behavior.

## Apply after review

Queue a fresh run with **Run stages = Preview and deploy** only after the target is enabled and its private agents/environments are configured. Review that run's new Preview report. Configure **approvals and exclusive locks** on the protected ADO deployment environment; YAML does not create those external checks automatically. All deployment jobs retain literal platform-owned bindings. Azure DevOps may validate private resources at queue time when both stages are selected, even if Preview later fails.

Preview-only cannot be changed into deployment mid-run. The next run repeats Preview and creates its own frozen inputs; it does not apply a historical preview artifact. Deploy requires Preview success and rejects disabled targets. The private jobs are omitted entirely for Preview-only/disabled routes, avoiding missing-pool authorization failures in those modes.

Publication occurs inside Deploy before the final workload recheck. If that recheck rejects drift, a Template Spec version may already have been published, but no workload apply is performed. An apply/runtime failure can leave partially created or updated resources; automatic rollback/deletion is not implemented. `deployment-result/receipt.json` reports Ready only after the existing workload verification succeeds. Re-run discovery/preview or follow the documented recovery process instead of bypassing failed checks.

## Local verification

```powershell
./scripts/Update-ServiceCatalog.ps1
./scripts/Test-Project.ps1
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
```

`Test-WorkloadPreview.ps1` exercises real preview input/report/coordinator logic using mocked Azure responses. Pipeline checks expand the local expression subset for both modes, both workloads and all registered environments. They verify hosted-only preview, protected enabled jobs, artifact/job ordering and retained failure summaries. These tests do not claim ADO server compilation, hosted UI rendering or live Azure acceptance.
