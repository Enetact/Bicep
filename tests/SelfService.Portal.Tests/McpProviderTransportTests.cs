using System.Diagnostics;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class McpProviderTransportTests
{
    internal static string? NodeExecutable() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node")).FirstOrDefault(File.Exists);
    static bool Running(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    static async Task DeleteFixtureDirectory(string folder)
    {
        // Windows can retain a transient file handle after the owned process tree
        // exits. Retry only this fixture's removal, after all Process handles close.
        var full = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Path.GetFileName(full).StartsWith("mcp-transport-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected transport fixture directory.");
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(full, true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) when (attempt < 5) { await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1))); }
        }
    }
    [McpNodeTheory][InlineData("fixture", false)][InlineData("azure", true)]
    public async Task RealStdioSdkPaginatesRedactsAndCleansOwnedProcessTree(string provider, bool azureEnumAllowed)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcp-transport-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        IProviderMcpClient? client = null;
        Process? providerProcess = null, descendantProcess = null;
        Exception? testFailure = null;
        var previous = Environment.GetEnvironmentVariable("PORTAL_TEST_AMBIENT_TOKEN");
        Environment.SetEnvironmentVariable("PORTAL_TEST_AMBIENT_TOKEN", "synthetic-ambient-secret-must-not-inherit");
        try
        {
            var script = Path.Combine(folder, "fixture.cjs");
            await File.WriteAllTextAsync(script, """
                const fs = require('node:fs');
                const child = require('node:child_process').spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore', windowsHide: true });
                fs.writeFileSync('fixture-child.json', JSON.stringify({ pid: child.pid }));
                setInterval(() => {}, 1000);
                const waiting = new Map(); let sequence = 0;
                const write = value => process.stdout.write(JSON.stringify(value) + '\n');
                function ask(context) {
                  const id = 'fixture-confirm-' + (++sequence); waiting.set(id, context);
                  let params;
                  if (context.kind === 'url') params = { mode: 'url', message: 'Synthetic URL request', elicitationId: 'synthetic-url', url: 'https://example.invalid/never-open' };
                  else if (context.kind.startsWith('enum')) {
                    const property = context.kind === 'enumTitled'
                      ? { type: 'string', oneOf: [{ const: 'accept', title: 'Accept' }, { const: 'reject', title: 'Reject' }] }
                      : { type: 'string', enum: context.kind === 'enumExtraChoice' ? ['accept', 'reject', 'other'] : ['accept', 'reject'] };
                    params = { mode: 'form', message: "Please confirm operation '" + (context.kind === 'enumWrongTool' ? 'wrong_tool' : 'write_fixture') + "'", requestedSchema: { type: 'object', properties: { decision: property }, required: ['decision'] } };
                  }
                  else params = { mode: 'form', message: 'Synthetic confirmation request', requestedSchema: { type: 'object', properties: context.kind === 'password' ? { password: { type: 'string' } } : { confirm: { type: 'boolean' } }, required: [context.kind === 'password' ? 'password' : 'confirm'] } };
                  write({ jsonrpc: '2.0', id, method: 'elicitation/create', params });
                }
                const rl = require('node:readline').createInterface({ input: process.stdin });
                rl.on('line', line => {
                  const request = JSON.parse(line);
                  if (!Object.hasOwn(request, 'id')) return;
                  if (waiting.has(request.id)) {
                    const context = waiting.get(request.id); waiting.delete(request.id);
                    context.results.push(request.result ?? { action: 'error' });
                    if (context.kind === 'double' && context.results.length === 1) ask(context);
                    else write({ jsonrpc: '2.0', id: context.callId, result: { content: [{ type: 'text', text: JSON.stringify(context.results) }], isError: false } });
                    return;
                  }
                  if (!['initialize', 'tools/list', 'tools/call', 'ping'].includes(request.method)) {
                    write({ jsonrpc: '2.0', id: request.id, error: { code: -32601, message: 'Synthetic fixture does not implement this method.' } }); return;
                  }
                  let result = {};
                  if (request.method === 'initialize') result = { protocolVersion: request.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'synthetic-test-provider', version: '1.0.0' } };
                  if (request.method === 'tools/list') result = request.params?.cursor
                    ? { tools: [
                        { name: 'write_fixture', description: 'Synthetic mutation', inputSchema: { type: 'object' }, annotations: { readOnlyHint: false } },
                        { name: 'get_settings_fixture', inputSchema: { type: 'object' }, annotations: { readOnlyHint: true }, _meta: { SecretHint: true } },
                        { name: 'get_settings_lowercase_fixture', inputSchema: { type: 'object' }, _meta: { secrethint: true } },
                        { name: 'get_public_settings_fixture', inputSchema: { type: 'object' }, _meta: { SecretHint: false } },
                        { name: 'get_settings_malformed_fixture', inputSchema: { type: 'object' }, _meta: { SecretHint: 'false' } },
                        { name: 'get_settings_null_fixture', inputSchema: { type: 'object' }, _meta: { SecretHint: null } }
                      ] }
                    : { tools: [{ name: 'list_fixture', description: process.env.FIXTURE_TOKEN, inputSchema: { type: 'object' }, annotations: { readOnlyHint: true } }], nextCursor: 'second-page' };
                  if (request.method === 'tools/call') {
                    if (request.params.name === 'write_fixture') { ask({ callId: request.id, kind: request.params.arguments.kind, results: [] }); return; }
                    result = { content: [{ type: 'text', text: 'token=' + process.env.FIXTURE_TOKEN + '; ambient=' + (process.env.PORTAL_TEST_AMBIENT_TOKEN ?? 'missing') }], isError: false };
                  }
                  write({ jsonrpc: '2.0', id: request.id, result });
                });
                """);
            const string credential = "synthetic-provider-credential-should-be-redacted";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            client = await new McpProviderFactory().Connect(new(provider, NodeExecutable()!, [script], null, new() { ["FIXTURE_TOKEN"] = credential }, [], folder), timeout.Token);
            var receiptPath = Path.Combine(folder, "process.json");
            Assert.True(File.Exists(receiptPath));
            var receiptText = await File.ReadAllTextAsync(receiptPath, timeout.Token);
            using var receipt = JsonDocument.Parse(receiptText);
            var pid = receipt.RootElement.GetProperty("pid").GetInt32();
            using var child = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "fixture-child.json"), timeout.Token));
            var childPid = child.RootElement.GetProperty("pid").GetInt32();
            providerProcess = Process.GetProcessById(pid); descendantProcess = Process.GetProcessById(childPid);
            Assert.True(Running(pid)); Assert.True(Running(childPid));
            Assert.DoesNotContain(credential, receiptText); Assert.DoesNotContain("FIXTURE_TOKEN", receiptText);

            var tools = await client.ListTools(timeout.Token);
            Assert.Equal(new[] { "list_fixture", "write_fixture", "get_settings_fixture", "get_settings_lowercase_fixture", "get_public_settings_fixture", "get_settings_malformed_fixture", "get_settings_null_fixture" }, tools.Select(tool => tool.Name));
            Assert.Equal(new[] { false, false, true, true, false, true, true }, tools.Select(tool => tool.SensitiveHint));
            Assert.Equal("[credential redacted]", tools[0].Description);
            var output = await client.Call("list_fixture", JsonSerializer.SerializeToElement(new { }), timeout.Token);
            Assert.DoesNotContain(credential, output.GetRawText());
            Assert.DoesNotContain("synthetic-ambient-secret-must-not-inherit", output.GetRawText());
            Assert.Contains("ambient=missing", output.GetRawText());

            static JsonElement Answers(JsonElement result) => JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
            var ordinary = Answers(await client.Call("write_fixture", JsonSerializer.SerializeToElement(new { kind = "confirm" }), timeout.Token));
            Assert.Equal("decline", ordinary[0].GetProperty("action").GetString());
            var approved = Answers(await client.CallApproved("write_fixture", JsonSerializer.SerializeToElement(new { kind = "confirm" }), timeout.Token));
            Assert.Equal("accept", approved[0].GetProperty("action").GetString()); Assert.True(approved[0].GetProperty("content").GetProperty("confirm").GetBoolean());
            var doublePrompt = Answers(await client.CallApproved("write_fixture", JsonSerializer.SerializeToElement(new { kind = "double" }), timeout.Token));
            Assert.Equal("accept", doublePrompt[0].GetProperty("action").GetString()); Assert.Equal("decline", doublePrompt[1].GetProperty("action").GetString());
            foreach (var kind in new[] { "password", "url" })
            {
                var rejected = Answers(await client.CallApproved("write_fixture", JsonSerializer.SerializeToElement(new { kind }), timeout.Token));
                Assert.Equal("decline", rejected[0].GetProperty("action").GetString());
            }
            var unapprovedEnum = Answers(await client.Call("write_fixture", JsonSerializer.SerializeToElement(new { kind = "enum" }), timeout.Token));
            Assert.Equal("decline", unapprovedEnum[0].GetProperty("action").GetString());
            foreach (var kind in new[] { "enum", "enumTitled" })
            {
                var enumResult = Answers(await client.CallApproved("write_fixture", JsonSerializer.SerializeToElement(new { kind }), timeout.Token));
                Assert.Equal(azureEnumAllowed ? "accept" : "decline", enumResult[0].GetProperty("action").GetString());
                if (azureEnumAllowed) Assert.Equal("accept", enumResult[0].GetProperty("content").GetProperty("decision").GetString());
            }
            foreach (var kind in new[] { "enumWrongTool", "enumExtraChoice" })
            {
                var invalidEnum = Answers(await client.CallApproved("write_fixture", JsonSerializer.SerializeToElement(new { kind }), timeout.Token));
                Assert.Equal("decline", invalidEnum[0].GetProperty("action").GetString());
            }

            await client.DisposeAsync(); client = null;
            await providerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await descendantProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(receiptPath));
            Assert.False(Running(pid)); Assert.False(Running(childPid));
        }
        catch (Exception e) { testFailure = e; throw; }
        finally
        {
            Environment.SetEnvironmentVariable("PORTAL_TEST_AMBIENT_TOKEN", previous);
            if (client is not null) await client.DisposeAsync();
            foreach (var owned in new[] { providerProcess, descendantProcess })
            {
                if (owned is null) continue;
                try { if (!owned.HasExited) { owned.Kill(entireProcessTree: true); await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
                finally { owned.Dispose(); }
            }
            try { await DeleteFixtureDirectory(folder); } catch (IOException) when (testFailure is not null) { /* Preserve the original test failure. */ }
        }
    }
}
public sealed class McpNodeTheoryAttribute : TheoryAttribute
{
    public McpNodeTheoryAttribute() { if (McpProviderTransportTests.NodeExecutable() is null) Skip = "Local Node.js is required for the synthetic stdio MCP transport fixture."; }
}
