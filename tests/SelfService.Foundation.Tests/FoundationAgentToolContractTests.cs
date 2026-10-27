using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation.Contracts;
using SelfService.Portal;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationAgentToolContractTests
{
    [Fact]
    public void Existing_tool_descriptions_schemas_and_action_order_retain_their_wire_commitment()
    {
        // Pinned from the seven original tools and AHP action order at 6d7ef65.
        var canonical = string.Join("\n", FoundationAgentToolContracts.Tools.Select(tool =>
            string.Join("\n", tool.Name, tool.Action, tool.Description, tool.InputSchemaJson)));
        Assert.Equal(7, FoundationAgentToolContracts.Tools.Count);
        Assert.Equal("31ca67ce8d46449657c7b0df6f3bbd1c59189cf5d117db50dedb55c4804138c3",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        Assert.Equal(FoundationAgentToolContracts.Tools.Select(tool => tool.Action), FoundationAgentToolContracts.Actions.Keys);
        Assert.Equal(FoundationAgentToolContracts.ToolNames, FoundationAgentToolContracts.Actions.Values);
        Assert.Equal(FoundationAgentToolContracts.ToolNames, AgentMcpBridge.FoundationToolNames);
    }

    [Fact]
    public void Shared_registration_is_read_only_case_sensitive_and_has_no_human_approval_action()
    {
        Assert.False(FoundationAgentToolContracts.Actions.ContainsKey("platform/foundationApprove"));
        Assert.False(FoundationAgentToolContracts.Actions.ContainsKey("PLATFORM/FOUNDATIONPLAN"));
        Assert.DoesNotContain("platform_foundation_approve", FoundationAgentToolContracts.ToolNames);
        Assert.Throws<NotSupportedException>(() => ((IList<FoundationAgentToolContract>)FoundationAgentToolContracts.Tools)
            .Add(FoundationAgentToolContracts.Tools[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)FoundationAgentToolContracts.ToolNames).Add("platform_foundation_approve"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)FoundationAgentToolContracts.Actions)
            .Add("platform/foundationApprove", "platform_foundation_approve"));
    }

    [Fact]
    public async Task Existing_bridge_advertises_shared_contracts_with_the_original_wire_shape()
    {
        await using var bridge = await AgentMcpBridge.Create("frozen evidence", "reviewed skill", () => true, CancellationToken.None);
        var originals = bridge.Tools.ToArray();
        bridge.EnableFoundation((_, _, _) => Task.FromResult<object>(new { observed = true }));
        Assert.Equal(originals.Length + 7, bridge.Tools.Length);
        Assert.Equal(originals, bridge.Tools.Take(originals.Length));
        foreach (var (value, contract) in bridge.Tools.Skip(originals.Length).Zip(FoundationAgentToolContracts.Tools))
        {
            var tool = JsonSerializer.SerializeToElement(value);
            Assert.Equal(new[] { "type", "name", "description", "inputSchema" }, tool.EnumerateObject().Select(property => property.Name));
            Assert.Equal("function", tool.GetProperty("type").GetString());
            Assert.Equal(contract.Name, tool.GetProperty("name").GetString());
            Assert.Equal(contract.Description, tool.GetProperty("description").GetString());
            Assert.Equal(contract.InputSchemaJson, tool.GetProperty("inputSchema").GetRawText());
            Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
        }
    }

    [Fact]
    public void Shared_contract_assembly_has_no_host_database_provider_or_model_dependency()
    {
        var references = typeof(FoundationAgentToolContracts).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name ?? "").ToArray();
        Assert.DoesNotContain(references, name => name.StartsWith("SelfService.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) || name.StartsWith("ModelContextProtocol", StringComparison.Ordinal));
    }
}
