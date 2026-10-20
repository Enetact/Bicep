using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SelfService.Portal;
using SelfService.Telemetry;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class AgentTelemetryTests
{
    const string Sensitive = "private-prompt-token-account-thread-canary";
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    static JsonElement Tool(string name = "platform_evidence") => Json(new { tool = name, arguments = new { } });

    sealed class Capture : IDisposable
    {
        readonly ConcurrentQueue<Activity> completed = new();
        readonly ActivityListener listener;
        public Capture()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == PlatformTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => completed.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(listener);
        }
        public Activity[] For(ActivityTraceId trace) => completed.Where(activity => activity.TraceId == trace).ToArray();
        public void Dispose() => listener.Dispose();
    }

    // A protocol reader is created before a later HTTP/AHP turn. Suppressing flow here
    // exercises that actual execution-context boundary instead of inheriting the test parent.
    static Task<T> OnReader<T>(Func<Task<T>> callback)
    {
        using (ExecutionContext.SuppressFlow()) return Task.Run(callback);
    }
    static void AssertContentFree(IEnumerable<Activity> spans)
    {
        foreach (var span in spans)
        {
            var serialized = JsonSerializer.Serialize(new
            {
                span.DisplayName, span.StatusDescription,
                tags = span.TagObjects.ToArray(),
                events = span.Events.Select(e => new { e.Name, tags = e.Tags.ToArray() })
            });
            Assert.DoesNotContain(Sensitive, serialized, StringComparison.Ordinal);
            Assert.Empty(span.Events);
            Assert.DoesNotContain(span.TagObjects, tag => tag.Key.Contains("session.id", StringComparison.Ordinal) ||
                tag.Key.Contains("conversation.id", StringComparison.Ordinal) || tag.Key.Contains("request.id", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task IndependentReaderAndRealMcpRoundTripRetainOriginatingAgentParent()
    {
        using var capture = new Capture();
        using var request = PlatformTelemetry.Start("test.agent.request");
        var requestActivity = Assert.IsType<Activity>(request.Activity);
        await using var bridge = await AgentMcpBridge.Create(Sensitive, Sensitive, () => true, CancellationToken.None);
        var handoff = new AgentTurnHandoff();
        ActivityContext invocationContext = default;
        await CodexAgentRuntime.ObserveInvocation(async parent =>
        {
            invocationContext = parent;
            var turn = handoff.Begin(Sensitive, parent, bridge.Call, CancellationToken.None);
            try
            {
                var output = await OnReader(async () =>
                {
                    using var unrelatedConnection = new Activity("stale.connection").Start();
                    var result = await handoff.Invoke(turn, Tool());
                    Assert.Same(unrelatedConnection, Activity.Current);
                    return result;
                });
                Assert.Contains(Sensitive, JsonSerializer.Serialize(output));
                return Sensitive;
            }
            finally { handoff.End(turn); }
        });
        Assert.Same(requestActivity, Activity.Current);
        var spans = capture.For(requestActivity.TraceId);
        var invocation = Assert.Single(spans, a => a.OperationName == "invoke_agent platform-studio");
        var callback = Assert.Single(spans, a => a.OperationName == "codex.tool_callback");
        var mcp = Assert.Single(spans, a => a.OperationName == "mcp.evidence.call");
        Assert.Equal(requestActivity.SpanId, invocation.ParentSpanId);
        Assert.Equal(invocationContext.SpanId, callback.ParentSpanId);
        Assert.Equal(callback.SpanId, mcp.ParentSpanId);
        Assert.Equal("tools/call", mcp.GetTagItem("mcp.method.name"));
        Assert.Equal("platform_evidence", mcp.GetTagItem("mcp.tool.name"));
        Assert.Equal(ActivityStatusCode.Ok, mcp.Status);
        AssertContentFree(spans);
    }

    [Fact]
    public async Task CancelledAndLateCallbacksCannotAttachToSiblingOrReuseCompletedLease()
    {
        using var capture = new Capture();
        using var first = PlatformTelemetry.Start("test.first.turn");
        var firstContext = Assert.IsType<Activity>(first.Activity).Context;
        var handoff = new AgentTurnHandoff();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTurn = handoff.Begin(Sensitive, firstContext, async (_, _) =>
        {
            entered.SetResult(); await release.Task; return Sensitive;
        }, CancellationToken.None);
        var oldCallback = OnReader(() => handoff.Invoke(firstTurn, Tool()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handoff.End(firstTurn);

        using var sibling = PlatformTelemetry.Start("test.sibling.turn", parentContext: new ActivityContext(
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded));
        var siblingContext = Assert.IsType<Activity>(sibling.Activity).Context;
        Assert.NotEqual(firstContext.TraceId, siblingContext.TraceId);
        var count = 0;
        var siblingTurn = handoff.Begin("other-" + Sensitive, siblingContext, (_, _) =>
        {
            Interlocked.Increment(ref count); return Task.FromResult<object>(new { success = true });
        }, CancellationToken.None);
        try
        {
            Assert.Null(handoff.Find(Sensitive));
            await Assert.ThrowsAsync<PortalException>(() => OnReader(() => handoff.Invoke(firstTurn, Tool())));
            handoff.End(firstTurn); // A stale finally must not clear the newer turn.
            Assert.Same(siblingTurn, handoff.Find("other-" + Sensitive));
            await OnReader(() => handoff.Invoke(siblingTurn, Tool()));
            Assert.Equal(1, count);
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldCallback);
            siblingTurn.Finished.TrySetResult("completed");
            Assert.Null(handoff.Find("other-" + Sensitive));
            await Assert.ThrowsAsync<PortalException>(() => handoff.Invoke(siblingTurn, Tool()));
        }
        finally { release.TrySetResult(); handoff.End(siblingTurn); }

        var callbacks = capture.For(firstContext.TraceId).Concat(capture.For(siblingContext.TraceId)).Where(a => a.OperationName == "codex.tool_callback").ToArray();
        Assert.Equal(2, callbacks.Length);
        var cancelled = Assert.Single(callbacks, a => a.ParentSpanId == firstContext.SpanId);
        Assert.Equal("canceled", cancelled.GetTagItem("platform.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, cancelled.Status);
        Assert.Single(callbacks, a => a.ParentSpanId == siblingContext.SpanId && a.Status == ActivityStatusCode.Ok);
        AssertContentFree(callbacks);
    }

    [Fact]
    public async Task ReaderWithNoTurnContextNeverInheritsEarlierConnectionActivity()
    {
        using var capture = new Capture();
        var handoff = new AgentTurnHandoff();
        ActivityContext callbackContext = default;
        var turn = handoff.Begin(Sensitive, default, (_, _) =>
        {
            callbackContext = Activity.Current!.Context;
            return Task.FromResult<object>(new { success = true });
        }, CancellationToken.None);
        try
        {
            await OnReader(async () =>
            {
                using var stale = new Activity("stale.connection").Start();
                var staleTrace = stale.TraceId;
                await handoff.Invoke(turn, Tool());
                Assert.Same(stale, Activity.Current);
                Assert.NotEqual(staleTrace, callbackContext.TraceId);
                Assert.Equal(default, Assert.Single(capture.For(callbackContext.TraceId)).ParentSpanId);
                Assert.Empty(capture.For(staleTrace));
                return true;
            });
        }
        finally { handoff.End(turn); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentInvocationRecordsOnlyObservedBoundaryAndSafeFailureType(bool cancelled)
    {
        using var capture = new Capture();
        using var parent = PlatformTelemetry.Start("test.invocation");
        var trace = Assert.IsType<Activity>(parent.Activity).TraceId;
        var error = cancelled ? (Exception)new OperationCanceledException(Sensitive) : new InvalidOperationException(Sensitive);
        await Assert.ThrowsAsync(error.GetType(), () => CodexAgentRuntime.ObserveInvocation(_ => Task.FromException<string>(error)));
        var span = Assert.Single(capture.For(trace), a => a.OperationName == "invoke_agent platform-studio");
        Assert.Equal("invoke_agent", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal(AgentPolicy.Model, span.GetTagItem("gen_ai.request.model"));
        Assert.Equal(error.GetType().FullName, span.GetTagItem("error.type"));
        Assert.Equal(cancelled ? "canceled" : "error", span.GetTagItem("platform.outcome"));
        Assert.Equal(cancelled ? ActivityStatusCode.Unset : ActivityStatusCode.Error, span.Status);
        Assert.DoesNotContain(span.TagObjects, t => t.Key.StartsWith("gen_ai.usage.", StringComparison.Ordinal) || t.Key == "gen_ai.response.model");
        AssertContentFree([span]);
    }

    [Fact]
    public async Task McpRejectedToolAndArgumentsDoNotBecomeTraceMetadata()
    {
        using var capture = new Capture();
        using var parent = PlatformTelemetry.Start("test.rejected.mcp");
        var trace = Assert.IsType<Activity>(parent.Activity).TraceId;
        await using var bridge = await AgentMcpBridge.Create(Sensitive, Sensitive, () => true, CancellationToken.None);
        await Assert.ThrowsAsync<PortalException>(() => bridge.Call(Json(new { tool = Sensitive, arguments = new { password = Sensitive } }), CancellationToken.None));
        var span = Assert.Single(capture.For(trace), a => a.OperationName == "mcp.evidence.call");
        Assert.Equal("unregistered", span.GetTagItem("mcp.tool.name"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(PortalException).FullName, span.GetTagItem("error.type"));
        AssertContentFree(capture.For(trace));
    }

    [Fact]
    public async Task AhpMessagesAreTypedSiblingsAndExcludeUntrustedEnvelopeContent()
    {
        using var capture = new Capture();
        using var channel = PlatformTelemetry.Start("test.ahp.connection");
        var channelActivity = Assert.IsType<Activity>(channel.Activity);
        var dispatch = Json(new { method = "dispatchAction", id = Sensitive, @params = new { channel = Sensitive, action = new { type = "platform/workflowRun", request = Sensitive } }, traceparent = Sensitive });
        await AgentHostChannel.ObserveMessage(dispatch, async () => { await Task.Yield(); return Sensitive; });
        var unsupported = Json(new { method = Sensitive, id = Sensitive, @params = new { action = new { type = Sensitive } } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentHostChannel.ObserveMessage<string>(unsupported, () => throw new InvalidOperationException(Sensitive)));
        var spans = capture.For(channelActivity.TraceId).Where(a => a.OperationName == "ahp.message").ToArray();
        Assert.Equal(2, spans.Length);
        Assert.All(spans, span => Assert.Equal(channelActivity.SpanId, span.ParentSpanId));
        Assert.Single(spans, a => Equals(a.GetTagItem("rpc.method"), "dispatchAction") && Equals(a.GetTagItem("platform.action"), "platform/workflowRun"));
        Assert.Single(spans, a => Equals(a.GetTagItem("rpc.method"), "unsupported") && Equals(a.GetTagItem("platform.action"), "none") && a.Status == ActivityStatusCode.Error);
        AssertContentFree(spans);
    }

    [Fact]
    public async Task WorkflowPreflightFailureIsTracedWithoutRequestedScopeOrGoal()
    {
        using var capture = new Capture();
        using var parent = PlatformTelemetry.Start("test.workflow");
        var trace = Assert.IsType<Activity>(parent.Activity).TraceId;
        using var session = new BrowserSession(new());
        var workflows = new AgentWorkflows(new(), null!, null!, null!);
        var request = new AgentRequest("visualize", Sensitive, Sensitive, Sensitive, Sensitive, Sensitive, null, DraftGoal: Sensitive);
        await Assert.ThrowsAsync<PortalException>(() => workflows.Run(session, request, CancellationToken.None));
        var span = Assert.Single(capture.For(trace), a => a.OperationName == "agent.workflow");
        Assert.Equal("visualize", span.GetTagItem("platform.workload"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        AssertContentFree([span]);
    }

    [Fact]
    public void CodexChildDoesNotInheritExporterSecretsOrUnsupportedTraceEnvelope()
    {
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = "system-path", ["OTEL_EXPORTER_OTLP_HEADERS"] = Sensitive,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = Sensitive, ["OTEL_SERVICE_NAME"] = Sensitive,
            ["TRACEPARENT"] = Sensitive, ["TRACESTATE"] = Sensitive, ["BAGGAGE"] = Sensitive
        };
        CodexAgentRuntime.ConfigureEnvironment(environment, "isolated-home");
        Assert.Equal(2, environment.Count);
        Assert.Equal("system-path", environment["PATH"]);
        Assert.Equal("isolated-home", environment["CODEX_HOME"]);
        Assert.Equal("other", CodexAgentRuntime.ObservedMethod(Sensitive));
    }
}
