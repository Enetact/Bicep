using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace SelfService.Portal;

public static class NamingArtifact
{
    public static async Task<string?> ResourceGroup(byte[] bytes, PortalOptions options, Product product, Target target, CancellationToken ct)
    {
        using var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
        var names=zip.Entries.Where(e=>e.FullName is "naming-draft.json" or "deployment-preview/naming-draft.json").ToArray();
        if(names.Length==0)return null;
        if(names.Length!=1 || names[0].Length>2*1024*1024)throw new PortalException("Ambiguous naming artifact.",409);
        using var stream=names[0].Open();var content=await PreviewDiagram.Bounded(stream,2*1024*1024,ct);
        var saved=JsonSerializer.Deserialize<JsonElement>(content);
        var inputs=zip.Entries.SingleOrDefault(e=>e.FullName is "preview-inputs.json" or "deployment-preview/preview-inputs.json")??throw new PortalException("Missing naming receipt.",409);
        using var receiptStream=inputs.Open();var receipt=JsonSerializer.Deserialize<JsonElement>(await PreviewDiagram.Bounded(receiptStream,1024*1024,ct));
        if(receipt.GetProperty("files").GetProperty("naming-draft.json").GetString()!=Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant())throw new PortalException("Naming artifact hash mismatch.",409);
        var intent=saved.GetProperty("plan").GetProperty("intent");var id=intent.GetProperty("instanceId").GetString();
        var registry=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(options.RepositoryRoot,"self-service/instances/registry.json")));
        var entries=registry.GetProperty("instances").EnumerateArray().Where(e=>e.GetProperty("intent").GetProperty("instanceId").GetString()==id).ToArray();
        if(entries.Length!=1 || entries[0].GetProperty("workloadType").GetString()!=product.Type || intent.GetProperty("environment").GetString()!=target.Environment ||
            !string.Equals(intent.GetProperty("subscriptionId").GetString(),target.SubscriptionId,StringComparison.OrdinalIgnoreCase))throw new PortalException("Naming instance no longer matches this scope.",409);
        var regenerated=await new NamingPlan(options).Generate(JsonSerializer.SerializeToElement(new {workloadType=product.Type,intent=entries[0].GetProperty("intent")}),ct);
        if(regenerated.GetProperty("draftSha256").GetString()!=saved.GetProperty("draftSha256").GetString() || !JsonElement.DeepEquals(regenerated,saved))throw new PortalException("Naming source or policy changed; collect fresh Preview evidence.",409);
        return regenerated.GetProperty("parameterPatch").GetProperty("parameters").GetProperty("workloadResourceGroupName").GetProperty("value").GetString();
    }
}
