using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SelfService.Portal;

// Only this trusted host acquires credentials. No credential is accepted from
// model arguments, returned in status, or copied into the inference process.
public sealed class McpProviderCredentials(PortalOptions options, ITokenProvider tokens, IGitHubIdentity github) : IProviderConnectionResolver
{
    public async Task<ProviderConnectionSpec> Resolve(BrowserSession s, string id, CancellationToken ct)
    {
        var directory = Path.Combine(options.RepositoryRoot, ".local", "portal", "mcp", s.Mcp.Id, id);
        Directory.CreateDirectory(directory);
        var environment = new Dictionary<string, string>();
        var headers = new Dictionary<string, string>();
        string? command = null, url = null; string[] args = [];
        var modules = Path.Combine(options.RepositoryRoot, "scripts", "mcp", "node_modules");
        switch (id)
        {
            case "bicep":
                command = string.IsNullOrEmpty(options.BicepMcpPath) ? Path.Combine(options.RepositoryRoot, ".tools", "mcp", "bicep", OperatingSystem.IsWindows() ? "Azure.Bicep.McpServer.exe" : "Azure.Bicep.McpServer") : options.BicepMcpPath;
                RequireFile(command);
                // Bicep's deployment snapshot may need Azure access. Never fall
                // through to another user's default CLI cache or managed identity.
                environment["AZURE_CONFIG_DIR"] = s.Connected.ContainsKey("azure") && s.Cli is not null ? s.Cli.DirectoryPath : Path.Combine(directory, "azure-empty");
                Directory.CreateDirectory(environment["AZURE_CONFIG_DIR"]);
                environment["AZURE_TOKEN_CREDENTIALS"] = "AzureCliCredential";
                environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.GetDirectoryName(Environment.ProcessPath)!;
                break;
            case "azure":
                if (s.Cli is null || !s.Connected.ContainsKey("azure")) throw new PortalException("Connect Azure with local CLI authentication before connecting Azure MCP.", 401);
                _ = await tokens.Token(s, "azure");
                var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
                var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
                command = Path.Combine(modules, "@azure", $"mcp-{platform}-{arch}", "dist", OperatingSystem.IsWindows() ? "azmcp.exe" : "azmcp");
                RequireFile(command); args = ["server", "start", "--mode", "all"];
                environment["AZURE_CONFIG_DIR"] = s.Cli.DirectoryPath;
                environment["AZURE_TOKEN_CREDENTIALS"] = "AzureCliCredential";
                environment["AZURE_MCP_COLLECT_TELEMETRY"] = "false";
                break;
            case "ado":
                var script = Path.Combine(modules, "@azure-devops", "mcp", "dist", "index.js"); RequireFile(script);
                command = options.NodePath; args = [script, options.Organization, "--authentication", "envvar"];
                environment["ADO_MCP_AUTH_TOKEN"] = await tokens.Token(s, "ado");
                break;
            case "github":
                var token = await github.Token(s, ct);
                if (string.IsNullOrEmpty(token)) throw new PortalException("Attach a verified GitHub account in Connections first.", 401);
                url = "https://api.githubcopilot.com/mcp/";
                headers["Authorization"] = "Bearer " + token; headers["X-MCP-Toolsets"] = "all";
                break;
            case "learn": url = "https://learn.microsoft.com/api/mcp"; break;
            default: throw new PortalException("Unknown MCP provider.", 404);
        }
        return new(id, command, args, url, environment, headers, directory);
    }
    static void RequireFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new PortalException("MCP runtime is missing. Run scripts/Setup-McpProviders.ps1, then reconnect.", 409);
    }
}
