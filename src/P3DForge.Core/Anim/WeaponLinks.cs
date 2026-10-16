using System.Text;
using System.Text.RegularExpressions;
using P3DForge.Core.Materials;

namespace P3DForge.Core.Anim;

/// <summary>A config class found in one of the loaded files.</summary>
public sealed record ClassRef(string File, string Section, ConfigClass Class)
{
    public string Name => Class.Name;
}

/// <summary>
/// Classes of config.cpp sections (CfgWeapons, CfgVehicles…) from many files — the mod's own and the game's — so that
/// inherited values (boneRemap from BoltActionRifle_Base, weaponStateAnim…) resolve like in the game: a class's base is
/// looked up by name in the same section of every loaded file, later files do not override earlier ones.
/// </summary>
public sealed class ConfigLibrary
{
    readonly Dictionary<string, Dictionary<string, ClassRef>> sections = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Files { get; } = [];
    public List<string> Warnings { get; } = [];

    public void Add(string file, string text) => Add(file, ConfigText.Parse(text));

    /// <summary>Adds an already parsed file (callers cache parsed game configs).</summary>
    public void Add(string file, ConfigClass root)
    {
        Files.Add(file);
        foreach (var section in root.Classes)
        {
            if (section.IsExtern) continue;
            if (!sections.TryGetValue(section.Name, out var map)) sections[section.Name] = map = new(StringComparer.OrdinalIgnoreCase);
            foreach (var c in section.Classes)
                if (!c.IsExtern && !map.ContainsKey(c.Name)) map[c.Name] = new ClassRef(file, section.Name, c);
        }
    }

    public ClassRef? Find(string section, string name) =>
        sections.TryGetValue(section, out var map) && map.TryGetValue(name, out var r) ? r : null;

    public IEnumerable<ClassRef> All(string section) => sections.TryGetValue(section, out var map) ? map.Values : [];

    /// <summary>The class and its bases, nearest first.</summary>
    public List<ClassRef> Chain(string section, string name)
    {
        var list = new List<ClassRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var c = Find(section, name); c is not null && seen.Add(c.Name); c = c.Class.Base is { } b ? Find(section, b) : null) list.Add(c);
        return list;
    }

    /// <summary>A value with inheritance; <paramref name="from"/> = the class that sets it.</summary>
    public ConfigValue? Get(string section, string name, string key, out ClassRef? from)
    {
        foreach (var c in Chain(section, name))
            if (c.Class.Get(key) is { } v)
            {
                from = c;
                return v;
            }
        from = null;
        return null;
    }

    /// <summary>Classes whose own model= names <paramref name="p3dGamePath"/> (or, failing that, the same file name).</summary>
    public List<(ClassRef Class, string Model, bool ExactPath)> ByModel(string section, string p3dGamePath)
    {
        string want = NormalizeModel(p3dGamePath);
        string file = Path.GetFileName(want);
        var exact = new List<(ClassRef, string, bool)>();
        var byName = new List<(ClassRef, string, bool)>();
        foreach (var c in All(section))
        {
            if (c.Class.GetString("model") is not { Length: > 0 } m) continue;
            var n = NormalizeModel(m);
            if (n == want) exact.Add((c, m, true));
            else if (Path.GetFileName(n) == file) byName.Add((c, m, false));
        }
        return exact.Count > 0 ? exact : byName;
    }

    public static string NormalizeModel(string path)
    {
        var s = path.Trim().Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        return s.EndsWith(".p3d") ? s : s + ".p3d";
    }
}

/// <summary>A call pType.AddItemInHandsProfileIK("Class", "x.asi", behavior, "ik.anm", "states.anm") in a script.</summary>
public sealed record HandsLink(string File, string ClassName, string Asi, string Behavior, string Ik, string States, int Start, int End);

/// <summary>One bone of a weapon's state animation and where it lands on the model.</summary>
/// <param name="AnmBone">Bone in the .anm ("Weapon_Bolt").</param>
/// <param name="ModelBone">Model bone it drives (boneRemap, else the same name), or "" when nothing is mapped.</param>
/// <param name="InSkeleton">The model bone is in the model.cfg skeleton.</param>
/// <param name="InModel">The model has a named selection with that name (in the checked LOD).</param>
/// <param name="Note">What is wrong or worth knowing; "" when fine.</param>
public sealed record BoneLink(string AnmBone, string ModelBone, bool InSkeleton, bool InModel, string Note)
{
    public bool Ok => Note.Length == 0;
}

/// <summary>
/// DayZ weapon animation wiring: the .asi / IK .anm / states .anm a weapon class is registered with
/// (ModItemRegisterCallbacks ▸ RegisterFireArms ▸ AddItemInHandsProfileIK in a script), weaponStateAnim and boneRemap
/// in config.cpp, and the check that the states animation's bones reach the model's bones and named selections.
/// Text edits change only the call / property they are about.
/// </summary>
public static partial class WeaponLinks
{
    [GeneratedRegex("""AddItemInHandsProfileIK\s*\(\s*"(?<cls>[^"]*)"\s*,\s*"(?<asi>[^"]*)"\s*,\s*(?<beh>[^,()]+?)\s*,\s*"(?<ik>[^"]*)"\s*(?:,\s*"(?<st>[^"]*)"\s*)?\)\s*;?""")]
    private static partial Regex CallRx();

