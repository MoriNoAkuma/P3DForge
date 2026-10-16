using System.Numerics;
using System.Text;
using P3DForge.Core.Model;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>Orientation of a proxy (RAW): origin + unit axes; <see cref="Size"/> = length of the long leg.</summary>
public readonly record struct ProxyFrame(Vector3 Center, Vector3 X, Vector3 Y, Vector3 Z, float Size);

/// <summary>A "proxy:" named selection of a LOD.</summary>
public sealed record ProxyInfo(string Name, string Path, int Index, string Label, int Face, Vector3 Anchor, ProxyFrame? Frame);

/// <summary>Pure helpers computing what the viewport draws besides the mesh (ported from legacy-ts render/scene-data.ts).</summary>
public static class SceneData
{
    public const string ProxyPrefix = "proxy:";

    public static bool IsProxyName(string name) =>
        name.Length > ProxyPrefix.Length && name.StartsWith(ProxyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The proxy selection tags of a LOD in tag order (identity list used as a cache key).</summary>
    public static List<SelectionTag> ProxyTags(Lod lod)
    {
        var list = new List<SelectionTag>();
        foreach (var t in lod.Tags)
            if (t is SelectionTag s && IsProxyName(s.Name)) list.Add(s);
        return list;
    }

    /// <summary>Faces belonging to any proxy selection, or null when there is none.</summary>
    public static byte[]? ProxyFaceMask(Lod lod, IReadOnlyList<SelectionTag> proxyTags)
    {
        if (proxyTags.Count == 0) return null;
        var mask = new byte[lod.FaceCount];
        foreach (var t in proxyTags)
        {
            int n = Math.Min(t.Faces.Length, lod.FaceCount);
            for (int f = 0; f < n; f++)
                if (t.Faces[f] != 0) mask[f] = 1;
        }
        return mask;
    }

    static float AngleAt(Vector3 a, Vector3 b, Vector3 c)
    {
        var u = b - a;
        var v = c - a;
        float lu = u.Length(), lv = v.Length();
        if (!(lu > 0) || !(lv > 0)) return float.NaN;
        return MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / (lu * lv), -1f, 1f));
    }

    /// <summary>
    /// Object Builder proxy triangle convention (a3ob utilities_proxy.find_axis_vertices): the vertex with the
    /// largest angle is the origin, the leg to the vertex with the second largest angle (short leg) is +Z
    /// (forward), the other (long) leg is +Y (up); X = Y × Z. RAW coordinates in and out.
    /// </summary>
    public static ProxyFrame? ComputeProxyFrame(Vector3 a, Vector3 b, Vector3 c)
    {
        Span<Vector3> pts = [a, b, c];
        Span<float> ang = [AngleAt(a, b, c), AngleAt(b, a, c), AngleAt(c, a, b)];
        for (int i = 0; i < 3; i++)
            if (!float.IsFinite(ang[i])) return null;
        Span<int> order = [0, 1, 2];
        // stable sort by angle, descending
        for (int i = 1; i < 3; i++)
            for (int j = i; j > 0 && ang[order[j]] > ang[order[j - 1]]; j--)
                (order[j], order[j - 1]) = (order[j - 1], order[j]);
        var center = pts[order[0]];
        var up = pts[order[2]] - center;
        var fwd = pts[order[1]] - center;
        float size = up.Length();
        if (!(size > 0) || !(fwd.Length() > 0)) return null;
        var y = up / size;
        var xr = Vector3.Cross(y, Vector3.Normalize(fwd));
        if (!(xr.Length() > 1e-9f)) return null;
        var x = Vector3.Normalize(xr);
        var z = Vector3.Cross(x, y);
        return new ProxyFrame(center, x, y, z, size);
    }

    /// <summary>Splits "proxy:path.001" into the model path and the index (-1 when there is no numeric suffix).</summary>
    public static (string Path, int Index) ParseProxyName(string name)
    {
        var body = IsProxyName(name) ? name[ProxyPrefix.Length..] : name;
        int dot = body.LastIndexOf('.');
        if (dot >= 0 && dot < body.Length - 1 && int.TryParse(body.AsSpan(dot + 1), System.Globalization.NumberStyles.None, null, out int idx))
            return (body[..dot], idx);
        return (body, -1);
    }

    /// <summary>Compact label: first path component + last two ("\dz\…\wheel\wheel.001").</summary>
    public static string ShortProxyLabel(string name, int max = 42)
    {
        var body = IsProxyName(name) ? name[ProxyPrefix.Length..] : name;
        if (body.Length <= max) return body;
        var parts = body.Split('\\', '/');
        var tail = parts.Length >= 2 ? parts[^2] + "\\" + parts[^1] : parts[^1];
        var head = parts[0].Length == 0 ? "\\" + (parts.Length > 1 ? parts[1] : "") : parts[0];
        var s = head + "\\…\\" + tail;
        return s.Length <= max + 6 ? s : "…" + body[^max..];
    }

    /// <summary>All proxies of a LOD with frames computed from <paramref name="raw"/> positions.</summary>
    public static List<ProxyInfo> FindProxies(Lod lod, IReadOnlyList<SelectionTag> proxyTags, float[] raw)
    {
        var result = new List<ProxyInfo>(proxyTags.Count);
        int np = Math.Min(lod.PointCount, raw.Length / 3);
        Vector3 V(uint p) => new(raw[p * 3], raw[p * 3 + 1], raw[p * 3 + 2]);
        foreach (var t in proxyTags)
        {
            var (path, index) = ParseProxyName(t.Name);
            int face = -1;
            int nf = Math.Min(t.Faces.Length, lod.FaceCount);
            for (int f = 0; f < nf; f++)
                if (t.Faces[f] != 0 && lod.FaceVertCount[f] == 3)
                {
                    face = f;
                    break;
                }
            uint[]? pts = null;
            if (face >= 0) pts = [lod.FacePoints[face * 4], lod.FacePoints[face * 4 + 1], lod.FacePoints[face * 4 + 2]];
            else
            {
                var sel = new List<uint>(4);
                for (int p = 0; p < Math.Min(t.Points.Length, lod.PointCount) && sel.Count < 4; p++)
                    if (t.Points[p] != 0) sel.Add((uint)p);
                if (sel.Count == 3) pts = [.. sel];
            }
            ProxyFrame? frame = null;
            Vector3 anchor = default;
            bool hasAnchor = false;
            if (pts is not null && pts[0] < np && pts[1] < np && pts[2] < np)
            {
                frame = ComputeProxyFrame(V(pts[0]), V(pts[1]), V(pts[2]));
                anchor = frame?.Center ?? (V(pts[0]) + V(pts[1]) + V(pts[2])) / 3;
                hasAnchor = true;
            }
            if (!hasAnchor)
            {
                // centroid of the selection's points
                var sum = Vector3.Zero;
                int cnt = 0;
                for (int p = 0; p < Math.Min(t.Points.Length, np); p++)
                    if (t.Points[p] != 0)
                    {
                        sum += V((uint)p);
                        cnt++;
                    }
                if (cnt == 0) continue;
                anchor = sum / cnt;
            }
            result.Add(new ProxyInfo(t.Name, path, index, ShortProxyLabel(t.Name), face, anchor, frame));
        }
        return result;
    }

    /// <summary>
    /// Per-point labels: names of the named selections containing the point, ", "-joined (proxies and names
    /// starting with '-' excluded). Null when the LOD has no labelled point or too many points.
    /// </summary>
    public static string?[]? PointLabels(Lod lod, int maxPoints = 20000)
    {
        if (lod.PointCount == 0 || lod.PointCount > maxPoints) return null;
        StringBuilder?[]? sb = null;
        foreach (var t in lod.Tags)
        {
            if (t is not SelectionTag s || IsProxyName(s.Name) || s.Name.StartsWith('-')) continue;
            int n = Math.Min(s.Points.Length, lod.PointCount);
            for (int p = 0; p < n; p++)
            {
                if (s.Points[p] == 0) continue;
                sb ??= new StringBuilder?[lod.PointCount];
                if (sb[p] is { } b) b.Append(", ").Append(s.Name);
                else sb[p] = new StringBuilder(s.Name);
            }
        }
        if (sb is null) return null;
        var labels = new string?[lod.PointCount];
        for (int i = 0; i < labels.Length; i++)
        {
            if (sb[i] is not { } b) continue;
            labels[i] = b.Length > 60 ? b.ToString(0, 58) + "…" : b.ToString();
        }
        return labels;
    }
}
