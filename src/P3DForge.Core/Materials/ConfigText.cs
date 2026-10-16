using System.Text;

namespace P3DForge.Core.Materials;

/// <summary>
/// Text config syntax (config.cpp / *.rvmat): tolerant parser and canonical writer.
/// Parser: classes (with base / extern), delete, scalars, arrays (nested, +=, -=), strings ("" escapes),
/// numbers (int, hex, float), unquoted words, // and /* */ comments; preprocessor lines (#include, #define)
/// are skipped with a warning. Malformed input never throws: it is skipped with a warning.
/// Writer: BI style (tabs, "class X\n{\n...\n};", CRLF), keeps entry order and number spelling, drops comments.
/// </summary>
public static class ConfigText
{
    /// <summary>Parses config text into a root class (Name ""). Problems go to <paramref name="warnings"/>.</summary>
    public static ConfigClass Parse(string text, List<string>? warnings = null)
    {
        var p = new Parser(text ?? "", warnings);
        var root = new ConfigClass("");
        p.ParseBody(root, topLevel: true);
        if (p.Comments > 0) warnings?.Add($"{p.Comments} comment(s) are not kept when the file is saved");
        return root;
    }

    /// <summary>Writes the entries of <paramref name="root"/> (the root's own name/base are not written).</summary>
    public static string Write(ConfigClass root, string newline = "\r\n") => Write(root, new ConfigTextStyle(newline));

    /// <summary>Writes the entries of <paramref name="root"/> in <paramref name="style"/> (see <see cref="ConfigTextStyle.Detect"/>).</summary>
    public static string Write(ConfigClass root, ConfigTextStyle style)
    {
        var sb = new StringBuilder(1024);
        WriteEntries(sb, root, 0, style.Newline, style.SpacedAssign ? " = " : "=");
        return sb.ToString();
    }

    static void WriteEntries(StringBuilder sb, ConfigClass cls, int indent, string nl, string eq)
    {
        foreach (var e in cls.Entries)
        {
            sb.Append('\t', indent);
            switch (e)
            {
                case ConfigClass c when c.IsExtern:
                    sb.Append("class ").Append(c.Name).Append(';').Append(nl);
                    break;
                case ConfigClass c:
                    sb.Append("class ").Append(c.Name);
                    if (!string.IsNullOrEmpty(c.Base)) sb.Append(": ").Append(c.Base);
                    sb.Append(nl);
                    sb.Append('\t', indent).Append('{').Append(nl);
                    WriteEntries(sb, c, indent + 1, nl, eq);
                    sb.Append('\t', indent).Append("};").Append(nl);
                    break;
                case ConfigDelete d:
                    sb.Append("delete ").Append(d.Name).Append(';').Append(nl);
                    break;
                case ConfigProperty p:
                    sb.Append(p.Name);
                    if (p.Value.IsArray) sb.Append("[]").Append(p.ArrayOp switch { 1 => eq.Replace("=", "+="), 2 => eq.Replace("=", "-="), _ => eq });
                    else sb.Append(eq);
                    AppendValue(sb, p.Value);
                    sb.Append(';').Append(nl);
                    break;
            }
        }
    }

    /// <summary>Config text of a value ("text", 0.5, {1,2}).</summary>
    public static string FormatValue(ConfigValue v)
    {
        var sb = new StringBuilder();
        AppendValue(sb, v);
        return sb.ToString();
    }

    static void AppendValue(StringBuilder sb, ConfigValue v)
    {
        switch (v.Kind)
        {
            case ConfigValueKind.Array:
                sb.Append('{');
                for (int i = 0; i < v.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendValue(sb, v.Items[i]);
                }
                sb.Append('}');
                break;
            case ConfigValueKind.Float:
            case ConfigValueKind.Int:
                sb.Append(v.Text.Length > 0 ? v.Text : ConfigValue.FormatFloat(v.Number));
                break;
            case ConfigValueKind.Word when IsSafeWord(v.Text):
                sb.Append(v.Text);
                break;
            default:
                sb.Append('"').Append(v.Text.Replace("\"", "\"\"")).Append('"');
                break;
        }
    }

