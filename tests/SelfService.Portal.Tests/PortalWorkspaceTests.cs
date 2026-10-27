using System.Diagnostics;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class PortalWorkspaceTests
{
    [Fact]
    public void Outer_workspace_resolves_only_its_known_Bicep_child()
    {
        using var f = new Workspace();
        Assert.Equal(f.Repository, PortalWorkspace.ResolveRepositoryRoot(null, f.Outer, f.UnrelatedBinary));
        Assert.Equal(f.Repository, PortalWorkspace.ResolveRepositoryRoot(f.Outer, f.Unrelated, f.UnrelatedBinary));
    }

    [Fact]
    public void Source_nested_launch_resolves_its_repository_ancestor()
    {
        using var f = new Workspace();
        Assert.Equal(f.Repository, PortalWorkspace.ResolveRepositoryRoot(null, f.SourcePortal, f.UnrelatedBinary));
    }

    [Fact]
    public void Binary_ancestry_wins_over_an_unrelated_launch_directory_and_other_source_marker()
    {
        using var f = new Workspace(); var other = f.MakeRepository("other-workspace");
        Assert.Equal(f.Repository, PortalWorkspace.ResolveRepositoryRoot(null, other, f.Binary));
        Assert.Equal(f.SourcePortal, PortalWorkspace.ResolveContentRoot(f.Binary, f.Unrelated));
    }

    [Fact]
    public void Published_static_files_and_bundled_repository_keep_precedence()
    {
        using var f = new Workspace(); var bundled = f.MakeRepository(Path.GetRelativePath(f.Root, Path.Combine(f.Binary, "repository")));
        Directory.CreateDirectory(Path.Combine(f.Binary, "wwwroot"));
        Assert.Equal(f.Binary, PortalWorkspace.ResolveContentRoot(f.Binary, f.SourcePortal));
        Assert.Equal(f.Binary, PortalWorkspace.ResolveContentRoot(f.Binary, f.Unrelated, explicitlyConfigured: true));
        Assert.Equal(bundled, PortalWorkspace.ResolveRepositoryRoot(null, f.SourcePortal, f.Binary));
        Assert.Equal(f.Repository, PortalWorkspace.ResolveRepositoryRoot(f.Repository, f.SourcePortal, f.Binary));
    }

    [Fact]
    public void Explicit_relative_repository_uses_trusted_configuration_base_instead_of_working_directory()
    {
        using var f = new Workspace(); var configuration = f.MakeDirectory(Path.Combine("workspace", "configuration"));
        Assert.Equal(f.Repository, PortalWorkspace.ResolveRepositoryRoot(Path.Combine("..", "Bicep"), configuration, f.UnrelatedBinary));
        Assert.Equal(f.Repository, PortalWorkspace.ResolveRepositoryRoot(Path.Combine("..", ".."), f.SourcePortal, f.UnrelatedBinary));
    }

    [Fact]
    public void Explicit_host_content_root_keeps_its_configuration_base()
    {
        using var f = new Workspace();
        Assert.Equal(f.Unrelated, PortalWorkspace.ResolveContentRoot(f.Binary, f.Unrelated, explicitlyConfigured: true));
        Assert.Equal(f.SourcePortal, PortalWorkspace.ResolveContentRoot(f.Binary, f.Unrelated));
    }

    [Theory]
    [InlineData("existing-without-marker")]
    [InlineData("missing")]
    [InlineData("workspace/Bicep/src/SelfService.Portal")]
    public void Invalid_explicit_selection_never_falls_back_to_the_binary_repository(string selected)
    {
        using var f = new Workspace(); f.MakeDirectory("existing-without-marker");
        Assert.Throws<InvalidOperationException>(() => PortalWorkspace.ResolveRepositoryRoot(
            selected.Replace('/', Path.DirectorySeparatorChar), f.Root, f.Binary));
    }

    [Fact]
    public void Missing_automatic_source_fails_without_creating_a_fallback_repository()
    {
        using var f = new Workspace();
        Assert.Throws<InvalidOperationException>(() => PortalWorkspace.ResolveRepositoryRoot(null, f.Unrelated, f.UnrelatedBinary));
        Assert.False(Directory.Exists(Path.Combine(f.UnrelatedBinary, "repository")));
    }

    [Fact]
    public void Ordinary_source_dot_segments_and_trailing_separator_keep_the_existing_root_identity()
    {
        using var f = new Workspace();
        Assert.Equal(f.Repository, PortalWorkspace.ResolveRepositoryRoot(f.Repository + Path.DirectorySeparatorChar, f.Root, f.UnrelatedBinary));
        Assert.Equal(f.Repository, PortalWorkspace.ResolveRepositoryRoot(Path.Combine(f.Repository, ".", "src", ".."), f.Root, f.UnrelatedBinary));
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("alias/../Bicep")]
    public void Source_links_are_rejected_before_lexical_dot_segment_normalization(string relative)
    {
        using var f = new Workspace(); var alias = Path.Combine(f.Outer, "alias"); f.Link(alias, f.Repository);
        Assert.Throws<InvalidOperationException>(() => PortalWorkspace.ResolveRepositoryRoot(
            relative.Replace('/', Path.DirectorySeparatorChar), f.Outer, f.Binary));
        Assert.True(File.Exists(Path.Combine(f.Repository, "config", "workloads.json")));
    }

    [Fact]
    public void Linked_binary_ancestry_cannot_select_a_source_root()
    {
        using var f = new Workspace(); var alias = Path.Combine(f.Root, "binary-alias"); f.Link(alias, f.Binary);
        Assert.Throws<InvalidOperationException>(() => PortalWorkspace.ResolveRepositoryRoot(null, f.SourcePortal, alias));
    }

    [Fact]
    public void Linked_catalog_marker_directory_is_rejected_without_fallback()
    {
        using var f = new Workspace(); var candidate = f.MakeDirectory("linked-catalog");
        f.Link(Path.Combine(candidate, "config"), Path.Combine(f.Repository, "config"));
        Assert.Throws<InvalidOperationException>(() => PortalWorkspace.ResolveRepositoryRoot(candidate, f.Root, f.Binary));
    }

    [Theory]
    [InlineData("workspace/Bicep.")]
    [InlineData("workspace/Bicep ")]
    public void Windows_source_alias_segments_are_not_accepted(string relative)
    {
        using var f = new Workspace();
        Assert.Throws<InvalidOperationException>(() => PortalWorkspace.ResolveRepositoryRoot(
            relative.Replace('/', Path.DirectorySeparatorChar), f.Root, f.Binary));
    }

    sealed class Workspace : IDisposable
    {
        readonly List<string> links = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "platform-source-workspace-" + Guid.NewGuid().ToString("N"));
        public string Outer { get; }
        public string Repository { get; }
        public string SourcePortal { get; }
        public string Binary { get; }
        public string Unrelated { get; }
        public string UnrelatedBinary { get; }
        public Workspace()
        {
            Directory.CreateDirectory(Root);
            Outer = MakeDirectory("workspace"); Repository = MakeRepository(Path.Combine("workspace", "Bicep"));
            SourcePortal = MakeDirectory(Path.Combine("workspace", "Bicep", "src", "SelfService.Portal"));
            Directory.CreateDirectory(Path.Combine(SourcePortal, "wwwroot"));
            Binary = MakeDirectory(Path.Combine("workspace", "Bicep", "artifacts", "isolated", "portal"));
            Unrelated = MakeDirectory("unrelated-launch"); UnrelatedBinary = MakeDirectory("separate-binary");
        }
        public string MakeDirectory(string relative) { var path = Path.Combine(Root, relative); Directory.CreateDirectory(path); return path; }
        public string MakeRepository(string relative)
        {
            var path = MakeDirectory(relative); Directory.CreateDirectory(Path.Combine(path, "config"));
            File.WriteAllText(Path.Combine(path, "config", "workloads.json"), "{}"); return path;
        }
        public void Link(string path, string target)
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/c", "mklink", "/J", path, target }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
                Assert.True(process.ExitCode == 0, "Fixture junction could not be created: " + output + error);
            }
            else Directory.CreateSymbolicLink(path, target);
            links.Add(path);
        }
        public void Dispose()
        {
            foreach (var path in links.AsEnumerable().Reverse()) if (Directory.Exists(path)) Directory.Delete(path);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
