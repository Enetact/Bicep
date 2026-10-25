# Foundation final-package secret review

This local PS-05 tool scans a pinned Foundation LocalPreview ZIP's ordinary text and managed PE user-string metadata. A completed scan is evidence about those surfaces. It provides no security, publication, production, ownership or license clearance. Findings remain open for review.

Run from the trusted Windows checkout with PowerShell 7.4 or newer:

```powershell
pwsh -NoProfile -File scripts/Test-FoundationPackageSecrets.ps1 `
  -ArchivePath 'C:\review\PlatformStudio.Foundation.LocalPreview.zip' `
  -ExpectedArchiveSha256 '<exact lowercase SHA256 from the reviewed package receipt>'
```

The default scanner is `.tools/gitleaks-8.30.1/gitleaks.exe`. The public review entry point requires the reviewed SHA256 `200df852fdecbedb19a33960657333cba5e231740bc8968972b507b50f93b194`, the locally qualified Windows ARM64 Gitleaks 8.30.1 executable. `-GitleaksPath` can select another path containing the same bytes. An arbitrary executable cannot become an accepted scanner by reporting the expected version. The hash identifies reviewed bytes; it does not verify a supplier signature. The script neither downloads tools nor signs in.

The ZIP must first pass the existing [publisher's](../scripts/Publish-Foundation.ps1) strict known-package validation: required entry points, canonical paths, no links, case uniqueness, outer and inner manifests, and dependency/runtime digest binding. The review holds a read-only, write/delete-sharing-denying handle to the original ZIP through extraction, scan and final hash comparison. It copies regular files with `CreateNew`; it never invokes package binaries, scripts, installers, module initializers, MSBuild targets or assembly loading APIs.

The fresh run is an atomic owner-private directory under `artifacts/foundation-package-secrets/<32-character-id>`. An explicit `-OutputDirectory` must be a new direct child of that directory. Originals and old runs are preserved. The staging workspace contains generic software, not a customer identity, database, credentials or runtime configuration authority. A scan grants no customer access. Retained extraction and projection files can contain secrets discovered during review: keep the whole run private. The exact generated raw scanner report is removed; this is not a secure-erasure claim.

## Covered surfaces

The [metadata helper](../scripts/FoundationPackageSecretMetadata.cs) reads PE structures using `PEReader` and `MetadataReader`, enumerates the `#US` user-string heap, and writes bounded UTF8 projections. It does not use `Assembly.Load`, resolve dependencies or execute inspected code. The projection maps findings back to numeric package entry IDs and heap offsets. Individual C0/DEL controls become line breaks; CR/CRLF become LF, preventing a NUL in an unrelated literal from making the whole projection look binary. Strict encoding rejects malformed UTF16 strings instead of declaring them clean.

All admitted nonbinary package entries are decoded as strict UTF8, or BOM-marked UTF16 LE/BE, and projected as UTF8. Unmarked text containing NUL is rejected as an unsupported encoding. Admitted native PE DLLs and `.so`, `.dylib`, `.a` and `.lib` files are explicitly classified as unqualified binary surfaces. Invalid or oversized PE metadata rejects the run; it is never silently counted as a clean managed assembly.

Gitleaks runs with built-in rules, an explicit empty ignore file, inline allow comments disabled, no baseline and `--redact=100`. Package/source ignore configuration is not used. The scanner environment is cleared: only `SystemRoot` and `WINDIR` are copied; `TEMP`, `TMP`, `HOME` and `USERPROFILE` point to the private run. No proxy, credential, startup hook, profiler, additional dependency or scanner-config variables are inherited. Its executable handle is retained and rehashed. The original process object is used for bounded wait/kill cleanup; descendant cleanup is not attested.

The public `findings.json` state is `NoFindingsInCoveredSurfaces` or `FindingsRequireReview`. Both indicate a completed scan; consumers must inspect the state and finding count. Findings expose rule ID, numeric entry ID, SHA256 of the archive-relative path, projection name/line, user-string heap offsets when applicable, and a verified redaction flag. They omit raw paths, secret/match/fingerprint fields, stdout and stderr. This also avoids exposing a token-shaped package filename. Public receipts include package entry byte hashes, the tool pin and hashes of the wrapper, metadata helper, reused publisher and private-directory helper.

The private `projection-index.json` binds the classified entries, projection ranges and projection-file hashes. `execution.json` pins that index, the same inputs, exact scanner argv and config/empty-ignore hashes. `findings.json` commits both files by SHA256. Input hashes are rechecked after scanning; helper files remain under read-only handles. A failure leaves only a generic `rejected.json` and any private partial workspace, without a successful findings receipt. Source/tool/ZIP/input pins are observations by the executing owner, not independent attestation or protection against a privileged owner.

## Limits

| Item | Enforced bound |
| --- | --- |
| Compressed ZIP; total actual expanded bytes | 256 MiB each |
| ZIP entries, including directories | 5,000 |
| Individual extracted file | 64 MiB |
| Outer manifest; dependency manifest; dependency report | 1 MiB; 4 KiB; 16 MiB |
| Managed `#US` heap; individual UTF8 literal | 16 MiB per DLL; 1 MiB |
| User strings | 100,000 per DLL; 200,000 per run |
| Managed projection chunk; total text/managed projection | 4 MiB; 128 MiB |
| Ordinary original/projected text | 16 MiB per file |
| Scanner raw report; each saved JSON artifact | 16 MiB |
| Scanner stdout and stderr | 1,048,576 characters each |
| Version probe; scan process deadline; cleanup wait | 10 seconds; 150 seconds; 5 seconds |
| Scanner internal timeout; maximum target; decode depth | 120 seconds; 32 MiB; 5 |

The process deadline is not a whole-run CPU/custody or scheduling guarantee. Oversized metadata, projections, reports or indexes fail closed. The scan does not inspect native/raw binary strings, `#Blob` constants/custom attributes, resources, other metadata heaps, runtime-generated/computed strings, encrypted or generally obfuscated secrets, Git history, local runtime caches or future changes. Limited scanner decoding does not establish coverage of every encoding. A no-findings result must not be described as full binary coverage or a clean/security release clearance. The [source-only review](../scripts/Test-CommercialSourceSecrets.ps1) remains separate.

## Local qualification

```powershell
pwsh -NoProfile -File tests/release/foundation-package-secrets.tests.ps1
```

The fixtures compile constant owned code with the explicit local .NET SDK, then inspect it as data. A synthetic credential-shaped value is present as UTF16 metadata and absent as ASCII. Actual pinned Gitleaks must detect its UTF8 projection, preserve heap provenance and omit the token/private paths from safe findings. A module initializer marker must remain absent. The existing coarse admission check rejects obvious credential-shaped text; whole-file text decoding can miss a PE literal at an odd byte offset, which the metadata extractor covers. A valid admitted package carrying that fixture must still require findings review when admission permits it. No real credential is used.

Other fixtures cover clean package scanning, malformed metadata, strict report/redaction/path validation, contradictory exits, fixed scanner pin, reused/foreign output refusal, unsafe ZIP path admission, BOM text, unknown projection files, cleared environment, and stalled/oversized-output process cleanup. Tests use no package code execution, cloud calls or primary application lifecycle changes.

To qualify a final new package, add `-PublishedArchivePath <absolute ZIP> -ExpectedArchiveSha256 <reviewed SHA256>` to the test command. This reruns synthetic checks and records a separate pinned actual scan; it does not assume the package has no findings. Prior revision/package scans do not cover newer bytes. The top-level [test workflow](../scripts/Test-Project.ps1) registers the synthetic profile. Local artifacts retain exact outcomes; broader commercial gates and production acceptance remain separate.
