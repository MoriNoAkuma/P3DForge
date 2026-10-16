namespace P3DForge.Core.Anim;

/// <summary>
/// Where a class sits in config text. <see cref="Start"/> is the start of its line (indentation included) when only
/// whitespace precedes "class"; <see cref="End"/> is after "};" and the rest of that line (newline included) when only
/// whitespace / a comment follows — so replacing or removing [Start, End) keeps the surrounding layout.
/// </summary>
/// <param name="Name">Class name as written.</param>
/// <param name="Base">Base class name, or null.</param>
/// <param name="Start">Start of the class (its line when the line holds only the class).</param>
/// <param name="Open">Index of "{" (-1 for a declaration "class X;").</param>
/// <param name="Close">Index of the matching "}" (-1 for a declaration).</param>
/// <param name="End">After "};" (and its line end when nothing else follows on the line).</param>
public readonly record struct ClassSpan(string Name, string? Base, int Start, int Open, int Close, int End)
{
    public bool HasBody => Open >= 0;
}

/// <summary>A property statement "name = value;" / "name[] = {…};" with the same line rules as <see cref="ClassSpan"/>.</summary>
public readonly record struct PropertySpan(string Name, int Start, int ValueStart, int ValueEnd, int End);

/// <summary>
/// Finds classes and properties in config text (model.cfg, config.cpp) without re-writing it: strings, // and /* */
/// comments and preprocessor lines are skipped, names compare case-insensitively. Used for edits that must leave the
/// rest of a hand-written file exactly as it was.
/// </summary>
public static class ConfigSpans
{
    /// <summary>Direct child classes of <paramref name="parent"/> (null = top level).</summary>
    public static List<ClassSpan> Classes(string text, ClassSpan? parent = null)
    {
        var (from, to) = Body(text, parent);
        var list = new List<ClassSpan>();
        Scan(text, from, to, (kind, start, nameStart, nameEnd) =>
        {
            if (kind != Kind.Class) return SkipProperty(text, nameEnd, to);
            int i = SkipSpace(text, nameEnd, to);
            string name = text[nameStart..nameEnd];
            string? @base = null;
            if (i < to && text[i] == ':')
            {
                int b = SkipSpace(text, i + 1, to);
                int be = Ident(text, b, to);
                @base = text[b..be];
                i = SkipSpace(text, be, to);
            }
            if (i < to && text[i] == ';')
            {
                list.Add(new ClassSpan(name, @base, LineStart(text, start), -1, -1, LineEnd(text, i + 1)));
                return i + 1;
            }
            if (i >= to || text[i] != '{') return i;
            int close = Match(text, i, to);
            if (close < 0) return to;
            int end = close + 1;
            int j = SkipSpace(text, end, to, newlines: false);
            if (j < to && text[j] == ';') end = j + 1;
            list.Add(new ClassSpan(name, @base, LineStart(text, start), i, close, LineEnd(text, end)));
            return end;
        });
        return list;
    }

    /// <summary>The class at <paramref name="path"/> (e.g. "CfgModels", "gun", "Animations"), or null.</summary>
    public static ClassSpan? Find(string text, params string[] path)
    {
        ClassSpan? cur = null;
        foreach (var name in path)
        {
            ClassSpan? next = null;
            foreach (var c in Classes(text, cur))
                if (c.HasBody && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    next = c;
                    break;
                }
            if (next is null) return null;
            cur = next;
        }
        return cur;
    }

    /// <summary>Direct properties of a class (or the top level).</summary>
    public static List<PropertySpan> Properties(string text, ClassSpan? parent = null)
    {
        var (from, to) = Body(text, parent);
        var list = new List<PropertySpan>();
        Scan(text, from, to, (kind, start, nameStart, nameEnd) =>
        {
            if (kind == Kind.Class)
            {
                // skip the nested class
                int k = nameEnd;
                while (k < to && text[k] != '{' && text[k] != ';') k++;
                if (k < to && text[k] == '{')
                {
                    int close = Match(text, k, to);
                    return close < 0 ? to : close + 1;
                }
                return k + 1;
            }
            int i = SkipSpace(text, nameEnd, to);
            if (i + 1 < to && text[i] == '[' && text[i + 1] == ']') i = SkipSpace(text, i + 2, to);
            if (i < to && (text[i] == '+' || text[i] == '-') && i + 1 < to && text[i + 1] == '=') i++;
            if (i >= to || text[i] != '=') return i + 1;
            int v = SkipSpace(text, i + 1, to);
            int e = ValueEnd(text, v, to);
            int end = e < to && text[e] == ';' ? e + 1 : e;
            list.Add(new PropertySpan(text[nameStart..nameEnd], LineStart(text, start), v, e, LineEnd(text, end)));
            return end;
        });
        return list;
    }