    public static List<HandsLink> FindLinks(string file, string text)
    {
        var list = new List<HandsLink>();
        foreach (Match m in CallRx().Matches(text))
        {
            int start = m.Index;
            // the call usually starts with "pType." on its own line
            int dot = text.LastIndexOf("pType", start, Math.Min(start, 16), StringComparison.Ordinal);
            if (dot >= 0 && text[dot..start].TrimEnd().EndsWith('.')) start = dot;
            list.Add(new HandsLink(file, m.Groups["cls"].Value, m.Groups["asi"].Value, m.Groups["beh"].Value.Trim(), m.Groups["ik"].Value,
                m.Groups["st"].Success ? m.Groups["st"].Value : "", start, m.Index + m.Length));
        }
        return list;
    }

    public static string Call(string className, string asi, string behavior, string ik, string states) =>
        $"pType.AddItemInHandsProfileIK(\"{className}\", \"{asi}\", {behavior}, \"{ik}\"{(states.Length > 0 ? $", \"{states}\"" : "")});";

    /// <summary>
    /// The script with the class's call replaced (when <paramref name="existing"/> is given) or added to the
    /// RegisterFireArms override (after its last registration, else after the super call). Null when the text has no
    /// RegisterFireArms override to add to.
    /// </summary>
    public static string? SetLink(string text, HandsLink? existing, string className, string asi, string ik, string states)
    {
        if (existing is not null)
            return text[..existing.Start] + Call(existing.ClassName, asi, existing.Behavior, ik, states) + text[existing.End..];
        int name = text.IndexOf("RegisterFireArms", StringComparison.Ordinal);
        while (name >= 0)
        {
            // the method definition: "RegisterFireArms(" ... ")" then "{"
            int paren = text.IndexOf('(', name);
            int close = paren < 0 ? -1 : text.IndexOf(')', paren);
            int brace = close < 0 ? -1 : SkipWs(text, close + 1);
            if (brace >= 0 && brace < text.Length && text[brace] == '{')
            {
                string behavior = BehaviorParam(text[paren..close]) ?? "pBehavior";
                int end = MatchBrace(text, brace);
                if (end < 0) return null;
                var nl = text.Contains("\r\n") ? "\r\n" : "\n";
                int super = text.IndexOf("super.RegisterFireArms", brace, end - brace, StringComparison.Ordinal);
                var inside = FindLinks("", text).Where(l => l.Start > brace && l.End < end).ToList();
                int at;
                string indent;
                if (inside.Count > 0)
                {
                    // after the last registration, so the list stays in order
                    var last = inside[^1];
                    int lineEnd = text.IndexOf('\n', last.End);
                    at = lineEnd < 0 || lineEnd > end ? end : lineEnd + 1;
                    indent = IndentOf(text, last.Start);
                }
                else if (super >= 0)
                {
                    int lineEnd = text.IndexOf('\n', super);
                    at = lineEnd < 0 || lineEnd > end ? end : lineEnd + 1;
                    indent = IndentOf(text, super);
                }
                else
                {
                    at = LineStartOf(text, end);
                    indent = IndentOf(text, brace) + "\t";
                }
                var line = indent + Call(className, asi, behavior, ik, states) + nl;
                return text.Insert(at, line);
            }
            name = text.IndexOf("RegisterFireArms", name + 1, StringComparison.Ordinal);
        }
        return null;
    }

    /// <summary>A new script file registering one weapon.</summary>
    public static string NewScript(string className, string asi, string ik, string states, string nl = "\r\n") =>
        $"modded class ModItemRegisterCallbacks{nl}{{{nl}\toverride void RegisterFireArms(DayZPlayerType pType, DayzPlayerItemBehaviorCfg pBehavior){nl}\t{{{nl}" +
        $"\t\tsuper.RegisterFireArms(pType, pBehavior);{nl}\t\t{Call(className, asi, "pBehavior", ik, states)}{nl}\t}}{nl}}}{nl}";

    static string? BehaviorParam(string parameters)
    {
        var m = Regex.Match(parameters, @"DayzPlayerItemBehaviorCfg\s+(\w+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    static int SkipWs(string t, int i)
    {
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        return i;
    }

    static int LineStartOf(string t, int i)
    {
        int s = i;
        while (s > 0 && t[s - 1] != '\n') s--;
        return string.IsNullOrWhiteSpace(t[s..i]) ? s : i;
    }

    static string IndentOf(string t, int i)
    {
        int s = i;
        while (s > 0 && t[s - 1] != '\n') s--;
        int e = s;
        while (e < t.Length && t[e] is ' ' or '\t') e++;
        return t[s..e];
    }

    /// <summary>Matching "}" of the "{" at <paramref name="open"/> in script text (strings and comments skipped).</summary>
    static int MatchBrace(string t, int open)
    {
        int depth = 0;
        for (int i = open; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '"')
            {
                for (i++; i < t.Length && t[i] != '"'; i++)
                    if (t[i] == '\\') i++;
            }
            else if (c == '/' && i + 1 < t.Length && t[i + 1] == '/')
                while (i < t.Length && t[i] != '\n') i++;
            else if (c == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                int e = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? t.Length : e + 1;
            }
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    // ---- config.cpp -------------------------------------------------------------------------------

    /// <summary>
    /// config.cpp text with <paramref name="key"/> = "<paramref name="value"/>"; set in section ▸ class: an existing
    /// line is replaced, otherwise the line goes after the class's model= line (or at its end). Null when the class is
    /// not in the text.
    /// </summary>
    public static string? SetConfigString(string text, string section, string className, string key, string value)
    {
        if (ConfigSpans.Find(text, section, className) is not { } cls) return null;
        var nl = ConfigSpans.NewlineOf(text);
        string indent = ConfigSpans.IndentAt(text, cls.Start) + "\t";
        // keep the file's style: "key=value" or "key = value"
        bool spaced = Regex.IsMatch(text[cls.Open..cls.Close], @"\w\s+=\s");
        string line = $"{indent}{key}{(spaced ? " = " : "=")}\"{value.Replace("\"", "\"\"")}\";{nl}";
        if (ConfigSpans.Property(text, cls, key) is { } p) return text[..p.Start] + line + text[p.End..];
        int at = ConfigSpans.Property(text, cls, "model") is { } m ? m.End : ConfigSpans.InsertPoint(text, cls);
        return text.Insert(at, line);
    }

    /// <summary>boneRemap[] = {model bone, anm bone, …} as pairs.</summary>
    public static List<(string ModelBone, string AnmBone)> BoneRemap(ConfigValue? v)
    {
        var list = new List<(string, string)>();
        if (v is not { IsArray: true }) return list;
        for (int i = 0; i + 1 < v.Items.Count; i += 2) list.Add((v.Items[i].AsString(), v.Items[i + 1].AsString()));
        return list;
    }

    /// <summary>
    /// Follows every bone of the states animation through boneRemap to the model: is it a bone of the skeleton and a
    /// named selection of the model? Remapped model bones the animation does not have are listed too.
    /// </summary>
    public static List<BoneLink> Check(IReadOnlyList<string> anmBones, IReadOnlyList<(string ModelBone, string AnmBone)> remap,
        IReadOnlyCollection<string> skeleton, IReadOnlyCollection<string> selections)
    {
        var sk = new HashSet<string>(skeleton, StringComparer.OrdinalIgnoreCase);
        var sel = new HashSet<string>(selections, StringComparer.OrdinalIgnoreCase);
        var anm = new HashSet<string>(anmBones, StringComparer.OrdinalIgnoreCase);
        var result = new List<BoneLink>();
        foreach (var bone in anmBones)
        {
            var mapped = remap.Where(r => string.Equals(r.AnmBone, bone, StringComparison.OrdinalIgnoreCase)).Select(r => r.ModelBone).ToList();
            if (mapped.Count == 0 && sk.Contains(bone)) mapped.Add(bone); // same name, no remap needed
            if (mapped.Count == 0)
            {
                result.Add(new BoneLink(bone, "", false, false, "not used: no model bone is mapped to it (boneRemap)"));
                continue;
            }
            foreach (var m in mapped)
            {
                bool inSk = sk.Contains(m), inSel = sel.Contains(m);
                string note = !inSk && !inSel ? $"\"{m}\" is neither a skeleton bone (model.cfg) nor a named selection"
                    : !inSk ? $"add \"{m}\" to the skeleton in model.cfg (CfgSkeletons ▸ skeletonBones)"
                    : !inSel ? $"the model has no named selection \"{m}\" — nothing moves"
                    : "";
                result.Add(new BoneLink(bone, m, inSk, inSel, note));
            }
        }
        foreach (var (m, a) in remap)
            if (!anm.Contains(a) && (sk.Contains(m) || sel.Contains(m)))
                result.Add(new BoneLink(a, m, sk.Contains(m), sel.Contains(m), $"the animation has no bone \"{a}\" — \"{m}\" stays still"));
        return result;
    }

    /// <summary>A file on the work drive as a script path ("dz/anims/…") or a config path ("dz\anims\…").</summary>
    public static string GamePath(string relative, bool script)
    {
        var s = relative.Replace('/', '\\').TrimStart('\\');
        if (s.StartsWith("DZ\\", StringComparison.Ordinal)) s = "dz" + s[2..]; // the game's own files are written lower case
        return script ? s.Replace('\\', '/') : s;
    }

    /// <summary>Text of a value list for display: "a, b, c".</summary>
    public static string Join(IEnumerable<string> items) => new StringBuilder().AppendJoin(", ", items).ToString();
}
