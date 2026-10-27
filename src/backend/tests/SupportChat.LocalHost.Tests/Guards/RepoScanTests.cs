namespace SupportChat.LocalHost.Tests.Guards;

/// <summary>
/// G6 (ADR 0005): nothing that ships (Dockerfiles, pipelines, infra, scripts) may mention LocalHost / LocalMode, except
/// the local runner script. Ordinal and case-sensitive, so "localhost" URLs do not match.
/// </summary>
public class RepoScanTests
{
    private static readonly string[] Needles = ["LocalHost", "LocalMode"];

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "backend", "SupportChat.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found (src/backend/SupportChat.slnx).");
    }

    private static IEnumerable<string> Files(string root, string relativeDir, string pattern = "*") =>
        Directory.Exists(Path.Combine(root, relativeDir))
            ? Directory.EnumerateFiles(Path.Combine(root, relativeDir), pattern, SearchOption.AllDirectories)
            : [];

    [Fact]
    public void No_shipped_file_mentions_LocalHost_or_LocalMode()
    {
        var root = FindRepoRoot();
        var runner = Path.GetFullPath(Path.Combine(root, "scripts", "run-local.mjs"));

        var dockerfiles = Directory.EnumerateFiles(Path.Combine(root, "src", "backend"), "Dockerfile*", SearchOption.TopDirectoryOnly).ToList();
        var pipelines = Files(root, ".github").ToList();
        var infra = Files(root, "infra").ToList();
        var scripts = Files(root, "scripts").Where(f => Path.GetFullPath(f) != runner).ToList();

        // No vacuous pass: every scanned set must contain files.
        Assert.NotEmpty(dockerfiles);
        Assert.NotEmpty(pipelines);
        Assert.NotEmpty(infra);
        Assert.NotEmpty(scripts);

        var offenders = dockerfiles.Concat(pipelines).Concat(infra).Concat(scripts)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => { var text = File.ReadAllText(f); return Needles.Any(n => text.Contains(n, StringComparison.Ordinal)); })
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Dockerignore_excludes_the_LocalHost_sources()
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), "src", "backend", ".dockerignore")).Select(l => l.Trim());
        Assert.Contains("src/SupportChat.LocalHost/", lines);
    }

    [Fact]
    public void No_other_backend_project_references_the_LocalHost()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "backend", "src");
        var offenders = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains("SupportChat.LocalHost", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("SupportChat.LocalHost", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(offenders);
    }
}
