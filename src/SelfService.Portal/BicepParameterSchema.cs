using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

// Bounded ARM input schemas become explicit Bicep UDTs. Values remain JSON data;
// neither schema references nor user strings are interpreted as Bicep source.
public static class BicepParameterSchema
{
    static readonly Regex PropertyName = new("^[a-zA-Z_][a-zA-Z0-9_]{0,127}$", RegexOptions.CultureInvariant);
    static readonly string[] Advanced = ["anyOf", "oneOf", "allOf", "discriminator", "prefixItems", "pattern", "patternProperties", "not"];

    public static JsonElement Expand(JsonElement schema, Dictionary<string, JsonElement>? definitions = null)
    {
        var count = 0;
        JsonNode Visit(JsonElement value, HashSet<string> references, int depth)
        {
            if (++count > 2048 || depth > 24 || value.ValueKind != JsonValueKind.Object) throw new PortalException("Module input schema exceeds the reviewed type adapter.");
            var result = JsonNode.Parse(value.GetRawText())!.AsObject();
            if (value.TryGetProperty("$ref", out var reference))
            {
                var path = reference.GetString();
                const string prefix = "#/definitions/";
                if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal) || path.Length <= prefix.Length || path[prefix.Length..].Contains('/') ||
                    definitions is null || !definitions.TryGetValue(path[prefix.Length..], out var definition) || !references.Add(path))
                    throw new PortalException("Unknown or cyclic module input type reference.");
                var expanded = Visit(definition, references, depth + 1).AsObject();
                references.Remove(path);
                foreach (var property in result.Where(p => p.Key != "$ref").ToArray()) expanded[property.Key] = property.Value?.DeepClone();
                result = expanded;
            }
            if (Advanced.Any(result.ContainsKey) || result["type"] is not JsonValue typeNode || !typeNode.TryGetValue<string>(out var type) || type is not ("string" or "int" or "bool" or "array" or "object"))
                throw new PortalException("Secure or advanced types require a separately qualified adapter.");
            if (result.TryGetPropertyValue("properties", out var propertiesNode))
            {
                if (type != "object" || propertiesNode is not JsonObject properties || properties.Count > 128) throw new PortalException("Unsupported module object schema.");
                foreach (var property in properties.ToArray())
                {
                    if (!PropertyName.IsMatch(property.Key) || property.Value is null) throw new PortalException("Unsupported module property identifier.");
                    properties[property.Key] = Visit(JsonSerializer.SerializeToElement(property.Value), references, depth + 1);
                }
            }
            if (result.TryGetPropertyValue("items", out var items))
            {
                if (type != "array" || items is null) throw new PortalException("Unsupported module array schema.");
                result["items"] = Visit(JsonSerializer.SerializeToElement(items), references, depth + 1);
            }
            if (result.TryGetPropertyValue("additionalProperties", out var additional) && (additional is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
                throw new PortalException("Typed additional properties require a separately qualified adapter.");
            if (result.TryGetPropertyValue("allowedValues", out var allowed) && (allowed is not JsonArray choices || choices.Count is 0 or > 128 || choices.Any(v => v is null || v.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))))
                throw new PortalException("Unsupported module allowed-value schema.");
            return result;
        }
        return JsonSerializer.SerializeToElement(Visit(schema, new(StringComparer.Ordinal), 0));
    }

    public static string Type(JsonElement expanded, string name, List<string> declarations)
    {
        var counter = 0;
        string Render(JsonElement schema)
        {
            var type = schema.GetProperty("type").GetString()!;
            string result;
            if (schema.TryGetProperty("allowedValues", out var choices) && type is "string" or "int" or "bool")
                result = string.Join(" | ", choices.EnumerateArray().Select(Literal));
            else if (type == "array" && schema.TryGetProperty("allowedValues", out var itemChoices)) result = "(" + string.Join(" | ", itemChoices.EnumerateArray().Select(Literal)) + ")[]";
            else if (type == "array" && schema.TryGetProperty("items", out var items)) result = "(" + Render(items) + ")[]";
            else if (type == "object" && schema.TryGetProperty("properties", out var properties))
            {
                var typeName = "t_" + name + "_" + counter++;
                var fields = new List<string>();
                foreach (var property in properties.EnumerateObject())
                {
                    foreach (var decorator in Decorators(property.Value)) fields.Add("  " + decorator);
                    fields.Add("  " + property.Name + ": " + Render(property.Value));
                }
                declarations.Add((schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False ? "@sealed()\n" : "") + "type " + typeName + " = {\n" + string.Join('\n', fields) + "\n}");
                result = typeName;
            }
            else result = type;
            return schema.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.True ? "(" + result + ")?" : result;
        }
        return Render(expanded);
    }

    public static IEnumerable<string> Decorators(JsonElement schema)
    {
        foreach (var constraint in new[] { "minLength", "maxLength", "minValue", "maxValue" })
            if (schema.TryGetProperty(constraint, out var bound))
            {
                if (bound.ValueKind != JsonValueKind.Number || !bound.TryGetInt64(out _)) throw new PortalException("Invalid module input bound.");
                yield return "@" + constraint + "(" + bound.GetRawText() + ")";
            }
    }

    public static void Validate(JsonElement value, JsonElement schema)
    {
        var count = 0;
        void Visit(JsonElement item, JsonElement definition, int depth)
        {
            if (++count > 8192 || depth > 24) throw new PortalException("Setting exceeds the reviewed type adapter.");
            if (item.ValueKind == JsonValueKind.Null && definition.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.True) return;
            var type = definition.GetProperty("type").GetString();
            var valid = type switch { "string" => item.ValueKind == JsonValueKind.String, "object" => item.ValueKind == JsonValueKind.Object,
                "array" => item.ValueKind == JsonValueKind.Array, "int" => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out _),
                "bool" => item.ValueKind is JsonValueKind.True or JsonValueKind.False, _ => false };
            if (!valid) throw new PortalException("Setting type differs from module input.");
            if (definition.TryGetProperty("allowedValues", out var allowed))
            {
                var candidates = type == "array" ? item.EnumerateArray().ToArray() : [item];
                if (candidates.Any(candidate => !allowed.EnumerateArray().Any(x => JsonElement.DeepEquals(x, candidate)))) throw new PortalException("Setting is outside module allowed values.");
            }
            var length = item.ValueKind == JsonValueKind.String ? item.GetString()!.Length : item.ValueKind == JsonValueKind.Array ? item.GetArrayLength() : (int?)null;
            if (length is { } n && (definition.TryGetProperty("minLength", out var min) && n < min.GetInt32() || definition.TryGetProperty("maxLength", out var max) && n > max.GetInt32())) throw new PortalException("Setting length is outside module bounds.");
            if (type == "int" && (definition.TryGetProperty("minValue", out var lo) && item.GetInt64() < lo.GetInt64() || definition.TryGetProperty("maxValue", out var hi) && item.GetInt64() > hi.GetInt64())) throw new PortalException("Setting number is outside module bounds.");
            if (type == "array" && definition.TryGetProperty("items", out var items)) foreach (var child in item.EnumerateArray()) Visit(child, items, depth + 1);
            if (type == "object")
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                var hasProperties = definition.TryGetProperty("properties", out var properties);
                foreach (var property in item.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new PortalException("Duplicate module input property.");
                    if (hasProperties && properties.TryGetProperty(property.Name, out var child)) Visit(property.Value, child, depth + 1);
                    else if (definition.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False) throw new PortalException("Unknown module child property.");
                }
                if (hasProperties)
                    foreach (var property in properties.EnumerateObject())
                        if (!names.Contains(property.Name) && !(property.Value.TryGetProperty("nullable", out var optional) && optional.ValueKind == JsonValueKind.True)) throw new PortalException("Required module child property is missing.");
            }
        }
        Visit(value, schema, 0);
    }

    static string Literal(JsonElement value) => value.ValueKind switch {
        JsonValueKind.String => "'" + value.GetString()!.Replace("\\", "\\\\").Replace("'", "\\'").Replace("${", "\\${").Replace("\r", "\\r").Replace("\n", "\\n") + "'",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => throw new PortalException("Unsupported module constraint.") };
}
