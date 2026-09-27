namespace SelfService.Portal;

public sealed class PortalOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Organization { get; set; } = "enetactgames";
    public string Project { get; set; } = "Enetact";
    public string RepositoryRoot { get; set; } = "";
    public int Port { get; set; } = 5087;
    public string NodePath { get; set; } = "node";
    public string CodexPath { get; set; } = "";
    public bool AuthenticationConfigured => Guid.TryParse(TenantId, out _) && Guid.TryParse(ClientId, out _);
    public string Origin => $"http://localhost:{Port}";
    public string AdoBase => $"https://dev.azure.com/{Uri.EscapeDataString(Organization)}/{Uri.EscapeDataString(Project)}";
    public void Validate()
    {
        if (Port is < 1024 or > 65535 || !System.Text.RegularExpressions.Regex.IsMatch(Organization, "^[a-zA-Z0-9-]{1,60}$") ||
            string.IsNullOrWhiteSpace(Project) || Project.Length > 100 || !File.Exists(Path.Combine(RepositoryRoot, "config/workloads.json")))
            throw new InvalidOperationException("Invalid portal configuration or repository root. See docs/local-portal.md.");
        if ((!string.IsNullOrEmpty(TenantId) || !string.IsNullOrEmpty(ClientId)) && !AuthenticationConfigured)
            throw new InvalidOperationException("Configure both TenantId and ClientId as GUIDs, or leave both empty.");
    }
}

public sealed class PortalException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
