using System.Collections.Immutable;
using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

/// <summary>Shared fixtures and checks for the geometry-operation tests.</summary>
internal static class OpsTestHelpers
{
    public static Lod RoundTrip(Lod lod) => Mlod.Read(Mlod.Write(new P3DModel([lod])), null).Lods[0];

    /// <summary>Integrity + valid normal indices + byte-exact write → read → write.</summary>
    public static void CheckLod(Lod lod)
    {
        Mlod.AssertIntegrity(lod, "test");
        int nn = lod.NormalCount;
        for (int f = 0; f < lod.FaceCount; f++)
            for (int k = 0; k < lod.FaceVertCount[f]; k++)
            {
                uint ni = lod.FaceNormals[f * 4 + k];
                if (nn > 0) Assert.True(ni < nn, $"face {f} slot {k} normal index {ni} out of range ({nn})");
                else Assert.Equal(0u, ni);
            }
        var bytes = Mlod.Write(new P3DModel([lod]));
        var back = Mlod.Read(bytes, null).Lods[0];
        Assert.Equal(bytes, Mlod.Write(new P3DModel([back])));
    }

    /// <summary>Right-handed signed volume over the faces (quads split 0-1-2 / 0-2-3), RAW coords, stored order.</summary>
    public static double SignedVolume(Lod lod, byte[]? faces = null)
    {
        var P = lod.Positions;
        double v = 0;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faces is not null && faces[f] == 0) continue;
            int nv = lod.FaceVertCount[f];
            long a = lod.FacePoints[f * 4] * 3L;
            for (int k = 1; k + 1 < nv; k++)
            {
                long b = lod.FacePoints[f * 4 + k] * 3L, c = lod.FacePoints[f * 4 + k + 1] * 3L;
                v += P[a] * ((double)P[b + 1] * P[c + 2] - (double)P[b + 2] * P[c + 1])
                   + P[a + 1] * ((double)P[b + 2] * P[c] - (double)P[b] * P[c + 2])
                   + P[a + 2] * ((double)P[b] * P[c + 1] - (double)P[b + 1] * P[c]);
            }
        }
        return v / 6;
    }

    /// <summary>Every undirected edge is used exactly twice, in opposite directions (closed, consistently oriented manifold).</summary>
    public static bool IsClosedConsistent(Lod lod)
    {
        var map = new HashSet<(uint, uint)>();
        for (int f = 0; f < lod.FaceCount; f++)
        {
            int nv = lod.FaceVertCount[f];
            for (int k = 0; k < nv; k++)
                if (!map.Add((lod.FacePoints[f * 4 + k], lod.FacePoints[f * 4 + (k + 1) % nv]))) return false;
        }
        foreach (var (a, b) in map)
            if (!map.Contains((b, a))) return false;
        return true;
    }

    /// <summary>Deterministic pseudo-random mask (xorshift32).</summary>
    public static byte[] RandomMask(int n, uint seed, double density = 0.5)
    {
        var m = new byte[n];
        uint s = seed == 0 ? 1 : seed;
        for (int i = 0; i < n; i++)
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            m[i] = s / (double)uint.MaxValue < density ? (byte)1 : (byte)0;
        }
        return m;
    }

    public static byte[] MaskOf(int n, params int[] idx)
    {
        var m = new byte[n];
        foreach (int i in idx) m[i] = 1;
        return m;
    }

    public static byte[] All(int n) => MeshMath.Ones(n);

    public static int[] Indices(byte[] a)
    {
        var o = new List<int>();
        for (int i = 0; i < a.Length; i++)
            if (a[i] != 0) o.Add(i);
        return [.. o];
    }

    public static int Count(byte[] a) => MeshMath.CountSet(a);

    public static uint[] FacePts(Lod lod, int f) => lod.FacePoints.AsSpan(f * 4, lod.FaceVertCount[f]).ToArray();

    public static float[] FaceUVs(Lod lod, int f, float[]? uv = null) => (uv ?? lod.FaceUV).AsSpan(f * 8, lod.FaceVertCount[f] * 2).ToArray();

    public static float[] Pos(Lod lod, int p) => lod.Positions.AsSpan(p * 3, 3).ToArray();

    public static Lod WithMask(Lod lod, int[] points, int[]? faces = null) =>
        Selection.WithSelected(lod, new SelectionMask(MaskOf(lod.PointCount, points), MaskOf(lod.FaceCount, faces ?? [])));

    /// <summary>Unit cube with every tag kind (P3D winding, two textures, UV sets 0 and 1, weights, mass, sharp edges, animation).</summary>
    public static Lod RichCube()
    {
        float[] P = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1];
        uint[] Q = [0, 1, 2, 3, 5, 4, 7, 6, 4, 0, 3, 7, 1, 5, 6, 2, 3, 2, 6, 7, 4, 5, 1, 0];
        var uv = new float[48];
        for (int f = 0; f < 6; f++)
        {
            float[] u = [0, 0, 1, 0, 1, 1, (float)(f / 6.0), 1];
            u.CopyTo(uv, f * 8);
        }
        var lod = Lod.Empty((float)1e13) with
        {
            Positions = P,
            PointFlags = new uint[8],
            FaceVertCount = [4, 4, 4, 4, 4, 4],
            FacePoints = Q,
            FaceNormals = new uint[24],
            FaceUV = uv,
            FaceFlags = new uint[6],
            FaceTexture = new uint[6],
            FaceMaterial = new uint[6],
        };
        lod = Structure.SetFaceTextures(lod, [1, 1, 0, 0, 1, 0], @"dz\a_co.paa", @"dz\a.rvmat");
        var anim = P.Select(v => v + 0.1f).ToArray();
        lod = lod with
        {
            Tags =
            [
                new MassTag(1, [1, 2, 3, 4, 5, 6, 7, 8]),
                new UVSetTag(1, 0, (float[])lod.FaceUV.Clone()),
                new UVSetTag(1, 1, lod.FaceUV.Select(v => v * 2 + 0.25f).ToArray()),
                new PropertyTag(1, "autocenter", "0", null),
                new SelectionTag(1, "Component01", All(8), All(6)),
                new SelectionTag(1, "top", [0, 0, 1, 1, 0, 0, 128, 255], [0, 0, 0, 0, 1, 0]),
                new SharpEdgesTag(1, [0, 1, 6, 7, 2, 3]),
                new AnimationTag(1, 0.5f, anim),
            ],
        };
        return Structure.RecalcNormals(lod);
    }

    /// <summary>Flat grid in XZ (facing +Y) with nx × nz quads, UVs, a texture, a #UVSet# 0 and a named selection "row0" of the first row.</summary>
    public static Lod GridLod(int nx, int nz)
    {
        var pts = new List<float>();
        for (int k = 0; k <= nz; k++)
            for (int i = 0; i <= nx; i++) pts.AddRange([i, 0, k]);
        var fv = new List<uint>();
        var uv = new List<float>();
        uint Idx(int i, int k) => (uint)(k * (nx + 1) + i);
        for (int k = 0; k < nz; k++)
            for (int i = 0; i < nx; i++)
            {
                // outward +Y → stored RH normal must point -Y: (i,k), (i+1,k), (i+1,k+1), (i,k+1)
                fv.AddRange([Idx(i, k), Idx(i + 1, k), Idx(i + 1, k + 1), Idx(i, k + 1)]);
                uv.AddRange([(float)i / nx, (float)k / nz, (float)(i + 1) / nx, (float)k / nz, (float)(i + 1) / nx, (float)(k + 1) / nz, (float)i / nx, (float)(k + 1) / nz]);
            }
        int nf = nx * nz, np = pts.Count / 3;
        var lod = Lod.Empty(0) with
        {
            Positions = [.. pts],
            PointFlags = new uint[np],
            FaceVertCount = Enumerable.Repeat((byte)4, nf).ToArray(),
            FacePoints = [.. fv],
            FaceNormals = new uint[nf * 4],
            FaceUV = [.. uv],
            FaceFlags = new uint[nf],
            FaceTexture = new uint[nf],
            FaceMaterial = new uint[nf],
        };
        lod = Structure.SetFaceTextures(lod, All(nf), @"grid\a_co.paa", @"grid\a.rvmat");
        var row = new byte[np];
        for (int i = 0; i <= nx; i++) row[i] = 1;
        var rowF = new byte[nf];
        for (int i = 0; i < nx; i++) rowF[i] = 1;
        lod = lod with
        {
            Tags = [new UVSetTag(1, 0, (float[])lod.FaceUV.Clone()), new SelectionTag(1, "row0", row, rowF)],
        };
        return Structure.RecalcNormals(lod);
    }
}
