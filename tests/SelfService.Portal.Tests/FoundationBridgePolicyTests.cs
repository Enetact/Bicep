using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class FoundationBridgePolicyTests
{
    [Fact]
    public async Task Concurrent_model_calls_cannot_exceed_budget_or_grow_audit_without_bound()
    {
        await using var bridge = await AgentMcpBridge.Create("frozen evidence", "pinned instructions", () => true, CancellationToken.None);
        var dispatched = 0;
        bridge.EnableFoundation(async (_, _, ct) =>
        {
            Interlocked.Increment(ref dispatched); await Task.Yield(); ct.ThrowIfCancellationRequested();
            return new { accepted = true, deploymentVerified = false };
        });
        var request = JsonSerializer.SerializeToElement(new { tool = "platform_foundation_catalog", arguments = new { } });
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            try { await bridge.Call(request, CancellationToken.None); } catch (PortalException) { }
        }));
        Assert.Equal(48, dispatched); Assert.InRange(bridge.Audit.Count, 48, 64);
        Assert.Equal(9, bridge.Tools.Length);
    }
}
