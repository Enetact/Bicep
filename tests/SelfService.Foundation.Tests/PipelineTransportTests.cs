using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class PipelineTransportTests
{
    [Theory]
    [InlineData("https://127.0.0.1:5123/")]
    [InlineData("http://localhost:5123/")]
    [InlineData("http://dev.azure.com:5123/")]
    [InlineData("http://10.0.0.1:5123/")]
    [InlineData("http://127.0.0.1:80/")]
    [InlineData("http://127.0.0.1:5123/path")]
    [InlineData("http://user@127.0.0.1:5123/")]
    [InlineData("http://127.0.0.1:5123/?target=https://attacker.invalid")]
    [InlineData("http://127.0.0.1:5123/#fragment")]
    [InlineData("relative-path")]
    public void Fixture_transport_only_accepts_an_explicit_literal_loopback_http_origin(string origin)
    {
        var error = Assert.Throws<PipelineContractException>(() => new PipelineLoopbackTransport(new Uri(origin, UriKind.RelativeOrAbsolute)));
        Assert.Equal("pipeline-fixture-origin-invalid", error.ReasonCode);
    }

    [Theory]
    [InlineData("http://api.github.com/repos/a/b/actions/workflows/c.yml/dispatches")]
    [InlineData("https://attacker.invalid/repos/a/b/actions/workflows/c.yml/dispatches")]
    [InlineData("https://api.github.com:8443/repos/a/b/actions/workflows/c.yml/dispatches")]
    [InlineData("https://user@api.github.com/repos/a/b/actions/workflows/c.yml/dispatches")]
    public async Task Fixture_transport_rejects_untrusted_upstream_targets_before_opening_a_socket(string upstream)
    {
        using var transport = new PipelineLoopbackTransport(new Uri("http://127.0.0.1:5123/"));
        var wire = new PipelineWireRequest("POST", new Uri(upstream), new Dictionary<string, string>(), "{}",
            new("customer-a", "cell-a", "connection-a", 1, "request-a", "digest", "revision", "auth-profile/a"));
        var error = await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(wire, default));
        Assert.Equal("pipeline-fixture-upstream-invalid", error.ReasonCode);
    }

    [Fact]
    public async Task Fixture_transport_never_forwards_authorization_headers()
    {
        using var transport = new PipelineLoopbackTransport(new Uri("http://127.0.0.1:5123/"));
        var wire = new PipelineWireRequest("POST", new Uri("https://api.github.com/repos/a/b/actions/workflows/c.yml/dispatches"),
            new Dictionary<string, string> { ["Authorization"] = "Bearer synthetic-only" }, "{}",
            new("customer-a", "cell-a", "connection-a", 1, "request-a", "digest", "revision", "auth-profile/a"));
        var error = await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(wire, default));
        Assert.Equal("pipeline-fixture-header-invalid", error.ReasonCode);
    }
}
