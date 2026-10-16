using System.Globalization;

namespace P3DForge.Core.Materials;

/*
 * Generic Bohemia config tree (config.cpp / model.cfg / *.rvmat syntax and its binarized raP form).
 * Entries keep their document order; names compare case-insensitively (like the engine).
 * The tree is mutable: objects handed out by MaterialStore are shared — Clone() before editing.
 */

public enum ConfigValueKind
{
    /// <summary>Quoted string ("text").</summary>
    String,
    /// <summary>Floating point number.</summary>
    Float,
    /// <summary>Integer number.</summary>
    Int,
    /// <summary>Unquoted word (tex, true, an expression) — the engine treats it as a string.</summary>
    Word,
    /// <summary>Array ({a, b, {c}}).</summary>
    Array,
}

/// <summary>An immutable config value: scalar or array.</summary>
public sealed class ConfigValue : IEquatable<ConfigValue>
{
    static readonly ConfigValue[] NoItems = [];

    public ConfigValueKind Kind { get; }

    /// <summary>String/Word: the content. Float/Int: the number as written ("0.200", "50", "1e-3"). Array: "".</summary>
    public string Text { get; }

    /// <summary>Float/Int: the value; NaN for strings and arrays.</summary>
    public double Number { get; }

    /// <summary>Array elements (empty for scalars).</summary>
    public IReadOnlyList<ConfigValue> Items { get; }

