using System.Collections.Immutable;
using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>Double-precision 3-vector for geometry operations (RAW P3D coordinates unless stated otherwise).</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    public double this[int i] => i switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new IndexOutOfRangeException(),
    };

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    /// <summary>Unit vector, or null for a zero / non-finite vector.</summary>
    public Vec3? Normalized()
    {
        double l = Length;
        return l > 0 && double.IsFinite(l) ? this / l : null;
    }

    /// <summary>Point <paramref name="i"/> of an xyz array.</summary>
    public static Vec3 At(float[] xyz, long i) => new(xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]);

    public override string ToString() => $"({X:0.####}, {Y:0.####}, {Z:0.####})";
}

/// <summary>Compressed point → faces adjacency: the faces of point p are <c>Faces[Start[p] .. Start[p+1])</c>.</summary>
public sealed class PointFaces
{
    public required int[] Start { get; init; }
    public required int[] Faces { get; init; }

    public ReadOnlySpan<int> Of(long p) => Faces.AsSpan(Start[p], Start[p + 1] - Start[p]);

    /// <summary>Builds point → faces adjacency (only faces with faceMask[f] != 0 when a mask is given).</summary>
    public static PointFaces Build(Lod lod, byte[]? faceMask = null)
    {
        int n = lod.PointCount, fc = lod.FaceCount;
        var fp = lod.FacePoints;
        var nvs = lod.FaceVertCount;
        var start = new int[n + 1];
        for (int f = 0; f < fc; f++)
        {
            if (faceMask is not null && faceMask[f] == 0) continue;
            for (int k = 0; k < nvs[f]; k++)
            {
                uint p = fp[f * 4 + k];
                if (p < n) start[p + 1]++;
            }
        }
        for (int i = 0; i < n; i++) start[i + 1] += start[i];
        var faces = new int[start[n]];
        var fill = start.AsSpan(0, n).ToArray();
        for (int f = 0; f < fc; f++)
        {
            if (faceMask is not null && faceMask[f] == 0) continue;
            for (int k = 0; k < nvs[f]; k++)
            {
                uint p = fp[f * 4 + k];
                if (p < n) faces[fill[p]++] = f;
            }
        }
        return new PointFaces { Start = start, Faces = faces };
    }
}

/// <summary>
/// Description of a new face list built from the faces of a LOD (see <see cref="MeshMath.RebuildFaces"/>).
/// Per-face data (flags, texture, material, the face bytes of every named selection / state tag) comes from
/// <c>Src[d]</c> (combined with <c>Src2[d]</c> by maximum weight when given); per-slot data (normal index,
/// UV set 0 and every #UVSet#) from face <c>SlotFace[d*4+k]</c>, slot <c>SlotIdx[d*4+k]</c>.
/// Faces or slots with source -1 get the defaults (<see cref="Texture"/>/<see cref="Material"/>/<see cref="Flags"/>/<see cref="UV"/>).
/// </summary>
public sealed class FaceBuild
{
    public int Count { get; }
    public byte[] VertCount { get; }
    /// <summary>Point index per slot, stride 4.</summary>
    public uint[] Points { get; }
    public int[] Src { get; }
    public int[]? Src2 { get; set; }
    public int[] SlotFace { get; }
    public sbyte[] SlotIdx { get; }
    /// <summary>Stride 8: UVs (all UV sets) for slots whose SlotFace is -1.</summary>
    public float[]? UV { get; set; }
    /// <summary>Per-face defaults for faces whose Src is -1.</summary>
    public uint[]? Texture { get; set; }
    public uint[]? Material { get; set; }
    public uint[]? Flags { get; set; }

    /// <summary>Room for <paramref name="count"/> faces; every source is -1.</summary>
    public FaceBuild(int count)
    {
        Count = count;
        VertCount = new byte[count];
        Points = new uint[count * 4];
        Src = new int[count];
        Array.Fill(Src, -1);
        SlotFace = new int[count * 4];
        Array.Fill(SlotFace, -1);
        SlotIdx = new sbyte[count * 4];
        Array.Fill(SlotIdx, (sbyte)-1);
    }

