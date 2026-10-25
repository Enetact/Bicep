using SelfService.Foundation;

namespace SelfService.Foundation.Host;

public sealed class FoundationTokenService
{
    readonly IdentityTokenValidator? pinned;
    readonly OidcIdentityValidator? discovery;
    public FoundationTokenService(FoundationHostOptions options)
    {
        if (options.Oidc is null) pinned = new(options.Identity);
        else discovery = new(options.Oidc with { Identity = options.Identity }, options.OidcTransport);
    }
    public Task<FoundationPrincipal> ValidateAsync(string token, CancellationToken ct = default) =>
        discovery is null ? pinned!.ValidateAsync(token, ct) : discovery.ValidateAsync(token, ct);
    public string Mode => discovery is null ? "pinned-public-keys" : "explicit-entra-oidc";
}
