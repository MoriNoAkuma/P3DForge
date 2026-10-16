using System.Globalization;
using System.Text;
using P3DForge.Core.Materials;

namespace P3DForge.Core.Anim;

/// <summary>One class of CfgModels ▸ model ▸ Animations.</summary>
public sealed record AnimDef
{
    public string Name { get; init; } = "";
    /// <summary>rotation, rotationX/Y/Z, translation, translationX/Y/Z, hide, direct (case as written).</summary>
    public string Type { get; init; } = "rotation";
    public string Source { get; init; } = "";
    /// <summary>The bone (named selection) it moves.</summary>
    public string Selection { get; init; } = "";
    /// <summary>Two-point memory selection; or <see cref="Begin"/> / <see cref="End"/> single points.</summary>
    public string Axis { get; init; } = "";
    public string Begin { get; init; } = "";
    public string End { get; init; } = "";
    /// <summary>Axis points are in the Memory LOD (default) or in the animated LOD itself.</summary>
    public bool Memory { get; init; } = true;
    public double MinValue { get; init; }
    public double MaxValue { get; init; } = 1;
    /// <summary>Radians.</summary>
    public double Angle0 { get; init; }
    public double Angle1 { get; init; }
    /// <summary>translation: multiples of the axis length; translationX/Y/Z: metres.</summary>
    public double Offset0 { get; init; }
    public double Offset1 { get; init; }
    /// <summary>hide: hidden while the phase is in [HideValue, UnHideValue).</summary>
    public double HideValue { get; init; } = 1;
    public double UnHideValue { get; init; } = double.PositiveInfinity;
    /// <summary>clamp (default), loop, mirror.</summary>
    public string SourceAddress { get; init; } = "clamp";
    // direct
    public double[] AxisPos { get; init; } = [0, 0, 0];
    public double[] AxisDir { get; init; } = [0, 0, 1];
    public double Angle { get; init; }
    public double AxisOffset { get; init; }
    /// <summary>Other entries (minPhase, initPhase, animPeriod…) as written, kept on save.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Other { get; init; } = [];
    /// <summary>The CfgModels class it comes from (the model itself or a base class).</summary>
    public string Owner { get; init; } = "";

    public string Kind => Type.ToLowerInvariant() switch
    {
        "rotation" or "rotationx" or "rotationy" or "rotationz" => "rotation",
        "translation" or "translationx" or "translationy" or "translationz" => "translation",
        "hide" => "hide",
        "direct" => "direct",
        _ => "unknown",
    };

    public static readonly string[] Types = ["rotation", "rotationX", "rotationY", "rotationZ", "translation", "translationX", "translationY", "translationZ", "hide", "direct"];

    /// <summary>The class as model.cfg text (canonical layout, tabs), starting at <paramref name="indent"/>.</summary>
    public string ToText(string indent, string nl)
    {
        var sb = new StringBuilder();
        string i1 = indent + "\t";
        sb.Append(indent).Append("class ").Append(Name).Append(nl).Append(indent).Append('{').Append(nl);
        void S(string k, string v) => sb.Append(i1).Append(k).Append(" = \"").Append(v.Replace("\"", "\"\"")).Append("\";").Append(nl);
        void N(string k, double v, string? comment = null)
        {
            sb.Append(i1).Append(k).Append(" = ").Append(ModelCfg.Num(v)).Append(';');
            if (comment is not null) sb.Append(" // ").Append(comment);
            sb.Append(nl);
        }
        static string Deg(double rad) => (Math.Round(rad * 180 / Math.PI, 3) + 0.0).ToString("0.###", CultureInfo.InvariantCulture) + "°"; // no "-0°"
        S("type", Type);
        S("source", Source);
        S("selection", Selection);
        if (Axis.Length > 0) S("axis", Axis);
        if (Begin.Length > 0) S("begin", Begin);
        if (End.Length > 0) S("end", End);
        if (!Memory) N("memory", 0);
        if (!SourceAddress.Equals("clamp", StringComparison.OrdinalIgnoreCase)) S("sourceAddress", SourceAddress);
        N("minValue", MinValue);
        N("maxValue", MaxValue);
        switch (Kind)
        {
            case "rotation":
                N("angle0", Angle0, Deg(Angle0));
                N("angle1", Angle1, Deg(Angle1));
                break;
            case "translation":
                N("offset0", Offset0);
                N("offset1", Offset1);
                break;
            case "hide":
                N("hideValue", HideValue);
                if (!double.IsPositiveInfinity(UnHideValue)) N("unHideValue", UnHideValue);
                break;
            case "direct":
                sb.Append(i1).Append("axisPos[] = {").Append(string.Join(", ", AxisPos.Select(ModelCfg.Num))).Append("};").Append(nl);
                sb.Append(i1).Append("axisDir[] = {").Append(string.Join(", ", AxisDir.Select(ModelCfg.Num))).Append("};").Append(nl);
                N("angle", Angle, Deg(Angle));
                N("axisOffset", AxisOffset);
                break;
        }
        foreach (var (k, v) in Other) sb.Append(i1).Append(k).Append(" = ").Append(v).Append(';').Append(nl);
        sb.Append(indent).Append("};").Append(nl);
        return sb.ToString();
    }

