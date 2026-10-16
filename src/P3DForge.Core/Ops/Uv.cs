using System.Numerics;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>
/// UV sets and UV edits (pure; same LOD back when nothing changes).
/// Set 0 is the UVs stored in the face records (plus the stage-0 #UVSet# that duplicates them in files from Object
/// Builder); set k ≥ 1 is the #UVSet# tag with stage k. A UV array always holds 8 floats per face (4 slots × u, v);
/// a "slot" index is face * 4 + vertex. V grows downward (DirectX texture space), like the files.
/// </summary>
public static class Uv
{
    /// <summary>The UV sets of the LOD, ascending; always starts with 0.</summary>
    public static int[] Sets(Lod lod)
    {
        var sets = new SortedSet<int> { 0 };
        foreach (var t in lod.UVSets) sets.Add((int)t.Stage);
        return [.. sets];
    }

    /// <summary>The UVs of a set (shared array — do not modify); a set the LOD does not have reads as zeros.</summary>
    public static float[] Get(Lod lod, int set)
    {
        if (set == 0) return lod.FaceUV;
        var uv = lod.UVSet((uint)set)?.UV;
        return uv is not null && uv.Length >= lod.FaceCount * 8 ? uv : new float[lod.FaceCount * 8];
    }

    /// <summary>Replaces every UV of a set (creating its #UVSet# tag for k ≥ 1). Set 0 also updates a stage-0 tag.</summary>
    public static Lod Put(Lod lod, int set, float[] uv)
    {
        if (set < 0) throw new ArgumentOutOfRangeException(nameof(set));
        if (uv.Length != lod.FaceCount * 8) throw new ArgumentException($"Expected {lod.FaceCount * 8} UV values, got {uv.Length}.");
        foreach (var x in uv)
            if (!float.IsFinite(x)) throw new ArgumentException("UV values must be finite numbers.");
        var tags = lod.Tags;
        bool found = false;
        for (int t = 0; t < tags.Length; t++)
        {
            if (tags[t] is not UVSetTag u || u.Stage != set) continue;
            found = true;
            if (!u.UV.AsSpan().SequenceEqual(uv)) tags = tags.SetItem(t, u with { UV = (float[])uv.Clone() });
        }
        if (set == 0)
        {
            bool same = lod.FaceUV.AsSpan().SequenceEqual(uv);
            if (same && tags == lod.Tags) return lod;
            return lod with { FaceUV = same ? lod.FaceUV : (float[])uv.Clone(), Tags = tags };
        }
        if (!found) tags = tags.Add(new UVSetTag(1, (uint)set, (float[])uv.Clone()));
        return tags == lod.Tags ? lod : lod with { Tags = tags };
    }

    /// <summary>Adds the next free set (largest + 1) as a copy of <paramref name="copyFrom"/>.</summary>
    public static Lod AddSet(Lod lod, int copyFrom, out int newSet)
    {
        newSet = Sets(lod)[^1] + 1;
        var tags = lod.Tags;
        // Object Builder writes set 0 as a tag too; keep the sets together and in order.
        if (lod.UVSet(0) is null && newSet == 1) tags = tags.Add(new UVSetTag(1, 0, (float[])lod.FaceUV.Clone()));
        tags = tags.Add(new UVSetTag(1, (uint)newSet, (float[])Get(lod, copyFrom).Clone()));
        return lod with { Tags = tags };
    }

    /// <summary>Removes a set (k ≥ 1); the face-record set 0 cannot be removed.</summary>
    public static Lod DeleteSet(Lod lod, int set)
    {
        if (set <= 0) throw new ArgumentException("UV set 0 is stored in the faces and cannot be deleted.");
        var tags = lod.Tags.RemoveAll(t => t is UVSetTag u && u.Stage == set);
        return tags.Length == lod.Tags.Length ? lod : lod with { Tags = tags };
    }

    // ---- slots ----------------------------------------------------------------------------------