    ConfigValue(ConfigValueKind kind, string text, double number, IReadOnlyList<ConfigValue> items)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Items = items;
    }

    public static ConfigValue Str(string s) => new(ConfigValueKind.String, s ?? "", double.NaN, NoItems);
    public static ConfigValue Word(string s) => new(ConfigValueKind.Word, s ?? "", double.NaN, NoItems);
    public static ConfigValue Float(double d, string? text = null) => new(ConfigValueKind.Float, text ?? FormatFloat(d), d, NoItems);
    public static ConfigValue Int(long v, string? text = null) => new(ConfigValueKind.Int, text ?? v.ToString(CultureInfo.InvariantCulture), v, NoItems);
    public static ConfigValue Array(IEnumerable<ConfigValue> items) => new(ConfigValueKind.Array, "", double.NaN, items.ToArray());

    /// <summary>A number: Int when integral (written "50"), Float otherwise (written in float32 precision, "0.2").</summary>
    public static ConfigValue Num(double d) =>
        Math.Abs(d) < 1e15 && d == Math.Floor(d) && !(d == 0 && double.IsNegative(d)) ? Int((long)d) : Float(d);

    /// <summary>An array of numbers (each Int when integral, Float otherwise).</summary>
    public static ConfigValue Numbers(params ReadOnlySpan<float> values)
    {
        var items = new ConfigValue[values.Length];
        for (int i = 0; i < values.Length; i++) items[i] = Num(values[i]);
        return new ConfigValue(ConfigValueKind.Array, "", double.NaN, items);
    }

    /// <summary>An array of strings.</summary>
    public static ConfigValue Strings(IEnumerable<string> values) => Array(values.Select(Str));

    /// <summary>Shortest text that round-trips the value as float32 ("0.2", "1E-05").</summary>
    public static string FormatFloat(double d)
    {
        var f = (float)d;
        if (float.IsFinite(f)) return f.ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Parses a config number: decimal int, hex int (0x..), float ("1.", ".5", "1e-3").</summary>
    public static bool TryParseNumber(string s, out double value, out bool isInt)
    {
        value = 0;
        isInt = false;
        s = s.Trim();
        if (s.Length == 0) return false;
        var neg = s[0] == '-';
        var body = s[0] is '-' or '+' ? s[1..] : s;
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && body.Length > 2)
        {
            if (!long.TryParse(body.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return false;
            value = neg ? -h : h;
            isInt = true;
            return true;
        }
        if (body.Length == 0 || !(char.IsDigit(body[0]) || body[0] == '.')) return false;
        for (int i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (!(char.IsDigit(c) || c is '.' or 'e' or 'E' or '+' or '-')) return false;
        }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        isInt = body.IndexOfAny(['.', 'e', 'E']) < 0 && Math.Abs(value) < 9.2e18;
        return true;
    }

    public bool IsNumber => Kind is ConfigValueKind.Float or ConfigValueKind.Int;
    public bool IsString => Kind is ConfigValueKind.String or ConfigValueKind.Word;
    public bool IsArray => Kind == ConfigValueKind.Array;

    /// <summary>String content, the number text, or "" for arrays.</summary>
    public string AsString() => Kind == ConfigValueKind.Array ? "" : Text;

    /// <summary>The number; strings holding a number are parsed ("0.5"); otherwise <paramref name="fallback"/>.</summary>
    public double AsDouble(double fallback = 0)
    {
        if (IsNumber) return Number;
        if (IsString && TryParseNumber(Text, out var d, out _)) return d;
        return fallback;
    }

    public float AsFloat(float fallback = 0) => (float)AsDouble(fallback);

    public int AsInt(int fallback = 0)
    {
        var d = AsDouble(double.NaN);
        return double.IsNaN(d) || d < int.MinValue || d > int.MaxValue ? fallback : (int)Math.Round(d);
    }

    /// <summary>Array elements as floats (scalars give a one-element array).</summary>
    public float[] AsFloats()
    {
        if (!IsArray) return [AsFloat()];
        var r = new float[Items.Count];
        for (int i = 0; i < r.Length; i++) r[i] = Items[i].AsFloat();
        return r;
    }

    /// <summary>Strict equality: same kind, same text (as written), same elements.</summary>
    public bool Equals(ConfigValue? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Kind != other.Kind || Text != other.Text || Items.Count != other.Items.Count) return false;
        for (int i = 0; i < Items.Count; i++)
            if (!Items[i].Equals(other.Items[i])) return false;
        return true;
    }

    /// <summary>Semantic equality: strings and words alike, numbers compared as float32, ints and floats alike.</summary>
    public bool SemanticEquals(ConfigValue? other)
    {
        if (other is null) return false;
        if (IsArray || other.IsArray)
        {
            if (!(IsArray && other.IsArray) || Items.Count != other.Items.Count) return false;
            for (int i = 0; i < Items.Count; i++)
                if (!Items[i].SemanticEquals(other.Items[i])) return false;
            return true;
        }
        if (IsNumber && other.IsNumber) return (float)Number == (float)other.Number;
        if (IsString && other.IsString) return Text == other.Text;
        return false;
    }

    public override bool Equals(object? obj) => obj is ConfigValue v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Kind, Text, Items.Count);
    public override string ToString() => ConfigText.FormatValue(this);
}

/// <summary>A named entry of a config class: property, nested class or delete statement.</summary>
public abstract class ConfigEntry
{
    public string Name { get; set; } = "";

    /// <summary>Deep copy.</summary>
    public abstract ConfigEntry Clone();
}

/// <summary>name = value; / name[] = {..}; / name[] += {..}; / name[] -= {..};</summary>
public sealed class ConfigProperty : ConfigEntry
{
    public ConfigProperty() { }

    public ConfigProperty(string name, ConfigValue value, int arrayOp = 0)
    {
        Name = name;
        Value = value;
        ArrayOp = arrayOp;
    }

    public ConfigValue Value { get; set; } = ConfigValue.Str("");

    /// <summary>0 = "=", 1 = "+=" (append), 2 = "-=" (remove). Arrays only.</summary>
    public int ArrayOp { get; set; }

    public override ConfigEntry Clone() => new ConfigProperty(Name, Value, ArrayOp);
}

/// <summary>delete Name;</summary>
public sealed class ConfigDelete : ConfigEntry
{
    public override ConfigEntry Clone() => new ConfigDelete { Name = Name };
}

/// <summary>class Name: Base { entries }; or class Name; (extern).</summary>
public sealed class ConfigClass : ConfigEntry
{
    public ConfigClass() { }

