using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class PipelineDispatchTests
{
    const string Customer = "10000000-0000-0000-0000-000000000001";
    const string Cell = "20000000-0000-0000-0000-000000000001";
    const string Project = "e1111111-1111-1111-1111-111111111111";
    const string Repository = "d1111111-1111-1111-1111-111111111111";
    static readonly string Revision = new('a', 40);
    static readonly string PlanDigest = new('b', 64);
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T12:00:00Z");

    internal static PipelineConnection Connection(string provider = PipelineRequestFactory.GitHub) =>
        new(Customer, Cell, "pipeline-a", provider, "customer-a",
            provider == PipelineRequestFactory.GitHub ? null : Project,
            provider == PipelineRequestFactory.GitHub ? "platform-infra" : Repository,
            provider == PipelineRequestFactory.GitHub ? "platform.yml" : "42",
            ["refs/heads/main", "refs/tags/v1.0"], ["dev"],
            ["location", "module", .. PipelineRequestFactory.ReservedInputs], "auth-profile/customer-a", 7)
        { EnforcesSourceRevision = true, PipelineVersion = provider == PipelineRequestFactory.GitHub ? null : 4 };

    static PipelineDispatchRequest Request(PipelineConnection connection) =>
        new(connection.CustomerId, connection.CellId, connection.Id, connection.Generation,
            "30000000-0000-0000-0000-000000000001", "refs/heads/main", "dev", Revision,
            new() { ["module"] = "avm/res/storage/storage-account", ["location"] = "eastus2" });

    static PipelineApproval Approval(PipelineConnection connection, PipelineDispatchRequest request) =>
        new(connection.CustomerId, connection.CellId, connection.Id, connection.Generation,
            PipelineRequestFactory.ConnectionDigest(connection), PipelineRequestFactory.RequestDigest(connection, request),
            PlanDigest, Now.AddMinutes(30));

    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pipelines", name));
    static PipelineDispatcher Dispatcher(IPipelineTransport transport) => new(transport, new FixedTime());

    [Theory]
    [InlineData("organization")]
    [InlineData("repository")]
    [InlineData("connectionId")]
    [InlineData("workflow")]
    [InlineData("environment")]
    [InlineData("inputKey")]
    public void Terminal_newlines_cannot_pass_connection_identifiers(string field)
    {
        var c = Connection();
        c = field switch
        {
            "organization" => c with { Organization = c.Organization + "\n" },
            "repository" => c with { Repository = c.Repository + "\n" },
            "connectionId" => c with { Id = c.Id + "\n" },
            "workflow" => c with { PipelineOrWorkflowId = c.PipelineOrWorkflowId + "\n" },
            "environment" => c with { AllowedEnvironments = ["dev\n"] },
            _ => c with { AllowedInputKeys = [.. c.AllowedInputKeys, "extra\n"] }
        };
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.ConnectionDigest(c));
    }

    [Fact]
    public void Terminal_newlines_cannot_pass_revision_or_approval_digest()
    {
        var c = Connection(); var r = Request(c); var a = Approval(c, r);
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.RequestDigest(c, r with { SourceRevision = Revision + "\n" }));
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.Create(c, r, a with { PlanDigest = PlanDigest + "\n" }, Now));
    }

    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, "github-workflow-dispatch-request.json", "github-workflow-dispatch-response.json", "9011203")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "azure-devops-run-pipeline-request.json", "azure-devops-run-pipeline-response.json", "440")]
    public async Task Serializes_real_provider_payload_and_accepts_correlated_queued_run_only_as_mock(
        string provider, string requestFixture, string responseFixture, string runId)
    {
        var connection = Connection(provider); var request = Request(connection);
        using var transport = new CapturedHttpTransport(HttpStatusCode.OK, Fixture(responseFixture));
        var result = await Dispatcher(transport).DispatchAsync(connection, request, Approval(connection, request));
        Assert.Equal("Accepted", result.State); Assert.Equal(runId, result.ExternalRunId); Assert.True(result.Mock);
        Assert.Equal("pipeline-run-accepted-not-verified", result.ReasonCode); Assert.Equal(1, transport.Calls);
        Assert.Equal(HttpMethod.Post, transport.Method);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Fixture(requestFixture)), JsonNode.Parse(transport.Body!)));
        Assert.DoesNotContain("Authorization", transport.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(connection.CredentialReference, transport.Body!);
        if (provider == PipelineRequestFactory.GitHub)
        {
            Assert.Equal("https://api.github.com/repos/customer-a/platform-infra/actions/workflows/platform.yml/dispatches", transport.Uri!.AbsoluteUri);
            Assert.Equal(PipelineRequestFactory.GitHubApiVersion, transport.Headers["X-GitHub-Api-Version"]);
        }
        else Assert.Equal($"https://dev.azure.com/customer-a/{Project}/_apis/pipelines/42/runs?pipelineVersion=4&api-version=7.1", transport.Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task Default_transport_denies_external_dispatch_even_with_an_exact_approval()
    {
        var connection = Connection(); var request = Request(connection);
        var result = await new PipelineDispatcher(clock: new FixedTime()).DispatchAsync(connection, request, Approval(connection, request));
        Assert.Equal("Rejected", result.State); Assert.Equal("pipeline-external-dispatch-disabled", result.ReasonCode);
        Assert.Null(result.ExternalRunId); Assert.True(result.Mock);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("connection")]
    [InlineData("generation")]
    [InlineData("ref")]
    [InlineData("environment")]
    [InlineData("source")]
    [InlineData("input")]
    [InlineData("request-id")]
    public async Task Request_scope_or_payload_drift_after_approval_never_reaches_transport(string change)
    {
        var connection = Connection(); var request = Request(connection); var approval = Approval(connection, request);
        request = change switch
        {
            "customer" => request with { CustomerId = "10000000-0000-0000-0000-000000000002" },
            "cell" => request with { CellId = "20000000-0000-0000-0000-000000000002" },
            "connection" => request with { ConnectionId = "pipeline-b" },
            "generation" => request with { ConnectionGeneration = 8 },
            "ref" => request with { Ref = "refs/tags/v1.0" },
            "environment" => request with { Environment = "prod" },
            "source" => request with { SourceRevision = new string('c', 40) },
            "input" => request with { Inputs = new() { ["location"] = "westus2" } },
            _ => request with { RequestId = "30000000-0000-0000-0000-000000000002" }
        };
        using var transport = new CapturedHttpTransport(HttpStatusCode.OK, Fixture("github-workflow-dispatch-response.json"));
        var result = await Dispatcher(transport).DispatchAsync(connection, request, approval);
        Assert.Equal("Rejected", result.State); Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("organization")]
    [InlineData("repository")]
    [InlineData("workflow")]
    [InlineData("auth-profile")]
    [InlineData("generation")]
    [InlineData("allowlist")]
    public async Task Connection_configuration_is_bound_to_approval_and_cannot_be_retargeted(string change)
    {
        var connection = Connection(); var request = Request(connection); var approval = Approval(connection, request);
        connection = change switch
        {
            "customer" => connection with { CustomerId = "10000000-0000-0000-0000-000000000002" },
            "cell" => connection with { CellId = "20000000-0000-0000-0000-000000000002" },
            "organization" => connection with { Organization = "customer-b" },
            "repository" => connection with { Repository = "other-repository" },
            "workflow" => connection with { PipelineOrWorkflowId = "other.yml" },
            "auth-profile" => connection with { CredentialReference = "auth-profile/customer-b" },
            "generation" => connection with { Generation = 8 },
            _ => connection with { AllowedEnvironments = ["dev", "prod"] }
        };
        using var transport = new CapturedHttpTransport(HttpStatusCode.OK, "{}");
        var result = await Dispatcher(transport).DispatchAsync(connection, request, approval);
        Assert.Equal("Rejected", result.State); Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public void Fingerprints_are_stable_across_object_order_but_include_credential_reference_and_source_guard()
    {
        var a = Connection(); var request = Request(a);
        var b = a with { AllowedRefs = a.AllowedRefs.Reverse().ToArray(), AllowedInputKeys = a.AllowedInputKeys.Reverse().ToArray() };
        var reordered = request with { Inputs = new() { ["location"] = "eastus2", ["module"] = "avm/res/storage/storage-account" } };
        Assert.Equal(PipelineRequestFactory.ConnectionDigest(a), PipelineRequestFactory.ConnectionDigest(b));
        Assert.Equal(PipelineRequestFactory.RequestDigest(a, request), PipelineRequestFactory.RequestDigest(b, reordered));
        Assert.NotEqual(PipelineRequestFactory.ConnectionDigest(a), PipelineRequestFactory.ConnectionDigest(a with { CredentialReference = "auth-profile/rotated" }));
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.ConnectionDigest(a with { EnforcesSourceRevision = false }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    public async Task Approval_must_be_unexpired_and_have_bounded_lifetime(int minutes)
    {
        var connection = Connection(); var request = Request(connection);
        using var transport = new CapturedHttpTransport(HttpStatusCode.OK, "{}");
        var result = await Dispatcher(transport).DispatchAsync(connection, request,
            Approval(connection, request) with { ExpiresUtc = Now.AddMinutes(minutes) });
        Assert.Equal("pipeline-approval-binding-invalid", result.ReasonCode); Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("connection-digest")]
    [InlineData("request-digest")]
    [InlineData("plan-digest")]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("generation")]
    public async Task Approval_provenance_fields_must_match_server_bound_connection_and_payload(string field)
    {
        var connection = Connection(); var request = Request(connection); var approval = Approval(connection, request);
        approval = field switch
        {
            "connection-digest" => approval with { ConnectionDigest = new string('c', 64) },
            "request-digest" => approval with { RequestDigest = new string('c', 64) },
            "plan-digest" => approval with { PlanDigest = "invalid" },
            "customer" => approval with { CustomerId = "10000000-0000-0000-0000-000000000002" },
            "cell" => approval with { CellId = "20000000-0000-0000-0000-000000000002" },
            _ => approval with { ConnectionGeneration = 8 }
        };
        using var transport = new CapturedHttpTransport(HttpStatusCode.OK, "{}");
        var result = await Dispatcher(transport).DispatchAsync(connection, request, approval);
        Assert.Equal("Rejected", result.State); Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("studio_request_id")]
    [InlineData("STUDIO_REQUEST_ID")]
    [InlineData("studio_plan_digest")]
    [InlineData("studio_source_revision")]
    [InlineData("studio_environment")]
    [InlineData("unapproved")]
    [InlineData("azure_token")]
    public void Callers_cannot_override_reserved_inputs_or_submit_unapproved_or_secret_shaped_inputs(string key)
    {
        var connection = Connection(); var request = Request(connection);
        request.Inputs[key] = "value";
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.RequestDigest(connection, request));
    }

    [Theory]
    [InlineData("Bearer abcdefghijklmnop")]
    [InlineData("ghp_exampleNeverARealCredential")]
    [InlineData("github_pat_exampleNeverARealCredential")]
    [InlineData("DefaultEndpointsProtocol=https;AccountKey=synthetic-only")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    public void Secrets_are_rejected_in_parameter_values_instead_of_forwarded_to_provider(string value)
    {
        var connection = Connection(); var request = Request(connection); request.Inputs["location"] = value;
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.RequestDigest(connection, request));
    }

    [Theory]
    [InlineData("https://attacker.invalid/repo")]
    [InlineData("../repo")]
    [InlineData("repo/name")]
    public void Server_configuration_cannot_use_a_repository_as_a_url_or_path(string repository)
    {
        var connection = Connection() with { Repository = repository };
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.ValidateConnection(connection));
    }

    [Fact]
    public void GitHub_requires_reviewed_source_revision_guard_and_all_correlation_input_declarations()
    {
        var connection = Connection();
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.ValidateConnection(connection with { EnforcesSourceRevision = false }));
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.ValidateConnection(connection with { AllowedInputKeys = ["location"] }));
        Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.ValidateConnection(connection with { AllowedInputKeys = [.. connection.AllowedInputKeys, "Location"] }));
    }

    [Theory]
    [InlineData("+42")]
    [InlineData(" 42")]
    [InlineData("042")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("42/../../9")]
    public void Ado_pipeline_identifier_must_be_a_canonical_positive_integer(string pipelineId)
        => Assert.Throws<PipelineContractException>(() => PipelineRequestFactory.ValidateConnection(
            Connection(PipelineRequestFactory.AzureDevOps) with { PipelineOrWorkflowId = pipelineId }));

    [Theory]
    [InlineData(202)]
    [InlineData(204)]
    public async Task Acceptance_without_a_correlated_run_identity_stays_unknown_and_is_not_retried(int status)
    {
        var connection = Connection(); var request = Request(connection);
        using var transport = new CapturedHttpTransport((HttpStatusCode)status, "");
        var result = await Dispatcher(transport).DispatchAsync(connection, request, Approval(connection, request));
        Assert.Equal("OutcomeUnknown", result.State); Assert.Null(result.ExternalRunId); Assert.Equal(1, transport.Calls);
        Assert.Equal("pipeline-accepted-without-correlated-run", result.ReasonCode);
    }

    [Theory]
    [InlineData(400, "Rejected")]
    [InlineData(401, "Rejected")]
    [InlineData(403, "Rejected")]
    [InlineData(404, "Rejected")]
    [InlineData(408, "OutcomeUnknown")]
    [InlineData(429, "Rejected")]
    [InlineData(500, "OutcomeUnknown")]
    [InlineData(503, "OutcomeUnknown")]
    public async Task Http_failures_are_bounded_receipts_without_upstream_secrets_or_post_retries(int status, string expected)
    {
        var connection = Connection(); var request = Request(connection);
        using var transport = new CapturedHttpTransport((HttpStatusCode)status, "Bearer synthetic-only-upstream-detail");
        var result = await Dispatcher(transport).DispatchAsync(connection, request, Approval(connection, request));
        Assert.Equal(expected, result.State); Assert.Equal(1, transport.Calls); Assert.Null(result.ExternalRunId);
        Assert.DoesNotContain("Bearer", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("http")]
    [InlineData("io")]
    public async Task Interrupted_post_is_unknown_and_never_automatically_retried(string failure)
    {
        var connection = Connection(); var request = Request(connection);
        using var transport = new CapturedHttpTransport(failure);
        var result = await Dispatcher(transport).DispatchAsync(connection, request, Approval(connection, request));
        Assert.Equal("OutcomeUnknown", result.State); Assert.Equal(1, transport.Calls); Assert.Null(result.ExternalRunId);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_has_no_side_effect()
    {
        var connection = Connection(); var request = Request(connection);
        using var transport = new CapturedHttpTransport(HttpStatusCode.OK, "{}");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var result = await Dispatcher(transport).DispatchAsync(connection, request, Approval(connection, request), cancelled.Token);
        Assert.Equal("Rejected", result.State); Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("organization")]
    [InlineData("host")]
    [InlineData("run-id")]
    [InlineData("query")]
    [InlineData("userinfo")]
    [InlineData("sha")]
    [InlineData("missing-url")]
    [InlineData("duplicate-key")]
    [InlineData("invalid-json")]
    public async Task GitHub_response_cannot_verify_a_different_repository_host_run_or_source(string change)
    {
        var connection = Connection(); var request = Request(connection);
        var response = Fixture("github-workflow-dispatch-response.json"); var document = JsonNode.Parse(response)!.AsObject();
        switch (change)
        {
            case "repository": document["run_url"] = document["run_url"]!.GetValue<string>().Replace("platform-infra", "other-repo"); break;
            case "organization": document["html_url"] = document["html_url"]!.GetValue<string>().Replace("customer-a", "customer-b"); break;
            case "host": document["run_url"] = "https://api.github.com.attacker.invalid/repos/customer-a/platform-infra/actions/runs/9011203"; break;
            case "run-id": document["workflow_run_id"] = 9011204; break;
            case "query": document["html_url"] = document["html_url"]!.GetValue<string>() + "?credential=synthetic"; break;
            case "userinfo": document["run_url"] = "https://user@api.github.com/repos/customer-a/platform-infra/actions/runs/9011203"; break;
            case "sha": document["head_sha"] = new string('c', 40); break;
            case "missing-url": document.Remove("run_url"); break;
        }
        response = change switch { "duplicate-key" => "{\"workflow_run_id\":9011203,\"WORKFLOW_RUN_ID\":9011204}", "invalid-json" => "not json", _ => document.ToJsonString() };
        using var transport = new CapturedHttpTransport(HttpStatusCode.OK, response);
        var result = await Dispatcher(transport).DispatchAsync(connection, request, Approval(connection, request));
        Assert.Equal("OutcomeUnknown", result.State); Assert.Null(result.ExternalRunId); Assert.Null(result.Url);
    }

    [Theory]
    [InlineData("pipeline")]
    [InlineData("project")]
    [InlineData("organization")]
    [InlineData("repository")]
    [InlineData("source")]
    [InlineData("ref")]
    [InlineData("host")]
    public async Task Ado_response_cannot_verify_a_different_customer_pipeline_or_source(string change)
    {
        var connection = Connection(PipelineRequestFactory.AzureDevOps); var request = Request(connection);
        var document = JsonNode.Parse(Fixture("azure-devops-run-pipeline-response.json"))!.AsObject();
        switch (change)
        {
            case "pipeline": document["pipeline"]!["id"] = 43; break;
            case "project": document["url"] = document["url"]!.GetValue<string>().Replace(Project, "e2222222-2222-2222-2222-222222222222"); break;
            case "organization": document["url"] = document["url"]!.GetValue<string>().Replace("customer-a", "customer-b"); break;
            case "repository": document["resources"]!["repositories"]!["self"]!["repository"]!["id"] = "d2222222-2222-2222-2222-222222222222"; break;
            case "source": document["resources"]!["repositories"]!["self"]!["version"] = new string('c', 40); break;
            case "ref": document["resources"]!["repositories"]!["self"]!["refName"] = "refs/heads/other"; break;
            case "host": document["url"] = document["url"]!.GetValue<string>().Replace("dev.azure.com", "attacker.invalid"); break;
        }
        using var transport = new CapturedHttpTransport(HttpStatusCode.OK, document.ToJsonString());
        var result = await Dispatcher(transport).DispatchAsync(connection, request, Approval(connection, request));
        Assert.Equal("OutcomeUnknown", result.State); Assert.Null(result.ExternalRunId);
    }

    [Fact]
    public async Task Oversized_response_is_not_persisted_or_trusted()
    {
        var connection = Connection(); var request = Request(connection);
        using var transport = new CapturedHttpTransport(HttpStatusCode.OK, new string('a', 262145));
        var result = await Dispatcher(transport).DispatchAsync(connection, request, Approval(connection, request));
        Assert.Equal("OutcomeUnknown", result.State); Assert.Null(result.ExternalRunId);
    }

    sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    // Captures a real HttpRequestMessage through HttpClient without creating any socket or using credentials.
    sealed class CapturedHttpTransport : HttpMessageHandler, IPipelineTransport
    {
        readonly HttpClient http; readonly HttpStatusCode status; readonly string response; readonly string? failure;
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CapturedHttpTransport(HttpStatusCode status, string response)
        { this.status = status; this.response = response; http = new(this, disposeHandler: false); }
        public CapturedHttpTransport(string failure) : this(HttpStatusCode.OK, "{}") { this.failure = failure; }
        public async Task<PipelineWireResponse> SendAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
        {
            using var message = new HttpRequestMessage(new(wire.Method), wire.Uri)
            { Content = new StringContent(wire.BodyJson, Encoding.UTF8, "application/json") };
            foreach (var pair in wire.Headers) message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            using var result = await http.SendAsync(message, cancellationToken);
            return new((int)result.StatusCode, await result.Content.ReadAsStringAsync(cancellationToken));
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Method = request.Method; Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            foreach (var pair in request.Headers) Headers[pair.Key] = string.Join(",", pair.Value);
            switch (failure)
            {
                case "timeout": throw new TimeoutException("synthetic-only");
                case "cancel": throw new OperationCanceledException("synthetic-only");
                case "http": throw new HttpRequestException("synthetic-only");
                case "io": throw new IOException("synthetic-only");
            }
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { if (disposing) http.Dispose(); base.Dispose(disposing); }
    }
}
