using System.Text.Json;

namespace SelfService.Portal;

// The pinned Bicep server's get_file_references exposes compiler inputs without
// returning their contents. Apply the broker's source-path policy to its complete
// dependency closure before allowing a compiler result into model context.
public static class BicepMcpScope
{
    const int MaxFiles = 2048, MaxCompilations = 256, MaxResultLength = 1024 * 1024;
    static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static async Task ValidateDependencies(IProviderMcpClient client, string toolName, JsonElement arguments,
        Action<string> validatePath, CancellationToken cancellationToken)
    {
        if (toolName is not ("build_bicep" or "build_bicepparam" or "format_bicep_file" or
            "get_deployment_snapshot" or "get_file_references" or "get_bicep_file_diagnostics")) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
            var candidates = arguments.EnumerateObject().Where(p => p.Name.Equals("filePath", StringComparison.Ordinal)).ToArray();
            if (candidates.Length != 1 || candidates[0].Value.ValueKind != JsonValueKind.String) throw new InvalidOperationException();
            var entry = CanonicalPath(candidates[0].Value.GetString()!);
            if (!IsSource(entry)) throw new InvalidOperationException();
            validatePath(entry);
            var pending = new Queue<string>(); pending.Enqueue(entry);
            var scheduled = new HashSet<string>(Paths) { entry };
            var files = new HashSet<string>(Paths);
            var calls = 0;
            while (pending.TryDequeue(out var source))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (++calls > MaxCompilations) throw new InvalidOperationException();
                var result = await client.Call("get_file_references", JsonSerializer.SerializeToElement(new { filePath = source }), deadline.Token);
                var references = References(result);
                var foundSource = false;
                // Validate every returned path before following any other source.
                foreach (var reference in references)
                {
                    if (reference.ValueKind != JsonValueKind.String ||
                        !Uri.TryCreate(reference.GetString(), UriKind.Absolute, out var uri) ||
                        !uri.IsFile || uri.IsUnc || uri.Host.Length != 0 || uri.UserInfo.Length != 0 ||
                        uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new InvalidOperationException();
                    var path = CanonicalPath(uri.LocalPath);
                    validatePath(path);
                    if (Paths.Equals(source, path)) foundSource = true;
                    if (files.Add(path) && files.Count > MaxFiles) throw new InvalidOperationException();
                    if (IsSource(path) && scheduled.Add(path)) pending.Enqueue(path);
                }
                // Native 0.48.1 includes the analyzed file itself. An empty or
                // incomplete reply cannot attest that the requested file was read.
                if (!foundSource) throw new InvalidOperationException();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Neither SDK errors nor diagnostic text may disclose source contents,
            // private filenames, or credentials through this guard.
            throw new PortalException("Bicep dependencies could not be verified within the permitted source paths.", 403);
        }
    }

    static bool IsSource(string path) => Path.GetExtension(path).Equals(".bicep", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".bicepparam", StringComparison.OrdinalIgnoreCase);

    static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException();
        return Path.GetFullPath(path);
    }

    static JsonElement.ArrayEnumerator References(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || result.GetRawText().Length > MaxResultLength ||
            result.TryGetProperty("isError", out var error) && error.ValueKind != JsonValueKind.False) throw new InvalidOperationException();
        JsonElement body;
        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind != JsonValueKind.Null) body = structured;
        else
        {
            if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1)
                throw new InvalidOperationException();
            var block = content[0];
            if (!block.TryGetProperty("type", out var type) || type.GetString() != "text" ||
                !block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new InvalidOperationException();
            using var document = JsonDocument.Parse(text.GetString()!);
            body = document.RootElement.Clone();
        }
        if (body.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        var fields = body.EnumerateObject().Where(p => p.Name.Equals("fileUris", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (fields.Length != 1 || fields[0].Value.ValueKind != JsonValueKind.Array ||
            fields[0].Value.GetArrayLength() is 0 or > MaxFiles) throw new InvalidOperationException();
        return fields[0].Value.EnumerateArray();
    }
}
