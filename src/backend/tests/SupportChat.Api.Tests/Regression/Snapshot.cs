namespace SupportChat.Api.Tests.Regression;

/// <summary>
/// Golden-file comparison for the ApiHost regression snapshots (ADR 0005 section 1). Set UPDATE_SNAPSHOTS=1 to
/// rewrite the files in the source tree; review the diff before committing.
/// </summary>
public static class Snapshot
{
    public static void Verify(string name, string actual)
    {
        actual = actual.Replace("\r\n", "\n");
        if (!actual.EndsWith('\n'))
        {
            actual += "\n";
        }

        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(Path.Combine(SourceDir(), "Golden", name), actual);
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Golden", name);
        Assert.True(File.Exists(path), $"Missing golden file {name}. Run with UPDATE_SNAPSHOTS=1.");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual);
    }

    private static string SourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SupportChat.Api.Tests.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Test project directory not found.");
    }
}
