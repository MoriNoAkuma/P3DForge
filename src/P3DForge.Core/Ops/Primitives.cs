using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>Where a primitive is placed relative to its placement point (Object Builder "Center To Pin" / "Align bottom").</summary>
public enum PrimitiveAlign
{
    Center,
    /// <summary>Lowest Y at the placement point.</summary>
    Bottom,
    /// <summary>Highest Y at the placement point.</summary>
    Top,
}

/// <summary>Placement options shared by all primitives.</summary>
public abstract record PrimitiveOptions
{
    /// <summary>Placement point (RAW): the pin or the origin.</summary>
    public Vec3 At { get; init; }
    public PrimitiveAlign Align { get; init; }
    /// <summary>Optional named selection that receives the new geometry.</summary>
    public string? Selection { get; init; }
}

/// <summary>Horizontal plane (XZ) facing up.</summary>
public sealed record PlaneParams(double SizeX = 1, double SizeZ = 1, int SegmentsX = 1, int SegmentsZ = 1) : PrimitiveOptions;

/// <summary>Axis-aligned box.</summary>
public sealed record BoxParams(double SizeX = 1, double SizeY = 1, double SizeZ = 1, int SegmentsX = 1, int SegmentsY = 1, int SegmentsZ = 1) : PrimitiveOptions;

/// <summary>Cylinder around the Y axis. <paramref name="Sides"/> = segments around (≥ 3), <paramref name="SegmentsY"/> along the height.</summary>
public sealed record CylinderParams(double Radius = 0.5, double Height = 1, int Sides = 16, int SegmentsY = 1, bool Caps = true) : PrimitiveOptions;

/// <summary>UV sphere around the Y axis. <paramref name="SegmentsY"/> = segments from pole to pole (≥ 2).</summary>
public sealed record SphereParams(double Radius = 0.5, int Sides = 16, int SegmentsY = 8) : PrimitiveOptions;

/// <summary>Flat disc in the XZ plane facing up; <paramref name="Center"/> adds a centre point (triangle fan), else quads fan from the first point.</summary>
public sealed record CircleParams(double Radius = 0.5, int Sides = 16, bool Center = true) : PrimitiveOptions;

/// <summary>Accumulates points and oriented faces (with UVs) and turns them into a <see cref="Fragment"/>.</summary>
public sealed class MeshBuilder
{
    readonly List<double> pos = [];
    readonly List<byte> nv = [];
    readonly List<uint> pts = [];
    readonly List<float> uvs = [];

    public int PointCount => pos.Count / 3;
    public int FaceCount => nv.Count;

    public int Point(double x, double y, double z)
    {
        pos.Add(x);
        pos.Add(y);
        pos.Add(z);
        return PointCount - 1;
    }

    /// <summary>
    /// Adds a 3- or 4-point face; <paramref name="uv"/> holds (u, v) per point. The order is reversed (keeping the first
    /// point) when needed so that the face looks towards <paramref name="outward"/> (P3D winding: the stored RH normal
    /// points inward).
    /// </summary>
    public void Face(ReadOnlySpan<int> points, ReadOnlySpan<double> uv, Vec3 outward)
    {
        int n = points.Length;
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < n; i++)
        {
            int a = points[i] * 3, b = points[(i + 1) % n] * 3;
            nx += (pos[a + 1] - pos[b + 1]) * (pos[a + 2] + pos[b + 2]);
            ny += (pos[a + 2] - pos[b + 2]) * (pos[a] + pos[b]);
            nz += (pos[a] - pos[b]) * (pos[a + 1] + pos[b + 1]);
        }
        bool reverse = nx * outward.X + ny * outward.Y + nz * outward.Z > 0;
        Span<int> order = stackalloc int[n];
        for (int i = 0; i < n; i++) order[i] = reverse && i > 0 ? n - i : i;
        nv.Add((byte)n);
        for (int k = 0; k < 4; k++)
        {
            if (k < n)
            {
                pts.Add((uint)points[order[k]]);
                uvs.Add((float)uv[order[k] * 2]);
                uvs.Add((float)uv[order[k] * 2 + 1]);
            }
            else
            {
                pts.Add(0);
                uvs.Add(0);
                uvs.Add(0);
            }
        }
    }

    public void Translate(Vec3 d)
    {
        for (int i = 0; i < pos.Count; i += 3)
        {
            pos[i] += d.X;
            pos[i + 1] += d.Y;
            pos[i + 2] += d.Z;
        }
    }

    public (Vec3 Min, Vec3 Max) Bounds()
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        for (int i = 0; i < pos.Count; i += 3)
        {
            x0 = Math.Min(x0, pos[i]);
            x1 = Math.Max(x1, pos[i]);
            y0 = Math.Min(y0, pos[i + 1]);
            y1 = Math.Max(y1, pos[i + 1]);
            z0 = Math.Min(z0, pos[i + 2]);
            z1 = Math.Max(z1, pos[i + 2]);
        }
        return (new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));
    }

    public Fragment ToFragment()
    {
        var fr = Fragment.Allocate(PointCount, FaceCount);
        for (int i = 0; i < pos.Count; i++) fr.Positions[i] = (float)pos[i];
        nv.CopyTo(fr.Faces.VertCount);
        pts.CopyTo(fr.Faces.Points);
        uvs.CopyTo(fr.Faces.UV);
        return fr;
    }
}

