using System.Text.Json;

namespace P3DForge.Core.Tests;

/// <summary>Locates repository test data (testdata/) and the user's real MLOD corpus on P:\.</summary>
public static class TestData
{
    public static string RepoRoot { get; } = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "P3DForge.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("P3DForge.sln not found above the test binaries");
    }

    public static string Path_(params string[] parts) => Path.Combine([RepoRoot, .. parts]);

    public static string SyntheticDir => Path_("testdata", "synthetic");

    /// <summary>A file the test needs: skips the test when it is not there (game samples and local models are not in the repository).</summary>
    public static string Need(string path)
    {
        Assert.SkipWhen(!File.Exists(path), $"{path} not available");
        return path;
    }

    /// <summary>A DayZ Tools sample model in testdata/dzsamples (copy them there yourself; Bohemia's files are not in the repository).</summary>
    public static string Sample(string file) => Need(Path_("testdata", "dzsamples", file));

    /// <summary>
    /// A model of your own work drive named in testdata/local.json (not in the repository), e.g.
    /// { "btr": "P:\\MyVehicle\\car.p3d" }; the test is skipped when the key or the file is missing.
    /// </summary>
    public static string Local(string key)
    {
        var file = Path_("testdata", "local.json");
        string? path = null;
        if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) is { } map) map.TryGetValue(key, out path);
        Assert.SkipWhen(path is null, $"testdata/local.json has no \"{key}\"");
        return Need(path!);
    }

    public record CorpusFile(string path, long size);

    /// <summary>The user's MLOD files on P:\ (from testdata/corpus.json), only those that currently exist.</summary>
    public static IReadOnlyList<string> Corpus()
    {
        var list = Path_("testdata", "corpus.json");
        if (!File.Exists(list)) return [];
        var files = JsonSerializer.Deserialize<List<CorpusFile>>(File.ReadAllText(list)) ?? [];
        return files.Select(f => f.path).Where(File.Exists).ToList();
    }

    public static string OutDir()
    {
        var d = Path_("testdata", "out", "cs-tests");
        Directory.CreateDirectory(d);
        return d;
    }
}
