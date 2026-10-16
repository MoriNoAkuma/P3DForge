using System.Globalization;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>Orientation of a proxy (RAW): origin + unit axes; <see cref="Size"/> = length of the long (up) leg.</summary>
public readonly record struct ProxyFrame(Vec3 Center, Vec3 X, Vec3 Y, Vec3 Z, double Size);

/// <summary>
/// Object Builder proxies: a triangle in a named selection "proxy:&lt;path&gt;.&lt;NNN&gt;". Naming, parsing, path
/// normalization, orientation frames and creation. Pure.
/// </summary>
public static class Proxies
{
    public const string Prefix = "proxy:";

    public static bool IsProxyName(string name) =>
        name.Length > Prefix.Length && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits "proxy:path.001" into the model path and the index (-1 when there is no numeric suffix).</summary>
    public static (string Path, int Index) Parse(string name)
    {
        var body = IsProxyName(name) ? name[Prefix.Length..] : name;
        int dot = body.LastIndexOf('.');
        if (dot >= 0 && dot < body.Length - 1 && int.TryParse(body.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int idx))
            return (body[..dot], idx);
        return (body, -1);
    }

    /// <summary>Selection name of a proxy: "proxy:&lt;path&gt;.&lt;NNN&gt;" (index zero-padded to 3 digits).</summary>
    public static string SelectionName(string path, int index) => $"{Prefix}{path}.{index.ToString("D3", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Normalizes a proxy model path to the Object Builder convention: backslashes, a leading backslash, no ".p3d".
    /// <paramref name="projectRoot"/> (e.g. "P:\") is stripped from absolute paths; other drive letters are dropped.
    /// </summary>
    public static string NormalizePath(string path, string projectRoot = @"P:\")
    {
        var s = path.Trim().Replace('/', '\\');
        var root = projectRoot.Replace('/', '\\').TrimEnd('\\');
        if (root.Length > 0 && s.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) s = s[root.Length..];
        else if (s.Length >= 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && s[2] == '\\') s = s[2..];
        if (s.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        while (s.Contains(@"\\", StringComparison.Ordinal)) s = s.Replace(@"\\", @"\", StringComparison.Ordinal);
        if (!s.StartsWith('\\')) s = "\\" + s;
        return s;
    }

    /// <summary>Next free index for proxies of <paramref name="path"/> in a LOD (1 + highest used, starting at 1).</summary>
    public static int NextIndex(Lod lod, string path)
    {
        var want = path.TrimStart('\\');
        int max = 0;
        foreach (var s in lod.Selections)
        {
            if (!IsProxyName(s.Name)) continue;
            var (p, i) = Parse(s.Name);
            if (i >= 0 && string.Equals(p.TrimStart('\\'), want, StringComparison.OrdinalIgnoreCase)) max = Math.Max(max, i);
        }
        return max + 1;
    }

    /// <summary>
    /// Adds a proxy: the triangle [at, at + (0, 2, 0), at + (0, 0, 1)] (origin, up 2 m, forward 1 m — identity
    /// orientation) in a new named selection "proxy:&lt;path&gt;.&lt;NNN&gt;". <paramref name="path"/> should already be
    /// normalized; <paramref name="index"/> null picks the next free index. The proxy becomes the selection.
    /// </summary>
    public static (Lod Lod, string Name) Create(Lod lod, string path, int? index, Vec3 at = default)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "\\") throw new ArgumentException("Proxy path is empty", nameof(path));
        int idx = index ?? NextIndex(lod, path);
        if (idx is < 0 or > 999999) throw new ArgumentException("Proxy index must be a whole number ≥ 0", nameof(index));
        var name = SelectionName(path, idx);
        if (lod.Selection(name) is not null) throw new ArgumentException($"A selection named \"{name}\" already exists");
        var frag = Fragment.Allocate(3, 1);
        float[] p = [(float)at.X, (float)at.Y, (float)at.Z, (float)at.X, (float)(at.Y + 2), (float)at.Z, (float)at.X, (float)at.Y, (float)(at.Z + 1)];
        p.CopyTo(frag.Positions, 0);
        frag.Faces.VertCount[0] = 3;
        frag.Faces.Points[0] = 0;
        frag.Faces.Points[1] = 1;
        frag.Faces.Points[2] = 2;
        frag = frag with { Selections = [new NamedWeights(name, MeshMath.Ones(3), MeshMath.Ones(1))] };
        return (Structure.RecalcNormals(MeshMath.AppendSafe(lod, frag, selectNew: true)), name);
    }

    static double AngleAt(Vec3 a, Vec3 b, Vec3 c)
    {
        var u = b - a;
        var v = c - a;
        double lu = u.Length, lv = v.Length;
        if (!(lu > 0) || !(lv > 0)) return double.NaN;
        return Math.Acos(Math.Clamp(Vec3.Dot(u, v) / (lu * lv), -1, 1));
    }

    /// <summary>
    /// Object Builder proxy triangle convention: the vertex with the largest angle is the origin, the leg to the vertex
    /// with the second largest angle (short leg) is +Z (forward), the other (long) leg is +Y (up); X = Y × Z.
    /// RAW coordinates in and out; null for a degenerate triangle.
    /// </summary>
    public static ProxyFrame? ComputeFrame(Vec3 a, Vec3 b, Vec3 c)
    {
        Span<Vec3> pts = [a, b, c];
        Span<double> ang = [AngleAt(a, b, c), AngleAt(b, a, c), AngleAt(c, a, b)];
        for (int i = 0; i < 3; i++)
            if (!double.IsFinite(ang[i])) return null;
        Span<int> order = [0, 1, 2];
        for (int i = 1; i < 3; i++) // stable sort by angle, descending
            for (int j = i; j > 0 && ang[order[j]] > ang[order[j - 1]]; j--)
                (order[j], order[j - 1]) = (order[j - 1], order[j]);
        var center = pts[order[0]];
        var up = pts[order[2]] - center;
        var fwd = pts[order[1]] - center;
        double size = up.Length;
        if (!(size > 0) || fwd.Normalized() is not { } f) return null;
        var y = up / size;
        if (Vec3.Cross(y, f).Normalized() is not { } x) return null;
        return new ProxyFrame(center, x, y, Vec3.Cross(x, y), size);
    }

    /// <summary>Compact label: first path component + last two ("\dz\…\wheel\wheel.001").</summary>
    public static string ShortLabel(string name, int max = 42)
    {
        var body = IsProxyName(name) ? name[Prefix.Length..] : name;
        if (body.Length <= max) return body;
        var parts = body.Split('\\', '/');
        var tail = parts.Length >= 2 ? parts[^2] + "\\" + parts[^1] : parts[^1];
        var head = parts[0].Length == 0 ? "\\" + (parts.Length > 1 ? parts[1] : "") : parts[0];
        var s = head + "\\…\\" + tail;
        return s.Length <= max + 6 ? s : "…" + body[^max..];
    }
}
