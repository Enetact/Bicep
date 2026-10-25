# LocalPreview software staging and readiness

`scripts/Manage-FoundationPreviewInstallation.ps1` stages an explicitly pinned,
unsigned Foundation LocalPreview ZIP on the current trusted Windows machine. It
can verify a staged generation, restage a damaged generation, and check a narrow
set of installed .NET runtime prerequisites. It does not activate a release or
execute anything from the ZIP.

This is development release-lifecycle evidence. It does not qualify a production
installer, a signed release, another CPU architecture, a clean machine, service
installation, application startup, upgrade compatibility, or uninstall. The
commercial release gates for those capabilities remain open.

## Scope and custody

The installation root holds **generic software only**. It is bound to the current
Windows owner SID and its absolute path, not to a customer, tenant, cell, or Entra
identity. Installing software grants no customer authority. Customer database,
WAL/SHM, configuration, credentials, keys, activation authority and runtime data
must stay outside this root in their separately governed cell-scoped locations.
The controller does not discover or reuse a shared configuration or credential
cache. Unexpected files are preserved and cause refusal.

Use an explicit absolute path on a fixed local Windows drive. UNC paths, drive
roots, alternate streams, path aliases, reparse points and widened owner ACLs are
rejected, including for read-only checks. A new root's parent must exist. Its
owner SID and protected DACL are supplied atomically to native `CreateDirectoryW`,
so it never briefly inherits a writable parent's permissions. Existing roots are
never adopted by that reservation. A new
root cannot be placed in a credential vault, key store or rollback-authority
namespace, including a partial namespace identified only by a marker/guard or
legacy `*.credential.json` / `*.dpapi.json` entry. Files and directories with these
names, including case aliases, cause refusal; no credential contents are parsed
or adopted. The same preflight rejects activation-authority, installation-owner/
guard, publisher-package-owner and owner-initialized cell ancestors. Protected
marker names and legacy credential suffixes are refused as the root's own leaf
name too, before creation can turn an otherwise clean parent into a custody
namespace. A valid software root's own
installation marker/guard remains expected. All four actions repeat the custody
check, so a misplaced existing installation is refused without repair, append,
readiness probing or data/ACL changes. Mutations recheck before root reservation
and generation creation. These are local observations, not an OS-wide lock
against a trusted owner changing an ancestor later. Root and child
entries receive or inherit a protected current-owner-only access policy. This
does not defend against a malicious current owner, an administrator, or offline
disk changes.

Operators remain responsible for custody of the immediate parent directory and
all higher ancestors. Namespace sentinel checks and a private child DACL do not
validate the parent's trusted-write permissions or reject a NULL or absent
parent DACL. The preview controller does not implement the activation authority's
immediate-parent ACL policy and does not repair parent permissions.

## Commands

Run in PowerShell 7.4 or later from the source checkout. The controller loads only
function definitions from the trusted sibling publisher script; it does not run
the publisher. Keep that script with this controller.

```powershell
$archive = 'C:\ReviewedReleases\foundation-preview.zip'
$root = 'C:\PreviewSoftware\Foundation'
# Copy this digest from the reviewed publishing receipt, independently of the ZIP.
$pin = '<reviewed-lowercase-sha256>'
$stage = .\scripts\Manage-FoundationPreviewInstallation.ps1 `
    -Action Stage -InstallRoot $root -ArchivePath $archive `
    -ExpectedArchiveSha256 $pin -ExpectedGeneration 0 -LocalPreview

$verify = .\scripts\Manage-FoundationPreviewInstallation.ps1 `
    -Action Verify -InstallRoot $root -Generation $stage.generation
if (!$verify.verification.verified) { throw 'Staged bytes failed verification.' }

$ready = .\scripts\Manage-FoundationPreviewInstallation.ps1 `
    -Action Readiness -InstallRoot $root -Generation $stage.generation `
    -DotNetPath 'C:\Program Files\dotnet\dotnet.exe'
