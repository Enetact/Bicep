using Microsoft.Extensions.Configuration;
using Xunit;

namespace BlobTransfer.Tests;

public class LocalDevelopmentTests
{
    private static Dictionary<string, string?> Settings() => new()
    {
        ["LocalDevelopment:Enabled"] = "true", ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Development",
        ["AzureWebJobsStorage"] = LocalDevelopment.ConnectionString,
        ["UploadStorage"] = LocalDevelopment.ConnectionString,
        ["TransferQueueStorage"] = LocalDevelopment.ConnectionString,
        ["Recovery:includeSourceVersions"] = "false"
    };
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    [Fact] public void ExplicitLoopbackDevelopmentConfigurationIsAccepted() => Assert.True(LocalDevelopment.IsEnabled(Config(Settings())));
    [Fact] public void ModeIsOffByDefault() => Assert.False(LocalDevelopment.IsEnabled(Config(new())));
    [Theory]
    [InlineData("AZURE_FUNCTIONS_ENVIRONMENT", "Production")]
    [InlineData("WEBSITE_INSTANCE_ID", "azure-instance")]
    [InlineData("WEBSITE_SITE_NAME", "azure-app")]
    [InlineData("UploadStorage", "DefaultEndpointsProtocol=https;AccountName=production")]
    [InlineData("AzureWebJobsStorage:blobServiceUri", "https://production.blob.core.windows.net")]
    [InlineData("Recovery:includeSourceVersions", "true")]
    public void CloudOrConflictingConfigurationFailsClosed(string key, string value)
    {
        var settings = Settings(); settings[key] = value;
        Assert.Throws<InvalidOperationException>(() => LocalDevelopment.IsEnabled(Config(settings)));
    }
}
