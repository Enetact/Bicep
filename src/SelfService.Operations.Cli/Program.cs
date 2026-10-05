using System.Text.Json;
using SelfService.Operations;

// Offline consumer of the same rules as the portal. Never acquires tokens or calls Azure/ADO.
if (args.Length is < 3 or > 4) { Console.Error.WriteLine("Usage: operations <private-dns|drift> <evidence-or-export.json> <new-output.json> [reference-export.json]"); return 2; }
try
{
    JsonElement Read(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new OperationsException("Input exceeds 16 MiB.");
        return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
    }
    OperationsEvidence Evidence(JsonElement value) => OperationsJson.Read<OperationsEvidence>((value.TryGetProperty("evidence",out var e)?e:value).GetRawText());
    var input=Read(args[1]);var observed=Evidence(input);var reference=args.Length==4?Evidence(Read(args[3])):null;
    var edits=input.TryGetProperty("desiredEdits",out var desired)?OperationsJson.Read<DesiredEdit[]>(desired.GetRawText()):[];
    var assessment=OperationsRules.Assess(args[0],observed,reference,edits);
    await using var output=new FileStream(args[2],FileMode.CreateNew,FileAccess.Write);
    await JsonSerializer.SerializeAsync(output,new {assessment,offline=true,executionAvailable=false},OperationsJson.Options);
    Console.WriteLine("Offline assessment saved. Input hashes establish consistency, not independent Azure authenticity. No execution is available."); return 0;
}
catch(Exception e) when(e is OperationsException or JsonException or IOException or UnauthorizedAccessException)
{ Console.Error.WriteLine("Assessment failed: "+e.Message); return 1; }
