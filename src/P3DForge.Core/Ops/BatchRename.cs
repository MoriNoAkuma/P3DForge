using System.Text.RegularExpressions;
using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>
/// Path renaming in files (moving a mod to another folder): MLOD models (face textures and materials, proxy paths)
/// and text files (rvmat, config.cpp: only inside quoted strings, a leading backslash kept). Pure: returns the new
/// content (null = unchanged) and the number of replaced references.
/// </summary>
public static partial class BatchRename
{
    /// <summary>A model's new bytes (null = nothing to change). Throws on files that are not MLOD.</summary>
    public static (byte[]? Bytes, int Count) Model(byte[] mlod, Func<string, string?> replacer, ReplaceOptions o)
    {
        var model = Mlod.Read(mlod, null);
        var (renamed, count) = Attributes.ReplacePaths(model, replacer, o);
        int proxies = 0;
        var lods = renamed.Lods;
        for (int i = 0; i < lods.Length; i++)
        {
            var lod = lods[i];
            var tags = lod.Tags;
            for (int t = 0; t < tags.Length; t++)
            {
                if (tags[t] is not SelectionTag s || !Proxies.IsProxyName(s.Name)) continue;
                // "proxy:\path\model.001": the path without its instance number
                var name = s.Name["proxy:".Length..];
                int dot = name.LastIndexOf('.');
                string path = dot > 0 ? name[..dot] : name, suffix = dot > 0 ? name[dot..] : "";
                bool slash = path.StartsWith('\\');
                if (replacer(slash ? path[1..] : path) is not { } to) continue;
                tags = tags.SetItem(t, s with { Name = "proxy:" + (slash ? "\\" : "") + to.TrimStart('\\') + suffix });
                proxies++;
            }
            if (tags != lod.Tags) lods = lods.SetItem(i, lod with { Tags = tags });
        }
        if (count + proxies == 0) return (null, 0);
        return (Mlod.Write(renamed with { Lods = lods }), count + proxies);
    }

    [GeneratedRegex("\"([^\"\\r\\n]*)\"")]
    private static partial Regex QuotedRx();

    /// <summary>Replaces paths inside the quoted strings of a config / rvmat text.</summary>
    public static (string? Text, int Count) Text(string text, Func<string, string?> replacer)
    {
        int count = 0;
        var result = QuotedRx().Replace(text, m =>
        {
            var value = m.Groups[1].Value;
            if (value.Length == 0 || value.StartsWith('#')) return m.Value;
            bool slash = value.StartsWith('\\');
            if (replacer(slash ? value[1..] : value) is not { } to) return m.Value;
            count++;
            return "\"" + (slash ? "\\" : "") + to.TrimStart('\\') + "\"";
        });
        return count == 0 ? (null, 0) : (result, count);
    }
}
