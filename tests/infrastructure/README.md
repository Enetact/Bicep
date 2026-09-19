# Infrastructure and pipeline checks

Run `./scripts/Test-Project.ps1` from the repository root. It compiles the required artifacts, then invokes `scripts/Test-PipelineStructure.ps1`, which restores the locked `yaml` 2.9.1 parser with install scripts disabled and runs `verify.mjs`. Node.js 22+ and npm are required. The parser is test tooling only; the Function application has no new dependency.

The checks cover all root/template YAML, local path resolution, required/allowed template parameters, four registered discovery and deployment routes, disabled setup-only isolation, all six enabled stages, protected-resource bindings, discovery run IDs, artifact producer/consumer order, and complete stack-wrapper forwarding to the resource-group composition. Results are written to `artifacts/test-results/pipeline-structure.json` and retained by the existing validation artifact publication.

Enabled-stage checks use supplied configuration without enabling any target. The accompanying discovery contract suite also generates an enabled catalog fixture and checks exact publisher bindings. No Azure login, publication or deployment is performed.

The expression interpreter supports only `parameters.*`, string/boolean literals, `eq`, `and`, `or`, `not` and stage `if` used by this repository. It rejects unsupported constructs and is not an Azure DevOps YAML compiler. It cannot verify server resource authorization, UI rendering, hosted capacity, environment checks or private connectivity. Those require the documented live acceptance run.

After compilation, rerun only these checks with `./scripts/Test-PipelineStructure.ps1`. A fresh checkout must first compile via `Test-Project.ps1`; the test never silently reuses missing compiler outputs.