    /// <summary>Same content (the owner does not count).</summary>
    public bool SameAs(AnimDef o) =>
        Name == o.Name && Type == o.Type && Source == o.Source && Selection == o.Selection && Axis == o.Axis && Begin == o.Begin
        && End == o.End && Memory == o.Memory && MinValue == o.MinValue && MaxValue == o.MaxValue && Angle0 == o.Angle0
        && Angle1 == o.Angle1 && Offset0 == o.Offset0 && Offset1 == o.Offset1 && HideValue == o.HideValue && UnHideValue == o.UnHideValue
        && SourceAddress == o.SourceAddress && AxisPos.SequenceEqual(o.AxisPos) && AxisDir.SequenceEqual(o.AxisDir) && Angle == o.Angle
        && AxisOffset == o.AxisOffset && Other.SequenceEqual(o.Other);
}

/// <summary>A bone of CfgSkeletons: its name and parent bone ("" = none).</summary>
public readonly record struct Bone(string Name, string Parent);

/// <summary>
/// A model.cfg: CfgSkeletons (bones with parents, skeletonInherit) and CfgModels (skeletonName, sections, Animations
/// with config inheritance — "class gun: Default", "class Animations: Animations"). Values are read the way binarize
/// reads them: numbers, quoted numbers (also with a decimal comma: "1,7453293"), "rad 90" and simple expressions.
/// Edits return new text that changes only the touched classes; everything else stays as written.
/// </summary>
public sealed class ModelCfg
{
    public string Text { get; }
    public string? Path { get; }
    public ConfigClass Root { get; }
    public IReadOnlyList<string> Warnings { get; }

    ModelCfg(string text, string? path)
    {
        Text = text;
        Path = path;
        var warnings = new List<string>();
        Root = ConfigText.Parse(text, warnings);
        Warnings = warnings;
    }

    public static ModelCfg Parse(string text, string? path = null) => new(text, path);

    public static ModelCfg Load(string path) => new(File.ReadAllText(path), path);

    /// <summary>The model.cfg binarize uses for a .p3d: in its folder, else the nearest parent folder that has one.</summary>
    public static string? FindFor(string p3dPath)
    {
        for (var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(p3dPath)); dir is not null; dir = System.IO.Path.GetDirectoryName(dir))
        {
            var f = System.IO.Path.Combine(dir, "model.cfg");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>The CfgModels class of a .p3d: its file name without extension.</summary>
    public static string ModelName(string p3dPath) => System.IO.Path.GetFileNameWithoutExtension(p3dPath);

    ConfigClass? Section(string name) => Root.Class(name);

    public IEnumerable<string> ModelNames => Section("CfgModels")?.Classes.Where(c => !c.IsExtern).Select(c => c.Name) ?? [];
    public IEnumerable<string> SkeletonNames => Section("CfgSkeletons")?.Classes.Where(c => !c.IsExtern).Select(c => c.Name) ?? [];

    public bool HasModel(string model) => Section("CfgModels")?.Class(model) is { IsExtern: false };

    /// <summary>The class and its bases (nearest first) inside one section.</summary>
    static List<ConfigClass> Chain(ConfigClass? section, string name)
    {
        var list = new List<ConfigClass>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var c = section?.Class(name); c is not null && seen.Add(c.Name); c = c.Base is { } b ? section!.Class(b) : null)
            list.Add(c);
        return list;
    }

