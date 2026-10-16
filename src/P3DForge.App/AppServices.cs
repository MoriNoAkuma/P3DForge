using System.IO;
using P3DForge.App.Editor;

namespace P3DForge.App;

/// <summary>Application singletons shared by every feature.</summary>
public static class AppServices
{
    public static EditorState Editor { get; } = new();
    public static Settings Settings { get; private set; } = new();

    internal static void Initialize()
    {
        Settings = Settings.Load();
        Editor.UndoLimit = Settings.UndoLimit;
        Commands.ErrorSink = m => Editor.Log(m, LogLevel.Error);
    }

    /// <summary>
    /// Resolves a model-relative path ("dz\weapons\x_co.paa", "\dz\...", "P:\dz\...") to an absolute path
    /// under the project root. Returns null for procedural textures ("#(argb,...)") and empty strings.
    /// </summary>
    public static string? ResolveProjectPath(string p, string? root = null)
    {
        var s = p.Trim();
        if (s.Length == 0 || s.StartsWith('#')) return null;
        if (s.Length > 2 && char.IsLetter(s[0]) && s[1] == ':') return s.Replace('/', '\\');
        root ??= Settings.ProjectRoot;
        return Path.Combine(root.TrimEnd('\\', '/') + "\\", s.TrimStart('\\', '/').Replace('/', '\\'));
    }

    /// <summary>
    /// A texture / material path the way the game needs it: relative to the work drive, starting with the first folder
    /// ("MyMod\rifles\...\x_co.paa"), never "P:\...". An absolute path under the work drive loses the drive
    /// part; '/' becomes '\'; procedural "#(...)" values stay as they are. Paths outside the work drive stay absolute.
    /// </summary>
    public static string NormalizeGamePath(string p)
    {
        var s = p.Trim();
        if (s.Length == 0 || s.StartsWith('#')) return s;
        s = s.Replace('/', '\\');
        if (s.Length > 2 && s[1] == ':') s = ToProjectPath(s);
        return s.Length > 2 && s[1] == ':' ? s : s.TrimStart('\\');
    }

    /// <summary>Makes an absolute path relative to the project root when it is inside it.</summary>
    public static string ToProjectPath(string abs, string? root = null)
    {
        root ??= Settings.ProjectRoot;
        var r = root.TrimEnd('\\', '/') + "\\";
        var a = abs.Replace('/', '\\');
        return a.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? a[r.Length..] : a;
    }
}
