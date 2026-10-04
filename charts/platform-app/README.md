# Restricted stateless application chart

This chart belongs to the separate [Helm Preview/Deploy workflow](../../workloads/private-aks/README.md#helm-application-onboarding). It manages five namespaced objects, uses an immutable ACR image digest, three or more replicas, private ClusterIP service, restricted container security, probes, PDB and explicit network policy. Defaults intentionally contain placeholders and fail schema validation until reviewed settings are supplied.

Templates use JSON syntax within `.yaml` files (JSON is valid YAML) so the PowerShell evidence reader can parse the exact Helm rendering without an additional YAML runtime dependency. Do not add arbitrary templates, hooks, CRDs, Secrets or cluster resources without updating the governed object contract and tests.

The pipeline packages this exact source as a same-run `.tgz` artifact. It does not publish an OCI chart. Images are built/scanned/published separately. Read [values.schema.json](values.schema.json) for supported settings and [values.yaml](values.yaml) for defaults. App data access, HTTPS egress, namespace RBAC and Gateway API ingress are separate platform decisions.
