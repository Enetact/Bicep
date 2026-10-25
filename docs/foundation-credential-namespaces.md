# Protected credential namespaces

Revision 22 adds [separate source-v2 signed recovery](foundation-scoped-recovery.md). Retained validation checks the exact scoped namespace markers, binding/state and complete positive-plus-absence inventory without reopening live roots. The distinct source kinds and fresh-only provisioning remain unchanged; the v1 stage/checkpoint/review protocols do not accept scoped sets.

Revision 21 adds distinct namespace kinds for [fresh opt-in scoped strong roots](foundation-absence-scope-provenance.md). Explicit mode/factory selection prevents legacy construction, format stripping or silent in-place conversion. Existing namespace markers and vault/authority guards continue to protect these roots. The default v1 namespace kinds and on-disk data remain unchanged; retained recovery support for source v2 is still gated.

Revision 20 protects `.credential-recovery-checkpoint.guard` and `.credential-recovery-checkpoint.json`, including partial authority roots, across operational output/provisioning guards. [Credential-set checkpoints](foundation-credential-recovery-checkpoints.md) occupy their own fresh private root; credential, key, rollback, activation, database recovery and software-installation namespaces cannot be placed beneath it.

Revision 18 protects the new `.credential-recovery-stage.guard` and `.credential-recovery-stage.json` sentinels, including partial stages, across operational namespace admission. [Inactive recovery staging](foundation-credential-recovery-staging.md) deliberately creates no live credential namespace metadata. Normal vault construction cannot activate the staged profiles; provisioning and other protected outputs refuse stage ancestors. Existing live namespace formats and caches are not migrated.

Credential vaults and Windows DPAPI key providers now distinguish **fresh provisioning** from **opening existing custody**. This changes the earlier constructor behavior described in [credential lifecycle](foundation-credential-lifecycle.md). It applies only to the Foundation custody library; it does not discover, read or migrate desktop/portal login caches, configure a live provider, or expose an HTTP, MCP, AHP or model operation.

## Provision once, then open existing

Normal constructors validate an existing protected namespace. They never create directories, create missing guard files, adopt empty/legacy directories, repair ACLs, generate cryptographic keys or change binding metadata. Missing, malformed, incomplete, foreign, linked or widened custody is refused. Refusal preserves the existing files and permissions.

The trusted owner provisions a **fresh** vault root explicitly:

```csharp
var options = new CredentialVaultOptions(newVaultPath, customerId, cellId, destinationKeyId);
var vault = CredentialVault.Provision(options, trustedKeyProvider, clock);
// Subsequent startup opens existing custody; it does not provision it.
var reopened = new CredentialVault(options, trustedKeyProvider, clock);
```

The root's immediate parent must already exist on a fixed local Windows drive. Supply an absolute canonical path; UNC/network locations, relative paths, drive roots, alternate streams, dot segments, device names, trailing-dot/space aliases and linked/reparse ancestors are rejected before namespace I/O. Path spelling is preserved, including lower-case names, and the marker compares that spelling exactly. Reuse the same configured absolute spelling when reopening; the library does not uppercase paths or assume that NTFS directories are case-insensitive. Use dedicated, separately protected runtime directories with trusted parents. A protected child directory cannot defend against an administrator or a hostile ancestor owner with rights to replace that child.

Before fresh creation, every existing ancestor is checked for protected activation, installation, publisher-package and credential namespaces. File **or directory** entries named `.activation.guard`, `authority.json`, `.installation-owner.json`, `.installation.guard`, `.package-owner.json`, `.credential-namespace.json`, `.vault.lock`, `.keys.lock`, `.rollback.lock` or `cell-binding.json`, and legacy `*.credential.json`/`*.dpapi.json` entries, cause refusal. Neither provisioning API may add a fresh child beneath such a namespace. The guard preserves its existing inventory and bytes; it does not inspect legacy secret content or repair that namespace.

The DPAPI provider retains its derived `cell-<customer/cell SHA256>` directory beneath an existing local base directory. Provision this directory separately from its cryptographic keys:

```csharp
var provider = CredentialWindowsUserKeyProvider.ProvisionNamespace(existingKeyBase, customerId, cellId);
using var key = await provider.ProvisionKeyAsync(customerId, cellId, freshKeyId, cancellationToken);
// Both operations above are explicit owner actions. Reopening performs neither.
var reopenedProvider = new CredentialWindowsUserKeyProvider(existingKeyBase, customerId, cellId);
```

Namespace provisioning does not request a key from `ICredentialKeyProvider`. Ordinary `GetKeyAsync` remains existing-only, and missing-key recovery never allocates a replacement under the old ID. See [lookup, provisioning and admission](foundation-credential-lifecycle.md) and [ciphertext rewrap](foundation-credential-rewrap.md).

## Binding and filesystem custody

