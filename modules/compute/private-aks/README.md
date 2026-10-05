# Private AKS module

Leaf resource-group module for the bounded [Private AKS Foundation](../../../workloads/private-aks/README.md). Owns the cluster, application user-assigned identity/federation and diagnostics. Existing network, custom DNS, control-plane/kubelet identities, registry authorization and workspace are platform prerequisites; this module does not grant permissions on them.

See [main.bicep](main.bicep) for exact parameters and outputs. Subscription Deployment Stack and Template Spec packaging are owned by the workload wrapper. The module is cataloged for audit but is not AI-draft-selectable until its specialized network/identity prerequisites are supported by the draft resolver.
