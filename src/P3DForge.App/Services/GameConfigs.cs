using System.IO;
using P3DForge.Core.Anim;
using P3DForge.Core.Materials;

namespace P3DForge.App.Services;

/// <summary>
/// config.cpp files for a model: the mod's own (every config.cpp under the first folder of the work drive that holds
/// the model, e.g. P:\MyMod) and the game's (P:\DZ, without worlds / sounds / UI), so inherited values resolve.
/// Parsed files are cached by time stamp; loading runs on any thread.
/// </summary>
public static class GameConfigs
{
    static readonly Dictionary<string, (DateTime Stamp, ConfigClass Root)> parsed = new(StringComparer.OrdinalIgnoreCase);
    static readonly string[] SkipDz = ["worlds", "sounds", "ui", "anims", "water", "surfaces", "navmesh"];

    /// <summary>The first folder under the work drive that holds the model ("P:\MyMod"), or the model's folder.</summary>
    public static string? ModRoot(string? p3d, string projectRoot)
    {
        if (p3d is null) return null;
        var root = Path.GetFullPath(projectRoot).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(p3d);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(full);
        return Path.Combine(root, full[root.Length..].Split('\\')[0]);
    }

    /// <summary>Mod configs first (they win), then the game's.</summary>
    public static (ConfigLibrary Library, List<string> Warnings) Load(string? modelFile, string projectRoot)
    {
        var warnings = new List<string>();
        var lib = new ConfigLibrary();
        var files = new List<string>();
        if (ModRoot(modelFile, projectRoot) is { } mod) files.AddRange(Files(mod, 600));
        var dz = Path.Combine(projectRoot, "DZ");
        if (Directory.Exists(dz))
        {
            files.Add(Path.Combine(dz, "data", "config.cpp"));
            foreach (var d in SafeDirs(dz))
                if (!SkipDz.Any(s => Path.GetFileName(d).StartsWith(s, StringComparison.OrdinalIgnoreCase))) files.AddRange(Files(d, 600));
        }
        else warnings.Add($"{dz} not found — classes of the game (bases of the mod's classes) are unknown");
        foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(f)) continue;
            try
            {
                lib.Add(f, Parse(f));
            }
            catch (Exception ex)
            {
                warnings.Add($"{f}: {ex.Message}");
            }
        }
        return (lib, warnings);
    }

    static ConfigClass Parse(string file)
    {
        var stamp = File.GetLastWriteTimeUtc(file);
        lock (parsed)
            if (parsed.TryGetValue(file, out var e) && e.Stamp == stamp) return e.Root;
        var root = ConfigText.Parse(File.ReadAllText(file));
        lock (parsed) parsed[file] = (stamp, root);
        return root;
    }

    static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch
        {
            return [];
        }
    }

    static List<string> Files(string dir, int max)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "config.cpp", SearchOption.AllDirectories).Take(max).ToList() : [];
        }
        catch
        {
            return [];
        }
    }
}