Every new root receives its protected current-owner-only DACL and owner SID in the native atomic directory-creation call. Creation fails for **any existing destination**, including an empty directory. Marker, guard, temporary-envelope and protected-key files are created with an explicit protected owner-only file descriptor and `CreateNew`. Provisioning does not briefly inherit a permissive root ACL. If provisioning fails or is interrupted after fresh creation, its partial directory is retained for owner investigation; it is not automatically deleted or adopted.

The immutable `.credential-namespace.json` marker binds schema version, namespace kind (`credential-vault` or `windows-user-keys`), exact customer/cell, canonical absolute root, current Windows SID, a random namespace ID and a SHA256 checksum. It excludes the active encryption-key ID: changing the configured write key and performing an explicit rewrap must not rebind the namespace. JSON parsing rejects unknown or duplicate fields and missing required values. Metadata is capped at 16 KiB. An existing instance also pins its opened namespace ID and rejects replacement with another otherwise well-formed marker.

The checksum detects defined metadata changes; it is not a signature, credential or rollback-resistant authority. The trusted OS owner can rewrite local metadata, ACLs and files. Copying a marker to another root, using a foreign customer/cell, or opening the key namespace as a vault fails its binding checks.

Provisioning pre-creates the empty `.vault.lock` or `.keys.lock` guard. Each operation opens that guard with `FileMode.Open`, never `OpenOrCreate`. Missing or nonempty guards fail closed. The existing bounded gate wait and caller cancellation remain in effect. Dispatch admission, profile replacement, revocation and rewrap retain their existing cross-instance ordering.

Opening a namespace validates its root, marker and guard owner/ACL/link state. Operations repeat those checks around guard admission, validate relevant envelope/key files before opening them, and validate custody again before returning material or committing a replacement. Checks refuse widened permissions; they do not tighten an existing file's ACL. Marker, profile and key reads hold read-only handles that deny writers/deletion while the bounded bytes are read. Profile envelopes are capped at 128 KiB and DPAPI envelopes at 16 KiB. Existing AES-GCM and DPAPI bindings still authenticate the profile/token/key; filesystem checks do not replace cryptography.

Fresh vaults may instead explicitly select [local rollback checks](foundation-credential-rollback.md). Their distinct namespace kind and bound authority prevent reopening them through the default unfenced path. This is a fresh-only option; the default marker format and existing namespaces are preserved. The external authority uses an existing-only `.rollback.lock`; that sentinel also prevents unrelated namespace provisioning or worker/owner writes beneath a partial authority root.

## Legacy state and remaining recovery work

This is an intentional constructor compatibility change. Existing unmarked vaults, old `cell-binding.json` key directories and roots whose custody has widened are not silently accepted or converted. Their bytes and ACLs remain intact. Do not delete an old root, regenerate a missing encryption key, widen a new root's ACL or copy a marker to bypass refusal. Preserve the original protected state for a separately reviewed recovery/migration procedure. No migration/adoption command is provided by this increment.

These controls are explicitly supported local **Windows** custody. Other operating systems fail closed before Windows custody work; there is no fallback to permissive files or an inherited credential cache. A different platform needs its own qualified owner/key implementation. This does not establish multi-host locking, resistance to a malicious current owner/OS administrator, external recovery epochs, production HSM/KMS custody, protected backup retention, key retirement or service-account operations.

Default namespace provisioning adds no durable application audit ledger or high-water authority. Fresh fenced vaults must separately select the [local rollback contract](foundation-credential-rollback.md). Trusted owner authorization, operational audit composition, old-backup key custody and production recovery acceptance remain separate release work. There are no new cloud rights, live connections or commercial readiness claims.

## Synthetic qualification

[Namespace custody tests](../tests/SelfService.Foundation.Tests/CredentialNamespaceCustodyTests.cs) use fresh temporary roots, synthetic profiles and ephemeral DPAPI keys. They cover existing-only startup, atomic owner protection, competing provisioning, preserved unmarked/legacy directories, rehashed binding changes, copied markers/links, widened root/marker/guard/profile/key ACLs, missing or malformed metadata/guards and early local-path rejection. Existing credential regressions explicitly provision only their own test namespaces, then exercise encrypted resolution, acquisition, cancellation, rewrap, revocation and retained dispatch admission. No real token cache, application database or primary process is changed.

On 24 October 2026, **192 affected credential tests passed, with 0 failures or skips**, including **56 namespace custody cases** and native Windows DPAPI/ACL checks. The final reviewed receipt is `artifacts/foundation-credential-namespace-check/results/credential-namespace-reviewed.trx`; build outputs and earlier intermediate receipts are retained separately under the same isolated artifact root. The protected-ancestor cases exercise both provisioning APIs against every named sentinel as a file and directory, checking original bytes, ACL and inventory preservation. A native junction fixture uses the existing portable PowerShell prerequisite in an owned process with a cleared environment; it does not require symlink-creation privilege.
