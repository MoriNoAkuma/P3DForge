using System.Globalization;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>
/// Pure named-property operations (<c>#Property#</c> tags, Object Builder "Named Properties" window).
/// <para>
/// Each property is one tag holding a 64-byte key and a 64-byte value (NUL-terminated, so at most 63 latin1
/// characters each). Methods never modify the input Lod; they return a new Lod, or the same instance when nothing
/// changes. Editing a property drops its <see cref="PropertyTag.Raw"/> payload (junk bytes after the terminators are
/// only preserved for untouched tags).
/// </para>
/// A property is addressed by key (case-insensitive, first match) or by its index in <see cref="Lod.Tags"/> (to
/// address one of several tags with the same key).
/// </summary>
public static class NamedProperties
{
    /// <summary>Longest key/value that fits a 64-byte field together with its NUL terminator.</summary>
    public const int MaxLength = 63;

    static string? ValidateText(string s, string what)
    {
        if (s.Length > MaxLength) return $"The {what} is {s.Length} characters long (at most {MaxLength})";
        if (s.Contains('\0')) return $"The {what} must not contain NUL characters";
        if (!NamedSelections.IsLatin1(s)) return $"The {what} contains characters that cannot be stored in a P3D file (latin1 only)";
        return null;
    }

    /// <summary>Error message for an invalid property key, or null.</summary>
    public static string? ValidateKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "The name must not be empty" : ValidateText(key, "name");

    /// <summary>Error message for an invalid property value (empty values are allowed), or null.</summary>
    public static string? ValidateValue(string? value) => ValidateText(value ?? "", "value");

    static void Assert(string? err)
    {
        if (err is not null) throw new ArgumentException(err);
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Index in <see cref="Lod.Tags"/> of the first property with this key (case-insensitive), or -1.</summary>
    public static int Find(Lod lod, string key)
    {
        var tags = lod.Tags;
        for (int i = 0; i < tags.Length; i++)
            if (tags[i] is PropertyTag p && Same(p.Key, key)) return i;
        return -1;
    }

    static (int Index, PropertyTag Tag) Require(Lod lod, int tagIndex)
    {
        if (tagIndex < 0 || tagIndex >= lod.Tags.Length || lod.Tags[tagIndex] is not PropertyTag p)
            throw new ArgumentException($"Tag {tagIndex} is not a named property");
        return (tagIndex, p);
    }

    static int RequireKey(Lod lod, string key)
    {
        int i = Find(lod, key);
        if (i < 0) throw new ArgumentException($"Property \"{key}\" does not exist");
        return i;
    }

    /// <summary>
    /// Where a new property goes: after the last <c>#Property#</c>; without properties, before the first named
    /// selection (Object Builder writes <c>#UVSet#, #Property#…, &lt;named&gt;…</c>); otherwise at the end.
    /// </summary>
    public static int InsertionIndex(Lod lod)
    {
        for (int i = lod.Tags.Length - 1; i >= 0; i--)
            if (lod.Tags[i] is PropertyTag) return i + 1;
        for (int i = 0; i < lod.Tags.Length; i++)
            if (lod.Tags[i] is SelectionTag) return i;
        return lod.Tags.Length;
    }

    /// <summary>
    /// Sets a property: updates the first property with this key (case-insensitive; keeps its tag position and key
    /// spelling) or adds a new one after the last <c>#Property#</c>.
    /// </summary>
    public static Lod Set(Lod lod, string key, string value)
    {
        Assert(ValidateKey(key) ?? ValidateValue(value));
        int i = Find(lod, key);
        if (i >= 0) return SetValue(lod, i, value);
        var tag = new PropertyTag(1, key, value, null);
        return lod with { Tags = lod.Tags.Insert(InsertionIndex(lod), tag) };
    }

    /// <summary>Changes the value of an existing property. Same Lod if the value is unchanged.</summary>
    public static Lod SetValue(Lod lod, int tagIndex, string value)
    {
        Assert(ValidateValue(value));
        var (i, tag) = Require(lod, tagIndex);
        if (tag.Value == value) return lod;
        return lod with { Tags = lod.Tags.SetItem(i, tag with { Value = value, Raw = null }) };
    }

    /// <summary>
    /// Renames a property (keeps position and value). Throws if the key is invalid or used by ANOTHER property
    /// (case-insensitive); a case-only rename is allowed. Same Lod if the key is unchanged.
    /// </summary>
    public static Lod Rename(Lod lod, string key, string newKey) => Rename(lod, RequireKey(lod, key), newKey);

    /// <inheritdoc cref="Rename(Lod, string, string)"/>
    public static Lod Rename(Lod lod, int tagIndex, string newKey)
    {
        var (i, tag) = Require(lod, tagIndex);
        if (tag.Key == newKey) return lod;
        Assert(ValidateKey(newKey));
        for (int j = 0; j < lod.Tags.Length; j++)
            if (j != i && lod.Tags[j] is PropertyTag p && Same(p.Key, newKey))
                throw new ArgumentException($"Property \"{newKey}\" already exists");
        return lod with { Tags = lod.Tags.SetItem(i, tag with { Key = newKey, Raw = null }) };
    }

    /// <summary>Deletes EVERY property with this key (case-insensitive). Same Lod if nothing matched.</summary>
    public static Lod Delete(Lod lod, string key)
    {
        var tags = lod.Tags.RemoveAll(t => t is PropertyTag p && Same(p.Key, key));
        return tags.Length == lod.Tags.Length ? lod : lod with { Tags = tags };
    }

    /// <summary>Deletes the property tag at <paramref name="tagIndex"/>. Same Lod if it is not a property.</summary>
    public static Lod DeleteAt(Lod lod, int tagIndex)
    {
        if (tagIndex < 0 || tagIndex >= lod.Tags.Length || lod.Tags[tagIndex] is not PropertyTag) return lod;
        return lod with { Tags = lod.Tags.RemoveAt(tagIndex) };
    }
}

public enum PropertyValueKind
{
    Bool,
    Enum,
    Number,
    Int,
    Text,
}

/// <summary>Knowledge about one named property (suggestions, validation, tooltip).</summary>
/// <param name="Key">Canonical (lower-case) key.</param>
/// <param name="Kind">Value type (drives the editor and validation).</param>
/// <param name="Values">Allowed (enum) or typical values.</param>
/// <param name="Lods">LOD where the property is normally placed (informational).</param>
/// <param name="Description">Tooltip text.</param>
/// <param name="DayZ">Seen in DayZ models (sorted first in suggestions).</param>
public sealed record PropertyHint(string Key, PropertyValueKind Kind, string[] Values, string Lods, string Description, bool DayZ = false);

/// <summary>
/// Knowledge about LOD named properties for suggestions and warnings in the Named Properties panel. Sources: BI wiki
/// "Named Properties" / "Arma 3: Named Properties", DayZ samples and the user's corpus. Pure data + pure functions.
/// </summary>
public static class PropertyHints
{
    static readonly string[] B = ["0", "1"];

