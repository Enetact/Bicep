# Call the customer Foundation service

[Service architecture](plans/platform-studio-application-service-architecture.md) · [Foundation API](foundation-service.md) · [Qualification](validation.md#application-service-boundary-refactor--26-october-2026)

`SelfService.Foundation.Client` supplies typed job and artifact reads without importing Foundation storage, credential verification, the Portal executable or a web host. It references only the neutral Contracts project. Main applications call the owning Foundation service; the service still authenticates the caller, resolves current customer authority and verifies stored state.

The current API/worker/owner preview contains the Contracts assembly, while Client remains a separately built source library. SDK binary distribution and customer Portal adoption are not automatically enabled by that ZIP.

The current API runs on loopback, normally port 5107, and is not automatically started by Studio. The Portal has not automatically switched to this client. Configure an explicit customer connection and obtain a token for the Foundation API's audience and admitted caller. An Azure ARM/ADO/GitHub token or a desktop sign-in is not that authority.

## Application use

Reference `src/SelfService.Foundation.Client/SelfService.Foundation.Client.csproj`. Keep one client for the configured cell connection's lifetime to reuse its connection pool. Supply a fresh authorized API token per call; do not put credentials in endpoint options or default headers.

```csharp
using SelfService.Foundation.Client;
using SelfService.Foundation.Contracts;

// Trusted application configuration; identifiers must match the configured service cell.
using var foundation = new FoundationReadClient(new FoundationReadClientOptions(
    new Uri("http://127.0.0.1:5107/"),
    new FoundationServiceScope(customerId, cellId, projectId, environmentId)));

var job = await foundation.GetJobAsync(jobId, foundationApiAccessToken, cancellationToken);
var metadata = await foundation.GetArtifactMetadataAsync(artifactId, foundationApiAccessToken, cancellationToken);
var bytes = await foundation.GetArtifactContentAsync(artifactId, metadata.Sha256,
    foundationApiAccessToken, cancellationToken);
```

Do not log tokens, raw exception bodies, private artifact content or receipt payloads. An application can handle the sanitized `FoundationReadException.Code` and optional HTTP status. Caller cancellation remains `OperationCanceledException`. Authentication/authorization/unavailability failures should be shown explicitly; they must not trigger direct provider execution as a fallback.

## Checks owned by the client

- Endpoint authority and customer/cell/project/environment are fixed at construction. Requests cannot select another endpoint or supply a trusted `CustomerContext`.
- HTTP is restricted to loopback; HTTPS can be configured for a separately qualified future service. The client does not expose a remote API listener or qualify remote hosting. The production handler disables redirects, cookies, proxies and decompression, and pools connections.
- A successful response requires exact `X-Platform-Foundation-Cell` and `X-Platform-Foundation-Contract: 1` headers. Scope and object identity must match the requested resource. These headers are routing/version evidence, not credentials or execution permits.
- JSON rejects duplicate/unknown/null-required fields and incompatible shapes/states. Existing reconciled job states remain readable. Artifact reads verify SHA-256; binary downloads require a caller-pinned expected digest.
- Defaults are a fifteen-second request deadline, two MiB JSON and one MiB artifact bytes. Configurable limits remain bounded. Streaming limits apply even without Content-Length.
- Each operation sends one GET with an explicit per-request bearer token. There is no token cache, automatic retry, write/approval/adoption operation or fabricated deployment result.

The existing artifact JSON endpoint includes content. `GetArtifactMetadataAsync` verifies those bytes before projecting metadata; it is not a server-side metadata-only optimization. A later additive metadata endpoint can improve large-artifact traffic after compatibility and scope tests.

The neutral client and protocol assemblies are not a shared authorization engine. Identity validation, current grants, database transactions, execution custody and any future recovery mutation remain with their owning service. Customer Portal delegation, remote/service discovery, production identity/TLS/backend/capacity and commercial SDK/package distribution remain separate qualification work.