    /// <summary>Slots (face * 4 + vertex) of the faces set in <paramref name="faces"/> (null = every face).</summary>
    public static int[] SlotsOf(Lod lod, byte[]? faces)
    {
        var list = new List<int>();
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faces is not null && (f >= faces.Length || faces[f] == 0)) continue;
            for (int j = 0; j < lod.FaceVertCount[f]; j++) list.Add(f * 4 + j);
        }
        return [.. list];
    }

    /// <summary>Bounds (min u, min v, max u, max v) of the slots; null for none.</summary>
    public static (float MinU, float MinV, float MaxU, float MaxV)? Bounds(float[] uv, IReadOnlyCollection<int> slots)
    {
        if (slots.Count == 0) return null;
        float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
        foreach (int s in slots)
        {
            float u = uv[s * 2], v = uv[s * 2 + 1];
            u0 = MathF.Min(u0, u);
            v0 = MathF.Min(v0, v);
            u1 = MathF.Max(u1, u);
            v1 = MathF.Max(v1, v);
        }
        return (u0, v0, u1, v1);
    }

    /// <summary>Centre of the slots' bounds.</summary>
    public static Vector2 Center(float[] uv, IReadOnlyCollection<int> slots) =>
        Bounds(uv, slots) is { } b ? new((b.MinU + b.MaxU) / 2, (b.MinV + b.MaxV) / 2) : Vector2.Zero;

    /// <summary>A copy of <paramref name="uv"/> with the slots moved by <paramref name="m"/> (row vectors: (u, v) · m).</summary>
    public static float[] Transform(float[] uv, IEnumerable<int> slots, Matrix3x2 m)
    {
        var r = (float[])uv.Clone();
        foreach (int s in slots)
        {
            var p = Vector2.Transform(new Vector2(uv[s * 2], uv[s * 2 + 1]), m);
            r[s * 2] = p.X;
            r[s * 2 + 1] = p.Y;
        }
        return r;
    }

    public static Matrix3x2 Rotation(float degrees, Vector2 pivot) => Matrix3x2.CreateRotation(degrees * MathF.PI / 180, pivot);
    public static Matrix3x2 Scale(float su, float sv, Vector2 pivot) => Matrix3x2.CreateScale(su, sv, pivot);

    /// <summary>Scales and moves the slots so their bounds fill 0..1 (<paramref name="keepAspect"/>: uniform scale, centred).</summary>
    public static float[] Fit(float[] uv, IReadOnlyCollection<int> slots, bool keepAspect)
    {
        if (Bounds(uv, slots) is not { } b) return uv;
        var (u0, v0, u1, v1) = b;
        float w = u1 - u0, h = v1 - v0;
        float su = w > 1e-9f ? 1 / w : 1, sv = h > 1e-9f ? 1 / h : 1;
        float ou = 0, ov = 0;
        if (keepAspect)
        {
            float s = MathF.Min(su, sv);
            su = sv = s;
            ou = (1 - w * s) / 2;
            ov = (1 - h * s) / 2;
        }
        var m = Matrix3x2.CreateTranslation(-u0, -v0) * Matrix3x2.CreateScale(su, sv) * Matrix3x2.CreateTranslation(ou, ov);
        return Transform(uv, slots, m);
    }

    /// <summary>
    /// Planar projection of the faces' points: u along <paramref name="uAxis"/>, v along −<paramref name="vAxis"/> (up in
    /// the view = up in the texture), then fitted to 0..1 keeping the aspect. Axes are RAW model directions.
    /// </summary>
    public static float[] Planar(Lod lod, float[] uv, byte[]? faces, Vector3 uAxis, Vector3 vAxis)
    {
        var slots = SlotsOf(lod, faces);
        if (slots.Length == 0) return uv;
        var r = (float[])uv.Clone();
        var p = lod.Positions;
        foreach (int s in slots)
        {
            int pt = (int)lod.FacePoints[s] * 3;
            var pos = new Vector3(p[pt], p[pt + 1], p[pt + 2]);
            r[s * 2] = Vector3.Dot(pos, uAxis);
            r[s * 2 + 1] = -Vector3.Dot(pos, vAxis);
        }
        return Fit(r, slots, keepAspect: true);
    }

    /// <summary>Planar projection along a model axis (0 = X, 1 = Y, 2 = Z), seen like the Front / Top / Left views.</summary>
    public static float[] PlanarAxis(Lod lod, float[] uv, byte[]? faces, int axis) => axis switch
    {
        0 => Planar(lod, uv, faces, Vector3.UnitZ, Vector3.UnitY), // along X: the side
        1 => Planar(lod, uv, faces, Vector3.UnitX, new Vector3(0, 0, 1)), // along Y: from the top, +Z (front) up
        _ => Planar(lod, uv, faces, Vector3.UnitX, Vector3.UnitY), // along Z: from the front
    };

    // ---- UV vertices and islands ------------------------------------------------------------------

    /// <summary>
    /// Every slot that shares the point and the UV of one of <paramref name="slots"/> (within 1e-5) among
    /// <paramref name="candidates"/> — what the UV editor moves together so welded UVs stay welded.
    /// </summary>
    public static HashSet<int> Welded(Lod lod, float[] uv, IEnumerable<int> slots, IReadOnlyCollection<int> candidates)
    {
        var keys = new HashSet<(uint, int, int)>();
        foreach (int s in slots) keys.Add(Key(lod, uv, s));
        var result = new HashSet<int>();
        foreach (int s in candidates)
            if (keys.Contains(Key(lod, uv, s))) result.Add(s);
        return result;
    }

    static (uint, int, int) Key(Lod lod, float[] uv, int s) =>
        (lod.FacePoints[s], (int)MathF.Round(uv[s * 2] * 1e5f), (int)MathF.Round(uv[s * 2 + 1] * 1e5f));

    /// <summary>
    /// Faces connected to <paramref name="face"/> in UV space (sharing an edge whose two ends have the same UVs on both
    /// faces), limited to <paramref name="faceMask"/> when given.
    /// </summary>
    public static HashSet<int> Island(Lod lod, float[] uv, int face, byte[]? faceMask)
    {
        var byEdge = new Dictionary<((uint, int, int), (uint, int, int)), List<int>>();
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faceMask is not null && (f >= faceMask.Length || faceMask[f] == 0)) continue;
            int n = lod.FaceVertCount[f];
            for (int j = 0; j < n; j++)
            {
                var a = Key(lod, uv, f * 4 + j);
                var b = Key(lod, uv, f * 4 + (j + 1) % n);
                var key = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
                if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = [];
                list.Add(f);
            }
        }
        var adjacent = new Dictionary<int, List<int>>();
        foreach (var list in byEdge.Values)
            for (int i = 0; i < list.Count; i++)
                for (int k = 0; k < list.Count; k++)
                    if (i != k)
                    {
                        if (!adjacent.TryGetValue(list[i], out var adj)) adjacent[list[i]] = adj = [];
                        adj.Add(list[k]);
                    }
        var island = new HashSet<int> { face };
        var stack = new Stack<int>();
        stack.Push(face);
        while (stack.Count > 0)
            if (adjacent.TryGetValue(stack.Pop(), out var adj))
                foreach (int g in adj)
                    if (island.Add(g)) stack.Push(g);
        return island;
    }
}
