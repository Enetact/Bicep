# Network discovery and agent diagram delivery

Implementation plan, 27 September 2026. User-selected address authority: **Azure Virtual Network Manager IPAM**. This delivery adds executable local capabilities; live Azure, model and allocation acceptance is performed separately using the manual checklist produced with the implementation.

## Delivery order

1. **Evidence-bound diagrams.** Normalize observed IDs and configured relationships into a server-owned graph. Supply stable aliases to Codex through the existing MCP evidence tool. Parse an intentionally restricted Mermaid flowchart grammar, reject executable/unsupported constructs and unknown nodes/edges, then render the validated graph as escaped, image-only SVG. Retain raw review, accepted Mermaid, graph and receipt. Invalid output remains downloadable evidence but never a trusted visual.
2. **Expanded read-only discovery.** Add registered, selected, management-group and accessible-tenant scope choices. Resolve subscriptions with the browser Azure identity and configured tenant, separately from deployment target registration. Reconcile management-group descendants, excluded subscriptions, paging and per-collection failures. Use bounded direct ARM inventory for the initial implementation; an eventual Resource Graph optimization must preserve the same coverage contract.
3. **Deterministic network findings.** Evaluate prefix containment/overlap, theoretical IPv4 capacity, delegation, configured routes, DNS links and endpoint state from available facts. Separate Known, Unknown and Unsupported. Do not claim effective connectivity, usable free capacity or allocation approval from inventory.
4. **AVNM allocation delivery.** Add a separately protected, manual ADO allocation path using reviewed pool/profile configuration, stable allocation identity, read-only planning, explicit reservation mode, retained provider allocations and reconciliation receipts. Default profiles remain unconfigured until the platform owner supplies a real AVNM pool. The portal can queue an explicitly reviewed ADO request; it and the model cannot mutate Azure directly. No automatic release on failure.
5. **Qualification and handoff.** Test malicious Mermaid, forged resource references, scope escapes, tenant filtering, pagination/partial coverage, network rules and allocation contracts. Rebuild the real portal, verify local UI/API behavior, reconcile documentation/manifest and provide manual tests. No live cloud deployment or address reservation is part of local implementation verification.

## Non-negotiable boundaries

- Observed configuration, agent interpretation, proposed workload components and saved Azure What-If changes remain distinct views with origin/coverage labels.
- A denied listing is not an empty scope. The caller's visible tenant is not proof of every subscription in that tenant.
- The browser's delegated Azure token stays in the collector. Codex receives a bounded projected snapshot, never credentials or arbitrary ARM tools.
- AVNM owns prefix allocation. Local files are receipts, not a competing IPAM database. Stable names and protected ADO serialization must be used; ambiguous provider outcomes require reconciliation, not deletion/retry with a new name.
- Provider-assigned exact prefixes are known only after reservation. A reservation does not approve a workload plan, establish routing or bind a shared subnet into a workload stack.
- Existing workload deployment inputs/ownership are not silently rewritten by broad discovery or by an allocation receipt.

## Acceptance

Local automated tests must exercise production code, with synthetic responses clearly labeled. Manual acceptance must cover a real browser-authenticated Azure scan, explicit model run, rendered/downloaded diagram, denied/partial scope, a configured test AVNM pool and a reviewed ADO reservation/reconciliation run before any production enablement. Production networking profiles still need effective DNS/routing probes, RBAC, overlap coverage and product-specific acceptance.

Sources: [subscription enumeration](https://learn.microsoft.com/en-us/rest/api/resources/subscriptions/list?view=rest-resources-2022-12-01), [management-group descendants](https://learn.microsoft.com/en-us/rest/api/managementgroups/management-groups/get-descendants?view=rest-managementgroups-2020-05-01), [AVNM IPAM](https://learn.microsoft.com/en-us/azure/virtual-network-manager/concept-ip-address-management), [static allocation schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.network/networkmanagers/ipampools/staticcidrs).

## Source delivery status

The five increments above are implemented and locally qualified within the bounded contracts in [the runbook](../network-discovery-and-diagrams.md). Broader scopes use direct ARM; Mermaid uses a restricted parser and the existing image-only renderer. AVNM uses a disabled reviewed profile, durable static allocation and separate create-only network stack, with two protected approvals. Live provider acceptance and broader enterprise connectivity/admission remain open. This does not mark the entire private networking roadmap complete.
