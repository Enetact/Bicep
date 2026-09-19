# Infrastructure and pipeline checks

Run `./scripts/Test-Project.ps1` from the repository root. It compiles the required artifacts, then invokes `scripts/Test-PipelineStructure.ps1`, which restores the locked `yaml` 2.9.1 parser with install scripts disabled and runs `verify.mjs`. Node.js 22+ and npm are required. The parser is test tooling only; the Function application has no new dependency.

The 34 checks cover all 15 root/template YAML files, local path resolution, required/allowed template parameters, eight discovery/deployment routes, disabled setup isolation, six enabled stages, protected bindings, exact discovery run IDs, artifact order and complete stack-wrapper forwarding for both patterns. Nested step templates are expanded to verify shared Build/Deploy qualification order, unconditional cleanup and evidence preparation before Azure authentication. Isolated PowerShell fixtures exercise missing/partial qualification results and stage context without fabricating readiness receipts. Results are written to `artifacts/test-results/pipeline-structure.json`; fixture evidence is under `artifacts/pipeline-tests/`.

Enabled-stage checks use supplied configuration without enabling any target. The accompanying discovery contract suite also generates an enabled catalog fixture and checks exact publisher bindings. No Azure login, publication or deployment is performed.

The Logic App qualifier is checked separately from the Function qualifier. Default menu combinations and cross-workload rejections are exercised. `Test-LogicWorkload.ps1` adds 46 contracts for scoped discovery, two storage exceptions, event schema, deterministic package inventory, typed bundle integrity, array serialization, evidence redaction and mocked failure/readiness sequencing. None execute the Logic Apps runtime.

The expression interpreter supports only `parameters.*`, string/boolean literals, `eq`, `and`, `or`, `not` and stage `if` used by this repository. It rejects unsupported constructs and is not an Azure DevOps YAML compiler. It cannot verify server resource authorization, UI rendering, hosted capacity, environment checks or private connectivity. Those require the documented live acceptance run.

After compilation, rerun only these checks with `./scripts/Test-PipelineStructure.ps1`. A fresh checkout must first compile via `Test-Project.ps1`; the test never silently reuses missing compiler outputs.
