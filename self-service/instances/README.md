# Stack instance drafts

This directory is the **non-deployable source draft contract**. `registry.json` starts empty. New discovery analyses (including the shared ADO Discover step and explicit portal uploads) read it to generate proposed-name artifacts for matching workload/environment/subscription/region entries. Matching entries can be selected for named Preview, but this is not a deployable target selector or registration API. Existing `../targets/` files remain unchanged and authoritative for the legacy deployment path.

Each future entry has `workloadType`, `state: "draft"` and an `intent` following [the naming contract](../../schemas/naming/intent.schema.json). The registry pins the canonical naming-policy SHA-256 and allows only the registered workload types. No `enabled`, service connection, target override, approval or deployment field is accepted. Current offerings allow internal exposure only.

The pure validator calculates the subscription Deployment Stack and resource-group identities. It rejects duplicate instance UUIDs and duplicate scoped identities, including collisions between different workload types with the same acronym/ordinal. It does not calculate every workload resource or check Azure availability. All receipts explicitly deny deployment/allocation authorization.

Run from the repository root:

```powershell
. ./scripts/naming-common.ps1
$registry = Get-Content ./self-service/instances/registry.json -Raw | ConvertFrom-Json -AsHashtable
$receipt = Test-StackInstanceRegistry -Registry $registry
$receipt | ConvertTo-Json -Depth 30
```

For a proposed change, supply both reviewed versions:

```powershell
$previous = Get-Content ./previous-instance-registry.json -Raw | ConvertFrom-Json -AsHashtable
$proposed = Get-Content ./proposed-instance-registry.json -Raw | ConvertFrom-Json -AsHashtable
$receipt = Test-StackInstanceRegistry -Registry $proposed -PreviousRegistry $previous
```

The last example uses operator-supplied local files; they are not included in the repository. Without a previous registry, `transitionChecked` is false: structural validation does not prove immutable identity preservation. With it, existing entries cannot be removed, reassigned to another workload, or have naming identity fields changed. A display-name edit is permitted, leaves physical names unchanged and changes the registry digest. New entries can be appended. Policy changes require explicit requalification; this validator does not migrate profiles.

This is a source reservation, not a transactional allocator. Review the complete combined registry against its trusted prior version before merging concurrent additions. Calculated workload drafts and frozen Preview transport are available for all nine workloads. After changing the reviewed registry, run `Update-ServiceCatalog.ps1` and fresh Discover; select the UUID in the dedicated Deploy menu with Preview only. Portal registration, deployment permission binding, protected CI transition enforcement and named Deploy remain planned. Do not copy an old target and enable it to work around those missing integrations. See [phase status](../../docs/plans/stack-naming-and-resource-previews.md).
