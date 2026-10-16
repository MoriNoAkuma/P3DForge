using System.Numerics;
using P3DForge.Core.Model;

namespace P3DForge.App.Viewport;

/// <summary>
/// Point visibility for one view: triangles of the visible faces are binned into screen tiles so a point only
/// ray-tests the triangles overlapping its tile (instead of every face of the LOD per point).
/// Triangles that cross the camera plane are tested for every point.
/// </summary>
public sealed class Occlusion
{
    const int Tile = 12;
    readonly IViewHandle view;
    readonly float[] rp;
    readonly uint[] tri;            // 3 point indices per triangle
    readonly int cols, rows;
    readonly int[] tileStart;       // CSR: tile → [tileStart[t], tileStart[t+1]) in tileTris
    readonly int[] tileTris;
    readonly int[] global;          // triangles with clipped vertices
    readonly float[] proj;          // view-local pixels per point (NaN = clipped)

    /// <param name="raw">RAW positions (3 per point) as drawn.</param>
    /// <param name="hiddenFaces">non-zero = face skipped.</param>
    public Occlusion(IViewHandle view, Lod lod, float[] raw, byte[] hiddenFaces)
    {
        this.view = view;
        int n = lod.PointCount;
        rp = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            rp[i * 3] = raw[i * 3];
            rp[i * 3 + 1] = raw[i * 3 + 1];
            rp[i * 3 + 2] = -raw[i * 3 + 2];
        }
        proj = new float[n * 2];
        view.ProjectRaw(raw.AsSpan(0, n * 3), proj);

        int tris = 0;
        for (int f = 0; f < lod.FaceCount; f++)
            if (hiddenFaces[f] == 0) tris += lod.FaceVertCount[f] - 2;
        tri = new uint[tris * 3];
        int t = 0;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (hiddenFaces[f] != 0) continue;
            for (int k = 1; k + 1 < lod.FaceVertCount[f]; k++)
            {
                tri[t * 3] = lod.FacePoints[f * 4];
                tri[t * 3 + 1] = lod.FacePoints[f * 4 + k];
                tri[t * 3 + 2] = lod.FacePoints[f * 4 + k + 1];
                t++;
            }
        }

        double w = Math.Max(1, view.Rect.Width), h = Math.Max(1, view.Rect.Height);
        cols = (int)Math.Ceiling(w / Tile);
        rows = (int)Math.Ceiling(h / Tile);
        // Two passes over the triangles' screen bounding boxes: count per tile, then fill (CSR layout).
        var start = new int[cols * rows + 1];
        var g = new List<int>();
        int[]? cursor = null;
        int[] list = [];
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                for (int i = 1; i < start.Length; i++) start[i] += start[i - 1];
                cursor = (int[])start.Clone();
                list = new int[start[^1]];
            }
            for (int i = 0; i < tris; i++)
            {
                uint a = tri[i * 3], b = tri[i * 3 + 1], c = tri[i * 3 + 2];
                float ax = proj[a * 2], ay = proj[a * 2 + 1], bx = proj[b * 2], by = proj[b * 2 + 1], cx = proj[c * 2], cy = proj[c * 2 + 1];
                if (float.IsNaN(ax) || float.IsNaN(bx) || float.IsNaN(cx))
                {
                    if (pass == 0) g.Add(i);
                    continue;
                }
                float minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx));
                float minY = Math.Min(ay, Math.Min(by, cy)), maxY = Math.Max(ay, Math.Max(by, cy));
                if (maxX < 0 || maxY < 0 || minX > w || minY > h) continue;
                int x0 = Math.Max(0, (int)(minX / Tile)), x1 = Math.Min(cols - 1, (int)(maxX / Tile));
                int y0 = Math.Max(0, (int)(minY / Tile)), y1 = Math.Min(rows - 1, (int)(maxY / Tile));
                for (int ty = y0; ty <= y1; ty++)
                    for (int tx = x0; tx <= x1; tx++)
                    {
                        int tile = ty * cols + tx;
                        if (pass == 0) start[tile + 1]++;
                        else list[cursor![tile]++] = i;
                    }
            }
        }
        tileStart = start;
        tileTris = list;
        global = [.. g];
    }

    /// <summary>View-local projection of a point (NaN when clipped).</summary>
    public (float X, float Y) Projected(int point) => (proj[point * 2], proj[point * 2 + 1]);

    /// <summary>True when a visible face lies between the camera and the point.</summary>
    public bool IsOccluded(int point)
    {
        float px = proj[point * 2], py = proj[point * 2 + 1];
        if (float.IsNaN(px)) return true;
        var target = new Vector3(rp[point * 3], rp[point * 3 + 1], rp[point * 3 + 2]);
        var o = view.IsOrthographic ? target - view.Forward * 1e4f : view.Eye;
        var dir = Vector3.Normalize(target - o);
        float dist = Vector3.Distance(o, target);
        float eps = Math.Max(dist * 1e-4f, 1e-4f);
        int tx = Math.Clamp((int)(px / Tile), 0, cols - 1), ty = Math.Clamp((int)(py / Tile), 0, rows - 1);
        int tile = ty * cols + tx;
        for (int k = tileStart[tile]; k < tileStart[tile + 1]; k++)
        {
            int i = tileTris[k];
            uint a = tri[i * 3], b = tri[i * 3 + 1], c = tri[i * 3 + 2];
            // cheap 2D rejection: the point must lie inside the triangle's screen bounding box
            float ax = proj[a * 2], bx = proj[b * 2], cx = proj[c * 2];
            if (px < Math.Min(ax, Math.Min(bx, cx)) || px > Math.Max(ax, Math.Max(bx, cx))) continue;
            float ay = proj[a * 2 + 1], by = proj[b * 2 + 1], cy = proj[c * 2 + 1];
            if (py < Math.Min(ay, Math.Min(by, cy)) || py > Math.Max(ay, Math.Max(by, cy))) continue;
            if (Hits(i, (uint)point, o, dir, dist - eps)) return true;
        }
        foreach (int i in global)
            if (Hits(i, (uint)point, o, dir, dist - eps)) return true;
        return false;
    }

    bool Hits(int i, uint point, Vector3 o, Vector3 dir, float maxT)
    {
        uint a = tri[i * 3], b = tri[i * 3 + 1], c = tri[i * 3 + 2];
        if (a == point || b == point || c == point) return false;
        float t = RayTriangle(o, dir, P(a), P(b), P(c));
        return t > 0 && t < maxT;
    }

    Vector3 P(uint i) => new(rp[i * 3], rp[i * 3 + 1], rp[i * 3 + 2]);

    /// <summary>Möller–Trumbore; returns the ray parameter or -1.</summary>
    public static float RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return -1;
        float inv = 1 / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return -1;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return -1;
        return Vector3.Dot(e2, q) * inv;
    }
}