/// <summary>Result of <see cref="Primitives.CreateFace"/>.</summary>
/// <param name="Lod">The new LOD (the face and its points are selected).</param>
/// <param name="Face">Index of the new face.</param>
/// <param name="Convex">False when 4 points do not form a convex quad (the largest-area ordering is used).</param>
/// <param name="Planarity">Distance of the 4th point from the plane of the first three, relative to the quad size.</param>
/// <param name="OrientedBy">How the orientation was decided.</param>
public sealed record CreateFaceResult(Lod Lod, int Face, bool Convex, double Planarity, FaceOrientationSource OrientedBy);

public enum FaceOrientationSource
{
    Default,
    Neighbours,
    View,
}

/// <summary>
/// Creation operations: primitives (plane, box, cylinder, sphere, circle), single points and a face from 3–4 existing
/// points. Pure; created geometry becomes the current selection (#Selected#). All faces follow the P3D winding.
/// </summary>
public static class Primitives
{
    public const int MaxSegments = 512, MaxFaces = 1_000_000;
    const double Tau = Math.PI * 2;

    static void CheckSize(string name, double v)
    {
        if (!(double.IsFinite(v) && v > 0)) throw new ArgumentException($"{name} must be a positive number");
    }

    static void CheckSegments(string name, int v, int min)
    {
        if (v < min || v > MaxSegments) throw new ArgumentException($"{name} must be a whole number between {min} and {MaxSegments}");
    }

    static void CheckFaces(long n)
    {
        if (n > MaxFaces) throw new ArgumentException($"Too many faces ({n}); the limit is {MaxFaces}");
    }

    public static MeshBuilder BuildPlane(PlaneParams p)
    {
        CheckSize("Size X", p.SizeX);
        CheckSize("Size Z", p.SizeZ);
        CheckSegments("Segments X", p.SegmentsX, 1);
        CheckSegments("Segments Z", p.SegmentsZ, 1);
        CheckFaces((long)p.SegmentsX * p.SegmentsZ);
        var b = new MeshBuilder();
        int nx = p.SegmentsX, nz = p.SegmentsZ;
        int Idx(int i, int k) => k * (nx + 1) + i;
        for (int k = 0; k <= nz; k++)
            for (int i = 0; i <= nx; i++) b.Point(-p.SizeX / 2 + p.SizeX * i / nx, 0, -p.SizeZ / 2 + p.SizeZ * k / nz);
        for (int k = 0; k < nz; k++)
            for (int i = 0; i < nx; i++)
            {
                double u0 = (double)i / nx, u1 = (double)(i + 1) / nx, v0 = 1 - (double)k / nz, v1 = 1 - (double)(k + 1) / nz;
                b.Face([Idx(i, k), Idx(i + 1, k), Idx(i + 1, k + 1), Idx(i, k + 1)], [u0, v0, u1, v0, u1, v1, u0, v1], new Vec3(0, 1, 0));
            }
        return b;
    }

