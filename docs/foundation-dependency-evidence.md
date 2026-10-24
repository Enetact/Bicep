# Foundation dependency and license evidence

Local release engineering evidence, 24 October 2026. `scripts/New-FoundationDependencyEvidence.ps1` reads the actual merged API/worker publish runtime and a read-only NuGet cache. It produces a bounded SPDX 2.3 inventory, exact runtime file hashes, package/asset verification and supplied license declarations/notices. It never restores or builds, executes package scripts/MSBuild targets, fetches packages, opens credentials, signs in, deploys or grants rights. [Preview packaging](foundation-service.md) remains separate from commercial clearance.

## Generate and consume

Supply explicit inputs and a fresh direct child of this checkout's `artifacts/foundation-dependency-evidence` directory. `CreatedUtc` must be an explicit UTC timestamp with seconds; no current-time default or random document identifier is used. Select the reviewed SDK directory containing Microsoft's NuGet reader assemblies when signed packages are present. The reader is loaded from that SDK, never from an untrusted package.

```powershell
./scripts/New-FoundationDependencyEvidence.ps1 `
  -RuntimeDirectory '<published-package>/runtime' `
  -NuGetPackagesDirectory '<read-only-NUGET_PACKAGES>' `
  -NuGetSdkDirectory '<reviewed-dotnet-sdk-directory>' `
  -RepositoryRoot '<reviewed-checkout>' `
  -OutputDirectory '<reviewed-checkout>/artifacts/foundation-dependency-evidence/review-001' `
  -CreatedUtc '2026-10-24T20:00:00Z'
```

The generator writes these four fresh files:

| File | Evidence |
|---|---|
| `dependency-evidence.json` | Actual runtime file inventory; API/worker lock hashes; NuGet IDs, versions, two distinct SHA-512 meanings, original nuspec/notices, verified published asset mappings, first-party project hashes, separate source vendor pins/licenses, external framework prerequisites and issues. |
| `sbom.spdx.json` | SPDX 2.3 packages, actual dependency edges, runtime file SHA-256 checksums and supplied nonstandard license text as `LicenseRef` entries. |
| `THIRD-PARTY-NOTICES.txt` | Package-declared licenses and the actual supplied license/notice text, followed by explicitly source-only guidance/template notices. No missing license text is invented. |
| `MANIFEST.sha256` | Exact UTF-8 hashes of those three files; it excludes itself. |

The document namespace is derived from a stable input hash that includes the explicit timestamp and NuGet reader assembly hash/version when used. Identical runtime/source/cache bytes, reader and explicit timestamp produce identical outputs, independent of destination paths. Every runtime file is SHA-256 inventoried again before output to reject changes during the read. Existing output directories/files, linked paths, unsafe archive paths and duplicate JSON/ZIP names are rejected. Evidence output contains relative source/runtime paths, not local cache locations or environment values.

The standalone command writes a blocked report and fails when `technicalStatus` is `Blocked`; examine `issues` rather than treating an emitted SBOM as clearance. The `New-FoundationDependencyEvidence` function returns a structured classification even when blocked so an explicitly labeled local-preview publisher can retain that evidence. A caller loading functions into another script should use an isolated child scope or parse/load only trusted local function definitions, as the release tests do; directly dot-sourcing the parameterized script can shadow caller variables.

## Integrity and coverage boundaries

The two entry `.deps.json` files supply the actual declared transitive package/project closure, asset locations and dependency edges. Their package identity, version and content hash must agree with the corresponding API/worker `packages.lock.json`. Current project and lock files are separately hashed; this does not claim that a previously published binary was built from the current source. Source freeze and reproducible-build qualification remain false.

