using System.Text.Json;

namespace SelfService.Portal;

/// <summary>Projects public dashboard configuration; authentication keys never enter a browser response.</summary>
public static class ObservabilityStatus
{
    public static string? Dashboard(string root, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) return null;
        try
        {
            var path = Path.Combine(root, "config/observability.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            using var config = JsonDocument.Parse(File.ReadAllBytes(path));
            var value = config.RootElement.GetProperty("dashboardUrl").GetString();
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
                uri.Host == "127.0.0.1" && uri.Port is >= 1024 and <= 65535 && uri.AbsolutePath == "/" &&
                uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0 ? uri.GetLeftPart(UriPartial.Authority) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }
}
