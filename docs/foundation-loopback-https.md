# Optional loopback HTTPS

The dedicated foundation host can encrypt its local API traffic with an explicitly selected server certificate. This is local transport confidentiality, not production TLS approval, certificate provisioning, service-identity onboarding or a new authorization grant. The existing signed-token, customer/scope, role, exact-plan approval and worker checks still apply. No Azure deployment or provider sign-in is involved.

With no `loopbackHttps` configuration, the existing HTTP scheme and port remain: `http://127.0.0.1:5107` by default. Both modes bind one literal IPv4 loopback endpoint and ignore ambient Kestrel endpoint configuration, preventing an environment/appsettings endpoint from adding another listener. Trusted composition can select another nonprivileged port. Neither mode exposes a public/nonloopback address.

## Existing Windows-store certificate

An operator must separately provision an appropriate certificate and its chain, give the host account the necessary private-key access, and establish appropriate client trust. This feature performs none of those operations. The JSON contains only public selection metadata:

```json
{
  "loopbackHttps": {
    "expectedThumbprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
    "storeLocation": "CurrentUser"
  }
}
```

Add that object to the existing protected foundation configuration and replace the placeholder with the exact existing certificate's forty hexadecimal thumbprint characters. This Windows thumbprint selects a certificate; it does not replace chain or hostname validation. `storeLocation` is exactly `CurrentUser` (default) or `LocalMachine`; selection always uses the existing `My` store opened read-only. A missing, ambiguous or invalid selection fails startup. There is no development-certificate fallback.

Presence of this object requires HTTPS at `https://127.0.0.1:<listenPort>`. There is no configuration toggle to fall back to HTTP after failure. Plain HTTP cannot reach the TLS listener successfully; the application also rejects a non-HTTPS request before `/health` or authenticated routes. `X-Forwarded-Proto` does not override the scheme. HTTP requests are not redirected.

The application accepts no PFX path/password, private key, certificate import, arbitrary trust roots or key material in this object. Existing configuration parsing rejects those unknown fields. Keys and certificate bundles must never be added to source, runtime ZIPs or public examples. The application reads an already provisioned private-key context; it does not serialize or export the private key.

## Certificate and chain checks

Before a listener starts, the host requires the exact thumbprint, a private key, current validity, an explicit server-authentication EKU, a leaf certificate and a SAN containing exactly `localhost` or the IP address `127.0.0.1`. CN-only, wildcard/other-name-only, client-auth-only, expired, not-yet-valid and keyless certificates are rejected. A present key-usage extension must permit digital signatures. The validity period is also checked when selecting the certificate for each new TLS handshake. Clients must use a name covered by that certificate's SAN.

The default verifies the certificate chain against the system trust configuration with no verification-flag bypass. Certificate downloads are disabled and revocation mode is `NoCheck`: there is no online AIA, CRL or OCSP retrieval. This deliberately offline policy cannot establish current online revocation status; a missing required intermediate/root fails rather than downloading or accepting it. Operational revocation, trust distribution and certificate rotation remain separately governed work. Restart the dedicated host through its controlled lifecycle after replacing its selected certificate; this implementation does not install trust or rotate certificates automatically.

The .NET Windows chain implementation maps disabled downloads to both AIA and automatic-root-update suppression; see the [runtime chain flags](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/X509Certificates/ChainPal.Windows.BuildChain.cs). That implementation detail supports this offline boundary rather than proving certificate revocation status.

The listener permits TLS 1.2 and TLS 1.3 according to OS support, with no TLS 1.0/1.1 fallback and no validation callback that accepts certificate errors. HTTPS does not change provider credentials, Entra consent or customer permissions.

## Trusted composition and tests

Trusted application composition can supply a borrowed `X509Certificate2` through `FoundationHostOptions.LoopbackTls.Certificate`, still paired with `ExpectedThumbprint`. It can supply up to eight explicit CA certificate objects through `TrustedRoots` for a deliberate custom trust boundary, such as an ephemeral test CA. Nonempty roots select `CustomRootTrust`; empty roots retain system trust. Only public root snapshots enter chain validation. Neither setting is admitted by JSON configuration.

The caller owns an injected server certificate and must retain it until the host stops. A Windows-store selection is owned by a DI-created certificate lease, disposed with the host or immediately after failed validation. Certificate objects are resolved during Kestrel options creation, before listening; they are not acquired by merely reading public JSON metadata.

The focused qualification uses a real owned Kestrel listener, an ephemeral in-memory CA, short-lived server certificates and clients with an explicit custom-root chain policy plus normal hostname validation. Windows Schannel rejects in-memory-only server keys, so the Windows fixture creates a new uniquely named, nonexportable CNG signing key under `PlatformStudio-TlsFixture-<GUID>` without overwrite. It deletes only that exact created key handle/name on disposal, checks that the name no longer exists, and records created/deleted counts in per-test `PLATFORM_FOUNDATION_TLS_CLEANUP` evidence. It never enumerates or removes existing keys. The certificate is not installed in a user/machine certificate store; there is no PFX import/export or OS trust installation. The observed platform limitation is described in the [.NET runtime issue](https://github.com/dotnet/runtime/issues/23749).

These Windows tests require normal permission to create/delete their temporary OS key containers; abnormal termination can leave an untrusted synthetic test key for an explicitly reviewed cleanup. The fixture's socket connection callback admits only the owned literal loopback port, avoiding DNS/proxy egress. Cases cover signed API authorization over HTTPS, wrong client roots/names, invalid server certificate variants, TLS 1.2, legacy protocol rejection, plaintext downgrade, forwarded-scheme spoofing, ambient endpoint suppression and missing exact store selection.

```powershell
dotnet restore ./tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj --locked-mode --artifacts-path ./artifacts/foundation-tls-check -m:1 -nodeReuse:false
dotnet test ./tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj --no-restore --artifacts-path ./artifacts/foundation-tls-check -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false --filter FullyQualifiedName~FoundationLoopbackTlsTests --logger 'trx;LogFileName=foundation-tls.trx' --results-directory ./artifacts/test-results/foundation-tls
```

Use normal Windows permissions for real listener/native certificate qualification. An accepted fixture does not prove that a particular operator's existing certificate, chain/private-key ACL, client trust, production reverse proxy, service account or deployment environment is configured correctly. See [the foundation service](foundation-service.md), [security and operations](foundation-security-operations.md), and [the release plan](plans/platform-studio-commercial-release.md).

## Local result — 24 October 2026

The focused Release run passed all 19 tests with zero failures/skips and no build warnings on Windows Arm64/.NET 10.0.8. The TRX contains 27 per-test cleanup records: 16 uniquely owned temporary CNG keys were created and all 16 were deleted with exact-name absence checks. No certificate-store count snapshot was taken; the fixture code has no certificate-store write path. `artifacts/test-results/foundation-tls/foundation-tls.trx` and the derived `summary.json` retain that evidence. No operator certificate, online revocation policy, cloud service or production TLS deployment was accepted by this local result.
