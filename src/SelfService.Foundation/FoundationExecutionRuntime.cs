using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfService.Foundation;

// Shared wire settings and compatibility version for the API and independent executors.
// This contains no host, browser, identity-provider or credential configuration.
public static class FoundationExecutionRuntime
{
    public const string RuntimeVersion = "1.0.0";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
