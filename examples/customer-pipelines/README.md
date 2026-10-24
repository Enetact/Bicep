# Customer-owned validation pipelines

These examples demonstrate the customer pipeline contract used by the new, dedicated **SelfService.Foundation.Host** cell service. They are inert examples outside `.github/workflows`; the existing portal MCP broker and governed Azure DevOps source adapters do not acquire a new dispatch capability from these files. The Foundation host defaults to denied external dispatch. Its admitted fixture transport only reaches an explicitly configured literal loopback HTTP endpoint.

Both examples compile a parameterized Bicep composition and a local child module. They do not authenticate to Azure, obtain an Azure identity, create a service connection, invoke an MCP provider, restore a registry module, or deploy resources. Customer pipeline execution itself is a later, separately qualified external capability. No live pipeline has been run to qualify these examples.

## Example contents

| File | Purpose |
| --- | --- |
| `github-actions.validate.yml` | Manual GitHub workflow with repository read permission and a SHA-pinned checkout action |
| `azure-devops.validate.yml` | Manually queued ADO pipeline with CI and PR triggers disabled |
| `Validate-Bicep.ps1` | Rechecks exact Git HEAD, tracked source files, fixed source paths, and parameter allowlists before offline compilation |
| `main.bicep`, `main.bicepparam`, `bicepconfig.json` | Explicit location, environment, storage name, and SKU parameters with local analyzer configuration |
| `modules/storage-account.bicep` | Local child module used to exercise source composition without registry access |
| `Test-Examples.mjs` | Offline YAML structure and safety checks using an already installed `yaml` package |
| `Test-Guarded-Example.ps1` | Creates an isolated ignored Git fixture and tests the complete clean-commit validator with both region values and source-drift rejection |

The sample child is **not an Azure Verified Module**. To use an AVM composition, first review the AVM version and complete transitive source closure, admit the composition into the customer's catalog, and provision its reviewed dependencies in the isolated runner cache. Keep `--no-restore` enabled. Expand the fixed source manifest and module selection allowlist only through the same reviewed change; never accept arbitrary module paths or registry addresses from workflow inputs.

## Runner and repository preparation

Use a dedicated ephemeral validation runner with Git, PowerShell 7.4 or later, and **Bicep 0.48.1** already installed on PATH. Review and pin the runner image separately. It should have repository-read access, no Azure credentials, no production network access, and no shared writable workspace with another customer. The GitHub example requests the label `platform-studio-validation`; the ADO example requests the pool `PlatformStudioValidation`. Provisioning those runners is outside these examples.

The example paths are relative to the Git repository root: `examples/customer-pipelines`. In this checkout, that root is the `Bicep` directory itself. The runner can place its checkout anywhere; no absolute developer path is embedded. A customer adopting a different relative layout must review and update the literal YAML invocation and fixed script manifest together. The validator refuses symlinks, untracked inputs, modified compilation inputs, and a checkout that differs from the approved commit. Commit the reviewed example before using it in a customer repository; an uncommitted local copy intentionally fails the source guard.