Signed NuGet packages have two different hashes. The cache's `.nupkg.sha512` hashes the signed archive bytes. The `.deps.json`/lock content hash excludes the package signature. The generator verifies each against its own meaning: SHA-512 of archive bytes against the cache hash, and the SDK NuGet reader's content hash against both entry dependency/lock metadata and `.nupkg.metadata` when present. It checks signed archive content integrity through `ValidateIntegrityAsync`. This follows the [official NuGet content-hash implementation](https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Packaging/PackageArchiveReader.cs) and [metadata definition](https://github.com/NuGet/Home/wiki/Nupkg-Metadata-File). It does not validate certificate chains, trusted signers, revocation or publisher identity; `signatureTrustVerified=false` remains explicit.

Nuspec XML is read from the verified archive with DTDs/external resolution disabled. Declared licenses, license files and supplied notices are retained from that archive, with exact original entry SHA-256 hashes. Each declared published package asset is hashed from the archive and matched with the actual published file. An additional runtime file lacking a declared package/project asset or known project dependency/runtime configuration classification is a technical blocker. `packageEvidenceComplete` and `publishedPackageAssetsByteMatched` describe those checks, not complete legal attribution of every byte inside a DLL/native library.

The SPDX format uses the [official SPDX 2.3 schema](https://github.com/spdx/spdx-spec/blob/v2.3/schemas/spdx-schema.json). Runtime files are listed with their actual hashes; package `filesAnalyzed=false` avoids asserting a complete package-file license analysis or package verification code. The exported schema has `licenseConcluded=NOASSERTION`. NuGet expression support is deliberately limited to MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, MPL-2.0, CC0-1.0, CC-BY-4.0, Unlicense, Zlib and BSL-1.0. Unsupported or compound expressions block rather than being silently reinterpreted. A declared license file is preserved under a `LicenseRef`; its text is not guessed to be a standard license.

First-party project commercial terms are currently unspecified and recorded as `NOASSERTION`/`FirstPartyLicenseUndeclared`. Packages that declare an expression but supply no actual license text receive `LicenseTextNotSuppliedByArchive`, a release blocker requiring review of the appropriate distribution obligations. Those findings are distinct from cryptographic/package metadata failures. This tool cannot determine ownership, employment/contributor rights, patents, trademarks, redistribution obligations, source-license compatibility or whether a particular commercial license is permitted.

The pinned `azure-skills`, `azure-devops-skills`, `microsoft-learn-skills` and AVM bundles are checked against their own manifests, file hashes and upstream commit pins. Their license/notices are classified as source guidance/templates and explicitly excluded from the runtime binary closure. Their presence in this source checkout is not proof they appear in a particular runtime ZIP. Conversely, embedded/compiled third-party material inside project DLLs is not established absent by that separation: `binaryLicenseCoverageVerified=false` requires a separate source/embedded-resource attribution review. Framework-dependent .NET/ASP.NET shared runtimes are external prerequisites, not packaged components or qualified distribution rights.

## Bounds and failure behavior

Runtime traversal admits at most 5,000 files, 64 MiB per file and 256 MiB total. Component closure is limited to 512 distinct package/project versions. JSON inputs are at most 2 MiB/depth 32. Individual package archives are at most 128 MiB, 20,000 entries, 64 MiB per entry and 512 MiB total declared expansion. All ZIP entries are inspected before reading metadata; no extraction is performed. Absolute paths, traversal, backslashes, percent-encoded path ambiguity, symlinks and case-insensitive duplicate entries fail closed.

Nuspec/license/notice reads are at most 256 KiB each, with at most 64 notices per package and 16 MiB aggregate retained package metadata. Each source bundle has at most 5,000 manifest files, source bytes are capped at 128 MiB total and source notices at 4 MiB. Output files are each at most 16 MiB/32 MiB combined. A rejected package receives `NOASSERTION`, a reason code and observed archive hash where safely readable. Fatal unsafe source/runtime input or capacity failure produces no completed evidence set. No cache content is modified or copied wholesale.

Every report keeps `legalClearance`, `signatureTrustVerified`, `binaryLicenseCoverageVerified`, `signed`, `sourceFreezeVerified` and `productionReady` false. These are evidence boundaries, not switches a caller can turn on through an input flag.

## Verification

The release test uses deterministic synthetic ZIP packages and fresh source/runtime/cache fixtures. It covers archive/content/lock/asset tampering, unknown runtime files, unsafe ZIP paths, missing/unsupported licenses, custom license-file retention, duplicate JSON, prohibited XML entities, vendor hash mismatch, fresh output policy, deterministic outputs and cache preservation. Package MSBuild files remain inert. Optional explicit inputs add the actual published API/worker closure and validation against a separately obtained official SPDX schema; the test itself performs no network request.

```powershell
./tests/release/foundation-dependencies.tests.ps1 `
  -RuntimeDirectory '<published-package>/runtime' `
  -NuGetPackagesDirectory '<read-only-NUGET_PACKAGES>' `
  -NuGetSdkDirectory '<reviewed-dotnet-sdk-directory>' `
  -SpdxSchemaPath '<saved-official-SPDX-2.3-schema>'
```

The 24 October 2026 focused run passed **42 checks**, including identical hashes for all four outputs across repeated inputs, actual published API/worker closure verification and validation against the pinned official SPDX schema (SHA-256 `239208b7ac287b3cf5d9a9af23f9d69863971102a5e1587a27a398b43490b89b`). Receipt: `artifacts/foundation-dependency-tests/011343648a9a4e7c8213500aed0f2b7a/summary.json`.

The actual published closure contains **72 runtime files, 32 NuGet packages, 7 first-party projects and 4 separately classified source bundles**. All package/archive/content/lock/asset checks passed. Its report is `artifacts/foundation-dependency-evidence/acceptance-ac90570de4da41fdb358d25fa02f30f4/dependency-evidence.json`. It remains **Blocked** with 21 release findings: seven undeclared first-party licenses and fourteen packages without archive-supplied license text. These are evidence of remaining distribution work; no full-product or legal acceptance is inferred.
