using System.Numerics;
using P3DForge.Core.Model;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>One draw range: triangles sharing a face texture and a face material (contiguous in the vertex buffer).</summary>
public readonly record struct MeshRange(int First, int Count, string Texture, string Material);

/// <summary>
/// CPU triangle data of a LOD in RENDER space. Vertex layout (<see cref="Stride"/> floats): position (3),
/// normal (3), UV set 0 (2), tangent xyz + handedness (4), UV set 1 (2; = set 0 when the LOD has no #UVSet# 1).
/// Pure CPU work on immutable data — safe to build on a background thread.
/// </summary>
public sealed class MeshData
{
    public const int Stride = 14;
    public float[] Geom = [];
    public int Vertices;
    public MeshRange[] Ranges = [];
    /// <summary>Triangle → face index.</summary>
    public int[] TriangleFace = [];
    /// <summary>Vertex → point index.</summary>
    public int[] VertPoint = [];
    /// <summary>RENDER positions per point.</summary>
    public float[] RenderPositions = [];
    public Vector3 Min, Max;
    public float NormalSign = 1;
    /// <summary>Unique edges (point indices), when requested.</summary>
    public uint[] EdgeA = [], EdgeB = [];
    public int Triangles => Vertices / 3;
}

public static class MeshBuilder
{
    /// <summary>
    /// Triangulates <paramref name="lod"/> into draw ranges sorted by (texture, material).
    /// <paramref name="raw"/> = RAW positions (preview or stored); <paramref name="flatNormals"/> ignores the stored
    /// normals (live previews); faces with <paramref name="skipFaces"/>[f] != 0 are left out; points with
    /// <paramref name="boundsSkipPoints"/>[p] != 0 do not count for the bounds.
    /// </summary>
    public static MeshData Build(Lod lod, float[] raw, bool flatNormals, byte[]? skipFaces, byte[]? boundsSkipPoints, bool edges)
    {
        var m = new MeshData();
        int n = lod.PointCount;
        var rp = new float[n * 3];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < n; i++)
        {
            float x = raw[i * 3], y = raw[i * 3 + 1], z = -raw[i * 3 + 2];
            rp[i * 3] = x;
            rp[i * 3 + 1] = y;
            rp[i * 3 + 2] = z;
            if (boundsSkipPoints is not null && i < boundsSkipPoints.Length && boundsSkipPoints[i] != 0) continue;
            if (!float.IsFinite(x + y + z)) continue;
            var p = new Vector3(x, y, z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        if (min.X > max.X) min = max = Vector3.Zero;
        m.RenderPositions = rp;
        m.Min = min;
        m.Max = max;

        int nf = lod.FaceCount;
        int ns = lod.Strings.Length;
        var faceTex = lod.FaceTexture;
        var faceMat = lod.FaceMaterial;
        var vertCount = lod.FaceVertCount;
        var facePoints = lod.FacePoints;

        // Group faces by (texture, material) → contiguous ranges, ordered by the strings (deterministic).
        var groupOf = new Dictionary<ulong, int>();
        var groupKeys = new List<(uint Tex, uint Mat)>();
        var faceGroup = new int[nf];
        var triPerGroup = new List<int>();
        int tris = 0;
        for (int f = 0; f < nf; f++)
        {
            if (skipFaces is not null && skipFaces[f] != 0)
            {
                faceGroup[f] = -1;
                continue;
            }
            uint tex = f < faceTex.Length ? faceTex[f] : 0, mat = f < faceMat.Length ? faceMat[f] : 0;
            if (tex >= ns) tex = 0;
            if (mat >= ns) mat = 0;
            ulong key = ((ulong)tex << 32) | mat;
            if (!groupOf.TryGetValue(key, out int g))
            {
                g = groupKeys.Count;
                groupOf[key] = g;
                groupKeys.Add((tex, mat));
                triPerGroup.Add(0);
            }
            faceGroup[f] = g;
            int t = Math.Max(0, vertCount[f] - 2);
            triPerGroup[g] += t;
            tris += t;
        }
        string S(uint i) => ns == 0 ? "" : lod.Strings[(int)i] ?? "";
        var order = new int[groupKeys.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = string.CompareOrdinal(S(groupKeys[a].Tex), S(groupKeys[b].Tex));
            return c != 0 ? c : string.CompareOrdinal(S(groupKeys[a].Mat), S(groupKeys[b].Mat));
        });
        var start = new int[groupKeys.Count];
        var ranges = new List<MeshRange>(order.Length);
        for (int i = 0, acc = 0; i < order.Length; i++)
        {
            int g = order[i];
            start[g] = acc;
            if (triPerGroup[g] > 0) ranges.Add(new MeshRange(acc * 3, triPerGroup[g] * 3, S(groupKeys[g].Tex), S(groupKeys[g].Mat)));
            acc += triPerGroup[g];
        }
        m.Ranges = [.. ranges];

        // Stored normals are used when present (sign voted against the geometric normals); flat otherwise.
        var normals = lod.Normals;
        var faceNormals = lod.FaceNormals;
        bool useFile = !flatNormals && normals.Length >= 3 && faceNormals.Length >= nf * 4;
        float sign = 1;
        if (useFile)
        {
            int agree = 0, disagree = 0;
            for (int f = 0; f < nf && agree + disagree < 20000; f++)
            {
                var g = FaceNormalRaw(lod, raw, f);
                if (g == Vector3.Zero) continue;
                uint ni = faceNormals[f * 4];
                if (ni * 3 + 2 >= normals.Length) continue;
                float d = g.X * normals[ni * 3] + g.Y * normals[ni * 3 + 1] + g.Z * normals[ni * 3 + 2];
                if (d > 0) agree++;
                else if (d < 0) disagree++;
            }
            sign = disagree > agree ? -1 : 1;
        }
        m.NormalSign = sign;

        var uv0 = lod.FaceUV;
        var uv1 = lod.UVSet(1)?.UV;
        if (uv1 is not null && uv1.Length < nf * 8) uv1 = null;
        const int st = MeshData.Stride;
        var geom = new float[tris * 3 * st];
        var vp = new int[tris * 3];
        var triFace = new int[tris];
        var corner = new int[tris * 3]; // vertex → f*4+k (normal slot) for tangent accumulation
        var fill = (int[])start.Clone();
        for (int f = 0; f < nf; f++)
        {
            int g = faceGroup[f];
            if (g < 0) continue;
            int nv = vertCount[f];
            var flat = Vector3.Zero;
            for (int k = 1; k + 1 < nv; k++)
            {
                int tri = fill[g]++;
                triFace[tri] = f;
                uint a = facePoints[f * 4], b = facePoints[f * 4 + k], c = facePoints[f * 4 + k + 1];
                if (!useFile || flat == Vector3.Zero)
                {
                    var pa = P(rp, a);
                    var nrm = Vector3.Cross(P(rp, b) - pa, P(rp, c) - pa);
                    flat = nrm.LengthSquared() > 0 ? Vector3.Normalize(nrm) : Vector3.UnitY;
                }
                int o = tri * 3;
                Corner(geom, vp, corner, o, lod, rp, uv0, uv1, f, 0, a, flat, useFile, sign);
                Corner(geom, vp, corner, o + 1, lod, rp, uv0, uv1, f, k, b, flat, useFile, sign);
                Corner(geom, vp, corner, o + 2, lod, rp, uv0, uv1, f, k + 1, c, flat, useFile, sign);
            }
        }
        ComputeTangents(geom, tris, corner, useFile ? faceNormals : null, useFile ? normals.Length / 3 : 0);
        m.Geom = geom;
        m.Vertices = tris * 3;
        m.VertPoint = vp;
        m.TriangleFace = triFace;
        if (edges) BuildEdges(m, lod, skipFaces);
        return m;
    }