    static bool IsSafeWord(string s)
    {
        if (s.Length == 0 || s[0] == '#' || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])) return false;
        foreach (var c in s)
            if (c is ';' or ',' or '{' or '}' or '"' or '\'' or '\r' or '\n' or '/') return false;
        return !ConfigValue.TryParseNumber(s, out _, out _);
    }

    sealed class Parser(string s, List<string>? warnings)
    {
        int pos;
        public int Comments;

        void Warn(string message, int at = -1)
        {
            if (warnings is null) return;
            if (at < 0) at = pos;
            int line = 1;
            for (int i = 0; i < at && i < s.Length; i++)
                if (s[i] == '\n') line++;
            warnings.Add($"line {line}: {message}");
        }

        bool AtLineStart(int p)
        {
            for (int i = p - 1; i >= 0; i--)
            {
                if (s[i] == '\n') return true;
                if (s[i] is not (' ' or '\t' or '\r')) return false;
            }
            return true;
        }

        void SkipTrivia()
        {
            while (pos < s.Length)
            {
                var c = s[pos];
                if (char.IsWhiteSpace(c) || c == '﻿')
                {
                    pos++;
                    continue;
                }
                if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
                {
                    Comments++;
                    while (pos < s.Length && s[pos] != '\n') pos++;
                    continue;
                }
                if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '*')
                {
                    Comments++;
                    var end = s.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        Warn("unterminated /* comment");
                        pos = s.Length;
                    }
                    else pos = end + 2;
                    continue;
                }
                if (c == '#' && AtLineStart(pos))
                {
                    var start = pos;
                    while (pos < s.Length && s[pos] != '\n')
                    {
                        if (s[pos] == '\\' && pos + 1 < s.Length && (s[pos + 1] == '\n' || (s[pos + 1] == '\r' && pos + 2 < s.Length && s[pos + 2] == '\n')))
                            pos += s[pos + 1] == '\r' ? 3 : 2;
                        else pos++;
                    }
                    var directive = s[start..pos].Trim();
                    Warn($"preprocessor directive ignored: {(directive.Length > 80 ? directive[..80] + "..." : directive)}", start);
                    continue;
                }
                break;
            }
        }

        string ReadIdent()
        {
            var start = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
            return s[start..pos];
        }

        bool NextIsIdent()
        {
            var save = pos;
            SkipTrivia();
            var ok = pos < s.Length && (char.IsLetter(s[pos]) || s[pos] == '_');
            if (!ok) pos = save;
            return ok;
        }

        /// <summary>Skips to after the next ';' (or to a '}' / EOF), honoring quotes.</summary>
        void SkipStatement()
        {
            while (pos < s.Length)
            {
                var c = s[pos];
                if (c == ';')
                {
                    pos++;
                    return;
                }
                if (c == '}') return;
                if (c is '"' or '\'')
                {
                    ReadQuoted();
                    continue;
                }
                pos++;
            }
        }

        void ExpectSemicolon(string after)
        {
            var save = pos;
            SkipTrivia();
            if (pos < s.Length && s[pos] == ';')
            {
                pos++;
                return;
            }
            pos = save;
            Warn($"missing ';' after {after}");
        }

        public void ParseBody(ConfigClass cls, bool topLevel)
        {
            while (true)
            {
                SkipTrivia();
                if (pos >= s.Length)
                {
                    if (!topLevel) Warn($"missing '}}' for class {cls.Name}");
                    return;
                }
                var c = s[pos];
                if (c == '}')
                {
                    pos++;
                    if (topLevel)
                    {
                        Warn("unexpected '}'", pos - 1);
                        continue;
                    }
                    var save = pos;
                    SkipTrivia();
                    if (pos < s.Length && s[pos] == ';') pos++;
                    else pos = save;
                    return;
                }
                if (c == ';')
                {
                    pos++;
                    continue;
                }
                if (!(char.IsLetter(c) || c == '_'))
                {
                    Warn($"unexpected '{c}'");
                    pos++;
                    SkipStatement();
                    continue;
                }
                var word = ReadIdent();
                if (word.Equals("class", StringComparison.OrdinalIgnoreCase) && NextIsIdent())
                {
                    ParseClass(cls);
                    continue;
                }
                if (word.Equals("delete", StringComparison.OrdinalIgnoreCase) && NextIsIdent())
                {
                    var name = ReadIdent();
                    cls.Entries.Add(new ConfigDelete { Name = name });
                    ExpectSemicolon("delete " + name);
                    continue;
                }
                if (word.Equals("enum", StringComparison.OrdinalIgnoreCase))
                {
                    Warn("enum ignored");
                    SkipTrivia();
                    if (pos < s.Length && s[pos] == '{')
                    {
                        var end = s.IndexOf('}', pos);
                        pos = end < 0 ? s.Length : end + 1;
                    }
                    SkipStatement();
                    continue;
                }
                ParseProperty(cls, word);
            }
        }

        void ParseClass(ConfigClass parent)
        {
            var name = ReadIdent();
            SkipTrivia();
            string? baseName = null;
            if (pos < s.Length && s[pos] == ':')
            {
                pos++;
                SkipTrivia();
                baseName = ReadIdent();
                if (baseName.Length == 0) baseName = null;
                SkipTrivia();
            }
            if (pos < s.Length && s[pos] == '{')
            {
                pos++;
                var c = new ConfigClass(name, baseName);
                parent.Entries.Add(c);
                ParseBody(c, topLevel: false);
                return;
            }
            if (pos < s.Length && s[pos] == ';')
            {
                pos++;
                parent.Entries.Add(new ConfigClass(name, baseName) { IsExtern = true });
                return;
            }
            Warn($"expected '{{' or ';' after class {name}");
            parent.Entries.Add(new ConfigClass(name, baseName) { IsExtern = true });
        }

        void ParseProperty(ConfigClass cls, string name)
        {
            var start = pos;
            SkipTrivia();
            var isArray = false;
            if (pos < s.Length && s[pos] == '[')
            {
                pos++;
                SkipTrivia();
                if (pos < s.Length && s[pos] == ']') pos++;
                else Warn($"expected ']' after {name}[");
                isArray = true;
                SkipTrivia();
            }
            var op = 0;
            if (pos + 1 < s.Length && s[pos] == '+' && s[pos + 1] == '=')
            {
                op = 1;
                pos += 2;
            }
            else if (pos + 1 < s.Length && s[pos] == '-' && s[pos + 1] == '=')
            {
                op = 2;
                pos += 2;
            }
            else if (pos < s.Length && s[pos] == '=') pos++;
            else
            {
                Warn($"expected '=' after {name}", start);
                SkipStatement();
                return;
            }
            SkipTrivia();
            ConfigValue v;
            if (pos < s.Length && s[pos] == '{') v = ParseArray(0);
            else v = ParseScalar();
            if (isArray && !v.IsArray)
            {
                Warn($"{name}[] has a scalar value");
                v = ConfigValue.Array([v]);
            }
            if (!v.IsArray) op = 0;
            cls.Entries.Add(new ConfigProperty(name, v, op));
            ExpectSemicolon(name);
        }

        ConfigValue ParseScalar()
        {
            if (pos < s.Length && s[pos] is '"' or '\'')
                return ConfigValue.Str(ReadQuoted());
            var start = pos;
            while (pos < s.Length && s[pos] is not (';' or '\n' or '}' or '{'))
            {
                if (s[pos] == '/' && pos + 1 < s.Length && s[pos + 1] is '/' or '*') break;
                pos++;
            }
            var text = s[start..pos].Trim();
            if (text.Length == 0)
            {
                Warn("empty value");
                return ConfigValue.Str("");
            }
            return Bare(text);
        }

        static ConfigValue Bare(string text)
        {
            if (ConfigValue.TryParseNumber(text, out var d, out var isInt))
                return isInt ? ConfigValue.Int((long)d, text) : ConfigValue.Float(d, text);
            return ConfigValue.Word(text);
        }

        ConfigValue ParseArray(int depth)
        {
            pos++; // '{'
            var items = new List<ConfigValue>();
            while (true)
            {
                SkipTrivia();
                if (pos >= s.Length)
                {
                    Warn("unterminated array");
                    break;
                }
                var c = s[pos];
                if (c == '}')
                {
                    pos++;
                    break;
                }
                if (c == ',')
                {
                    pos++;
                    continue;
                }
                if (c == '{')
                {
                    if (depth > 32)
                    {
                        Warn("array nested too deep");
                        SkipStatement();
                        break;
                    }
                    items.Add(ParseArray(depth + 1));
                    continue;
                }
                if (c is '"' or '\'')
                {
                    items.Add(ConfigValue.Str(ReadQuoted()));
                    continue;
                }
                if (c == ';')
                {
                    Warn("unterminated array");
                    break;
                }
                var start = pos;
                while (pos < s.Length && s[pos] is not (',' or '}' or ';' or '\n' or '{'))
                {
                    if (s[pos] == '/' && pos + 1 < s.Length && s[pos + 1] is '/' or '*') break;
                    pos++;
                }
                var text = s[start..pos].Trim();
                if (text.Length > 0) items.Add(Bare(text));
                else if (pos < s.Length && s[pos] == '/') continue;
                else if (pos == start) pos++;
            }
            return ConfigValue.Array(items);
        }

        string ReadQuoted()
        {
            var q = s[pos++];
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                var c = s[pos];
                if (c == q)
                {
                    pos++;
                    if (pos < s.Length && s[pos] == q)
                    {
                        sb.Append(q);
                        pos++;
                        continue;
                    }
                    return sb.ToString();
                }
                if (c is '\n' or '\r')
                {
                    Warn("unterminated string");
                    return sb.ToString();
                }
                sb.Append(c);
                pos++;
            }
            Warn("unterminated string");
            return sb.ToString();
        }
    }
}