    /// <summary>Known named properties (OB names, lower-case like the engine).</summary>
    public static readonly IReadOnlyList<PropertyHint> Known =
    [
        new("autocenter", PropertyValueKind.Bool, B, "Geometry", "0 = use the model origin (0,0,0) instead of the automatic centre; required for proxies and most DayZ items.", true),
        new("lodnoshadow", PropertyValueKind.Bool, B, "Resolution LODs", "1 = this LOD never casts shadows (use in LODs with many faces).", true),
        new("canocclude", PropertyValueKind.Bool, B, "Geometry / View Geometry", "0 = the object does not hide objects behind it (foliage, fences).", true),
        new("canbeoccluded", PropertyValueKind.Bool, B, "Geometry / View Geometry", "0 = always drawn, even when hidden behind other objects.", true),
        new("prefershadowvolume", PropertyValueKind.Bool, B, "Geometry / View Geometry", "1 = shadow volume, 0 = shadow buffer.", true),
        new("sbsource", PropertyValueKind.Enum, ["explicit", "none", "shadow", "shadowvolume", "visual", "visualex"], "Geometry / View Geometry", "Source LOD for shadow-buffer shadows.", true),
        new("reversed", PropertyValueKind.Bool, B, "Memory", "Model is oriented backwards (used by some DayZ memory LODs).", true),
        new("damage", PropertyValueKind.Enum, ["building", "engine", "no", "tent", "tree", "wall", "wreck"], "Geometry", "What happens to the object when it is destroyed.", true),
        new("placement", PropertyValueKind.Enum, ["slope", "slopez", "slopex", "slopelandcontact", "vertical"], "Geometry", "How the object adapts to the terrain slope.", true),
        new("generateoffmeshconnections", PropertyValueKind.Bool, B, "Geometry", "DayZ navmesh: generate off-mesh connections (jumps/climbs) for this object.", true),
        new("canclimb", PropertyValueKind.Bool, B, "Geometry", "DayZ: whether players can climb onto the object.", true),
        new("ignorecomponents", PropertyValueKind.Bool, B, "Geometry", "DayZ navmesh: ignore the geometry components.", true),
        new("drawimportance", PropertyValueKind.Number, ["0.1", "0.2", "1"], "Geometry", "Multiplier of the on-screen size used for draw distance (smaller disappears sooner).", true),
        new("class", PropertyValueKind.Enum, ["breakablehouseanimated", "bridge", "building", "bushhard", "bushsoft", "church", "clutter", "forest", "house", "housesimulated", "land_decal", "man", "none", "pond", "road", "streetlamp", "thing", "thingx", "tower", "treehard", "treesoft", "vehicle", "wall"], "Geometry", "Simulation class of terrain objects (house = doors/ladders work in DayZ).", true),
        new("map", PropertyValueKind.Enum, ["main road", "road", "track", "trail", "building", "house", "fence", "wall", "bush", "small tree", "tree", "rock", "bunker", "fortress", "fuelstation", "hospital", "lighthouse", "quay", "view-tower", "ruin", "busstop", "church", "chapel", "cross", "fountain", "power lines", "powersolar", "powerwave", "powerwind", "railway", "shipwreck", "stack", "tourism", "transmitter", "watertower", "hide"], "Geometry", "Map icon of terrain objects."),
        new("mass", PropertyValueKind.Number, [], "Geometry", "Total mass (prefer per-point mass, Structure ▸ Mass)."),
        new("armor", PropertyValueKind.Number, ["200"], "Geometry", "Structural integrity coefficient."),
        new("buoyancy", PropertyValueKind.Bool, B, "Geometry", "1 = the object floats (uses the Buoyancy geometry)."),
        new("shadow", PropertyValueKind.Enum, ["hybrid"], "Geometry", "hybrid = vegetation shadow mode."),
        new("shadowoffset", PropertyValueKind.Number, ["0.001", "0.02"], "Geometry", "Shadow-buffer offset against rubber banding."),
        new("loddensitycoef", PropertyValueKind.Number, ["0.5", "1"], "Geometry", "How fast LODs are switched (0.1 fast … 100 barely)."),
        new("viewdensitycoef", PropertyValueKind.Number, ["1"], "Geometry", "AI view-density coefficient for semi-transparent objects."),
        new("explosionshielding", PropertyValueKind.Number, ["0.1", "1"], "Geometry", "Explosion shielding coefficient."),
        new("forcenotalpha", PropertyValueKind.Bool, B, "Geometry", "1 = never treat the model as alpha (receives shadows)."),
        new("frequent", PropertyValueKind.Bool, B, "Geometry", "1 = object placed in large numbers (instancing)."),
        new("keyframe", PropertyValueKind.Bool, B, "Resolution LODs", "1 = LOD has a keyframe animation stored in the p3d."),
        new("aicovers", PropertyValueKind.Bool, B, "Geometry", "0 = AI does not search cover at this object."),
        new("cratercolor", PropertyValueKind.Text, [], "Geometry", "Bullet crater colour, AARRGGBB hex."),
        new("destroysound", PropertyValueKind.Enum, ["treebroadleaf", "treepalm"], "Geometry", "Sound played when the object is destroyed."),
        new("shadowlod", PropertyValueKind.Int, ["-1"], "Resolution LODs", "Index of the shadow LOD used for this LOD (-1 = automatic)."),
        new("shadowvolumelod", PropertyValueKind.Int, ["-1"], "Resolution LODs", "Index of the shadow volume LOD (-1 = automatic)."),
        new("shadowbufferlod", PropertyValueKind.Int, ["-1"], "Resolution LODs", "Index of the shadow buffer LOD (-1 = automatic)."),
        new("shadowbufferlodvis", PropertyValueKind.Int, ["-1"], "Resolution LODs", "Visual shadow buffer LOD (-1 = automatic)."),
        new("animated", PropertyValueKind.Bool, B, "Geometry", "Animated object."),
        new("notl", PropertyValueKind.Text, [], "Geometry", "Legacy (no T&L)."),
        new("maxsegments", PropertyValueKind.Int, [], "Geometry", "Legacy segment limit."),
        new("minsegments", PropertyValueKind.Int, [], "Geometry", "Legacy segment limit."),
        new("viewclass", PropertyValueKind.Text, [], "Geometry", "Legacy view class."),
        new("xcount", PropertyValueKind.Int, [], "Geometry", "Animated flags (obsolete)."),
        new("ycount", PropertyValueKind.Int, [], "Geometry", "Animated flags (obsolete)."),
        new("xsize", PropertyValueKind.Number, [], "Geometry", "Animated flags (obsolete)."),
        new("ysize", PropertyValueKind.Number, [], "Geometry", "Animated flags (obsolete)."),
        new("xstep", PropertyValueKind.Number, [], "Geometry", "Generated by animation export."),
    ];