    static Vector3 FaceNormalRaw(Lod lod, float[] raw, int f)
    {
        uint a = lod.FacePoints[f * 4], b = lod.FacePoints[f * 4 + 1], c = lod.FacePoints[f * 4 + 2];
        var pa = new Vector3(raw[a * 3], raw[a * 3 + 1], raw[a * 3 + 2]);
        var pb = new Vector3(raw[b * 3], raw[b * 3 + 1], raw[b * 3 + 2]);
        var pc = new Vector3(raw[c * 3], raw[c * 3 + 1], raw[c * 3 + 2]);
        // RAW is left-handed: the outward normal of the stored winding is -(b-a)×(c-a).
        return -Vector3.Cross(pb - pa, pc - pa);
    }

    static Vector3 P(float[] rp, uint i) => new(rp[i * 3], rp[i * 3 + 1], rp[i * 3 + 2]);

    static void Corner(float[] geom, int[] vp, int[] corner, int vertex, Lod lod, float[] rp, float[] uv0, float[]? uv1,
        int f, int k, uint point, Vector3 flat, bool useFile, float sign)
    {
        var nrm = flat;
        if (useFile)
        {
            uint ni = lod.FaceNormals[f * 4 + k];
            if (ni * 3 + 2 < lod.Normals.Length)
            {
                var fn = new Vector3(lod.Normals[ni * 3], lod.Normals[ni * 3 + 1], -lod.Normals[ni * 3 + 2]) * sign;
                if (fn.LengthSquared() > 1e-12f) nrm = Vector3.Normalize(fn);
            }
        }
        int o = vertex * MeshData.Stride;
        geom[o] = rp[point * 3];
        geom[o + 1] = rp[point * 3 + 1];
        geom[o + 2] = rp[point * 3 + 2];
        geom[o + 3] = nrm.X;
        geom[o + 4] = nrm.Y;
        geom[o + 5] = nrm.Z;
        int u = f * 8 + k * 2;
        float s0 = u + 1 < uv0.Length ? uv0[u] : 0, t0 = u + 1 < uv0.Length ? uv0[u + 1] : 0;
        geom[o + 6] = s0;
        geom[o + 7] = t0;
        // tangent (o + 8 .. o + 11) filled by ComputeTangents
        geom[o + 12] = uv1 is null ? s0 : uv1[u];
        geom[o + 13] = uv1 is null ? t0 : uv1[u + 1];
        vp[vertex] = (int)point;
        corner[vertex] = f * 4 + k;
    }