/// <summary>
/// Layout of written config text: newline and assignment spacing ("a=1" BI style or "a = 1"). Saving an edited file
/// in its own style keeps the diff to the lines that really changed.
/// </summary>
public sealed record ConfigTextStyle(string Newline = "\r\n", bool SpacedAssign = false)
{
    public static readonly ConfigTextStyle Bi = new();

    /// <summary>Style of existing text: CRLF / LF by the first line break, spaced assignments when most property
    /// lines use "name = value".</summary>
    public static ConfigTextStyle Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) return Bi;
        var lf = text.IndexOf('\n');
        var nl = lf < 0 || (lf > 0 && text[lf - 1] == '\r') ? "\r\n" : "\n";
        int spaced = 0, tight = 0;
        for (var i = text.IndexOf('='); i > 0 && spaced + tight < 64; i = text.IndexOf('=', i + 1))
        {
            var j = i - 1;
            if (text[j] is '+' or '-') j--;
            if (j < 0) continue;
            if (text[j] == ' ' && i + 1 < text.Length && text[i + 1] == ' ')
            {
                if (j > 0 && IsNameEnd(text[j - 1])) spaced++;
            }
            else if (IsNameEnd(text[j])) tight++;
        }
        return new ConfigTextStyle(nl, spaced > tight);
    }

    static bool IsNameEnd(char c) => char.IsLetterOrDigit(c) || c is ']' or '_';
}