    public static PropertySpan? Property(string text, ClassSpan? parent, string name)
    {
        foreach (var p in Properties(text, parent))
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>The indentation of the line containing <paramref name="pos"/>.</summary>
    public static string IndentAt(string text, int pos)
    {
        int s = LineStart(text, pos);
        int e = s;
        while (e < text.Length && text[e] is ' ' or '\t') e++;
        return text[s..e];
    }

    /// <summary>"\r\n" when the text uses it, else "\n".</summary>
    public static string NewlineOf(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>Where text goes to become the last entry of a class: the start of the line holding its "}".</summary>
    public static int InsertPoint(string text, ClassSpan cls)
    {
        int s = LineStart(text, cls.Close);
        return s < cls.Close && string.IsNullOrWhiteSpace(text[s..cls.Close]) ? s : cls.Close;
    }

    // ---- scanning ---------------------------------------------------------------------------------

    enum Kind { Class, Property }

    static (int From, int To) Body(string text, ClassSpan? parent) =>
        parent is { } p ? (p.Open + 1, p.Close) : (0, text.Length);

    /// <summary>Calls <paramref name="onEntry"/> for every entry start at depth 0 of [from, to); it returns where to continue.</summary>
    static void Scan(string text, int from, int to, Func<Kind, int, int, int, int> onEntry)
    {
        int i = from;
        while (i < to)
        {
            i = SkipTrivia(text, i, to);
            if (i >= to) break;
            char c = text[i];
            if (IsIdentStart(c))
            {
                int e = Ident(text, i, to);
                var word = text.AsSpan(i, e - i);
                if (word.Equals("class", StringComparison.OrdinalIgnoreCase))
                {
                    int n = SkipSpace(text, e, to);
                    int ne = Ident(text, n, to);
                    i = ne > n ? Math.Max(onEntry(Kind.Class, i, n, ne), i + 1) : e;
                }
                else if (word.Equals("delete", StringComparison.OrdinalIgnoreCase))
                    i = SkipStatement(text, e, to);
                else i = Math.Max(onEntry(Kind.Property, i, i, e), i + 1);
                continue;
            }
            if (c == '{')
            {
                int m = Match(text, i, to);
                i = m < 0 ? to : m + 1;
                continue;
            }
            i++;
        }
    }

    /// <summary>After "name": past "[] = value;" (or just past the name when no "=" follows).</summary>
    static int SkipProperty(string text, int nameEnd, int to)
    {
        int i = SkipSpace(text, nameEnd, to);
        if (i + 1 < to && text[i] == '[' && text[i + 1] == ']') i = SkipSpace(text, i + 2, to);
        if (i < to && (text[i] == '+' || text[i] == '-') && i + 1 < to && text[i + 1] == '=') i++;
        if (i >= to || text[i] != '=') return nameEnd;
        int e = ValueEnd(text, SkipSpace(text, i + 1, to), to);
        return e < to && text[e] == ';' ? e + 1 : e;
    }

    static int SkipStatement(string text, int i, int to)
    {
        while (i < to && text[i] != ';') i++;
        return i + 1;
    }

    /// <summary>Whitespace, comments and preprocessor lines.</summary>
    static int SkipTrivia(string text, int i, int to)
    {
        while (i < to)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) i++;
            else if (c == '/' && i + 1 < to && text[i + 1] == '/') i = LineEndNoNl(text, i);
            else if (c == '/' && i + 1 < to && text[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? to : e + 2;
            }
            else if (c == '#' && LineStart(text, i) is var ls && string.IsNullOrWhiteSpace(text[ls..i])) i = LineEndNoNl(text, i);
            else break;
        }
        return i;
    }

    static int SkipSpace(string text, int i, int to, bool newlines = true)
    {
        while (i < to)
        {
            char c = text[i];
            if (c is ' ' or '\t' || (newlines && c is '\r' or '\n')) i++;
            else if (newlines && c == '/' && i + 1 < to && (text[i + 1] == '/' || text[i + 1] == '*')) i = SkipTrivia(text, i, to);
            else break;
        }
        return i;
    }

    static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    static int Ident(string text, int i, int to)
    {
        while (i < to && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
        return i;
    }

    /// <summary>Index of the "}" matching the "{" at <paramref name="open"/>; -1 when unbalanced.</summary>
    static int Match(string text, int open, int to)
    {
        int depth = 0;
        for (int i = open; i < to; i++)
        {
            char c = text[i];
            if (c == '"') i = SkipString(text, i, to) - 1;
            else if (c == '/' && i + 1 < to && text[i + 1] == '/') i = LineEndNoNl(text, i) - 1;
            else if (c == '/' && i + 1 < to && text[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? to : e + 1;
            }
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Index after a "…" string ("" is an escaped quote).</summary>
    static int SkipString(string text, int i, int to)
    {
        i++;
        while (i < to)
        {
            if (text[i] == '"')
            {
                if (i + 1 < to && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            if (text[i] is '\r' or '\n') return i; // unterminated: stop at the line end
            i++;
        }
        return to;
    }

    /// <summary>End of a value: the ";" ending it (braces and strings skipped), or the end of the line for a missing ";".</summary>
    static int ValueEnd(string text, int i, int to)
    {
        while (i < to)
        {
            char c = text[i];
            if (c == '"') i = SkipString(text, i, to);
            else if (c == '{')
            {
                int m = Match(text, i, to);
                i = m < 0 ? to : m + 1;
            }
            else if (c == ';' || c == '}') return i;
            else if (c is '\n' && !text.AsSpan(LineStart(text, i), i - LineStart(text, i)).Contains('{')) return TrimEnd(text, i);
            else i++;
        }
        return i;
    }

    static int TrimEnd(string text, int i)
    {
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        return i;
    }

    static int LineStart(string text, int pos)
    {
        int s = pos;
        while (s > 0 && text[s - 1] is not ('\n' or '\r')) s--;
        return string.IsNullOrWhiteSpace(text[s..pos]) ? s : pos;
    }

    static int LineEndNoNl(string text, int i)
    {
        while (i < text.Length && text[i] is not ('\n' or '\r')) i++;
        return i;
    }

    /// <summary>After <paramref name="pos"/>: through the newline when only spaces / a // comment follow, else pos.</summary>
    static int LineEnd(string text, int pos)
    {
        int i = pos;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/') i = LineEndNoNl(text, i);
        if (i < text.Length && text[i] == '\r') i++;
        if (i < text.Length && text[i] == '\n') return i + 1;
        return i >= text.Length ? i : pos;
    }
}