    /// <summary>
    /// Per-vertex tangents from UV set 0 (dP/du), area-weighted and shared between corners with the same stored
    /// normal and the same UV handedness; orthogonalized against each vertex normal. w = handedness (bitangent =
    /// w · cross(n, t) points along +v).
    /// </summary>
    static void ComputeTangents(float[] g, int tris, int[] corner, uint[]? faceNormals, int normalCount)
    {
        const int st = MeshData.Stride;
        var triT = new Vector3[tris];
        var triW = new sbyte[tris];
        for (int t = 0; t < tris; t++)
        {
            int a = t * 3 * st, b = a + st, c = b + st;
            var p0 = new Vector3(g[a], g[a + 1], g[a + 2]);
            var e1 = new Vector3(g[b], g[b + 1], g[b + 2]) - p0;
            var e2 = new Vector3(g[c], g[c + 1], g[c + 2]) - p0;
            float du1 = g[b + 6] - g[a + 6], dv1 = g[b + 7] - g[a + 7];
            float du2 = g[c + 6] - g[a + 6], dv2 = g[c + 7] - g[a + 7];
            float det = du1 * dv2 - du2 * dv1;
            var area = Vector3.Cross(e1, e2).Length();
            if (MathF.Abs(det) < 1e-12f || area <= 0)
            {
                triW[t] = 0;
                continue;
            }
            float r = 1f / det;
            var tu = (e1 * dv2 - e2 * dv1) * r;  // dP/du
            var tv = (e2 * du1 - e1 * du2) * r;  // dP/dv
            float tl = tu.Length();
            if (!(tl > 1e-12f) || !float.IsFinite(tl))
            {
                triW[t] = 0;
                continue;
            }
            var faceN = Vector3.Cross(e1, e2);
            triT[t] = tu / tl * area;
            triW[t] = (sbyte)(Vector3.Dot(Vector3.Cross(faceN, tu), tv) < 0 ? -1 : 1);
        }

        // Accumulate per (stored normal index, handedness) so smooth surfaces get continuous tangents.
        Vector3[]? acc = null;
        if (faceNormals is not null && normalCount > 0)
        {
            acc = new Vector3[normalCount * 2];
            for (int t = 0; t < tris; t++)
            {
                if (triW[t] == 0) continue;
                for (int k = 0; k < 3; k++)
                {
                    uint ni = faceNormals[corner[t * 3 + k]];
                    if (ni >= normalCount) continue;
                    acc[ni * 2 + (triW[t] < 0 ? 1 : 0)] += triT[t];
                }
            }
        }
        for (int t = 0; t < tris; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                int v = t * 3 + k, o = v * st;
                var nrm = new Vector3(g[o + 3], g[o + 4], g[o + 5]);
                var tan = triT[t];
                float w = triW[t] == 0 ? 1 : triW[t];
                if (acc is not null && triW[t] != 0)
                {
                    uint ni = faceNormals![corner[v]];
                    if (ni < normalCount) tan = acc[ni * 2 + (triW[t] < 0 ? 1 : 0)];
                }
                tan -= nrm * Vector3.Dot(nrm, tan);
                if (tan.LengthSquared() < 1e-16f)
                {
                    // Any vector perpendicular to the normal (no UVs → normal maps are meaningless anyway).
                    tan = MathF.Abs(nrm.X) < 0.9f ? Vector3.Cross(nrm, Vector3.UnitX) : Vector3.Cross(nrm, Vector3.UnitY);
                    if (tan.LengthSquared() < 1e-16f) tan = Vector3.UnitX;
                }
                tan = Vector3.Normalize(tan);
                g[o + 8] = tan.X;
                g[o + 9] = tan.Y;
                g[o + 10] = tan.Z;
                g[o + 11] = w;
            }
        }
    }

    /// <summary>Unique edges of the drawn faces (sort + unique; a HashSet&lt;ulong&gt; degenerates on nearby indices).</summary>
    static void BuildEdges(MeshData m, Lod lod, byte[]? skipFaces)
    {
        int nf = lod.FaceCount;
        int total = 0;
        for (int f = 0; f < nf; f++)
            if (skipFaces is null || skipFaces[f] == 0) total += lod.FaceVertCount[f];
        var keys = new ulong[total];
        int c = 0;
        for (int f = 0; f < nf; f++)
        {
            if (skipFaces is not null && skipFaces[f] != 0) continue;
            int nv = lod.FaceVertCount[f];
            for (int k = 0; k < nv; k++)
            {
                uint a = lod.FacePoints[f * 4 + k], b = lod.FacePoints[f * 4 + (k + 1) % nv];
                keys[c++] = a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;
            }
        }
        Array.Sort(keys, 0, c);
        int u = 0;
        for (int i = 0; i < c; i++)
            if (u == 0 || keys[i] != keys[u - 1]) keys[u++] = keys[i];
        var ea = new uint[u];
        var eb = new uint[u];
        for (int i = 0; i < u; i++)
        {
            ea[i] = (uint)(keys[i] >> 32);
            eb[i] = (uint)keys[i];
        }
        m.EdgeA = ea;
        m.EdgeB = eb;
    }
}