The GitHub checkout action is pinned to `34e114876b0b11c390a56381ad16ebd13914f8d5`, the verified official [v4.3.1 release](https://github.com/actions/checkout/releases/tag/v4.3.1) and [commit](https://github.com/actions/checkout/commit/34e114876b0b11c390a56381ad16ebd13914f8d5). This identifies the reviewed dependency; assess its current suitability and runner compatibility during customer admission rather than treating the pin as a continuing security assurance. Checkout credentials are not persisted.

## Connection admission and parameters

An authorized customer administrator must register a connection bound to its customer, cell, project, environment, provider, repository, and pipeline/workflow. The connection must allow the exact ref, environment, and input keys. Store only an opaque `authProfileId` in configuration. Store no PAT, bearer token, private key, or connection string. A credential reference does not establish authentication or authorization.

The examples declare all four reserved inputs: `studio_request_id`, `studio_plan_digest`, `studio_source_revision`, and `studio_environment`. Platform Studio adds these from the reviewed request and plan; callers cannot override them. The nonreserved inputs are `location` (`eastus2` or `westus2`) and `module` (`local-storage-example`). The only admitted environment here is `dev`. Parameter values are passed through environment variables and validated before use; they never choose script text, tools, or source paths.

For GitHub, record the approved owner, repository name, workflow filename or numeric ID, and ref allowlist. Set `EnforcesSourceRevision=true` only after reviewing the workflow and its repository protection controls. The first inline step checks both the event SHA and workflow SHA against the expected source SHA before checkout or repository scripts. Checkout then selects that immutable SHA, and the validator checks HEAD again. A mutable dispatch branch/tag can drift; the example rejects that drift. These checks depend on the admitted workflow remaining trustworthy: a boolean flag, an input carrying a SHA, or the dispatch response cannot prove that a customer workflow enforces its guard. Review branch/tag protection, workflow changes, runner isolation, and all privileged steps separately. These examples have no privileged deployment step.

For ADO, use canonical **project GUID and repository GUID**, plus organization name and numeric pipeline ID. The adapter's exact response checks use those IDs, so display names can yield an unverified result when the API returns canonical IDs. Record the reviewed pipeline revision as `PipelineVersion` and update the connection generation after any relevant change. Review the pipeline's repository/YAML path and disable server-side schedules and build-completion triggers separately; `trigger: none` and `pr: none` do not attest the entire ADO project configuration. Requests pin `resources.repositories.self.version` to the approved source commit and pass the four correlation values as template parameters. The first script compares `Build.SourceVersion` before checkout; the validator compares the actual checkout again. A pipeline revision pin complements the repository commit pin.

The plan digest is a correlation value in these files. The workflow validates its shape; it cannot independently prove the plan's approval, authority, expiry, or input digest. The Foundation service performs those checks using server-owned membership, connection generations, exact approvals, and durable dispatch state before an admitted transport is called. Customer workflow admission must include an independently reviewed mechanism if a future pipeline needs to verify that authority itself.

## Evidence and future credentials

The script emits local compilation evidence with `mock: true` and `deploymentVerified: false`, including source, plan, request and output hashes. It is not a live provider receipt. The current Foundation dispatcher also marks all adapter results as mock. Never relabel these as `mock: false` to satisfy a release gate or infer Azure access from a successful compile.

A correlated provider response can establish that a run was accepted; it does not establish successful pipeline execution or deployment. The pinned [GitHub REST workflow API, version 2026-03-10](https://docs.github.com/en/rest/actions/workflows?apiVersion=2026-03-10#create-a-workflow-dispatch-event), documents HTTP 200 with run identity and URLs. Legacy HTTP 204, HTTP 202 without correlated identity, interrupted requests, and source/URL mismatches remain `OutcomeUnknown` and require reconciliation; the adapter never retries a dispatch POST automatically. The [ADO Run Pipeline API, version 7.1](https://learn.microsoft.com/en-us/rest/api/azure/devops/pipelines/runs/run-pipeline?view=azure-devops-rest-7.1) supplies the pipeline version, repository version, and template-parameter request fields.

Production credentials, credential retrieval/rotation/revocation, provider permission checks, external transport approval, and provider run reconciliation remain separate release work. Future GitHub dispatch credentials need the narrowly scoped Actions write permission for the selected repository; the workflow's own `contents: read` permission is a different identity. ADO queue authorization must be restricted to the admitted customer pipeline and repository. These examples contain no production credentials or live-dispatch switch.

## Offline checks

Use an already installed `yaml` package; do not install dependencies as part of this check:

```text
node examples/customer-pipelines/Test-Examples.mjs /absolute/path/to/node_modules/yaml
pwsh -NoProfile -File examples/customer-pipelines/Test-Guarded-Example.ps1
```

Neither check contacts GitHub, ADO, or Azure. The guarded test creates a new repository only under `artifacts/customer-pipeline-guard-tests`, commits copied examples with an explicit local fixture author, and leaves the original repository and Git configuration unchanged. It compiles both `eastus2/dev` and `westus2/dev` and checks rejection of a different source SHA and modified child module. It retains its fixture and local evidence under ignored artifacts. For direct compilation, use the preinstalled Bicep executable with `--no-restore`, writing outputs to a temporary directory. The official [Bicep CLI reference](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/bicep-cli) describes compilation and `--no-restore` behavior. Passing these checks establishes local source and payload behavior, not customer runner or provider acceptance.