    /// <summary>Axis-aligned box, every side subdivided into quads; edge points are shared (closed mesh).</summary>
    public static MeshBuilder BuildBox(BoxParams p)
    {
        CheckSize("Size X", p.SizeX);
        CheckSize("Size Y", p.SizeY);
        CheckSize("Size Z", p.SizeZ);
        CheckSegments("Segments X", p.SegmentsX, 1);
        CheckSegments("Segments Y", p.SegmentsY, 1);
        CheckSegments("Segments Z", p.SegmentsZ, 1);
        int nx = p.SegmentsX, ny = p.SegmentsY, nz = p.SegmentsZ;
        CheckFaces(2L * (nx * ny + ny * nz + nx * nz));
        var b = new MeshBuilder();
        var ids = new Dictionary<long, int>();
        int At(int i, int j, int k)
        {
            long key = i + (long)(nx + 1) * (j + (long)(ny + 1) * k);
            if (!ids.TryGetValue(key, out int v))
            {
                v = b.Point(-p.SizeX / 2 + p.SizeX * i / nx, -p.SizeY / 2 + p.SizeY * j / ny, -p.SizeZ / 2 + p.SizeZ * k / nz);
                ids[key] = v;
            }
            return v;
        }
        // ±Y: grid over (i, k)
        foreach (var (j, o) in new[] { (0, -1), (ny, 1) })
            for (int k = 0; k < nz; k++)
                for (int i = 0; i < nx; i++)
                {
                    double u0 = (double)i / nx, u1 = (double)(i + 1) / nx;
                    double v0 = o > 0 ? 1 - (double)k / nz : (double)k / nz, v1 = o > 0 ? 1 - (double)(k + 1) / nz : (double)(k + 1) / nz;
                    b.Face([At(i, j, k), At(i + 1, j, k), At(i + 1, j, k + 1), At(i, j, k + 1)], [u0, v0, u1, v0, u1, v1, u0, v1], new Vec3(0, o, 0));
                }
        // ±X: grid over (k, j)
        foreach (var (i, o) in new[] { (0, -1), (nx, 1) })
            for (int j = 0; j < ny; j++)
                for (int k = 0; k < nz; k++)
                {
                    double u0 = o > 0 ? (double)k / nz : 1 - (double)k / nz, u1 = o > 0 ? (double)(k + 1) / nz : 1 - (double)(k + 1) / nz;
                    double v0 = 1 - (double)j / ny, v1 = 1 - (double)(j + 1) / ny;
                    b.Face([At(i, j, k), At(i, j, k + 1), At(i, j + 1, k + 1), At(i, j + 1, k)], [u0, v0, u1, v0, u1, v1, u0, v1], new Vec3(o, 0, 0));
                }
        // ±Z: grid over (i, j)
        foreach (var (k, o) in new[] { (0, -1), (nz, 1) })
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    double u0 = o > 0 ? 1 - (double)i / nx : (double)i / nx, u1 = o > 0 ? 1 - (double)(i + 1) / nx : (double)(i + 1) / nx;
                    double v0 = 1 - (double)j / ny, v1 = 1 - (double)(j + 1) / ny;
                    b.Face([At(i, j, k), At(i + 1, j, k), At(i + 1, j + 1, k), At(i, j + 1, k)], [u0, v0, u1, v0, u1, v1, u0, v1], new Vec3(0, 0, o));
                }
        return b;
    }

    /// <summary>Cylinder around the Y axis; side quads, optional triangle-fan caps.</summary>
    public static MeshBuilder BuildCylinder(CylinderParams p)
    {
        CheckSize("Radius", p.Radius);
        CheckSize("Height", p.Height);
        CheckSegments("Segments Radius", p.Sides, 3);
        CheckSegments("Segments Y", p.SegmentsY, 1);
        int n = p.Sides, ny = p.SegmentsY;
        CheckFaces((long)n * ny + (p.Caps ? 2 * n : 0));
        var b = new MeshBuilder();
        double r = p.Radius, h = p.Height;
        int Ring(int j, int i) => j * n + i % n;
        for (int j = 0; j <= ny; j++)
        {
            double y = -h / 2 + h * j / ny;
            for (int i = 0; i < n; i++) b.Point(r * Math.Sin(Tau * i / n), y, r * Math.Cos(Tau * i / n));
        }
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < n; i++)
            {
                double a = Tau * (i + 0.5) / n;
                double u0 = (double)i / n, u1 = (double)(i + 1) / n, v0 = 1 - (double)j / ny, v1 = 1 - (double)(j + 1) / ny;
                b.Face([Ring(j, i), Ring(j, i + 1), Ring(j + 1, i + 1), Ring(j + 1, i)], [u0, v0, u1, v0, u1, v1, u0, v1], new Vec3(Math.Sin(a), 0, Math.Cos(a)));
            }
        if (p.Caps)
            foreach (var (j, o) in new[] { (0, -1), (ny, 1) })
            {
                int c = b.Point(0, -h / 2 + h * j / ny, 0);
                for (int i = 0; i < n; i++)
                {
                    double t0 = Tau * i / n, t1 = Tau * (i + 1) / n;
                    b.Face([c, Ring(j, i), Ring(j, i + 1)],
                        [0.5, 0.5, 0.5 + 0.5 * Math.Sin(t0), 0.5 - 0.5 * o * Math.Cos(t0), 0.5 + 0.5 * Math.Sin(t1), 0.5 - 0.5 * o * Math.Cos(t1)],
                        new Vec3(0, o, 0));
                }
            }
        return b;
    }

    /// <summary>UV sphere around the Y axis: triangle fans at the poles, quads elsewhere.</summary>
    public static MeshBuilder BuildSphere(SphereParams p)
    {
        CheckSize("Radius", p.Radius);
        CheckSegments("Segments Radius", p.Sides, 3);
        CheckSegments("Segments Y", p.SegmentsY, 2);
        int n = p.Sides, m = p.SegmentsY;
        double r = p.Radius;
        CheckFaces((long)n * m);
        var b = new MeshBuilder();
        int top = b.Point(0, r, 0);
        int Ring(int j, int i) => 1 + (j - 1) * n + i % n;
        for (int j = 1; j < m; j++)
        {
            double phi = Math.PI * j / m, y = r * Math.Cos(phi), rho = r * Math.Sin(phi);
            for (int i = 0; i < n; i++) b.Point(rho * Math.Sin(Tau * i / n), y, rho * Math.Cos(Tau * i / n));
        }
        int bottom = b.Point(0, -r, 0);
        Vec3 DirOf(double j, double i)
        {
            double phi = Math.PI * j / m, th = Tau * i / n;
            return new Vec3(Math.Sin(phi) * Math.Sin(th), Math.Cos(phi), Math.Sin(phi) * Math.Cos(th));
        }
        for (int i = 0; i < n; i++)
        {
            double u0 = (double)i / n, u1 = (double)(i + 1) / n, v1 = 1.0 / m;
            b.Face([top, Ring(1, i), Ring(1, i + 1)], [(u0 + u1) / 2, 0, u0, v1, u1, v1], DirOf(0.5, i + 0.5));
        }
        for (int j = 1; j < m - 1; j++)
            for (int i = 0; i < n; i++)
            {
                double u0 = (double)i / n, u1 = (double)(i + 1) / n, v0 = (double)j / m, v1 = (double)(j + 1) / m;
                b.Face([Ring(j, i), Ring(j, i + 1), Ring(j + 1, i + 1), Ring(j + 1, i)], [u0, v0, u1, v0, u1, v1, u0, v1], DirOf(j + 0.5, i + 0.5));
            }
        for (int i = 0; i < n; i++)
        {
            double u0 = (double)i / n, u1 = (double)(i + 1) / n, v0 = (double)(m - 1) / m;
            b.Face([Ring(m - 1, i), Ring(m - 1, i + 1), bottom], [u0, v0, u1, v0, (u0 + u1) / 2, 1], DirOf(m - 0.5, i + 0.5));
        }
        return b;
    }

    /// <summary>Flat disc in the XZ plane facing up (+Y).</summary>
    public static MeshBuilder BuildCircle(CircleParams p)
    {
        CheckSize("Radius", p.Radius);
        CheckSegments("Segments Radius", p.Sides, 3);
        int n = p.Sides;
        double r = p.Radius;
        var b = new MeshBuilder();
        double U(int i) => 0.5 + 0.5 * Math.Sin(Tau * i / n);
        double V(int i) => 0.5 - 0.5 * Math.Cos(Tau * i / n);
        for (int i = 0; i < n; i++) b.Point(r * Math.Sin(Tau * i / n), 0, r * Math.Cos(Tau * i / n));
        var up = new Vec3(0, 1, 0);
        if (p.Center)
        {
            CheckFaces(n);
            int c = b.Point(0, 0, 0);
            for (int i = 0; i < n; i++) b.Face([c, i, (i + 1) % n], [0.5, 0.5, U(i), V(i), U(i + 1), V(i + 1)], up);
        }
        else
        {
            // fan of quads (0, i, i+1, i+2) — planar and convex because the disc is
            int i = 1;
            for (; i + 2 <= n - 1; i += 2) b.Face([0, i, i + 1, i + 2], [U(0), V(0), U(i), V(i), U(i + 1), V(i + 1), U(i + 2), V(i + 2)], up);
            if (i + 1 <= n - 1) b.Face([0, i, i + 1], [U(0), V(0), U(i), V(i), U(i + 1), V(i + 1)], up);
        }
        return b;
    }

    /// <summary>Moves a built primitive so that its bounds are centred on <paramref name="at"/> (or sit on / hang from it).</summary>
    public static void Place(MeshBuilder b, Vec3 at, PrimitiveAlign align)
    {
        var (min, max) = b.Bounds();
        double cx = (min.X + max.X) / 2, cz = (min.Z + max.Z) / 2;
        double y = align switch
        {
            PrimitiveAlign.Bottom => min.Y,
            PrimitiveAlign.Top => max.Y,
            _ => (min.Y + max.Y) / 2,
        };
        b.Translate(new Vec3(at.X - cx, at.Y - y, at.Z - cz));
    }

    /// <summary>
    /// Appends a built primitive: the new geometry becomes the selection and is added to the named selection
    /// <see cref="PrimitiveOptions.Selection"/> when given. Normals are recalculated.
    /// </summary>
    public static Lod Add(Lod lod, MeshBuilder b, PrimitiveOptions o)
    {
        Place(b, o.At, o.Align);
        var frag = b.ToFragment();
        var name = o.Selection?.Trim();
        if (!string.IsNullOrEmpty(name))
            frag = frag with { Selections = [new NamedWeights(name, MeshMath.Ones(b.PointCount), MeshMath.Ones(b.FaceCount))] };
        return Structure.RecalcNormals(MeshMath.AppendSafe(lod, frag, selectNew: true));
    }

    public static Lod CreatePlane(Lod lod, PlaneParams p) => Add(lod, BuildPlane(p), p);
    public static Lod CreateBox(Lod lod, BoxParams p) => Add(lod, BuildBox(p), p);
    public static Lod CreateCylinder(Lod lod, CylinderParams p) => Add(lod, BuildCylinder(p), p);
    public static Lod CreateSphere(Lod lod, SphereParams p) => Add(lod, BuildSphere(p), p);
    public static Lod CreateCircle(Lod lod, CircleParams p) => Add(lod, BuildCircle(p), p);

    /// <summary>Appends one point at <paramref name="at"/>; it becomes the selection.</summary>
    public static Lod CreatePoint(Lod lod, Vec3 at)
    {
        if (!at.IsFinite) throw new ArgumentException("Invalid point position", nameof(at));
        var frag = Fragment.Allocate(1, 0);
        frag.Positions[0] = (float)at.X;
        frag.Positions[1] = (float)at.Y;
        frag.Positions[2] = (float)at.Z;
        return MeshMath.AppendSafe(lod, frag, selectNew: true);
    }

    static readonly int[][] QuadOrders = [[0, 1, 2, 3], [0, 1, 3, 2], [0, 2, 1, 3]];

    static bool IsConvexQuad(float[] P, ReadOnlySpan<uint> q, Vec3 normal)
    {
        int sign = 0;
        for (int k = 0; k < 4; k++)
        {
            var a = Vec3.At(P, q[k]);
            var b = Vec3.At(P, q[(k + 1) % 4]);
            var c = Vec3.At(P, q[(k + 2) % 4]);
            int s = Math.Sign(Vec3.Dot(Vec3.Cross(b - a, c - b), normal));
            if (s == 0) return false;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    /// <summary>
    /// Creates a face from 3 or 4 existing points. Four points are ordered as the largest-area (non-crossing) quad.
    /// Orientation: facing <paramref name="viewDir"/> (RAW, from the camera into the scene) when given — unless
    /// <paramref name="preferNeighbours"/> and the face shares edges with existing faces — else consistent with the
    /// neighbours, else as ordered. The new face gets corner UVs, no texture, and becomes the selection (with its points).
    /// </summary>
    public static CreateFaceResult CreateFace(Lod lod, IReadOnlyList<int> points, Vec3? viewDir = null, bool preferNeighbours = false)
    {
        int count = points.Count;
        if (count != 3 && count != 4) throw new ArgumentException($"Select 3 or 4 points to create a face ({count} selected)");
        if (points.Distinct().Count() != count) throw new ArgumentException("The points of a face must be distinct");
        foreach (int p in points)
            if (p < 0 || p >= lod.PointCount) throw new ArgumentException($"Point {p} does not exist");
        var P = lod.Positions;

        uint[] order = points.Select(p => (uint)p).ToArray();
        double best = -1;
        if (count == 4)
        {
            Span<uint> q = stackalloc uint[4];
            foreach (var o in QuadOrders)
            {
                for (int k = 0; k < 4; k++) q[k] = (uint)points[o[k]];
                double area = MeshMath.Newell(P, q).Length;
                if (area > best)
                {
                    best = area;
                    order = q.ToArray();
                }
            }
        }
        else best = MeshMath.Newell(P, order).Length;
        double ext = 0;
        for (int i = 0; i < order.Length; i++)
            for (int j = i + 1; j < order.Length; j++) ext = Math.Max(ext, (Vec3.At(P, order[i]) - Vec3.At(P, order[j])).Length);
        if (!(best > 1e-9 * Math.Max(ext * ext, 1e-12)) || ext == 0) throw new ArgumentException("The selected points are collinear or coincident");

        var normal = MeshMath.Newell(P, order);
        var unit = normal / normal.Length;
        bool convex = true;
        double planarity = 0;
        if (order.Length == 4)
        {
            convex = IsConvexQuad(P, order, unit);
            var n3 = MeshMath.Newell(P, order.AsSpan(0, 3));
            double l3 = n3.Length;
            if (l3 > 0) planarity = Math.Abs(Vec3.Dot(n3, Vec3.At(P, order[3]) - Vec3.At(P, order[0])) / l3) / (ext > 0 ? ext : 1);
        }

        // Neighbour votes: an existing face traversing a→b (same direction) asks for a flip.
        int votes = 0;
        if (lod.FaceCount > 0)
        {
            var adj = PointFaces.Build(lod);
            for (int k = 0; k < order.Length; k++)
                MeshMath.ForEachEdgeNeighbour(lod, adj, -1, order[k], order[(k + 1) % order.Length], (_, dir) => votes += dir > 0 ? -1 : dir < 0 ? 1 : 0);
        }
        bool flip = false;
        var orientedBy = FaceOrientationSource.Default;
        Vec3? view = viewDir is { } vd && vd.Length > 0 ? vd : null;
        if (votes != 0 && (preferNeighbours || view is null))
        {
            flip = votes < 0;
            orientedBy = FaceOrientationSource.Neighbours;
        }
        else if (view is { } v)
        {
            // the stored normal points away from the viewer (into the surface) = along the view direction
            flip = Vec3.Dot(normal, v) < 0;
            orientedBy = FaceOrientationSource.View;
        }
        if (flip) order = order.Length == 4 ? [order[0], order[3], order[2], order[1]] : [order[0], order[2], order[1]];

        int fc = lod.FaceCount;
        var b = FaceBuild.Identity(lod, 1);
        b.VertCount[fc] = (byte)order.Length;
        b.UV = new float[(fc + 1) * 8];
        ReadOnlySpan<float> corners = order.Length == 4 ? [0, 0, 1, 0, 1, 1, 0, 1] : [0, 0, 1, 0, 0, 1];
        for (int k = 0; k < order.Length; k++)
        {
            b.Points[fc * 4 + k] = order[k];
            b.UV[(fc * 4 + k) * 2] = corners[k * 2];
            b.UV[(fc * 4 + k) * 2 + 1] = corners[k * 2 + 1];
        }
        var next = MeshMath.RebuildFaces(lod, b);
        var sel = SelectionMask.Empty(next);
        foreach (uint p in order) sel.Points[p] = 1;
        sel.Faces[fc] = 1;
        next = Structure.RecalcNormals(Selection.WithSelected(next, sel));
        return new CreateFaceResult(next, fc, convex, planarity, orientedBy);
    }

    /// <summary>Point indices of the current selection (helper for commands).</summary>
    public static List<int> SelectedPointList(Lod lod)
    {
        var s = Selection.PeekSelected(lod)?.Points;
        var o = new List<int>();
        if (s is null) return o;
        for (int i = 0; i < s.Length; i++)
            if (s[i] != 0) o.Add(i);
        return o;
    }
}
