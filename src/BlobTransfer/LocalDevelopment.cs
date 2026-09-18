using Microsoft.Extensions.Configuration;

namespace BlobTransfer;

public static class LocalDevelopment
{
    // The SDK expands this public emulator setting to loopback endpoints only.
    // Never accept an arbitrary connection string through the local mode switch.
    public const string ConnectionString = "UseDevelopmentStorage=true";

    public static bool IsEnabled(IConfiguration configuration)
    {
        var setting = configuration["LocalDevelopment:Enabled"];
        if (string.IsNullOrEmpty(setting)) return false;
        if (!bool.TryParse(setting, out var enabled)) throw new InvalidOperationException("LocalDevelopment:Enabled must be true or false.");
        if (!enabled) return false;
        if (configuration["AZURE_FUNCTIONS_ENVIRONMENT"] != "Development" ||
            !string.IsNullOrEmpty(configuration["WEBSITE_INSTANCE_ID"]) ||
            !string.IsNullOrEmpty(configuration["WEBSITE_SITE_NAME"]))
            throw new InvalidOperationException("Local emulator mode is permitted only in a local Development host.");
        foreach (var name in new[] { "AzureWebJobsStorage", "UploadStorage", "TransferQueueStorage" })
        {
            if (configuration[name] != ConnectionString || configuration.GetSection(name).GetChildren().Any())
                throw new InvalidOperationException($"Local mode requires only the fixed emulator connection for {name}.");
        }
        if (!string.Equals(configuration["Recovery:includeSourceVersions"], "false", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azurite mode must disable source version enumeration.");
        return true;
    }
}