    static ConfigValue? Inherited(List<ConfigClass> chain, string key)
    {
        foreach (var c in chain)
            if (c.Get(key) is { } v) return v;
        return null;
    }

    public string SkeletonName(string model) => Inherited(Chain(Section("CfgModels"), model), "skeletonName")?.AsString() ?? "";

    public IReadOnlyList<string> Sections(string model) =>
        Inherited(Chain(Section("CfgModels"), model), "sections") is { IsArray: true } a ? a.Items.Select(i => i.AsString()).ToList() : [];

    public bool IsDiscrete(string skeleton) => (Inherited(Chain(Section("CfgSkeletons"), skeleton), "isDiscrete")?.AsDouble(1) ?? 1) != 0;

    /// <summary>Bones of a skeleton, inherited ones (skeletonInherit) first.</summary>
    public IReadOnlyList<Bone> Bones(string skeleton)
    {
        var result = new List<Bone>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, int depth)
        {
            if (depth > 16 || !seen.Add(name)) return;
            var chain = Chain(Section("CfgSkeletons"), name);
            if (chain.Count == 0) return;
            if (Inherited(chain, "skeletonInherit")?.AsString() is { Length: > 0 } inherit) Add(inherit, depth + 1);
            if (Inherited(chain, "skeletonBones") is { IsArray: true } bones)
                for (int i = 0; i + 1 < bones.Items.Count; i += 2)
                    result.Add(new Bone(bones.Items[i].AsString(), bones.Items[i + 1].AsString()));
        }
        Add(skeleton, 0);
        return result;
    }

    /// <summary>The model's own bones (skeletonBones of its skeleton class, without inherited ones).</summary>
    public IReadOnlyList<Bone> OwnBones(string skeleton)
    {
        var cls = Section("CfgSkeletons")?.Class(skeleton);
        if (cls?.Get("skeletonBones") is not { IsArray: true } bones) return [];
        var list = new List<Bone>();
        for (int i = 0; i + 1 < bones.Items.Count; i += 2) list.Add(new Bone(bones.Items[i].AsString(), bones.Items[i + 1].AsString()));
        return list;
    }

    /// <summary>
    /// The model's animations: its own Animations class plus those inherited from base models or through
    /// "class Animations: Animations"; an own animation overrides an inherited one with the same name.
    /// </summary>
    public IReadOnlyList<AnimDef> Animations(string model)
    {
        var models = Section("CfgModels");
        var chain = Chain(models, model);
        var result = new List<AnimDef>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // nearest model first; within a model, its Animations class and that class's bases
        foreach (var m in chain)
        {
            var anims = m.Class("Animations");
            if (anims is null) continue;
            var animChain = new List<(ConfigClass Cls, string Owner)> { (anims, m.Name) };
            var seen = new HashSet<ConfigClass> { anims };
            // "class Animations: Animations" refers to the base model's Animations
            for (var (a, owner) = (anims, m); a.Base is { } b;)
            {
                var ownerIndex = chain.IndexOf(owner);
                ConfigClass? next = null;
                ConfigClass? nextOwner = null;
                for (int k = ownerIndex + 1; k < chain.Count && next is null; k++)
                    if (chain[k].Class(b) is { } c && !seen.Contains(c))
                    {
                        next = c;
                        nextOwner = chain[k];
                    }
                if (next is null) break;
                seen.Add(next);
                animChain.Add((next, nextOwner!.Name));
                (a, owner) = (next, nextOwner);
            }
            foreach (var (ac, ownerName) in animChain)
                foreach (var c in ac.Classes)
                    if (!c.IsExtern && names.Add(c.Name)) result.Add(Read(c, ownerName, ac));
            break; // a model's Animations class (with its bases) replaces those of the models it inherits from
        }
        return result;
    }

    /// <summary>The model's own Animations class names (those an edit can change).</summary>
    public IReadOnlyList<string> OwnAnimationNames(string model) =>
        Section("CfgModels")?.Class(model)?.Class("Animations")?.Classes.Where(c => !c.IsExtern).Select(c => c.Name).ToList() ?? [];

    static AnimDef Read(ConfigClass c, string owner, ConfigClass animations)
    {
        // an animation class can inherit from a sibling animation ("class door2: door1 {…}")
        var chain = new List<ConfigClass>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var k = c; k is not null && seen.Add(k.Name); k = k.Base is { } b ? animations.Class(b) : null) chain.Add(k);
        string S(string key, string fallback = "") => Inherited(chain, key)?.AsString() ?? fallback;
        double D(string key, double fallback) => Inherited(chain, key) is { } v ? Value(v, fallback) : fallback;
        double[] V(string key, double[] fallback) =>
            Inherited(chain, key) is { IsArray: true } a && a.Items.Count >= 3 ? a.Items.Take(3).Select(i => Value(i, 0)).ToArray() : fallback;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "type", "source", "selection", "axis", "begin", "end", "memory", "minValue", "maxValue", "angle0", "angle1", "offset0",
            "offset1", "hideValue", "unHideValue", "sourceAddress", "axisPos", "axisDir", "angle", "axisOffset",
        };
        var other = c.Properties.Where(p => !known.Contains(p.Name))
            .Select(p => new KeyValuePair<string, string>(p.Value.IsArray ? p.Name + "[]" : p.Name, ConfigText.FormatValue(p.Value))).ToList();
        return new AnimDef
        {
            Name = c.Name,
            Type = S("type", "rotation"),
            Source = S("source"),
            Selection = S("selection"),
            Axis = S("axis"),
            Begin = S("begin"),
            End = S("end"),
            Memory = D("memory", 1) != 0,
            MinValue = D("minValue", 0),
            MaxValue = D("maxValue", 1),
            Angle0 = D("angle0", 0),
            Angle1 = D("angle1", 0),
            Offset0 = D("offset0", 0),
            Offset1 = D("offset1", 0),
            HideValue = D("hideValue", 1),
            UnHideValue = D("unHideValue", double.PositiveInfinity),
            SourceAddress = S("sourceAddress", "clamp"),
            AxisPos = V("axisPos", [0, 0, 0]),
            AxisDir = V("axisDir", [0, 0, 1]),
            Angle = D("angle", 0),
            AxisOffset = D("axisOffset", 0),
            Other = other,
            Owner = owner,
        };
    }

    // ---- values -----------------------------------------------------------------------------------

    /// <summary>A number as binarize reads it: 1.5, "1.5", "1,5", "rad 90", "-rad 45", "3.14159/2", "(rad 30)*2".</summary>
    public static double Value(ConfigValue v, double fallback = 0)
    {
        if (v.IsNumber) return v.Number;
        return v.IsString && TryEval(v.Text, out var d) ? d : fallback;
    }

    public static bool TryEval(string text, out double value)
    {
        var s = text.Trim().Trim('"').Trim();
        // a lone decimal comma ("1,7453293") — not a list
        if (s.Count(ch => ch == ',') == 1 && !s.Contains('.') && !s.Contains(' ')) s = s.Replace(',', '.');
        try
        {
            int pos = 0;
            value = Expr(s, ref pos);
            Skip(s, ref pos);
            return pos == s.Length && double.IsFinite(value);
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
    }

    static void Skip(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    static double Expr(string s, ref int i)
    {
        double v = Term(s, ref i);
        while (true)
        {
            Skip(s, ref i);
            if (i < s.Length && s[i] is '+' or '-')
            {
                char op = s[i++];
                double r = Term(s, ref i);
                v = op == '+' ? v + r : v - r;
            }
            else return v;
        }
    }

    static double Term(string s, ref int i)
    {
        double v = Unary(s, ref i);
        while (true)
        {
            Skip(s, ref i);
            if (i < s.Length && s[i] is '*' or '/')
            {
                char op = s[i++];
                double r = Unary(s, ref i);
                v = op == '*' ? v * r : v / r;
            }
            else return v;
        }
    }

    static double Unary(string s, ref int i)
    {
        Skip(s, ref i);
        if (i < s.Length && s[i] == '-')
        {
            i++;
            return -Unary(s, ref i);
        }
        if (i < s.Length && s[i] == '+')
        {
            i++;
            return Unary(s, ref i);
        }
        if (i + 3 <= s.Length && s.AsSpan(i, 3).Equals("rad", StringComparison.OrdinalIgnoreCase) && (i + 3 == s.Length || !char.IsLetterOrDigit(s[i + 3])))
        {
            i += 3;
            return Unary(s, ref i) * Math.PI / 180;
        }
        if (i < s.Length && s[i] == '(')
        {
            i++;
            double v = Expr(s, ref i);
            Skip(s, ref i);
            if (i >= s.Length || s[i] != ')') throw new FormatException();
            i++;
            return v;
        }
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || ((s[i] == 'e' || s[i] == 'E') && i > start)
                                || ((s[i] == '-' || s[i] == '+') && i > start && s[i - 1] is 'e' or 'E'))) i++;
        if (i == start || !double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            throw new FormatException();
        return d;
    }

    /// <summary>Number as written to model.cfg (invariant, up to 7 significant digits).</summary>
    public static string Num(double v)
    {
        if (Math.Abs(v) < 1e-12) return "0";
        return v.ToString("0.#######", CultureInfo.InvariantCulture);
    }

    // ---- edits ------------------------------------------------------------------------------------

    /// <summary>
    /// New text with the model's own animations replaced by <paramref name="own"/>: changed classes are rewritten in
    /// place, new ones appended, removed ones deleted; untouched classes keep their exact text. Missing CfgModels /
    /// model / Animations classes are created ("class Animations: Animations" when the base model has animations).
    /// </summary>
    public string WithOwnAnimations(string model, IReadOnlyList<AnimDef> own)
    {
        var text = EnsureModel(Text, model, out _);
        var nl = ConfigSpans.NewlineOf(text);
        var modelSpan = ConfigSpans.Find(text, "CfgModels", model)!.Value;
        if (ConfigSpans.Find(text, "CfgModels", model, "Animations") is null)
        {
            var parsed = Parse(text);
            bool inherit = parsed.Section("CfgModels")?.Class(model)?.Base is { } b && parsed.Animations(b).Count > 0;
            string ind = ConfigSpans.IndentAt(text, modelSpan.Start) + "\t";
            var block = $"{ind}class Animations{(inherit ? ": Animations" : "")}{nl}{ind}{{{nl}{ind}}};{nl}";
            int at = ConfigSpans.InsertPoint(text, modelSpan);
            text = text.Insert(at, block);
        }
        var animSpan = ConfigSpans.Find(text, "CfgModels", model, "Animations")!.Value;
        var existing = ConfigSpans.Classes(text, animSpan).Where(c => c.HasBody).ToList();
        var current = Parse(text).OwnAnimations(model).ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        string indent = ConfigSpans.IndentAt(text, animSpan.Start) + "\t";
        var edits = new List<(int Start, int End, string Text)>();
        var wanted = new HashSet<string>(own.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var span in existing)
        {
            if (!wanted.Contains(span.Name)) edits.Add((span.Start, span.End, ""));
            else
            {
                var a = own.First(x => string.Equals(x.Name, span.Name, StringComparison.OrdinalIgnoreCase));
                if (!current.TryGetValue(span.Name, out var old) || !old.SameAs(a with { Owner = old.Owner }))
                    edits.Add((span.Start, span.End, a.ToText(ConfigSpans.IndentAt(text, span.Start), nl)));
            }
        }
        var appended = new StringBuilder();
        foreach (var a in own)
            if (!existing.Any(s => string.Equals(s.Name, a.Name, StringComparison.OrdinalIgnoreCase)))
                appended.Append(a.ToText(indent, nl));
        if (appended.Length > 0)
        {
            int at = ConfigSpans.InsertPoint(text, animSpan);
            // an empty "{}" on one line: open it up first
            if (at == animSpan.Close) appended.Insert(0, nl).Append(ConfigSpans.IndentAt(text, animSpan.Start));
            edits.Add((at, at, appended.ToString()));
        }
        foreach (var (s, e, t) in edits.OrderByDescending(x => x.Start)) text = text[..s] + t + text[e..];
        return text;
    }

    /// <summary>The model's own animations as read (owner = model).</summary>
    public IReadOnlyList<AnimDef> OwnAnimations(string model)
    {
        var anims = Section("CfgModels")?.Class(model)?.Class("Animations");
        return anims is null ? [] : anims.Classes.Where(c => !c.IsExtern).Select(c => Read(c, model, anims)).ToList();
    }

    /// <summary>New text with the skeleton's own bones replaced (the skeleton class and CfgSkeletons are created if missing).</summary>
    public string WithBones(string model, IReadOnlyList<Bone> bones)
    {
        var text = EnsureModel(Text, model, out string skeleton);
        var nl = ConfigSpans.NewlineOf(text);
        var skel = ConfigSpans.Find(text, "CfgSkeletons", skeleton)!.Value;
        string ind = ConfigSpans.IndentAt(text, skel.Start) + "\t";
        var sb = new StringBuilder();
        sb.Append(ind).Append("skeletonBones[] =").Append(nl).Append(ind).Append('{').Append(nl);
        for (int i = 0; i < bones.Count; i++)
            sb.Append(ind).Append('\t').Append('"').Append(bones[i].Name).Append("\", \"").Append(bones[i].Parent).Append('"')
                .Append(i + 1 < bones.Count ? "," : "").Append(nl);
        sb.Append(ind).Append("};").Append(nl);
        if (ConfigSpans.Property(text, skel, "skeletonBones") is { } p) return text[..p.Start] + sb + text[p.End..];
        int at = ConfigSpans.InsertPoint(text, skel);
        return text.Insert(at, sb.ToString());
    }

    /// <summary>Makes sure CfgSkeletons ▸ skeleton and CfgModels ▸ model exist (the model gets skeletonName); returns the text.</summary>
    static string EnsureModel(string text, string model, out string skeleton)
    {
        var nl = ConfigSpans.NewlineOf(text.Length == 0 ? "\r\n" : text);
        if (text.Length == 0) nl = "\r\n";
        var cfg = Parse(text);
        skeleton = cfg.SkeletonName(model);
        if (skeleton.Length == 0) skeleton = model + "_skeleton";
        if (ConfigSpans.Find(text, "CfgSkeletons") is null)
        {
            var block = $"class CfgSkeletons{nl}{{{nl}\tclass Default{nl}\t{{{nl}\t\tisDiscrete = 1;{nl}\t\tskeletonInherit = \"\";{nl}\t\tskeletonBones[] = {{}};{nl}\t}};{nl}}};{nl}";
            int at = ConfigSpans.Find(text, "CfgModels") is { } models ? models.Start : text.Length;
            if (at == text.Length && text.Length > 0 && !text.EndsWith('\n')) block = nl + block;
            text = text.Insert(at, block + (at < text.Length ? nl : ""));
        }
        if (ConfigSpans.Find(text, "CfgSkeletons", skeleton) is null)
        {
            var s = ConfigSpans.Find(text, "CfgSkeletons")!.Value;
            bool hasDefault = Parse(text).Section("CfgSkeletons")?.Class("Default") is { IsExtern: false };
            var block = $"\tclass {skeleton}{(hasDefault ? ": Default" : "")}{nl}\t{{{nl}\t\tisDiscrete = 1;{nl}\t\tskeletonInherit = \"\";{nl}\t\tskeletonBones[] = {{}};{nl}\t}};{nl}";
            text = text.Insert(ConfigSpans.InsertPoint(text, s), block);
        }
        if (ConfigSpans.Find(text, "CfgModels") is null)
        {
            var block = $"{nl}class CfgModels{nl}{{{nl}\tclass Default{nl}\t{{{nl}\t\tsectionsInherit = \"\";{nl}\t\tsections[] = {{}};{nl}\t\tskeletonName = \"\";{nl}\t}};{nl}}};{nl}";
            if (!text.EndsWith('\n')) block = nl + block;
            text += block;
        }
        if (ConfigSpans.Find(text, "CfgModels", model) is null)
        {
            var m = ConfigSpans.Find(text, "CfgModels")!.Value;
            bool hasDefault = Parse(text).Section("CfgModels")?.Class("Default") is { IsExtern: false };
            var block = $"\tclass {model}{(hasDefault ? ": Default" : "")}{nl}\t{{{nl}\t\tskeletonName = \"{skeleton}\";{nl}\t\tsections[] = {{}};{nl}\t}};{nl}";
            text = text.Insert(ConfigSpans.InsertPoint(text, m), block);
        }
        else if (Parse(text).SkeletonName(model).Length == 0)
        {
            var m = ConfigSpans.Find(text, "CfgModels", model)!.Value;
            var ind = ConfigSpans.IndentAt(text, m.Start) + "\t";
            var line = $"{ind}skeletonName = \"{skeleton}\";{nl}";
            if (ConfigSpans.Property(text, m, "skeletonName") is { } p) text = text[..p.Start] + line + text[p.End..];
            else text = text.Insert(m.Open + 1 < text.Length && text[m.Open + 1] is '\r' or '\n' ? text.IndexOf('\n', m.Open) + 1 : m.Open + 1, line);
        }
        return text;
    }
}
