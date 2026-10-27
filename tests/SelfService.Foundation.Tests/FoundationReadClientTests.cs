using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation.Client;
using SelfService.Foundation.Contracts;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationReadClientTests
{
    const string Cell = "00000000-0000-0000-0000-000000000003";
    const string SyntheticToken = "synthetic-foundation-api-token";
    static readonly FoundationServiceScope Scope = new(FoundationApiTests.ApiCell.Customer, Cell, "default", "dev");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static FoundationReadClientOptions Options(FoundationServiceScope? scope = null) => new(new Uri("http://localhost"), scope ?? Scope);
    static string Token(FoundationApiTests.ApiCell cell, string name = "viewer")
    { using var request = cell.Request(name, HttpMethod.Get, "/jobs"); return request.Headers.Authorization!.Parameter!; }

    [Fact]
    public async Task Typed_client_reads_actual_signed_job_status_and_correlated_mock_receipt()
    {
        await using var cell = await FoundationApiTests.ApiCell.Start();
        var plan = await cell.Plan(); await cell.Approve(plan); var job = await cell.Enqueue(plan);
        using var client = new FoundationReadClient(Options(), cell.App.GetTestServer().CreateHandler());
        var queued = await client.GetJobAsync(job.Id, Token(cell));
        Assert.Equal("Queued", queued.State); Assert.Equal(plan.Id, queued.PlanId); Assert.Equal(Scope.CellId, queued.CellId);
        Assert.Empty(cell.Transport.Requests);
        Assert.True(await cell.App.Services.GetRequiredService<FoundationJobRunner>().RunOnceAsync("typed-read-worker"));
        var complete = await client.GetJobAsync(job.Id, Token(cell));
        Assert.Equal("Succeeded", complete.State);
        using var receipt = JsonDocument.Parse(complete.ReceiptJson!);
        Assert.True(receipt.RootElement.GetProperty("mock").GetBoolean());
        Assert.False(receipt.RootElement.GetProperty("deploymentVerified").GetBoolean());
    }

    [Fact]
    public async Task Typed_client_reads_actual_scoped_artifact_metadata_and_digest_pinned_download()
    {
        await using var cell = await FoundationApiTests.ApiCell.Start();
        using var response = await cell.Send("requester", HttpMethod.Post, "/artifacts", new ArtifactCommand("read-evidence.json", "{\"synthetic\":true}"));
        response.EnsureSuccessStatusCode(); var saved = (await response.Content.ReadFromJsonAsync<FoundationArtifact>())!;
        using var client = new FoundationReadClient(Options(), cell.App.GetTestServer().CreateHandler());
        var artifact = await client.GetArtifactAsync(saved.Id, Token(cell));
        var metadata = await client.GetArtifactMetadataAsync(saved.Id, Token(cell));
        var bytes = await client.GetArtifactContentAsync(saved.Id, metadata.Sha256, Token(cell));
        Assert.Equal(saved.Content, artifact.Content); Assert.Equal(saved.Content, bytes);
        Assert.Equal(saved.Sha256, metadata.Sha256); Assert.Equal(Scope.CellId, metadata.CellId); Assert.Equal(bytes.Length, metadata.Bytes);
        Assert.Empty(cell.Transport.Requests);
    }

    [Theory]
    [InlineData("Succeeded", "ReconciledSucceeded")]
    [InlineData("FailedNoEffect", "ReconciledNoEffect")]
    public async Task Typed_client_preserves_actual_reconciled_job_history(string outcome, string expectedState)
    {
        await using var cell = await FoundationApiTests.ApiCell.Start(); cell.Transport.StatusCode = 204;
        var plan = await cell.Plan(); await cell.Approve(plan); var job = await cell.Enqueue(plan);
        Assert.True(await cell.App.Services.GetRequiredService<FoundationJobRunner>().RunOnceAsync("typed-uncertain-worker"));
        using var client = new FoundationReadClient(Options(), cell.App.GetTestServer().CreateHandler());
        Assert.Equal("OutcomeUnknown", (await client.GetJobAsync(job.Id, Token(cell))).State);
        using var response = await cell.Send("operator", HttpMethod.Post, "/jobs/" + job.Id + "/reconcile",
            new { outcome, evidence = new { synthetic = true, reviewedRun = "fixture-only" } });
        response.EnsureSuccessStatusCode();
        var reconciled = await client.GetJobAsync(job.Id, Token(cell));
        Assert.Equal(expectedState, reconciled.State); Assert.Contains("fixture-only", reconciled.ReceiptJson);
        Assert.Single(cell.Transport.Requests);
    }

    [Fact]
    public async Task Actual_api_revalidates_revoked_membership_instead_of_reusing_client_authority()
    {
        await using var cell = await FoundationApiTests.ApiCell.Start();
        var plan = await cell.Plan(); await cell.Approve(plan); var job = await cell.Enqueue(plan);
        using var client = new FoundationReadClient(Options(), cell.App.GetTestServer().CreateHandler());
        var token = Token(cell); _ = await client.GetJobAsync(job.Id, token);
        await cell.Member("viewer", [FoundationRoles.Viewer], enabled: false);
        var error = await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync(job.Id, token));
        Assert.Equal("access-denied", error.Code); Assert.Equal(403, error.StatusCode); Assert.DoesNotContain(token, error.ToString());
        Assert.Empty(cell.Transport.Requests);
    }

    [Fact]
    public async Task Actual_api_rejects_a_token_for_another_audience_without_returning_server_body()
    {
        await using var cell = await FoundationApiTests.ApiCell.Start();
        var claims = cell.Issuer.UserClaims(FoundationRoles.Viewer, FoundationApiTests.ApiCell.Subjects["viewer"]);
        claims["aud"] = "another-service"; var token = cell.Issuer.Token(claims);
        using var client = new FoundationReadClient(Options(), cell.App.GetTestServer().CreateHandler());
        var error = await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync("unknown", token));
        Assert.Equal("authentication-required", error.Code); Assert.Equal(401, error.StatusCode); Assert.DoesNotContain(token, error.ToString());
    }

    [Theory]
    [InlineData("customer", "access-denied")]
    [InlineData("project", "access-denied")]
    [InlineData("environment", "access-denied")]
    [InlineData("cell", "incompatible-response")]
    public async Task Fixed_scope_is_checked_by_actual_service_and_successful_response_contract(string changed, string expected)
    {
        await using var cell = await FoundationApiTests.ApiCell.Start();
        var plan = await cell.Plan(); await cell.Approve(plan); var job = await cell.Enqueue(plan);
        var scope = changed switch
        {
            "customer" => Scope with { CustomerId = FoundationApiTests.ApiCell.OtherCustomer },
            "project" => Scope with { ProjectId = "another-project" },
            "environment" => Scope with { EnvironmentId = "prod" },
            _ => Scope with { CellId = "another-cell" }
        };
        using var client = new FoundationReadClient(Options(scope), cell.App.GetTestServer().CreateHandler());
        Assert.Equal(expected, (await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync(job.Id, Token(cell)))).Code);
    }

    [Fact]
    public async Task Actual_authorized_other_project_cannot_read_foreign_artifact()
    {
        await using var cell = await FoundationApiTests.ApiCell.Start();
        using var savedResponse = await cell.Send("requester", HttpMethod.Post, "/artifacts", new ArtifactCommand("evidence.txt", "synthetic-private-source"));
        savedResponse.EnsureSuccessStatusCode(); var artifact = (await savedResponse.Content.ReadFromJsonAsync<FoundationArtifact>())!;
        using (var db = new SqliteConnection("Data Source=" + cell.DatabasePath))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO memberships SELECT customer_id,actor_key,'other',environment_id,roles,enabled FROM memberships WHERE actor_key=$actor";
            command.Parameters.AddWithValue("$actor", FoundationApiTests.ApiCell.Actor("viewer")); command.ExecuteNonQuery();
        }
        using var client = new FoundationReadClient(Options(Scope with { ProjectId = "other" }), cell.App.GetTestServer().CreateHandler());
        Assert.Equal("not-found", (await Assert.ThrowsAsync<FoundationReadException>(() => client.GetArtifactAsync(artifact.Id, Token(cell)))).Code);
        Assert.Equal("not-found", (await Assert.ThrowsAsync<FoundationReadException>(() => client.GetArtifactAsync("missing", Token(cell)))).Code);
        Assert.Empty(cell.Transport.Requests);
    }

    [Fact]
    public async Task Bearer_is_applied_to_each_request_without_identity_state_or_automatic_retries()
    {
        var seen = new List<string?>(); var paths = new List<string>();
        using var handler = new Handler((request, _) =>
        {
            seen.Add(request.Headers.Authorization?.Parameter); paths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme); Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Response(Job()));
        });
        using var client = new FoundationReadClient(Options(), handler);
        _ = await client.GetJobAsync("job-1", "first-synthetic-token");
        _ = await client.GetJobAsync("job-1", "second-synthetic-token");
        Assert.Equal(new[] { "first-synthetic-token", "second-synthetic-token" }, seen);
        Assert.All(paths, path => Assert.Equal("/api/foundation/customers/" + Scope.CustomerId + "/projects/default/environments/dev/jobs/job-1", path));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Production_handler_does_not_retain_response_cookies_or_follow_redirects()
    {
        await using var server = new LoopbackServer();
        using var client = new FoundationReadClient(Options() with { ServiceUri = server.Uri, RequestTimeout = TimeSpan.FromSeconds(3) });
        _ = await client.GetJobAsync("job-1", SyntheticToken);
        _ = await client.GetJobAsync("job-1", SyntheticToken);
        var error = await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync("job-1", SyntheticToken));
        Assert.Equal("redirect-refused", error.Code);
        await server.Completed;
        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests, headers => Assert.DoesNotContain(headers, line => line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(301, "redirect-refused")]
    [InlineData(302, "redirect-refused")]
    [InlineData(401, "authentication-required")]
    [InlineData(403, "access-denied")]
    [InlineData(404, "not-found")]
    [InlineData(429, "service-throttled")]
    [InlineData(503, "service-unavailable")]
    public async Task Non_success_is_sanitized_and_never_retried(int status, string expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("private-server-body synthetic-foundation-api-token") }));
        using var client = new FoundationReadClient(Options(), handler);
        var error = await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync("job-1", SyntheticToken));
        Assert.Equal(expected, error.Code); Assert.Equal(status, error.StatusCode); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("private-server-body", error.ToString()); Assert.DoesNotContain(SyntheticToken, error.ToString()); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("id")]
    [InlineData("state")]
    [InlineData("version")]
    public async Task Well_formed_job_from_another_scope_or_incompatible_contract_is_rejected(string fault)
    {
        var job = Job(); job = fault switch
        {
            "customer" => job with { CustomerId = "other-customer" }, "cell" => job with { CellId = "other-cell" },
            "project" => job with { ProjectId = "other-project" }, "environment" => job with { EnvironmentId = "other-environment" },
            "id" => job with { Id = "other-job" }, "state" => job with { State = "InventedSuccess" }, _ => job with { OperatorVersion = 0 }
        };
        using var handler = new Handler((_, _) => Task.FromResult(Response(job)));
        using var client = new FoundationReadClient(Options(), handler);
        Assert.Equal("incompatible-response", (await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync("job-1", SyntheticToken))).Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("case-duplicate")]
    [InlineData("null-required")]
    [InlineData("malformed")]
    [InlineData("null")]
    [InlineData("array")]
    public async Task Strict_wire_parser_refuses_missing_unknown_duplicate_null_or_malformed_data(string fault)
    {
        var json = JsonSerializer.Serialize(Job(), Json);
        var body = fault switch
        {
            "missing" => json.Replace("\"state\":\"Queued\",", "", StringComparison.Ordinal),
            "unknown" => json[..^1] + ",\"unexpected\":true}",
            "duplicate" => json[..^1] + ",\"id\":\"job-1\"}",
            "case-duplicate" => json[..^1] + ",\"Id\":\"job-1\"}",
            "null-required" => json.Replace("\"id\":\"job-1\"", "\"id\":null", StringComparison.Ordinal),
            "malformed" => "{", "null" => "null", _ => "[]"
        };
        using var handler = new Handler((_, _) => Task.FromResult(ResponseBytes(Encoding.UTF8.GetBytes(body))));
        using var client = new FoundationReadClient(Options(), handler);
        Assert.Equal("incompatible-response", (await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync("job-1", SyntheticToken))).Code);
    }

    [Theory]
    [InlineData("contract-missing")]
    [InlineData("contract-wrong")]
    [InlineData("contract-duplicate")]
    [InlineData("cell-missing")]
    [InlineData("cell-wrong")]
    [InlineData("cell-duplicate")]
    [InlineData("content-type")]
    [InlineData("content-encoding")]
    public async Task Response_protocol_cell_and_media_metadata_are_required(string fault)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Response(Job());
            if (fault.StartsWith("contract-", StringComparison.Ordinal)) Change(FoundationServiceProtocol.ContractHeader, "2");
            if (fault.StartsWith("cell-", StringComparison.Ordinal)) Change(FoundationServiceProtocol.CellHeader, "other-cell");
            if (fault == "content-type") response.Content.Headers.ContentType = new("text/html");
            if (fault == "content-encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
            void Change(string name, string wrong)
            {
                var current = response.Headers.GetValues(name).Single(); response.Headers.Remove(name);
                if (!fault.EndsWith("missing", StringComparison.Ordinal)) response.Headers.TryAddWithoutValidation(name,
                    fault.EndsWith("duplicate", StringComparison.Ordinal) ? new[] { current, current } : new[] { wrong });
            }
        });
        using var client = new FoundationReadClient(Options(), handler);
        Assert.Equal("incompatible-response", (await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync("job-1", SyntheticToken))).Code);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("binary")]
    public async Task Streaming_response_is_bounded_even_without_content_length(string kind)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = ResponseBytes(new byte[513], kind == "json" ? "application/json" : "application/octet-stream");
            response.Content.Dispose(); response.Content = new StreamContent(new NonSeekableStream(new byte[513]));
            response.Content.Headers.ContentType = new(kind == "json" ? "application/json" : "application/octet-stream");
            Assert.Null(response.Content.Headers.ContentLength);
            return Task.FromResult(response);
        });
        using var client = new FoundationReadClient(Options() with { MaximumJsonBytes = 512, MaximumArtifactBytes = 512 }, handler);
        var error = await Assert.ThrowsAsync<FoundationReadException>(() => kind == "json" ? ReadJob() : ReadContent());
        Assert.Equal("response-too-large", error.Code); Assert.Equal(1, handler.Calls);
        async Task ReadJob() => _ = await client.GetJobAsync("job-1", SyntheticToken);
        async Task ReadContent() => _ = await client.GetArtifactContentAsync("artifact-1", new string('a', 64), SyntheticToken);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("content")]
    public async Task Artifact_bytes_must_match_exact_digest(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic changed bytes");
        var artifact = new FoundationArtifactReadResult("artifact-1", Scope.CustomerId, Scope.ProjectId, Scope.EnvironmentId,
            "evidence.txt", new string('a', 64), bytes, DateTimeOffset.UtcNow);
        using var handler = new Handler((_, _) => Task.FromResult(kind == "metadata" ? Response(artifact) : ResponseBytes(bytes, "application/octet-stream")));
        using var client = new FoundationReadClient(Options(), handler);
        var error = await Assert.ThrowsAsync<FoundationReadException>(() => kind == "metadata" ? Metadata() : Content());
        Assert.Equal("artifact-integrity-failed", error.Code);
        async Task Metadata() => _ = await client.GetArtifactMetadataAsync("artifact-1", SyntheticToken);
        async Task Content() => _ = await client.GetArtifactContentAsync("artifact-1", artifact.Sha256, SyntheticToken);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("id")]
    public async Task Artifact_metadata_rejects_a_foreign_object_even_with_valid_bytes(string fault)
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic artifact");
        var artifact = new FoundationArtifactReadResult("artifact-1", Scope.CustomerId, Scope.ProjectId, Scope.EnvironmentId,
            "evidence.txt", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes, DateTimeOffset.UtcNow);
        artifact = fault switch
        {
            "customer" => artifact with { CustomerId = "foreign" }, "project" => artifact with { ProjectId = "foreign" },
            "environment" => artifact with { EnvironmentId = "foreign" }, _ => artifact with { Id = "foreign" }
        };
        using var handler = new Handler((_, _) => Task.FromResult(Response(artifact)));
        using var client = new FoundationReadClient(Options(), handler);
        Assert.Equal("incompatible-response", (await Assert.ThrowsAsync<FoundationReadException>(() => client.GetArtifactAsync("artifact-1", SyntheticToken))).Code);
    }

    [Fact]
    public async Task Caller_cancellation_and_bounded_deadline_cancel_the_owned_request()
    {
        using var handler = new Handler(async (_, cancellationToken) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return Response(Job()); });
        using var client = new FoundationReadClient(Options() with { RequestTimeout = TimeSpan.FromMilliseconds(100) }, handler);
        Assert.Equal("deadline-exceeded", (await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync("job-1", SyntheticToken))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetJobAsync("job-1", SyntheticToken, cancel.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task In_flight_caller_cancellation_reaches_transport_and_is_not_reclassified_as_timeout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, cancellationToken) =>
        { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return Response(Job()); });
        using var client = new FoundationReadClient(Options(), handler); using var cancel = new CancellationTokenSource();
        var pending = client.GetJobAsync("job-1", SyntheticToken, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Transport_failure_never_exposes_destination_or_private_error_details()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("private-handler-detail " + SyntheticToken));
        using var client = new FoundationReadClient(Options(), handler);
        var error = await Assert.ThrowsAsync<FoundationReadException>(() => client.GetJobAsync("job-1", SyntheticToken));
        Assert.Equal("service-unavailable", error.Code); Assert.Null(error.InnerException); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("private-handler-detail", error.ToString()); Assert.DoesNotContain(SyntheticToken, error.ToString());
    }

    [Theory]
    [InlineData("http://remote.example.invalid")]
    [InlineData("http://localhost/another-service/")]
    [InlineData("http://localhost/?select=other")]
    [InlineData("http://localhost/#fragment")]
    [InlineData("http://user:password@localhost/")]
    [InlineData("file:///C:/private")]
    public void Endpoint_must_be_fixed_root_without_credentials_query_fragment_or_insecure_remote_transport(string uri)
    { Assert.Throws<ArgumentException>(() => new FoundationReadClient(Options() with { ServiceUri = new Uri(uri) })); }

    [Theory]
    [InlineData("../foreign")]
    [InlineData("foreign/id")]
    [InlineData("%2fescape")]
    [InlineData("id?select=other")]
    public async Task Object_identifier_cannot_change_the_configured_route(string id)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Job())));
        using var client = new FoundationReadClient(Options(), handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetJobAsync(id, SyntheticToken)); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("token with spaces")]
    [InlineData("token\r\nX-Other: injected")]
    public async Task Invalid_bearer_never_reaches_transport(string token)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Job())));
        using var client = new FoundationReadClient(Options(), handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetJobAsync("job-1", token)); Assert.Equal(0, handler.Calls);
    }

    static FoundationJobReadResult Job() => new("job-1", Scope.CustomerId, Scope.CellId, "plan-1", Scope.ProjectId,
        Scope.EnvironmentId, "user:synthetic", 1, "Queued", null, null, 0, 0, null, null,
        DateTimeOffset.Parse("2026-10-26T12:00:00Z"), DateTimeOffset.Parse("2026-10-26T12:00:00Z"));
    static HttpResponseMessage Response<T>(T value) => ResponseBytes(JsonSerializer.SerializeToUtf8Bytes(value, Json));
    static HttpResponseMessage ResponseBytes(byte[] bytes, string type = "application/json")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        response.Headers.Add(FoundationServiceProtocol.CellHeader, Scope.CellId);
        response.Headers.Add(FoundationServiceProtocol.ContractHeader, FoundationServiceProtocol.ContractVersion); return response;
    }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
    sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    sealed class LoopbackServer : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        public Uri Uri { get; }
        public Task Completed { get; }
        public List<string[]> Requests { get; } = [];
        public LoopbackServer()
        {
            listener.Start(); Uri = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
            Completed = Serve();
        }
        async Task Serve()
        {
            for (var index = 0; index < 3; index++)
            {
                using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var headers = new List<string>(); string? line; var size = 0;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
                {
                    size += line.Length; if (size > 16384) throw new InvalidOperationException("Synthetic request exceeds fixture bound.");
                    headers.Add(line);
                }
                Requests.Add(headers.ToArray());
                byte[] body = index == 2 ? [] : JsonSerializer.SerializeToUtf8Bytes(Job(), Json);
                var status = index == 2 ? "302 Found" : "200 OK";
                var extra = index == 2 ? "Location: " + Uri + "redirected\r\n" : "Set-Cookie: fixture=synthetic; Path=/\r\n";
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nConnection: close\r\nContent-Type: application/json\r\n" +
                    "Content-Length: " + body.Length + "\r\n" + FoundationServiceProtocol.CellHeader + ": " + Scope.CellId + "\r\n" +
                    FoundationServiceProtocol.ContractHeader + ": " + FoundationServiceProtocol.ContractVersion + "\r\n" + extra + "\r\n");
                await stream.WriteAsync(response, stop.Token); await stream.WriteAsync(body, stop.Token); await stream.FlushAsync(stop.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await Completed; } catch (Exception error) when (error is OperationCanceledException or SocketException or IOException) { }
            stop.Dispose();
        }
    }
}
