namespace SelfService.Portal;

// Resolve trusted application/source locations at startup. Private runtime and credential
// paths have different identities and must not pass through this source resolver.
public static class PortalWorkspace
{
    public static string ResolveRepositoryRoot(string? configuredRoot, string contentRoot, string applicationBaseDirectory)
    {
        var content = AbsoluteDirectory(contentRoot);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var selected = RelativeDirectory(configuredRoot, content);
            return RootOrKnownChild(selected) ?? throw InvalidRoot();
        }

        var application = AbsoluteDirectory(applicationBaseDirectory);
        return AutomaticRoot(application, content) ?? throw InvalidRoot();
    }

    public static string ResolveContentRoot(string applicationBaseDirectory, string launchContentRoot, bool explicitlyConfigured = false)
    {
        var application = AbsoluteDirectory(applicationBaseDirectory);
        var launch = AbsoluteDirectory(launchContentRoot);
        if (ExistingDirectory(Path.Combine(application, "wwwroot"))) return application;
        // An explicit host content root keeps its configuration-file and static-file meaning.
        if (explicitlyConfigured) return launch;
        var repository = AutomaticRoot(application, launch);
        if (repository is not null)
        {
            var sourcePortal = Path.Combine(repository, "src", "SelfService.Portal");
            if (ExistingDirectory(Path.Combine(sourcePortal, "wwwroot"))) return sourcePortal;
        }
        return launch;
    }

    static string? AutomaticRoot(string application, string content)
    {
        var bundled = Path.Combine(application, "repository");
        if (IsRoot(bundled)) return bundled;
        // The compiled application's own ancestry wins over the launch directory.
        for (var current = new DirectoryInfo(application); current is not null; current = current.Parent)
            if (IsRoot(current.FullName)) return current.FullName;
        for (var current = new DirectoryInfo(content); current is not null; current = current.Parent)
            if (RootOrKnownChild(current.FullName) is { } selected) return selected;
        return null;
    }

    static string? RootOrKnownChild(string path)
    {
        if (IsRoot(path)) return path;
        var child = Path.Combine(path, "Bicep");
        return IsRoot(child) ? child : null;
    }

    static bool IsRoot(string path)
    {
        var marker = Path.Combine(path, "config", "workloads.json");
        RejectLinks(marker);
        return Directory.Exists(path) && File.Exists(marker);
    }

    static bool ExistingDirectory(string path) { RejectLinks(path); return Directory.Exists(path); }

    static string AbsoluteDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw InvalidRoot();
        return RelativeDirectory(value, Path.GetPathRoot(value)!);
    }

    static string RelativeDirectory(string value, string baseDirectory)
    {
        try
        {
            if (value.Length > 4096 || value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                Path.IsPathRooted(value) && !Path.IsPathFullyQualified(value)) throw InvalidRoot();
            var original = Path.IsPathFullyQualified(value) ? value : Path.Combine(baseDirectory, value);
            // Inspect before removing dot segments so a linked segment followed by '..'
            // cannot be erased by lexical normalization.
            RejectLinks(original);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(original));
            if (!Directory.Exists(full)) throw InvalidRoot();
            return full;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { throw InvalidRoot(); }
    }

    static void RejectLinks(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) throw InvalidRoot();
        var current = root; Check(current);
        foreach (var part in path[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { current = Directory.GetParent(current)?.FullName ?? root; Check(current); continue; }
            if (part.EndsWith('.') || part.EndsWith(' ')) throw InvalidRoot();
            current = Path.Combine(current, part); Check(current);
        }
    }

    static void Check(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw InvalidRoot();
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    static InvalidOperationException InvalidRoot() => new("A trusted source repository containing config/workloads.json is required. See docs/local-portal.md.");
}
