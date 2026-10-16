using System.IO;

namespace P3DForge.App.Services;

/// <summary>
/// Does a game path (texture, rvmat, model, as written in configs and models) exist on the work drive? Besides
/// "missing" it recognises a path that only exists one folder deeper (P:\MyMod\rifles\… for
/// \rifles\…): fine in game when that folder is packed with the PBO prefix, but binarize on the work drive does
/// not find it. Results are cached per work drive (call <see cref="Forget"/> after files moved).
/// </summary>
public static class GamePaths
{
    static readonly Dictionary<string, (bool Error, string Why)?> cache = new(StringComparer.OrdinalIgnoreCase);
    static string cachedRoot = "";
    static string[]? topDirs;

    public static void Forget()
    {
        lock (cache)
        {
            cache.Clear();
            topDirs = null;
        }
    }

    /// <summary>Null when <paramref name="gamePath"/> resolves on the work drive, else (error?, why).</summary>
    public static (bool Error, string Why)? Problem(string gamePath)
    {
        var p = gamePath.Trim().Replace('/', '\\').TrimStart('\\');
        if (p.Length == 0 || p.StartsWith('#')) return null;
        var root = AppServices.Settings.ProjectRoot;
        lock (cache)
        {
            if (!string.Equals(root, cachedRoot, StringComparison.OrdinalIgnoreCase))
            {
                cache.Clear();
                topDirs = null;
                cachedRoot = root;
            }
            if (cache.TryGetValue(p, out var hit)) return hit;
        }
        var result = Find(p, root);
        lock (cache) cache[p] = result;
        return result;
    }

    static (bool Error, string Why)? Find(string p, string root)
    {
        if (p.Length > 2 && p[1] == ':') return File.Exists(p) ? null : (true, "file not found");
        var full = AppServices.ResolveProjectPath(p, root);
        if (full is null || File.Exists(full)) return null;
        if (Path.GetExtension(p).Equals(".paa", StringComparison.OrdinalIgnoreCase))
            foreach (var ext in new[] { ".tga", ".png" })
                if (File.Exists(Path.ChangeExtension(full, ext)))
                    return (false, $"only the {ext} exists on the work drive (packing converts it to .paa)");
        topDirs ??= SafeDirs(root);
        foreach (var top in topDirs)
        {
            var deeper = Path.Combine(top, p);
            if (File.Exists(deeper))
                return (false, $"not at {full}, but at {deeper}: works in game only if {Path.Combine(top, p.Split('\\')[0])} is packed with the PBO prefix \"{p.Split('\\')[0]}\"; binarize on the work drive does not find it");
        }
        return (true, $"not found on the work drive ({full})");
    }

    static string[] SafeDirs(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch
        {
            return [];
        }
    }
}
