using System.Diagnostics;

namespace SelfService.Portal;

/// <summary>Passes correlation to owned calculation processes without forwarding credentials or baggage.</summary>
public static class ChildProcessTelemetry
{
    static readonly HashSet<string> OperatingEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "USERPROFILE", "HOME",
        "HOMEDRIVE", "HOMEPATH", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "LANG", "LC_ALL",
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
    };

    public static void Configure(ProcessStartInfo start)
    {
        var retained = start.Environment.Where(pair => OperatingEnvironment.Contains(pair.Key)).ToArray();
        start.Environment.Clear();
        foreach (var pair in retained) start.Environment[pair.Key] = pair.Value;
        // The portal observes process lifetime. Internal child spans require the child to support W3C context.
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent })
            start.Environment["TRACEPARENT"] = traceParent;
    }
}
