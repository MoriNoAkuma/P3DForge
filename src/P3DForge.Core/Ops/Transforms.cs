using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>
/// Affine 4×4 matrices for <see cref="Structure.TransformPoints"/>: column-major <c>double[16]</c> in RAW
/// coordinates (element row r, column c = m[c * 4 + r]; a point maps as x' = m0·x + m4·y + m8·z + m12).
/// Rotations follow the right-hand rule applied to the raw numbers. <see cref="Multiply"/>(a, b) applies b first.
/// </summary>
public static class Transforms
{
    public static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public static double[] Translate(double x, double y, double z) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1];

    public static double[] Translate(Vec3 d) => Translate(d.X, d.Y, d.Z);

    public static double[] Scale(double sx, double sy, double sz) => [sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, sz, 0, 0, 0, 0, 1];

    /// <summary>Scales about a fixed point.</summary>
    public static double[] ScaleAbout(Vec3 center, double sx, double sy, double sz) =>
        Multiply(Translate(center), Multiply(Scale(sx, sy, sz), Translate(-center)));

    /// <summary>Rotation about an axis through the origin (Rodrigues; the axis need not be normalized).</summary>
    public static double[] Rotate(Vec3 axis, double radians)
    {
        double len = Math.Sqrt(Vec3.Dot(axis, axis));
        if (len < 1e-300) return Identity();
        double x = axis.X / len, y = axis.Y / len, z = axis.Z / len;
        double c = Math.Cos(radians), s = Math.Sin(radians), t = 1 - c;
        return
        [
            t * x * x + c, t * x * y + s * z, t * x * z - s * y, 0,
            t * x * y - s * z, t * y * y + c, t * y * z + s * x, 0,
            t * x * z + s * y, t * y * z - s * x, t * z * z + c, 0,
            0, 0, 0, 1,
        ];
    }

    /// <summary>Rotation about an axis through <paramref name="center"/>.</summary>
    public static double[] RotateAbout(Vec3 center, Vec3 axis, double radians) =>
        Multiply(Translate(center), Multiply(Rotate(axis, radians), Translate(-center)));

    /// <summary>Unit vector of a principal axis (0 = X, 1 = Y, 2 = Z).</summary>
    public static Vec3 Axis(int axis) => axis switch
    {
        0 => new Vec3(1, 0, 0),
        1 => new Vec3(0, 1, 0),
        2 => new Vec3(0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    /// <summary>Mirror across the plane through <paramref name="center"/> perpendicular to a principal axis (0 = X …).</summary>
    public static double[] MirrorAbout(Vec3 center, int axis)
    {
        if (axis is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(axis));
        return ScaleAbout(center, axis == 0 ? -1 : 1, axis == 1 ? -1 : 1, axis == 2 ? -1 : 1);
    }

    /// <summary>a·b: the transform that applies <paramref name="b"/> first, then <paramref name="a"/>.</summary>
    public static double[] Multiply(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var o = new double[16];
        for (int c = 0; c < 4; c++)
        for (int r = 0; r < 4; r++)
        {
            double s = 0;
            for (int k = 0; k < 4; k++) s += a[k * 4 + r] * b[c * 4 + k];
            o[c * 4 + r] = s;
        }
        return o;
    }

    public static Vec3 Apply(ReadOnlySpan<double> m, Vec3 p) => new(
        m[0] * p.X + m[4] * p.Y + m[8] * p.Z + m[12],
        m[1] * p.X + m[5] * p.Y + m[9] * p.Z + m[13],
        m[2] * p.X + m[6] * p.Y + m[10] * p.Z + m[14]);

    /// <summary>Determinant of the linear (3×3) part.</summary>
    public static double Determinant(ReadOnlySpan<double> m) =>
        m[0] * (m[5] * m[10] - m[9] * m[6]) - m[4] * (m[1] * m[10] - m[9] * m[2]) + m[8] * (m[1] * m[6] - m[5] * m[2]);

    /// <summary>True when the transform mirrors (negative determinant): face winding must be reversed.</summary>
    public static bool FlipsWinding(ReadOnlySpan<double> m) => Determinant(m) < 0;

    public static bool IsIdentity(ReadOnlySpan<double> m, double eps = 1e-12)
    {
        for (int i = 0; i < 16; i++)
            if (Math.Abs(m[i] - (i % 5 == 0 ? 1 : 0)) > eps) return false;
        return true;
    }

    /// <summary>
    /// Writes transformed positions into <paramref name="dst"/> (same length as <paramref name="src"/>; points outside
    /// <paramref name="mask"/> are copied). Same arithmetic as <see cref="Structure.TransformPoints"/>, so a live
    /// preview matches the committed edit exactly. <paramref name="src"/> and <paramref name="dst"/> may not alias.
    /// </summary>
    public static void TransformPositions(float[] src, float[] dst, byte[]? mask, ReadOnlySpan<double> m, bool useWeights = false)
    {
        int n = src.Length / 3;
        for (int i = 0; i < n; i++)
        {
            int o = i * 3;
            byte b = mask is null ? (byte)1 : mask[i];
            if (b == 0)
            {
                dst[o] = src[o];
                dst[o + 1] = src[o + 1];
                dst[o + 2] = src[o + 2];
                continue;
            }
            double x = src[o], y = src[o + 1], z = src[o + 2];
            double nx = m[0] * x + m[4] * y + m[8] * z + m[12];
            double ny = m[1] * x + m[5] * y + m[9] * z + m[13];
            double nz = m[2] * x + m[6] * y + m[10] * z + m[14];
            double w = useWeights && b != 1 ? (256 - b) / 255.0 : 1;
            dst[o] = (float)(x + (nx - x) * w);
            dst[o + 1] = (float)(y + (ny - y) * w);
            dst[o + 2] = (float)(z + (nz - z) * w);
        }
    }

    /// <summary>
    /// Transforms the points in <paramref name="mask"/> (all when null). When the matrix mirrors, the faces whose
    /// points are all transformed are reversed so they keep facing outwards.
    /// </summary>
    public static Lod Apply(Lod lod, byte[]? mask, ReadOnlySpan<double> m, bool useWeights = false)
    {
        if (IsIdentity(m)) return lod;
        var o = Structure.TransformPoints(lod, mask, m, useWeights);
        if (!FlipsWinding(m)) return o;
        var faces = mask is null ? MeshMath.Ones(lod.FaceCount) : Selection.FacesFromPoints(lod, mask);
        return ReverseFaces(o, faces);
    }

    /// <summary>Reverses the vertex order (points, normals, UVs, extra UV sets) of the faces in <paramref name="faceMask"/>.</summary>
    static Lod ReverseFaces(Lod lod, byte[] faceMask)
    {
        if (MeshMath.CountSet(faceMask) == 0) return lod;
        var fp = (uint[])lod.FacePoints.Clone();
        var fnrm = (uint[])lod.FaceNormals.Clone();
        var fuv = (float[])lod.FaceUV.Clone();
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faceMask[f] == 0) continue;
            int n = lod.FaceVertCount[f];
            for (int k = 0; k < n / 2; k++)
            {
                int a = f * 4 + k, b = f * 4 + n - 1 - k;
                (fp[a], fp[b]) = (fp[b], fp[a]);
                (fnrm[a], fnrm[b]) = (fnrm[b], fnrm[a]);
                SwapUV(fuv, f, k, n - 1 - k);
            }
        }
        var tags = lod.Tags;
        for (int t = 0; t < tags.Length; t++)
        {
            if (tags[t] is not UVSetTag u || u.UV.Length < lod.FaceCount * 8) continue;
            var uv = (float[])u.UV.Clone();
            for (int f = 0; f < lod.FaceCount; f++)
            {
                if (faceMask[f] == 0) continue;
                int n = lod.FaceVertCount[f];
                for (int k = 0; k < n / 2; k++) SwapUV(uv, f, k, n - 1 - k);
            }
            tags = tags.SetItem(t, u with { UV = uv });
        }
        return lod with { FacePoints = fp, FaceNormals = fnrm, FaceUV = fuv, Tags = tags };
    }

    static void SwapUV(float[] uv, int f, int a, int b)
    {
        int ia = f * 8 + a * 2, ib = f * 8 + b * 2;
        (uv[ia], uv[ib]) = (uv[ib], uv[ia]);
        (uv[ia + 1], uv[ib + 1]) = (uv[ib + 1], uv[ia + 1]);
    }

    // ---- model-level helpers ------------------------------------------------------------------------

    /// <summary>Bounding-box centre of the points in <paramref name="mask"/> (all when null); null when none.</summary>
    public static Vec3? Center(Lod lod, byte[]? mask)
    {
        var b = MeshMath.Bounds(lod.Positions, mask);
        return b is { } bb ? (bb.Min + bb.Max) * 0.5 : null;
    }

    /// <summary>Applies <paramref name="m"/> to every point of every LOD (faces reversed when the matrix mirrors).</summary>
    public static P3DModel ApplyAll(P3DModel model, ReadOnlySpan<double> m)
    {
        if (IsIdentity(m)) return model;
        var lods = model.Lods.ToBuilder();
        for (int i = 0; i < lods.Count; i++)
            if (lods[i].PointCount > 0) lods[i] = Apply(lods[i], null, m);
        return model with { Lods = lods.ToImmutable() };
    }

    /// <summary>The LOD that Center All centres: the Geometry LOD when present, else LOD 0 (-1 for an empty model).</summary>
    public static int CenterReferenceLod(P3DModel model)
    {
        for (int i = 0; i < model.Lods.Length; i++)
            if (model.Lods[i].PointCount > 0 && LodNames.TypeOf(model.Lods[i].Resolution).Type.Id == "geometry") return i;
        return model.Lods.Length > 0 ? 0 : -1;
    }

    /// <summary>Translation that moves the bounding-box centre of <see cref="CenterReferenceLod"/> to the origin (null when it has no points).</summary>
    public static Vec3? CenterAllOffset(P3DModel model)
    {
        int i = CenterReferenceLod(model);
        if (i < 0) return null;
        return Center(model.Lods[i], null) is { } c ? -c : null;
    }

    // ---- mouse-drag math (RAW coordinates; rays need not be normalized) --------------------------------

    /// <summary>Intersection of a ray (or line, both directions) with a plane; null when parallel.</summary>
    public static Vec3? RayPlane(Vec3 origin, Vec3 dir, Vec3 planePoint, Vec3 normal)
    {
        double d = Vec3.Dot(dir, normal);
        if (Math.Abs(d) < 1e-12 * Math.Max(1, dir.Length * normal.Length)) return null;
        double t = Vec3.Dot(planePoint - origin, normal) / d;
        var p = origin + dir * t;
        return p.IsFinite ? p : null;
    }

    /// <summary>
    /// Parameter t of the point linePoint + t·lineDir (lineDir normalized) closest to the given ray; NaN when the
    /// line and the ray are (nearly) parallel.
    /// </summary>
    public static double ClosestOnLine(Vec3 linePoint, Vec3 lineDir, Vec3 rayOrigin, Vec3 rayDir)
    {
        var d = rayDir.Normalized() ?? Vec3.Zero;
        double b = Vec3.Dot(lineDir, d);
        double den = 1 - b * b;
        if (den < 1e-8) return double.NaN;
        var w = linePoint - rayOrigin;
        double dd = Vec3.Dot(lineDir, w), e = Vec3.Dot(d, w);
        return (b * e - dd) / den;
    }

    /// <summary>
    /// Object Builder mouse move: translation for a drag from ray <paramref name="a"/> to ray <paramref name="b"/>.
    /// Without locks (or with all three) the selection follows the mouse in the plane through
    /// <paramref name="center"/> facing the view (<paramref name="viewDir"/>). OB axis locks restrict movement to
    /// the locked axes: one lock = along that axis (closest point to the mouse ray), two locks = in their plane.
    /// </summary>
    public static Vec3 DragDelta(Vec3 center, Vec3 viewDir, (Vec3 O, Vec3 D) a, (Vec3 O, Vec3 D) b, bool lockX, bool lockY, bool lockZ)
    {
        var vd = viewDir.Normalized() ?? new Vec3(0, 0, 1);
        int locks = (lockX ? 1 : 0) + (lockY ? 1 : 0) + (lockZ ? 1 : 0);
        Vec3 Free()
        {
            var pa = RayPlane(a.O, a.D, center, vd);
            var pb = RayPlane(b.O, b.D, center, vd);
            return pa is { } x && pb is { } y ? y - x : Vec3.Zero;
        }
        if (locks is 0 or 3) return Free();
        if (locks == 1)
        {
            var axis = Axis(lockX ? 0 : lockY ? 1 : 2);
            if (Math.Abs(Vec3.Dot(axis, vd)) > 0.995) return Vec3.Zero; // axis points into the screen
            double ta = ClosestOnLine(center, axis, a.O, a.D), tb = ClosestOnLine(center, axis, b.O, b.D);
            return double.IsFinite(ta) && double.IsFinite(tb) ? axis * (tb - ta) : Vec3.Zero;
        }
        int free = !lockX ? 0 : !lockY ? 1 : 2;
        var n = Axis(free);
        Vec3 delta;
        if (Math.Abs(Vec3.Dot(n, vd)) > 0.2)
        {
            var pa = RayPlane(a.O, a.D, center, n);
            var pb = RayPlane(b.O, b.D, center, n);
            delta = pa is { } x && pb is { } y ? y - x : Vec3.Zero;
        }
        else delta = Free(); // plane seen edge-on: drop the out-of-plane part of the view-plane motion
        return free switch
        {
            0 => delta with { X = 0 },
            1 => delta with { Y = 0 },
            _ => delta with { Z = 0 },
        };
    }

    /// <summary>
    /// Mouse rotation axis: the single locked axis, else the view direction, oriented away from the viewer. RAW is
    /// left-handed (camera right × up = forward on the raw numbers), so a positive <see cref="Rotate"/> angle about
    /// this axis turns counter-clockwise on screen; callers negate the angle for clockwise.
    /// </summary>
    public static Vec3 RotationAxis(Vec3 viewDir, bool lockX, bool lockY, bool lockZ)
    {
        var vd = viewDir.Normalized() ?? new Vec3(0, 0, 1);
        int locks = (lockX ? 1 : 0) + (lockY ? 1 : 0) + (lockZ ? 1 : 0);
        if (locks != 1) return vd;
        var axis = Axis(lockX ? 0 : lockY ? 1 : 2);
        return Vec3.Dot(axis, vd) < 0 ? -axis : axis;
    }

    /// <summary>Scale factors for a mouse scale: uniform, or only along the locked axes when any lock is on.</summary>
    public static Vec3 ScaleFactors(double factor, bool lockX, bool lockY, bool lockZ)
    {
        if (!lockX && !lockY && !lockZ) return new Vec3(factor, factor, factor);
        return new Vec3(lockX ? factor : 1, lockY ? factor : 1, lockZ ? factor : 1);
    }
}
