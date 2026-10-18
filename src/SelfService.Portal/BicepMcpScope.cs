using System.Text.Json;

namespace SelfService.Portal;

// Native get_file_references omits ARM JSON module inputs. Preflight literal local
// inputs before any compiler call, then reconcile native auxiliary references too.
public static class BicepMcpScope
{
    const int MaxFiles = 2048, MaxCompilations = 256, MaxResultLength = 1024 * 1024;
    const int MaxSourceBytes = 1024 * 1024, MaxInputBytes = 8 * 1024 * 1024, MaxReadBytes = 32 * 1024 * 1024;
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
            var pending = new Queue<string>(); var local = new Queue<string>(); var templates = new Queue<string>(); var configurations = new Queue<string>();
            var scheduled = new HashSet<string>(Paths); var arm = new HashSet<string>(Paths); var configs = new HashSet<string>(Paths);
            var files = new HashSet<string>(Paths);
            var inspected = new Dictionary<string, (int Maximum, string Hash)>(Paths);
            var calls = 0; long readBytes = 0;
            void Track(string path, bool template = false)
            {
                path = CanonicalPath(path); validatePath(path);
                var info = new FileInfo(path); if (!info.Exists || info.Length > MaxInputBytes) throw new InvalidOperationException();
                if (files.Add(path) && files.Count > MaxFiles) throw new InvalidOperationException();
                if (IsSource(path) && scheduled.Add(path))
                {
                    if (scheduled.Count > MaxCompilations) throw new InvalidOperationException();
                    local.Enqueue(path); pending.Enqueue(path);
                }
                else if (template && arm.Add(path)) templates.Enqueue(path);
                if (Path.GetFileName(path).Equals("bicepconfig.json", StringComparison.OrdinalIgnoreCase) && configs.Add(path)) configurations.Enqueue(path);
            }
            byte[] Read(string path, int maximum)
            {
                deadline.Token.ThrowIfCancellationRequested(); validatePath(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > maximum || (readBytes += stream.Length) > MaxReadBytes) throw new InvalidOperationException();
                var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                if (inspected.TryGetValue(path, out var previous) && previous.Hash != hash) throw new InvalidOperationException();
                inspected[path] = (maximum, hash); return bytes;
            }
            void Preflight()
            {
                while (local.TryDequeue(out var source))
                {
                    var bytes = Read(source, MaxSourceBytes);
                    using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, true);
                    foreach (var input in BicepSourceInputs.Read(source, reader.ReadToEnd())) Track(input.Path, input.Template);
                    for (var directory = new FileInfo(source).Directory; directory is not null; directory = directory.Parent)
                    {
                        var config = Path.Combine(directory.FullName, "bicepconfig.json");
                        if (File.Exists(config)) { Track(config); break; }
                    }
                }
                while (configurations.TryDequeue(out var config)) BicepSourceInputs.CheckConfiguration(Read(config, MaxSourceBytes), deadline.Token);
                while (templates.TryDequeue(out var path)) BicepSourceInputs.CheckTemplate(Read(path, MaxInputBytes), deadline.Token);
            }
            Track(entry); Preflight();
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
                    Track(path);
                    if (Paths.Equals(source, path)) foundSource = true;
                }
                // Native 0.48.1 includes the analyzed file itself. An empty or
                // incomplete reply cannot attest that the requested file was read.
                if (!foundSource) throw new InvalidOperationException();
                Preflight();
            }
            // Do not let a changed JSON module or compiler config slip between preflight and the
            // compiler probe. The broker repeats this entire policy after the tool.
            foreach (var inspectedFile in inspected.ToArray()) _ = Read(inspectedFile.Key, inspectedFile.Value.Maximum);
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