```

The first stage requires expected generation `0` and no prior state digest. A
subsequent stage requires the exact generation and `stateSha256` returned by the
last successful operation. These are compare-and-swap inputs checked while the
exclusive installation guard is held. They prevent a stale cooperating operator
from appending against a different staging history.

```powershell
$next = .\scripts\Manage-FoundationPreviewInstallation.ps1 `
    -Action Stage -InstallRoot $root -ArchivePath $archive `
    -ExpectedArchiveSha256 $pin -ExpectedGeneration $stage.generation `
    -ExpectedStateSha256 $stage.stateSha256 -LocalPreview

$repair = .\scripts\Manage-FoundationPreviewInstallation.ps1 `
    -Action Repair -InstallRoot $root -Generation 1 -ArchivePath $archive `
    -ExpectedArchiveSha256 $pin -ExpectedGeneration $next.generation `
    -ExpectedStateSha256 $next.stateSha256 -LocalPreview
```

Repair requires the original archive digest recorded for the selected generation.
It copies the reviewed archive into a fresh generation. It never overwrites,
deletes or renames the damaged original, which might still be in use. A new
archive belongs in `Stage`, not `Repair`.

Commands return structured objects. Callers must inspect
`verification.verified` or `runtimePrerequisitesReady`; an integrity mismatch in
known package bytes is a false verification result, not a successful readiness
claim. Invalid ownership, metadata, archive inputs or namespace structure throw.
Do not save extra receipts inside the strict installation root.

## State and verification boundaries

```text
Foundation/
  .installation-owner.json
  .installation.guard
  generations/
    000001/
      package/                 exact reviewed ZIP files
      generation.json          final commit marker
    000002/
      package/
      generation.json
```

The largest generation number is the latest **staged** generation. There is no
current-version pointer or activation operation. A generation receipt binds the
installation ID, operation, original ZIP SHA256, previous receipt SHA256, copied
file paths/lengths/SHA256 and explicit false qualification flags. SHA256 protects
against accidental/stale metadata changes; it is not a signature, a protected
audit ledger or publisher authentication. The current OS owner can rewrite
metadata and recompute its hashes.

Before staging, the expected archive hash is checked while an original read-only
handle denies archive writes/replacement. The shared publisher verifier checks
the ZIP inventory, package manifest, dependency-evidence commitments and actual
bounded entry bytes. Windows-ambiguous segments, case-colliding file paths,
symlinks and unsafe directories are refused. Files are copied individually with
`CreateNew`; ZIP scripts and binaries are never run. The copied package tree is
verified before its final receipt is written. Further operations validate exact
metadata keys, hash-chain/scope binding, ACLs, allowed namespaces and known file
inventory. Missing or corrupted known package files can be reported and repaired;
unknown data, unknown generations and incomplete commit markers cause refusal.

An interrupted stage is preserved as a partial generation and blocks automatic
continuation. There is no automatic cleanup or rollback that deletes files.
Owner review of such a root is required. This controller intentionally has no
launch, stop, service/registry/PATH change, active-version switch, in-place
update, uninstall, secure-erasure, or customer-data deletion operation.

## Runtime prerequisite check

Readiness invokes only the explicitly supplied existing `dotnet.exe` with
`--list-runtimes`, outside the staging root. An original read handle binds its
reported SHA256 through process completion. Its environment is cleared and rebuilt
from a small allowlist; parent startup hooks, profiler settings, additional deps,
proxies and credentials are not inherited. Output is bounded to 65,536 stdout
characters and 16,384 stderr characters. The asynchronous probe has a five-second
deadline and original-process cleanup, with at most another five seconds to wait
after termination. Any stderr, nonzero exit or unexpected output causes refusal.
No program from the ZIP is executed.

After package verification, readiness reads the packaged entry projects'
`runtimeconfig.json` descriptors dynamically. It recognizes only stable
three-component versions of `Microsoft.NETCore.App` and
`Microsoft.AspNetCore.App`. It accepts a matching major/minor installed version
at or above the requested patch for default policies or explicit `LatestPatch`
with patching enabled. It rejects unsupported framework names, descriptor shapes,
options and pinning policies, including `rollForward: Disable` and
`applyPatches: false`. It does not reproduce general .NET host resolution,
architecture compatibility or application behavior. A descriptor is capped at
64 KiB and eight requested frameworks.

Readiness leaves `customerConfigurationEvaluated`, `activationEvaluated`,
`identityEvaluated`, `providerAccessEvaluated`, `sourceBackedFeaturesEvaluated`,
`customerAuthorityGranted`, `signed`, `architectureQualified`,
`cleanMachineQualified`, `legalClearance` and `productionReady` false. A passing
runtime prerequisite check does not close those release gates.

## Limits and local evidence

The controller allows 64 staged generations, 5,000 files per generation, 64 MiB
per copied file, 1 GiB cumulative logical receipt bytes, 4 MiB per generation
metadata record, 1,024-character archive paths and 255-character segments. A
physical package walk is capped at 10,000 entries. The shared ZIP validator also
enforces its 256 MiB archive/expanded-content caps, 5,000 entries including
directories, and narrower manifest/evidence caps. Owner metadata is capped at
16 KiB. Growth is refused explicitly; these are development safety bounds, not
storage quotas enforced against an administrator or malicious current owner.

Run the retained synthetic qualification with:

```powershell
.\tests\release\foundation-installation.tests.ps1 `
    -DotNetPath 'C:\Program Files\dotnet\dotnet.exe' `
    -ProbeSdkPath 'C:\Program Files\dotnet\sdk\10.0.300'