    public ConfigClass(string name, string? baseName = null)
    {
        Name = name;
        Base = baseName;
    }

    public string? Base { get; set; }

    /// <summary>"class Name;" — a forward/extern declaration without a body.</summary>
    public bool IsExtern { get; set; }

    /// <summary>Entries in document order.</summary>
    public List<ConfigEntry> Entries { get; } = [];

    public int IndexOf(string name)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (string.Equals(Entries[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public ConfigEntry? Find(string name)
    {
        var i = IndexOf(name);
        return i < 0 ? null : Entries[i];
    }

    public ConfigProperty? Property(string name)
    {
        foreach (var e in Entries)
            if (e is ConfigProperty p && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    public ConfigClass? Class(string name)
    {
        foreach (var e in Entries)
            if (e is ConfigClass c && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public IEnumerable<ConfigClass> Classes => Entries.OfType<ConfigClass>();
    public IEnumerable<ConfigProperty> Properties => Entries.OfType<ConfigProperty>();

    public ConfigValue? Get(string name) => Property(name)?.Value;
    public string? GetString(string name) => Property(name)?.Value.AsString();

    public double? GetNumber(string name)
    {
        var v = Property(name)?.Value;
        if (v is null) return null;
        var d = v.AsDouble(double.NaN);
        return double.IsNaN(d) ? null : d;
    }

    /// <summary>Replaces the value of an existing property (keeping its position) or inserts a new one
    /// before the first nested class (properties stay above classes, like hand-written files).</summary>
    public ConfigProperty Set(string name, ConfigValue value)
    {
        var p = Property(name);
        if (p is not null)
        {
            p.Value = value;
            if (!value.IsArray) p.ArrayOp = 0;
            return p;
        }
        p = new ConfigProperty(name, value);
        var at = Entries.FindIndex(e => e is ConfigClass);
        if (at < 0) Entries.Add(p);
        else Entries.Insert(at, p);
        return p;
    }

    /// <summary>Removes every entry with this name; true when something was removed.</summary>
    public bool Remove(string name) =>
        Entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>The nested class with this name; created (appended, or inserted at <paramref name="insertAt"/>) when missing.</summary>
    public ConfigClass EnsureClass(string name, int insertAt = -1)
    {
        if (Class(name) is { } c) return c;
        c = new ConfigClass(name);
        if (insertAt < 0 || insertAt > Entries.Count) Entries.Add(c);
        else Entries.Insert(insertAt, c);
        return c;
    }

    public override ConfigEntry Clone() => DeepClone();

    public ConfigClass DeepClone()
    {
        var c = new ConfigClass(Name, Base) { IsExtern = IsExtern };
        foreach (var e in Entries) c.Entries.Add(e.Clone());
        return c;
    }

    /// <summary>Structural equality. <paramref name="semantic"/>: compare values with <see cref="ConfigValue.SemanticEquals"/>
    /// (raP round trips lose number spelling and word/string distinction).</summary>
    public bool DeepEquals(ConfigClass? other, bool semantic = false)
    {
        if (other is null) return false;
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal) || (Base ?? "") != (other.Base ?? "") ||
            IsExtern != other.IsExtern || Entries.Count != other.Entries.Count) return false;
        for (int i = 0; i < Entries.Count; i++)
        {
            var a = Entries[i];
            var b = other.Entries[i];
            if (a.GetType() != b.GetType() || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return false;
            switch (a)
            {
                case ConfigClass ca when !ca.DeepEquals((ConfigClass)b, semantic):
                    return false;
                case ConfigProperty pa:
                {
                    var pb = (ConfigProperty)b;
                    if (pa.ArrayOp != pb.ArrayOp) return false;
                    if (semantic ? !pa.Value.SemanticEquals(pb.Value) : !pa.Value.Equals(pb.Value)) return false;
                    break;
                }
            }
        }
        return true;
    }

    public override string ToString() => $"class {Name} ({Entries.Count} entries)";
}
