using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SelfService.Portal;

// Conservative input preflight, not a replacement Bicep compiler. Native 0.48.1
// omits local ARM JSON modules from get_file_references. Resolve literal inputs
// before invoking it, and refuse dependency forms this adapter cannot verify.
internal static class BicepSourceInputs
{
    internal record Input(string Path, bool Template);
    readonly record struct Token(string Kind, string Value, bool Interpolated = false, bool Multiline = false);
    static readonly HashSet<string> Loads = ["loadTextContent", "loadJsonContent", "loadYamlContent", "loadFileAsBase64"];
    static readonly HashSet<string> UnsupportedReads = ["loadDirectoryFileInfo", "readEnvironmentVariable"];

    public static IEnumerable<Input> Read(string source, string text)
    {
        var tokens = new Lexer(text).Read();
        Input Reference(Token token, bool template)
        {
            if (token.Kind != "string" || token.Interpolated || token.Multiline || string.IsNullOrWhiteSpace(token.Value) || token.Value.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw new InvalidOperationException();
            var value = token.Value;
            if (value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal) ||
                value.Select((c, i) => (c, i)).Any(x => x.c == ':' && !(OperatingSystem.IsWindows() && x.i == 1 && char.IsAsciiLetter(value[0]) && Path.IsPathFullyQualified(value))))
                throw new InvalidOperationException(); // Registry/Template Spec/network restores are not local source.
            var path = Path.GetFullPath(value, Path.GetDirectoryName(source)!);
            if (template && !new[] { ".bicep", ".json", ".jsonc", ".arm" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException();
            return new(path, template && !Path.GetExtension(path).Equals(".bicep", StringComparison.OrdinalIgnoreCase));
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i]; if (t.Kind != "identifier") continue;
            // These declaration forms can load extensions or source not included in
            // the native reference result. Do not probe them before host support.
            if (t.Value is "extension" or "provider" or "extends" ||
                t.Value == "test" && i + 1 < tokens.Count && tokens[i + 1].Kind == "identifier") throw new InvalidOperationException();
            if (UnsupportedReads.Contains(t.Value) && i + 1 < tokens.Count && tokens[i + 1].Value == "(")
                throw new InvalidOperationException(); // Directory metadata and environment reads have no attestable file closure.
            if (t.Value == "module" && i + 2 < tokens.Count && tokens[i + 1].Kind == "identifier")
            {
                if (i + 3 >= tokens.Count || tokens[i + 3].Value != "=") throw new InvalidOperationException();
                yield return Reference(tokens[i + 2], true);
            }
            else if (t.Value == "using" && i + 1 < tokens.Count)
            {
                if (tokens[i + 1].Kind == "identifier" && tokens[i + 1].Value == "none") continue;
                yield return Reference(tokens[i + 1], true);
            }
            else if (t.Value == "import" && i + 1 < tokens.Count && tokens[i + 1].Value is "{" or "*")
            {
                var from = i + 2;
                while (from < tokens.Count && from - i < 1024 && !(tokens[from].Kind == "identifier" && tokens[from].Value == "from")) from++;
                if (from + 1 >= tokens.Count || from - i >= 1024) throw new InvalidOperationException();
                yield return Reference(tokens[from + 1], true);
            }
            else if (t.Value == "import") throw new InvalidOperationException();
            else if (Loads.Contains(t.Value) && i + 1 < tokens.Count && tokens[i + 1].Value == "(")
            {
                if (i + 3 >= tokens.Count || tokens[i + 3].Value is not (")" or ",")) throw new InvalidOperationException();
                yield return Reference(tokens[i + 2], false);
            }
        }
    }

    public static void CheckTemplate(byte[] bytes, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 128, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("$schema", out var schema) || schema.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(schema.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "schema.management.azure.com" || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.EndsWith("deploymentTemplate.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        var count = 0;
        void Visit(JsonElement value)
        {
            if (++count > 500_000) throw new InvalidOperationException();
            if ((count & 255) == 0) ct.ThrowIfCancellationRequested();
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name) || property.Name.Equals("templateLink", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("linkedTemplates", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException();
                    if (property.Name.Equals("$ref", StringComparison.OrdinalIgnoreCase) && (property.Value.ValueKind != JsonValueKind.String || !(property.Value.GetString() ?? "").StartsWith("#/", StringComparison.Ordinal)))
                        throw new InvalidOperationException();
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
        Visit(json.RootElement);
    }

    public static void CheckConfiguration(byte[] bytes, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        foreach (var section in json.RootElement.EnumerateObject())
        {
            // Only built-in lint and formatting settings are inert for this adapter.
            // Profiles, implicit extensions and alternate caches need separate support.
            if (section.Name is not ("analyzers" or "formatting") || section.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException();
            if (section.Name == "analyzers")
                foreach (var analyzer in section.Value.EnumerateObject())
                    if (analyzer.Name != "core" || analyzer.Value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        }
        var count = 0;
        void Visit(JsonElement value)
        {
            if (++count > 500_000) throw new InvalidOperationException();
            if ((count & 255) == 0) ct.ThrowIfCancellationRequested();
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                { if (!names.Add(property.Name)) throw new InvalidOperationException(); Visit(property.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
        Visit(json.RootElement);
    }

    sealed class Lexer(string text)
    {
        int position, nesting;
        const int MaxTokens = 65536;
        public List<Token> Read()
        {
            var result = new List<Token>();
            while (Next(result)) if (result.Count > MaxTokens) throw new InvalidOperationException();
            return result;
        }
        bool Next(List<Token> result)
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position])) { position++; continue; }
                if (text.AsSpan(position).StartsWith("//")) { var end = text.IndexOf('\n', position + 2); position = end < 0 ? text.Length : end + 1; continue; }
                if (text.AsSpan(position).StartsWith("/*"))
                {
                    var end = text.IndexOf("*/", position + 2, StringComparison.Ordinal); if (end < 0) throw new InvalidOperationException();
                    position = end + 2; continue;
                }
                break;
            }
            if (position >= text.Length) return false;
            var start = position;
            if (char.IsAsciiLetter(text[position]) || text[position] == '_')
            {
                while (++position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_')) { }
                result.Add(new("identifier", text[start..position]));
            }
            else if (text.AsSpan(position).StartsWith("'''"))
            {
                var end = text.IndexOf("'''", position + 3, StringComparison.Ordinal); if (end < 0) throw new InvalidOperationException();
                result.Add(new("string", text[(position + 3)..end], Multiline: true)); position = end + 3;
            }
            else if (text[position] == '\'') ReadString(result);
            else { result.Add(new("symbol", text[position].ToString())); position++; }
            return true;
        }
        void ReadString(List<Token> result)
        {
            if (++nesting > 24) throw new InvalidOperationException();
            position++; var literal = new StringBuilder(); var nested = new List<Token>(); var interpolation = false; var closed = false;
            while (position < text.Length)
            {
                var c = text[position++];
                if (c == '\'') { closed = true; break; }
                if (c is '\r' or '\n') throw new InvalidOperationException();
                if (c == '$' && position < text.Length && text[position] == '{')
                {
                    position++; interpolation = true; var depth = 1;
                    while (depth > 0)
                    {
                        var part = new List<Token>(); if (!Next(part)) throw new InvalidOperationException();
                        foreach (var token in part)
                        {
                            if (token.Kind == "symbol" && token.Value == "{") depth++;
                            if (token.Kind == "symbol" && token.Value == "}" && --depth == 0) break;
                            nested.Add(token);
                        }
                        if (nested.Count > MaxTokens) throw new InvalidOperationException();
                    }
                    continue;
                }
                if (c != '\\') { literal.Append(c); continue; }
                if (position >= text.Length) throw new InvalidOperationException();
                var escape = text[position++];
                if (escape == 'u')
                {
                    if (position >= text.Length || text[position++] != '{') throw new InvalidOperationException();
                    var end = text.IndexOf('}', position); if (end < 0 || end - position is < 1 or > 6 ||
                        !int.TryParse(text.AsSpan(position, end - position), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) || !Rune.TryCreate(number, out var rune)) throw new InvalidOperationException();
                    literal.Append(rune); position = end + 1;
                }
                else literal.Append(escape switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '\'' => '\'', '$' => '$', _ => throw new InvalidOperationException() });
            }
            nesting--; if (!closed) throw new InvalidOperationException();
            result.Add(new("string", literal.ToString(), interpolation)); result.AddRange(nested);
        }
    }
}
