using System.Reflection;
using SelfService.Foundation.Client;
using SelfService.Foundation.Contracts;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationServiceBoundaryTests
{
    [Fact]
    public void Contracts_do_not_depend_on_domain_storage_web_or_provider_implementations() =>
        AssertNeutral(typeof(FoundationAgentToolContracts).Assembly, []);

    [Fact]
    public void Client_depends_only_on_protocol_and_runtime_libraries() =>
        AssertNeutral(typeof(FoundationReadClient).Assembly, ["SelfService.Foundation.Contracts"]);

    static void AssertNeutral(Assembly assembly, string[] allowedProjects)
    {
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            var name = reference.Name!;
            Assert.True(name.StartsWith("System.", StringComparison.Ordinal) || name is "System" or "netstandard" ||
                allowedProjects.Contains(name, StringComparer.Ordinal), $"Unexpected service boundary dependency: {name}");
        }
    }

}
