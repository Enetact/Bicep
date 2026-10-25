using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

// Reviewed source closure only. Catalog metadata cannot introduce another module,
// filesystem location, registry restore, executable hook or linked ARM template.
public static class BicepAvmSources
{
    public static readonly string[] Paths = [
        "modules/avm/identity/user-assigned-identity/main.bicep",
        "modules/avm/containers/container-registry/main.bicep",
        "modules/avm/configuration/app-configuration/main.bicep",
        "modules/avm/monitoring/application-insights/main.bicep",
        "modules/avm/network/network-security-group/main.bicep",
        "modules/avm/network/route-table/main.bicep"];
    const string Vendor = "vendor/azure-verified-modules/";
    static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Id(string path) => "avm-" + path["modules/avm/".Length..^"/main.bicep".Length].Replace('/', '-');

    public static Dictionary<string, string> Read(string root, BicepModuleContract module)
    {
        try
        {
            if (!Paths.Contains(module.Path, StringComparer.Ordinal) || module.Id != Id(module.Path) || !module.DraftSelectable)
                throw new InvalidOperationException();
            var own = Vendor + module.Id[4..] + "/";
            var expected = new[] { module.Path, own + "main.json", own + "manifest.json", Vendor + "LICENSE", Vendor + "bundle.json" };
            if (module.Sources is null || module.Sources.Count != expected.Length || !module.Sources.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
                throw new InvalidOperationException();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var path in expected)
            {
                var text = ReadLocked(root, path, module.Sources[path]);
                total += Utf8.GetByteCount(text);
                if (total > 32 * 1024 * 1024) throw new InvalidOperationException();
                result.Add(path, text);
            }
            if (BicepDrafts.Hash(result[module.Path]) != module.SourceSha256) throw new InvalidOperationException();
            var source = result[module.Path];
            if (Regex.IsMatch(source, @"(?m)^\s*(import|extension)\b|\bload(TextContent|JsonContent|YamlContent|FileAsBase64)\s*\(", RegexOptions.CultureInvariant))
                throw new InvalidOperationException();
            var dependencies = Regex.Matches(source, @"(?m)^\s*module\s+\w+\s+'([^']+)'\s*=", RegexOptions.CultureInvariant);
            if (dependencies.Count != 1 || Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(module.Path)!, dependencies[0].Groups[1].Value)) != Path.GetFullPath(Path.Combine(root, own + "main.json")))
                throw new InvalidOperationException();
            using var template = JsonDocument.Parse(result[own + "main.json"]);
            void Check(JsonElement element, int depth)
            {
                if (depth > 100) throw new InvalidOperationException();
                if (element.ValueKind == JsonValueKind.Object)
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name.Equals("templateLink", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
                        Check(property.Value, depth + 1);
                    }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Check(child, depth + 1);
            }
            Check(template.RootElement, 0);
            return result;
        }
        catch (PortalException) { throw; }
        catch { throw new PortalException("AVM sources differ from their reviewed dependency lock or cannot be verified. Refresh and review the AVM contracts.", 409); }
    }

    static string ReadLocked(string root, string relative, string hash)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is "" or "." or "..") || !Regex.IsMatch(hash ?? "", "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException();
        var full = Path.GetFullPath(root);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
        foreach (var component in relative.Split('/'))
        {
            full = Path.Combine(full, component);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
        }
        var info = new FileInfo(full);
        if (!info.Exists || info.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidOperationException();
        var bytes = File.ReadAllBytes(full);
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != hash) throw new InvalidOperationException();
        return Utf8.GetString(bytes);
    }
}
