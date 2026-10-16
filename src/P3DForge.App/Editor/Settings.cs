using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace P3DForge.App.Editor;

/// <summary>Persistent application settings (%APPDATA%\P3DForge\settings.json).</summary>
public sealed class Settings
{
    /// <summary>Root that texture/proxy/rvmat paths are relative to (DayZ work drive).</summary>
    public string ProjectRoot { get; set; } = @"P:\";
    /// <summary>Keep "&lt;file&gt;.p3d.bak" when overwriting.</summary>
    public bool BackupOnSave { get; set; } = false; // .bak copies on save are opt-in (File ▸ Preferences)
    public List<string> RecentFiles { get; set; } = [];
    public int UndoLimit { get; set; } = 200;
    /// <summary>Largest texture edge uploaded to the GPU (picks the matching PAA mipmap).</summary>
    public int TextureMaxSize { get; set; } = 1024;
    public string ViewportBackground { get; set; } = "#2A2D33";
    public bool ShowGrid { get; set; } = true;
    /// <summary>Feature-owned values, namespaced by feature ("viewport.quad", "create.box.size", ...).</summary>
    public Dictionary<string, JsonNode?> Extra { get; set; } = [];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath =>
        Environment.GetEnvironmentVariable("P3DFORGE_SETTINGS")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "P3DForge", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch
        {
            // Corrupt settings: start with defaults (the bad file is overwritten on the next save).
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Settings are best effort.
        }
    }

    public T Get<T>(string key, T fallback)
    {
        try
        {
            return Extra.TryGetValue(key, out var n) && n is not null ? n.Deserialize<T>() ?? fallback : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public void Set<T>(string key, T value)
    {
        Extra[key] = JsonSerializer.SerializeToNode(value);
        Save();
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        Save();
    }
}
