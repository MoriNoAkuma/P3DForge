using System.Collections.Immutable;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>Selection mask: 0 = unselected, non-zero = selected (encoded weight, normally 1).</summary>
public readonly record struct SelectionMask(byte[] Points, byte[] Faces)
{
    public static SelectionMask Empty(Lod lod) => new(new byte[lod.PointCount], new byte[lod.FaceCount]);
    public int PointCount => CountNonZero(Points);
    public int FaceCount => CountNonZero(Faces);
    public bool IsEmpty => !Points.AsSpan().ContainsAnyExcept((byte)0) && !Faces.AsSpan().ContainsAnyExcept((byte)0);

    public static int CountNonZero(byte[] a)
    {
        int n = 0;
        foreach (byte b in a)
            if (b != 0) n++;
        return n;
    }
}

public enum MaskOp
{
    Set,
    Add,
    Subtract,
    Intersect,
    Xor,
}

/// <summary>
/// Editing-selection helpers. The current selection of a LOD lives in its <c>#Selected#</c> state tag
/// (Object Builder saves it in the file too), so it is part of undo snapshots.
/// </summary>
public static class Selection
{
    /// <summary>Fresh (safe to modify) copy of the LOD's current selection; zeros if it has none.</summary>
    public static SelectionMask GetSelected(Lod lod)
    {
        var t = lod.State(StateKind.Selected);
        return t is null ? SelectionMask.Empty(lod) : new((byte[])t.Points.Clone(), (byte[])t.Faces.Clone());
    }

    /// <summary>Read-only view of the current selection (do NOT modify the arrays), or null.</summary>
    public static SelectionMask? PeekSelected(Lod lod)
    {
        var t = lod.State(StateKind.Selected);
        return t is null ? null : new SelectionMask(t.Points, t.Faces);
    }

    /// <summary>Returns a LOD whose state tag <paramref name="kind"/> holds <paramref name="mask"/> (replaced in place or prepended).</summary>
    public static Lod WithStateTag(Lod lod, StateKind kind, SelectionMask mask)
    {
        if (mask.Points.Length != lod.PointCount || mask.Faces.Length != lod.FaceCount)
            throw new ArgumentException($"{kind} mask sized {mask.Points.Length}/{mask.Faces.Length}, LOD has {lod.PointCount}/{lod.FaceCount}");
        int idx = -1;
        for (int i = 0; i < lod.Tags.Length; i++)
            if (lod.Tags[i] is StateTag s && s.Kind == kind) idx = i;
        var tag = new StateTag(idx >= 0 ? lod.Tags[idx].Active : (byte)1, kind, mask.Points, mask.Faces);
        return lod with { Tags = idx >= 0 ? lod.Tags.SetItem(idx, tag) : lod.Tags.Insert(0, tag) };
    }

    public static Lod WithSelected(Lod lod, SelectionMask mask) => WithStateTag(lod, StateKind.Selected, mask);

    /// <summary>Faces whose every vertex is selected in <paramref name="points"/>.</summary>
    public static byte[] FacesFromPoints(Lod lod, byte[] points)
    {
        var faces = new byte[lod.FaceCount];
        var fp = lod.FacePoints;
        for (int f = 0; f < faces.Length; f++)
        {
            bool all = true;
            for (int k = 0; k < lod.FaceVertCount[f]; k++)
                if (points[fp[f * 4 + k]] == 0)
                {
                    all = false;
                    break;
                }
            if (all) faces[f] = 1;
        }
        return faces;
    }

    /// <summary>Points used by faces selected in <paramref name="faces"/>, OR-ed into <paramref name="into"/> (or a new array).</summary>
    public static byte[] PointsFromFaces(Lod lod, byte[] faces, byte[]? into = null)
    {
        var points = into ?? new byte[lod.PointCount];
        var fp = lod.FacePoints;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faces[f] == 0) continue;
            for (int k = 0; k < lod.FaceVertCount[f]; k++) points[fp[f * 4 + k]] = 1;
        }
        return points;
    }

    /// <summary>Selection mask of a named selection (weights preserved), or null.</summary>
    public static SelectionMask? MaskOfNamed(Lod lod, string name)
    {
        var s = lod.Selection(name);
        return s is null ? null : new SelectionMask((byte[])s.Points.Clone(), (byte[])s.Faces.Clone());
    }

    /// <summary>Combines two masks element-wise; result values are 0/1.</summary>
    public static byte[] Combine(byte[] a, byte[] b, MaskOp op)
    {
        var o = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            bool x = a[i] != 0, y = b[i] != 0;
            bool r = op switch
            {
                MaskOp.Set => y,
                MaskOp.Add => x || y,
                MaskOp.Subtract => x && !y,
                MaskOp.Intersect => x && y,
                _ => x != y,
            };
            o[i] = r ? (byte)1 : (byte)0;
        }
        return o;
    }

    /// <summary>Hidden points: point flag 0x01000000 or the <c>#Hidden#</c> tag.</summary>
    public static byte[] HiddenPoints(Lod lod) => FlagOrTag(lod, PointFlags.Hidden, StateKind.Hidden);

    /// <summary>Locked points (not selectable/editable): point flag 0x02000000 or the <c>#Lock#</c> tag.</summary>
    public static byte[] LockedPoints(Lod lod) => FlagOrTag(lod, PointFlags.Locked, StateKind.Lock);

    static byte[] FlagOrTag(Lod lod, uint bit, StateKind kind)
    {
        var o = new byte[lod.PointCount];
        var tag = lod.State(kind);
        for (int i = 0; i < o.Length; i++)
            if ((lod.PointFlags[i] & bit) != 0 || (tag is not null && tag.Points[i] != 0)) o[i] = 1;
        return o;
    }

    /// <summary>A face is hidden when any of its points is hidden.</summary>
    public static byte[] FacesTouching(Lod lod, byte[] points)
    {
        var o = new byte[lod.FaceCount];
        var fp = lod.FacePoints;
        for (int f = 0; f < o.Length; f++)
            for (int k = 0; k < lod.FaceVertCount[f]; k++)
                if (points[fp[f * 4 + k]] != 0)
                {
                    o[f] = 1;
                    break;
                }
        return o;
    }

    /// <summary>Points that are neither hidden nor locked.</summary>
    public static byte[] SelectablePoints(Lod lod)
    {
        var h = HiddenPoints(lod);
        var l = LockedPoints(lod);
        var o = new byte[h.Length];
        for (int i = 0; i < o.Length; i++) o[i] = h[i] == 0 && l[i] == 0 ? (byte)1 : (byte)0;
        return o;
    }

    /// <summary>Sets/clears a special point flag (hidden/locked) for the points in <paramref name="mask"/> (all when null); keeps a matching state tag in sync.</summary>
    public static Lod WithPointStateFlag(Lod lod, uint bit, StateKind kind, byte[]? mask, bool on)
    {
        var flags = (uint[])lod.PointFlags.Clone();
        for (int i = 0; i < flags.Length; i++)
        {
            if (mask is not null && mask[i] == 0) continue;
            flags[i] = on ? flags[i] | bit : flags[i] & ~bit;
        }
        var next = lod with { PointFlags = flags };
        var tag = lod.State(kind);
        if (tag is not null)
        {
            var pts = (byte[])tag.Points.Clone();
            for (int i = 0; i < pts.Length; i++)
            {
                if (mask is not null && mask[i] == 0) continue;
                pts[i] = on ? (byte)1 : (byte)0;
            }
            next = WithStateTag(next, kind, new SelectionMask(pts, FacesTouching(lod, pts)));
        }
        return next;
    }
}