    /// <summary>A build that keeps every face of <paramref name="lod"/> unchanged (callers then modify entries).</summary>
    public static FaceBuild Identity(Lod lod, int extra = 0)
    {
        int fc = lod.FaceCount;
        var b = new FaceBuild(fc + extra);
        lod.FaceVertCount.CopyTo(b.VertCount, 0);
        lod.FacePoints.CopyTo(b.Points, 0);
        for (int f = 0; f < fc; f++)
        {
            b.Src[f] = f;
            for (int k = 0; k < 4; k++)
            {
                b.SlotFace[f * 4 + k] = f;
                b.SlotIdx[f * 4 + k] = (sbyte)k;
            }
        }
        return b;
    }

    /// <summary>Writes face <paramref name="d"/> as a copy of face <paramref name="f"/> of <paramref name="lod"/> whose slots are taken in <paramref name="order"/>.</summary>
    public void CopyFace(int d, Lod lod, int f, ReadOnlySpan<int> order, int[]? pointMap = null)
    {
        VertCount[d] = (byte)order.Length;
        Src[d] = f;
        for (int k = 0; k < order.Length; k++)
        {
            int s = order[k];
            uint p = lod.FacePoints[f * 4 + s];
            Points[d * 4 + k] = pointMap is null ? p : (uint)pointMap[p];
            SlotFace[d * 4 + k] = f;
            SlotIdx[d * 4 + k] = (sbyte)s;
        }
    }

    /// <summary>Copies face <paramref name="f"/> unchanged into slot <paramref name="d"/>.</summary>
    public void CopyFace(int d, Lod lod, int f) => CopyFace(d, lod, f, lod.FaceVertCount[f] == 4 ? [0, 1, 2, 3] : [0, 1, 2]);
}

/// <summary>
/// Shared low-level helpers of the geometry operations (create, faces, points, select, extrude). Pure: never
/// writes into arrays of an input Lod.
/// <para>
/// Winding (docs/ARCHITECTURE.md §2): for an outward-facing face the right-handed normal of the stored vertex
/// order on RAW coordinates (cross(P1 − P0, P2 − P0) / Newell) points INTO the mesh;
/// <see cref="OutwardNormal"/> returns the negated (outward) direction.
/// </para>
/// </summary>
public static class MeshMath
{
    /// <summary>Right-handed Newell normal (length = 2 × area) of a polygon given by point indices; points INTO the mesh for P3D winding.</summary>
    public static Vec3 Newell(float[] positions, ReadOnlySpan<uint> pts)
    {
        double nx = 0, ny = 0, nz = 0;
        int n = pts.Length;
        for (int i = 0; i < n; i++)
        {
            long a = pts[i] * 3L, b = pts[(i + 1) % n] * 3L;
            double ax = positions[a], ay = positions[a + 1], az = positions[a + 2];
            double bx = positions[b], by = positions[b + 1], bz = positions[b + 2];
            nx += (ay - by) * (az + bz);
            ny += (az - bz) * (ax + bx);
            nz += (ax - bx) * (ay + by);
        }
        return new Vec3(nx, ny, nz);
    }

    /// <inheritdoc cref="Newell(float[], ReadOnlySpan{uint})"/>
    public static Vec3 Newell(float[] positions, ReadOnlySpan<int> pts)
    {
        Span<uint> u = stackalloc uint[pts.Length];
        for (int i = 0; i < pts.Length; i++) u[i] = (uint)pts[i];
        return Newell(positions, u);
    }

    /// <summary>Newell normal of face <paramref name="f"/> (see <see cref="Newell(float[], ReadOnlySpan{uint})"/>).</summary>
    public static Vec3 FaceNewell(Lod lod, int f, float[]? positions = null) =>
        Newell(positions ?? lod.Positions, lod.FacePoints.AsSpan(f * 4, lod.FaceVertCount[f]));

    /// <summary>Outward unit normal of face <paramref name="f"/> (P3D convention), or null for a degenerate face.</summary>
    public static Vec3? OutwardNormal(Lod lod, int f) => (-FaceNewell(lod, f)).Normalized();