    static readonly Dictionary<string, PropertyHint> byKey = BuildIndex();

    static Dictionary<string, PropertyHint> BuildIndex()
    {
        var d = new Dictionary<string, PropertyHint>(StringComparer.Ordinal);
        foreach (var h in Known) d[h.Key] = h;
        d["dammage"] = d["damage"] with { Key = "dammage" }; // OFP spelling
        return d;
    }

    /// <summary>Hint for a key (trimmed, case-insensitive), or null for unknown keys.</summary>
    public static PropertyHint? Get(string key) => byKey.GetValueOrDefault(key.Trim().ToLowerInvariant());

    /// <summary>Keys to suggest for a LOD (DayZ-relevant first, then alphabetical), excluding keys already present.</summary>
    public static IReadOnlyList<string> SuggestedKeys(Lod lod)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in lod.Properties) present.Add(p.Key.Trim());
        return Known.Where(h => !present.Contains(h.Key))
            .OrderByDescending(h => h.DayZ)
            .ThenBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => h.Key)
            .ToList();
    }

    /// <summary>Typical properties (key, default value) worth offering for an empty LOD of this resolution.</summary>
    public static IReadOnlyList<(string Key, string Value)> Typical(float resolution) => LodNames.TypeOf(resolution).Type.Id switch
    {
        "geometry" => [("autocenter", "0"), ("canocclude", "0"), ("canbeoccluded", "1")],
        "visual" => [("lodnoshadow", "1"), ("autocenter", "0")],
        "viewGeometry" => [("prefershadowvolume", "0"), ("sbsource", "shadowvolume")],
        _ => [("autocenter", "0")],
    };

    /// <summary>Problem of a value for a known key (null when fine or the key is unknown).</summary>
    public static string? ValueProblem(string key, string value)
    {
        var h = Get(key);
        if (h is null) return null;
        var v = value.Trim();
        return h.Kind switch
        {
            PropertyValueKind.Bool => v is "0" or "1" ? null : "Expected 0 or 1",
            PropertyValueKind.Enum => h.Values.Contains(v.ToLowerInvariant()) ? null : $"Expected one of: {string.Join(", ", h.Values)}",
            PropertyValueKind.Number => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? null : "Expected a number",
            PropertyValueKind.Int => IsInt(v) ? null : "Expected an integer",
            _ => null,
        };
    }

    static bool IsInt(string v)
    {
        int start = v.StartsWith('-') ? 1 : 0;
        if (v.Length <= start) return false;
        for (int i = start; i < v.Length; i++)
            if (v[i] is < '0' or > '9') return false;
        return true;
    }

    /// <summary>Warnings for the property tag at <paramref name="tagIndex"/> (unknown key, duplicates, invalid value, spaces).</summary>
    public static IReadOnlyList<string> Warnings(Lod lod, int tagIndex)
    {
        var o = new List<string>();
        if (tagIndex < 0 || tagIndex >= lod.Tags.Length || lod.Tags[tagIndex] is not PropertyTag tag) return o;
        if (NamedProperties.ValidateKey(tag.Key) is { } ke) o.Add(ke);
        if (NamedProperties.ValidateValue(tag.Value) is { } ve) o.Add(ve);
        int first = -1, same = 0;
        for (int i = 0; i < lod.Tags.Length; i++)
            if (lod.Tags[i] is PropertyTag p && string.Equals(p.Key, tag.Key, StringComparison.OrdinalIgnoreCase))
            {
                if (first < 0) first = i;
                same++;
            }
        if (same > 1) o.Add(first == tagIndex ? $"Duplicate key ({same}×) — the engine uses only one" : "Duplicate key — this one is ignored by the engine");
        if (Get(tag.Key) is null) o.Add("Unknown property name (not used by DayZ/Arma)");
        else if (ValueProblem(tag.Key, tag.Value) is { } vp) o.Add(vp);
        if (tag.Key != tag.Key.Trim()) o.Add("The name has leading/trailing spaces");
        if (tag.Value != tag.Value.Trim()) o.Add("The value has leading/trailing spaces");
        return o;
    }

    /// <summary>One-line description for a tooltip.</summary>
    public static string Text(string key)
    {
        var h = Get(key);
        if (h is null) return "Unknown property";
        var vals = h.Kind switch
        {
            PropertyValueKind.Enum or PropertyValueKind.Bool => $" Values: {string.Join(", ", h.Values)}.",
            PropertyValueKind.Number => " Value: a number.",
            PropertyValueKind.Int => " Value: an integer.",
            _ => "",
        };
        return $"{h.Key} — {h.Description}{vals} Usually in: {h.Lods}.";
    }
}