```

An optional actual reviewed release input uses `-PublishedArchivePath` and
`-ExpectedPublishedArchiveSha256`. Tests retain their artifacts and
`installation-summary.json` under `artifacts/foundation-installation-tests/`.
They exercise pinned archive refusal, fresh staging, exact stale-state rejection,
side-by-side history, byte corruption and preserved-original repair, protected
ancestor namespaces, unknown/partial metadata, owner ACL and junction refusal,
actual noncanonical/colliding ZIP entries, external synthetic database/config/key
preservation and unsupported runtime pinning. The optional explicit SDK input
builds a constant owned console fixture offline using existing .NET 10.0.8
reference/apphost packs, without restore. It exercises a stalled child and
oversized stdout, verifies their owned processes stop, and records only child
environment variable names to prove the allowlist. That fixture is never part of
the ZIP, and its synthetic runtime output is not evidence of installed runtimes.
`boundedProbeFailuresQualified` remains false when that optional input is omitted.
Optional actual-package checks stage/verify the published closure and inspect
installed prerequisites on this machine. They do not qualify a clean-machine
installation or start the application.

The earlier Owner-inclusive package passed **80 checks** with the optional actual
profile on 24 October 2026. In addition to the 74 synthetic/probe cases, the run
staged the exact pinned ZIP, verified actual files and prerequisites, corrupted
only its disposable staged owner DLL, detected the mismatch, and repaired into
a new generation. The damaged original remained unchanged; repaired bytes and
prerequisites verified. Receipt:
`artifacts/foundation-installation-tests/4209a0e2dd85428e9b521bab751ea300/installation-summary.json`.
The package was `6a03ad1c4e844d3e9b465d66b443f04e`, SHA-256
`dd8e26f83be7735f61ec0304e8f3a9d35f9755868f4afe67f88a7bd3f1049813`.
No package code, activation, customer-data mutation or production qualification
is implied by this current-machine software-staging result.

The 2026-10-24 current-machine synthetic qualification passed **74 checks** with
SDK 10.0.300 and the explicit .NET 10.0.8 runtime, including the bounded-process
fixtures (`boundedProbeFailuresQualified: true`). Its retained receipt is
`artifacts/foundation-installation-tests/a5d2c3a90bd74f07885b8891f676823f/installation-summary.json`.
That receipt has no actual published-package input; fresh published-package
acceptance is separate. It records no active-version change, customer-data touch,
sign-in, cloud operation, architecture/clean-machine qualification or production
readiness.

The namespace-parity fixtures additionally check protected leaf names, marker-
or guard-only partial custody, file/directory and case variants, an existing
installation later identified as custody, and owner-initialized cell roots.
Each refusal compares recursive entry names, file hashes and access/owner/group
ACLs. The normal Windows run retained at
`artifacts/foundation-installation-tests/1fc591f9702b44f78079e824a66ee249/installation-summary.json`
passed **448 checks**, including the bounded native probe and compatibility with
the explicitly pinned revision-13 ZIP. That compatibility input does not qualify
a newer release's bytes; final-package acceptance must use its new exact pin.