    /// <summary>Centroid of the points in <paramref name="mask"/> (non-zero entries), or null when empty.</summary>
    public static Vec3? Centroid(float[] positions, byte[] mask)
    {
        double x = 0, y = 0, z = 0;
        int n = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0) continue;
            x += positions[i * 3];
            y += positions[i * 3 + 1];
            z += positions[i * 3 + 2];
            n++;
        }
        return n > 0 ? new Vec3(x / n, y / n, z / n) : null;
    }

    /// <summary>Axis-aligned bounds of the points in <paramref name="mask"/> (all points when null), or null when empty.</summary>
    public static (Vec3 Min, Vec3 Max)? Bounds(float[] positions, byte[]? mask)
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        int n = positions.Length / 3;
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            if (mask is not null && mask[i] == 0) continue;
            any = true;
            double x = positions[i * 3], y = positions[i * 3 + 1], z = positions[i * 3 + 2];
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
            if (z < z0) z0 = z;
            if (z > z1) z1 = z;
        }
        return any ? (new Vec3(x0, y0, z0), new Vec3(x1, y1, z1)) : null;
    }

    /// <summary>
    /// Least-squares plane through the points in <paramref name="mask"/>: unit normal = eigenvector of the smallest
    /// eigenvalue of the covariance matrix. Null for fewer than 3 points or when all points coincide; for collinear
    /// points any plane containing the line is returned.
    /// </summary>
    public static (Vec3 Normal, Vec3 Centroid)? FitPlane(float[] positions, byte[] mask)
    {
        if (Centroid(positions, mask) is not { } c) return null;
        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        int n = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0) continue;
            double x = positions[i * 3] - c.X, y = positions[i * 3 + 1] - c.Y, z = positions[i * 3 + 2] - c.Z;
            xx += x * x;
            xy += x * y;
            xz += x * z;
            yy += y * y;
            yz += y * z;
            zz += z * z;
            n++;
        }
        if (n < 3 || xx + yy + zz == 0) return null;
        var (values, vectors) = EigenSymmetric3([xx, xy, xz, xy, yy, yz, xz, yz, zz]);
        int k = 0;
        if (values[1] < values[k]) k = 1;
        if (values[2] < values[k]) k = 2;
        return new Vec3(vectors[k], vectors[3 + k], vectors[6 + k]).Normalized() is { } normal ? (normal, c) : null;
    }

    /// <summary>
    /// Eigen decomposition of a symmetric 3×3 matrix (row-major) by cyclic Jacobi rotations. Returns the eigenvalues
    /// and the eigenvectors as COLUMNS of <c>Vectors</c> (row-major 3×3).
    /// </summary>
    public static (double[] Values, double[] Vectors) EigenSymmetric3(double[] m)
    {
        var a = (double[])m.Clone();
        double[] v = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        ReadOnlySpan<(int P, int Q)> pairs = [(0, 1), (0, 2), (1, 2)];
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = a[1] * a[1] + a[2] * a[2] + a[5] * a[5];
            if (off < 1e-30 * (a[0] * a[0] + a[4] * a[4] + a[8] * a[8] + 1e-300)) break;
            foreach (var (p, q) in pairs)
            {
                double apq = a[p * 3 + q];
                if (Math.Abs(apq) < 1e-300) continue;
                double app = a[p * 3 + p], aqq = a[q * 3 + q];
                double theta = (aqq - app) / (2 * apq);
                double t = (theta >= 0 ? 1 : -1) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                double cs = 1 / Math.Sqrt(t * t + 1), sn = t * cs;
                for (int k = 0; k < 3; k++)
                {
                    double akp = a[k * 3 + p], akq = a[k * 3 + q];
                    a[k * 3 + p] = cs * akp - sn * akq;
                    a[k * 3 + q] = sn * akp + cs * akq;
                }
                for (int k = 0; k < 3; k++)
                {
                    double apk = a[p * 3 + k], aqk = a[q * 3 + k];
                    a[p * 3 + k] = cs * apk - sn * aqk;
                    a[q * 3 + k] = sn * apk + cs * aqk;
                }
                for (int k = 0; k < 3; k++)
                {
                    double vkp = v[k * 3 + p], vkq = v[k * 3 + q];
                    v[k * 3 + p] = cs * vkp - sn * vkq;
                    v[k * 3 + q] = sn * vkp + cs * vkq;
                }
            }
        }
        return ([a[0], a[4], a[8]], v);
    }

    /// <summary>Slot of point <paramref name="p"/> in face <paramref name="f"/>, or -1.</summary>
    public static int SlotOf(Lod lod, int f, uint p)
    {
        for (int k = 0; k < lod.FaceVertCount[f]; k++)
            if (lod.FacePoints[f * 4 + k] == p) return k;
        return -1;
    }

    /// <summary>
    /// Faces (other than <paramref name="f"/>, optionally restricted by <paramref name="faceMask"/>) containing the
    /// undirected edge {a, b}. Calls <paramref name="fn"/>(g, dir) with dir = +1 when g traverses a→b, −1 for b→a,
    /// 0 if a and b are both in g but not adjacent.
    /// </summary>
    public static void ForEachEdgeNeighbour(Lod lod, PointFaces adj, int f, uint a, uint b, Action<int, int> fn, byte[]? faceMask = null)
    {
        var fp = lod.FacePoints;
        for (int i = adj.Start[a]; i < adj.Start[a + 1]; i++)
        {
            int g = adj.Faces[i];
            if (g == f) continue;
            if (faceMask is not null && faceMask[g] == 0) continue;
            if (i > adj.Start[a] && adj.Faces[i - 1] == g) continue; // a face listing a point twice
            int nv = lod.FaceVertCount[g], ka = -1, kb = -1;
            for (int k = 0; k < nv; k++)
            {
                uint p = fp[g * 4 + k];
                if (p == a && ka < 0) ka = k;
                else if (p == b && kb < 0) kb = k;
            }
            if (ka < 0 || kb < 0) continue;
            fn(g, (ka + 1) % nv == kb ? 1 : (kb + 1) % nv == ka ? -1 : 0);
        }
    }

    /// <summary>The selection byte with the larger decoded weight (0 = unselected, 1 = full weight).</summary>
    public static byte MaxWeightByte(byte a, byte b)
    {
        if (a == 0) return b;
        if (b == 0) return a;
        return Weights.Table[a] >= Weights.Table[b] ? a : b;
    }

    /// <summary>Number of non-zero bytes.</summary>
    public static int CountSet(ReadOnlySpan<byte> a)
    {
        int n = 0;
        foreach (byte b in a)
            if (b != 0) n++;
        return n;
    }

    /// <summary>A mask with every entry set to 1.</summary>
    public static byte[] Ones(int count)
    {
        var o = new byte[count];
        Array.Fill(o, (byte)1);
        return o;
    }

    /// <summary>
    /// Replaces the faces of <paramref name="lod"/> by the faces described in <paramref name="b"/>, remapping every
    /// per-face and per-slot tag. Points and per-point data are unchanged. The unused slot 3 of a triangle copied
    /// from a triangle keeps its bytes (lossless round trips).
    /// </summary>
    public static Lod RebuildFaces(Lod lod, FaceBuild b)
    {
        int count = b.Count;
        var fa = FaceArrays.Allocate(count);
        var L = lod.FaceVertCount;
        for (int d = 0; d < count; d++)
        {
            int nv = b.VertCount[d], s = b.Src[d];
            fa.VertCount[d] = (byte)nv;
            if (s >= 0)
            {
                fa.Flags[d] = lod.FaceFlags[s];
                fa.Texture[d] = lod.FaceTexture[s];
                fa.Material[d] = lod.FaceMaterial[s];
            }
            else
            {
                fa.Flags[d] = b.Flags?[d] ?? 0;
                fa.Texture[d] = b.Texture?[d] ?? 0;
                fa.Material[d] = b.Material?[d] ?? 0;
            }
            for (int k = 0; k < 4; k++)
            {
                int o = d * 4 + k;
                if (k < nv)
                {
                    fa.Points[o] = b.Points[o];
                    int sf = b.SlotFace[o];
                    if (sf >= 0)
                    {
                        int si = sf * 4 + b.SlotIdx[o];
                        fa.Normals[o] = lod.FaceNormals[si];
                        fa.UV[o * 2] = lod.FaceUV[si * 2];
                        fa.UV[o * 2 + 1] = lod.FaceUV[si * 2 + 1];
                    }
                    else if (b.UV is not null)
                    {
                        fa.UV[o * 2] = b.UV[o * 2];
                        fa.UV[o * 2 + 1] = b.UV[o * 2 + 1];
                    }
                }
                else if (k == 3 && nv == 3 && s >= 0 && L[s] == 3)
                {
                    int si = s * 4 + 3;
                    fa.Points[o] = lod.FacePoints[si];
                    fa.Normals[o] = lod.FaceNormals[si];
                    fa.UV[o * 2] = lod.FaceUV[si * 2];
                    fa.UV[o * 2 + 1] = lod.FaceUV[si * 2 + 1];
                }
            }
        }

        byte[] RemapFaceBytes(byte[] src)
        {
            var o = new byte[count];
            var s2 = b.Src2;
            for (int d = 0; d < count; d++)
            {
                int s = b.Src[d];
                byte v = s >= 0 ? src[s] : (byte)0;
                if (s2 is not null && s2[d] >= 0) v = MaxWeightByte(v, src[s2[d]]);
                o[d] = v;
            }
            return o;
        }

        float[] RemapUV(float[] uv, bool stage0)
        {
            var o = new float[count * 8];
            for (int d = 0; d < count; d++)
            {
                int nv = b.VertCount[d], s = b.Src[d];
                for (int k = 0; k < 4; k++)
                {
                    int q = d * 4 + k;
                    if (k < nv)
                    {
                        int sf = b.SlotFace[q];
                        if (sf >= 0)
                        {
                            int si = sf * 4 + b.SlotIdx[q];
                            o[q * 2] = uv[si * 2];
                            o[q * 2 + 1] = uv[si * 2 + 1];
                        }
                        else if (b.UV is not null && stage0)
                        {
                            o[q * 2] = b.UV[q * 2];
                            o[q * 2 + 1] = b.UV[q * 2 + 1];
                        }
                    }
                    else if (k == 3 && nv == 3 && s >= 0 && L[s] == 3)
                    {
                        o[q * 2] = uv[(s * 4 + 3) * 2];
                        o[q * 2 + 1] = uv[(s * 4 + 3) * 2 + 1];
                    }
                }
            }
            return o;
        }

        var tags = lod.Tags.Select(t => t switch
        {
            SelectionTag s => s with { Faces = RemapFaceBytes(s.Faces) },
            StateTag s => s with { Faces = RemapFaceBytes(s.Faces) },
            UVSetTag u => u with { UV = RemapUV(u.UV, u.Stage == 0) },
            _ => t,
        }).ToImmutableArray();
        return (lod with { Tags = tags }).WithFaces(fa);
    }

    /// <summary>
    /// Appends points at <paramref name="positions"/> (xyz per point). <c>src[i]</c> (or -1) is a point whose flags,
    /// named-selection weights and animation offset the new point inherits. #Selected#/#Hidden#/#Lock# bytes of new
    /// points are 0. Faces are unchanged.
    /// </summary>
    public static Lod AddPoints(Lod lod, int[] src, float[] positions, bool copyMass = false, bool copySelections = true)
    {
        int n0 = lod.PointCount, add = src.Length;
        if (add == 0) return lod;
        var pos = new float[(n0 + add) * 3];
        lod.Positions.CopyTo(pos, 0);
        Array.Copy(positions, 0, pos, n0 * 3, add * 3);
        var flags = new uint[n0 + add];
        lod.PointFlags.CopyTo(flags, 0);
        for (int i = 0; i < add; i++)
            flags[n0 + i] = src[i] >= 0 ? lod.PointFlags[src[i]] & ~(PointFlags.Hidden | PointFlags.Locked) : 0;

        byte[] Grow(byte[] bytes, bool copy)
        {
            var o = new byte[n0 + add];
            bytes.CopyTo(o, 0);
            if (copy)
                for (int i = 0; i < add; i++)
                    if (src[i] >= 0) o[n0 + i] = bytes[src[i]];
            return o;
        }

        var tags = lod.Tags.Select(t =>
        {
            switch (t)
            {
                case SelectionTag s:
                    return s with { Points = Grow(s.Points, copySelections) };
                case StateTag s:
                    return s with { Points = Grow(s.Points, false) };
                case MassTag m:
                {
                    var mass = new float[n0 + add];
                    m.Mass.CopyTo(mass, 0);
                    if (copyMass)
                        for (int i = 0; i < add; i++)
                            if (src[i] >= 0) mass[n0 + i] = m.Mass[src[i]];
                    return m with { Mass = mass };
                }
                case AnimationTag a:
                {
                    var p = new float[(n0 + add) * 3];
                    a.Positions.CopyTo(p, 0);
                    for (int i = 0; i < add; i++)
                    {
                        int s = src[i];
                        for (int c = 0; c < 3; c++)
                        {
                            float np = positions[i * 3 + c];
                            p[(n0 + i) * 3 + c] = s >= 0 ? a.Positions[s * 3 + c] + (np - lod.Positions[s * 3 + c]) : np;
                        }
                    }
                    return (Tag)(a with { Positions = p });
                }
                default:
                    return t;
            }
        }).ToImmutableArray();
        return lod with { Positions = pos, PointFlags = flags, Tags = tags };
    }

    /// <summary>
    /// <see cref="Structure.Append"/> with a fix for UV set 0: when the target already has faces but no stage-0
    /// #UVSet#, a stage-0 set carried by the fragment is folded into the fragment's face UVs instead of creating a
    /// tag whose entries for the existing faces would be zero (and override their UVs).
    /// </summary>
    public static Lod AppendSafe(Lod lod, Fragment frag, bool selectNew = true)
    {
        var f = frag;
        if (lod.FaceCount > 0 && lod.UVSet(0) is null)
        {
            foreach (var (stage, uv) in frag.UVSets)
            {
                if (stage != 0) continue;
                f = frag with { Faces = frag.Faces with { UV = uv }, UVSets = frag.UVSets.Where(u => u.Stage != 0).ToImmutableArray() };
                break;
            }
        }
        return Structure.Append(lod, f, selectNew);
    }

    // ---- clipboard serialization -------------------------------------------------------------------------

    /// <summary>
    /// Serializes a fragment as a one-LOD MLOD file (the clipboard format shared between P3D Forge windows): the
    /// fragment appended to an empty LOD of the given resolution, without #Selected#.
    /// </summary>
    public static byte[] FragmentToMlod(Fragment frag, float resolution = 0)
    {
        var lod = Structure.Append(Lod.Empty(resolution), frag, selectNew: false);
        return Mlod.Write(new P3DModel([lod]));
    }

    /// <summary>Reads clipboard bytes back into a fragment (the whole first LOD). Throws on invalid data.</summary>
    public static (Fragment Fragment, float Resolution) MlodToFragment(byte[] bytes)
    {
        var model = Mlod.Read(bytes);
        if (model.Lods.Length == 0) throw new InvalidDataException("Clipboard data contains no LOD");
        var lod = model.Lods[0];
        return (Structure.Extract(lod, Ones(lod.PointCount), Ones(lod.FaceCount)), lod.Resolution);
    }

    /// <summary>Human-readable one-line summary of a fragment (plain-text clipboard companion).</summary>
    public static string FragmentSummary(Fragment frag)
    {
        var names = frag.Selections.Select(s => s.Name).ToList();
        string shown = string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", …" : "");
        return $"P3D Forge geometry: {frag.PointCount} points, {frag.FaceCount} faces"
            + (names.Count > 0 ? $", {names.Count} named selections ({shown})" : "");
    }
}
