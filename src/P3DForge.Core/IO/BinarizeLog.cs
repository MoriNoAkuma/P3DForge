using System.Globalization;
using System.Text.RegularExpressions;
using P3DForge.Core.Model;

namespace P3DForge.Core.IO;

/// <summary>One finding of a binarize run.</summary>
/// <param name="Error">An error (else a warning or note).</param>
/// <param name="Text">The message, as binarize wrote it (paths of the temporary copy shortened).</param>
/// <param name="Lod">LOD index in the model (-1 = whole model / unknown).</param>
/// <param name="Points">Vertices (point indices) binarize names, empty when none.</param>
/// <param name="Count">How many times binarize wrote it.</param>
public sealed record BinarizeMessage(bool Error, string Text, int Lod = -1, int[]? Points = null, int Count = 1);

/// <summary>
/// Reads the console output of DayZ Tools binarize.exe for one model: errors ("Error occured:" + the next line),
/// warnings with their LOD ("model.p3d:2, vertex: 107, …" or a "Warnings in model.p3d:VIEW_PILOT" header), and
/// drops the start-up noise of a bare binarize (missing CfgVehicles / CfgWorlds entries, terrain grid notes).
/// </summary>
public static partial class BinarizeLog
{
    static readonly string[] Noise =
    [
        "No entry '", "Trying to access error value", "FileServer MaxCacheSize", "Distance 0.0 - min grid", "Terrain grid", "BINARIZE ",
        "Binarize started", "Binarize run in silent mode", "ENGINE ", "DEFAULT ", "Model config used", "DIAG-SERIALIZED", "KB saved in selections",
        "Convert model ", "<model", "</model>", "DirIterator",
    ];

    /// <summary>Binarize's names of special LODs → <see cref="LodNames"/> type ids.</summary>
    static readonly Dictionary<string, string> SpecialNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VIEW_PILOT"] = "viewPilot", ["VIEW_GUNNER"] = "viewGunner", ["VIEW_CARGO"] = "viewCargo", ["VIEW_COMMANDER"] = "viewCommander",
        ["geometry"] = "geometry", ["geometryFire"] = "fireGeometry", ["geometryView"] = "viewGeometry", ["geometryPhysX"] = "geometryPhysX",
        ["memory"] = "memory", ["landContact"] = "landContact", ["roadway"] = "roadway", ["paths"] = "paths", ["hitpoints"] = "hitpoints",
        ["shadowVolume"] = "shadowVolume", ["shadowBuffer"] = "shadowBuffer", ["wreck"] = "wreck",
    };

    [GeneratedRegex(@"\.p3d:(\d+), vertex: (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LodVertexRx();

    [GeneratedRegex(@"^Warnings? in .+\.p3d:(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionRx();

    [GeneratedRegex(@"[A-Za-z]:\\[^""'<>|]*?\\([^\\""'<>|]+\.p3d)", RegexOptions.IgnoreCase)]
    private static partial Regex TempPathRx();

    public static List<BinarizeMessage> Parse(string log, P3DModel model)
    {
        var list = new List<BinarizeMessage>();
        var lines = log.Replace("\r", "").Split('\n');
        int sectionLod = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            // a "Warnings in model.p3d:LOD" header covers the warning lines right after it, up to the next other line
            if (line.Length == 0 || Noise.Any(n => line.Contains(n, StringComparison.Ordinal))
                || Regex.IsMatch(line, @"^\d\d:\d\d:\d\d") // engine timestamps
                || line.StartsWith('"') && line.Contains("binarize", StringComparison.OrdinalIgnoreCase)) // the command line
            {
                sectionLod = -1;
                continue;
            }
            if (SectionRx().Match(line) is { Success: true } sec)
            {
                sectionLod = LodByName(sec.Groups[1].Value.Trim(), model);
                continue;
            }
            if (line.StartsWith("Error occured", StringComparison.OrdinalIgnoreCase))
            {
                // the message is on the next line(s); "In F:\jenkins\…" is the engine source location
                var next = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
                if (next.Length > 0)
                {
                    list.Add(new BinarizeMessage(true, Short(next), sectionLod));
                    i++;
                }
                continue;
            }
            if (line.StartsWith("In ", StringComparison.Ordinal) && line.Contains(".cpp:", StringComparison.Ordinal)) continue;
            bool error = line.Contains("error", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase);
            if (!line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)) sectionLod = -1;
            int lod = line.Contains(".p3d':", StringComparison.OrdinalIgnoreCase) ? -1 : sectionLod, point = -1; // 'model.p3d': … is about the whole model
            if (LodVertexRx().Match(line) is { Success: true } lv)
            {
                lod = int.Parse(lv.Groups[1].Value, CultureInfo.InvariantCulture);
                point = int.Parse(lv.Groups[2].Value, CultureInfo.InvariantCulture);
                if (lod >= model.Lods.Length) lod = -1;
            }
            var text = Short(line.StartsWith("Warning: ", StringComparison.OrdinalIgnoreCase) ? line[9..] : line);
            list.Add(new BinarizeMessage(error, text, lod, point >= 0 ? [point] : null));
        }
        // the same line many times (one per face / section / vertex): once, with a count and every vertex named
        return [.. list.GroupBy(m => (m.Error, m.Lod, Key: m.Points is null ? m.Text : NumbersRx().Replace(m.Text, "#")))
            .Select(g =>
            {
                var pts = g.SelectMany(m => m.Points ?? []).Distinct().Order().ToArray();
                var first = g.First();
                var text = pts.Length > 1 ? $"{first.Text} — {pts.Length} vertices: {string.Join(", ", pts.Take(12))}{(pts.Length > 12 ? ", …" : "")}" : first.Text;
                return first with { Text = text, Points = pts.Length > 0 ? pts : null, Count = g.Count() };
            })];
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumbersRx();

    /// <summary>Paths of the temporary copy → the model's file name.</summary>
    static string Short(string s) => TempPathRx().Replace(s, m => m.Groups[1].Value);

    static int LodByName(string name, P3DModel model)
    {
        if (SpecialNames.TryGetValue(name, out var id))
        {
            for (int i = 0; i < model.Lods.Length; i++)
                if (LodNames.TypeOf(model.Lods[i].Resolution).Type.Id == id) return i;
            return -1;
        }
        if (float.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out var res))
            for (int i = 0; i < model.Lods.Length; i++)
                if (Math.Abs(model.Lods[i].Resolution - res) < 1e-3f * Math.Max(1, Math.Abs(res))) return i;
        return -1;
    }
}
