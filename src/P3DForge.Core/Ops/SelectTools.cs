using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>Combination of the current selection with a stored one (Object Builder "Selection operators").</summary>
public enum StoredSelectionOp
{
    Union,
    /// <summary>Current minus stored.</summary>
    Complement,
    Intersect,
    Recall,
}

/// <summary>Result of a hide/lock operation.</summary>
/// <param name="Lod">The new LOD (the input instance when nothing changed).</param>
/// <param name="Count">Points whose state changed.</param>
public sealed record VisibilityResult(Lod Lod, int Count);

/// <summary>
/// Selection tools (pure mask computations) and visibility (hide / lock) operations. Masks: 0 = unselected, non-zero =
/// selected. <c>blocked</c> = hidden ∪ locked points: they are never added to a selection, and faces using them are
/// never selected. Results are fresh arrays (never the inputs).
/// </summary>
public static class SelectTools
{
    /// <summary>Hidden ∪ locked points of a LOD.</summary>
    public static byte[] BlockedPoints(Lod lod)
    {
        var h = Selection.HiddenPoints(lod);
        var l = Selection.LockedPoints(lod);
        for (int i = 0; i < h.Length; i++) h[i] = h[i] != 0 || l[i] != 0 ? (byte)1 : (byte)0;
        return h;
    }

    /// <summary>Clears blocked points and faces using blocked points (in place) and returns the mask.</summary>
    static SelectionMask ApplyBlocked(Lod lod, SelectionMask m, byte[]? blocked)
    {
        if (blocked is null) return m;
        bool any = false;
        for (int i = 0; i < m.Points.Length; i++)
            if (blocked[i] != 0)
            {
                m.Points[i] = 0;
                any = true;
            }
        if (any)
        {
            var bad = Selection.FacesTouching(lod, blocked);
            for (int f = 0; f < m.Faces.Length; f++)
                if (bad[f] != 0) m.Faces[f] = 0;
        }
        return m;
    }

    static byte[] Normalized(byte[] a)
    {
        var o = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) o[i] = a[i] != 0 ? (byte)1 : (byte)0;
        return o;
    }

    /// <summary>
    /// Grow: points mode adds every point of a face touching a selected point (faces follow the points); faces mode adds
    /// every face sharing a point with a selected face.
    /// </summary>
    public static SelectionMask Grow(Lod lod, SelectionMask sel, bool facesMode, byte[]? blocked = null)
    {
        if (facesMode)
        {
            var pts = Selection.PointsFromFaces(lod, sel.Faces);
            var touch = Selection.FacesTouching(lod, pts);
            var faces = Normalized(sel.Faces);
            for (int f = 0; f < faces.Length; f++)
                if (touch[f] != 0) faces[f] = 1;
            var points = Normalized(sel.Points);
            Selection.PointsFromFaces(lod, faces, points);
            return ApplyBlocked(lod, new SelectionMask(points, faces), blocked);
        }
        var touched = Selection.FacesTouching(lod, sel.Points);
        var p = Normalized(sel.Points);
        Selection.PointsFromFaces(lod, touched, p);
        if (blocked is not null)
            for (int i = 0; i < p.Length; i++)
                if (blocked[i] != 0) p[i] = 0;
        return ApplyBlocked(lod, new SelectionMask(p, Selection.FacesFromPoints(lod, p)), blocked);
    }

    /// <summary>
    /// Shrink: points mode removes selected points used by a face that has an unselected point; faces mode removes
    /// selected faces sharing a point with an unselected face.
    /// </summary>
    public static SelectionMask Shrink(Lod lod, SelectionMask sel, bool facesMode, byte[]? blocked = null)
    {
        var fp = lod.FacePoints;
        if (facesMode)
        {
            var outside = new byte[lod.PointCount]; // points touched by an unselected face
            for (int f = 0; f < lod.FaceCount; f++)
            {
                if (sel.Faces[f] != 0) continue;
                for (int k = 0; k < lod.FaceVertCount[f]; k++) outside[fp[f * 4 + k]] = 1;
            }
            var faces = new byte[lod.FaceCount];
            for (int f = 0; f < lod.FaceCount; f++)
            {
                if (sel.Faces[f] == 0) continue;
                bool keep = true;
                for (int k = 0; k < lod.FaceVertCount[f] && keep; k++)
                    if (outside[fp[f * 4 + k]] != 0) keep = false;
                faces[f] = keep ? (byte)1 : (byte)0;
            }
            var points = Selection.PointsFromFaces(lod, faces);
            var used = Structure.PointsUsedByFaces(lod); // selected points without any face stay selected
            for (int i = 0; i < points.Length; i++)
                if (sel.Points[i] != 0 && used[i] == 0) points[i] = 1;
            return ApplyBlocked(lod, new SelectionMask(points, faces), blocked);
        }
        var boundary = new byte[lod.PointCount];
        for (int f = 0; f < lod.FaceCount; f++)
        {
            int nv = lod.FaceVertCount[f];
            bool partial = false;
            for (int k = 0; k < nv; k++)
                if (sel.Points[fp[f * 4 + k]] == 0)
                {
                    partial = true;
                    break;
                }
            if (!partial) continue;
            for (int k = 0; k < nv; k++) boundary[fp[f * 4 + k]] = 1;
        }
        var p = new byte[lod.PointCount];
        for (int i = 0; i < p.Length; i++) p[i] = sel.Points[i] != 0 && boundary[i] == 0 ? (byte)1 : (byte)0;
        return ApplyBlocked(lod, new SelectionMask(p, Selection.FacesFromPoints(lod, p)), blocked);
    }

    /// <summary>Everything connected (through shared points of faces) to the selected points or faces; propagation does not pass through blocked points.</summary>
    public static SelectionMask Connected(Lod lod, SelectionMask sel, byte[]? blocked = null)
    {
        int n = lod.PointCount;
        var points = Normalized(sel.Points);
        Selection.PointsFromFaces(lod, sel.Faces, points);
        if (blocked is not null)
            for (int i = 0; i < n; i++)
                if (blocked[i] != 0) points[i] = 0;
        var adj = PointFaces.Build(lod);
        var queue = new int[n];
        int head = 0, tail = 0;
        for (int i = 0; i < n; i++)
            if (points[i] != 0) queue[tail++] = i;
        var doneFace = new bool[lod.FaceCount];
        var fp = lod.FacePoints;
        while (head < tail)
        {
            int p = queue[head++];
            for (int s = adj.Start[p]; s < adj.Start[p + 1]; s++)
            {
                int f = adj.Faces[s];
                if (doneFace[f]) continue;
                doneFace[f] = true;
                for (int k = 0; k < lod.FaceVertCount[f]; k++)
                {
                    uint q = fp[f * 4 + k];
                    if (points[q] != 0 || (blocked is not null && blocked[q] != 0)) continue;
                    points[q] = 1;
                    queue[tail++] = (int)q;
                }
            }
        }
        return ApplyBlocked(lod, new SelectionMask(points, Selection.FacesFromPoints(lod, points)), blocked);
    }

    /// <summary>"Select faces from points": faces whose every point is selected; points unchanged.</summary>
    public static SelectionMask FacesFromPoints(Lod lod, SelectionMask sel) =>
        new((byte[])sel.Points.Clone(), Selection.FacesFromPoints(lod, sel.Points));

    /// <summary>"Select points from faces": exactly the points of the selected faces; faces unchanged.</summary>
    public static SelectionMask PointsFromFaces(Lod lod, SelectionMask sel) =>
        new(Selection.PointsFromFaces(lod, sel.Faces), (byte[])sel.Faces.Clone());

    /// <summary>Deselects selected triangles; their points stay selected only when a remaining selected face uses them (or no deselected triangle does).</summary>
    public static SelectionMask UnselectTriangles(Lod lod, SelectionMask sel)
    {
        var faces = (byte[])sel.Faces.Clone();
        var dropped = new byte[lod.FaceCount];
        bool any = false;
        for (int f = 0; f < faces.Length; f++)
            if (faces[f] != 0 && lod.FaceVertCount[f] == 3)
            {
                faces[f] = 0;
                dropped[f] = 1;
                any = true;
            }
        var points = (byte[])sel.Points.Clone();
        if (!any) return new SelectionMask(points, faces);
        var keep = Selection.PointsFromFaces(lod, faces);
        var loose = Selection.PointsFromFaces(lod, dropped);
        for (int i = 0; i < points.Length; i++)
            if (points[i] != 0 && loose[i] != 0 && keep[i] == 0) points[i] = 0;
        return new SelectionMask(points, faces);
    }

    /// <summary>"Deselect Faces": clears the face part of the selection; points stay selected.</summary>
    public static SelectionMask DeselectFaces(SelectionMask sel) => new((byte[])sel.Points.Clone(), new byte[sel.Faces.Length]);

    /// <summary>"Deselect Vertices": clears the point part of the selection; faces stay selected.</summary>
    public static SelectionMask DeselectPoints(SelectionMask sel) => new(new byte[sel.Points.Length], (byte[])sel.Faces.Clone());

    /// <summary>
    /// Combines the current selection with a stored one. Sizes may differ when the LOD changed since the selection was
    /// stored: missing entries count as unselected.
    /// </summary>
    public static SelectionMask CombineWithStored(Lod lod, SelectionMask cur, SelectionMask stored, StoredSelectionOp op, byte[]? blocked = null)
    {
        byte[] Comb(byte[] a, byte[] b, int len)
        {
            var o = new byte[len];
            for (int i = 0; i < len; i++)
            {
                bool x = i < a.Length && a[i] != 0, y = i < b.Length && b[i] != 0;
                bool r = op switch
                {
                    StoredSelectionOp.Union => x || y,
                    StoredSelectionOp.Complement => x && !y,
                    StoredSelectionOp.Intersect => x && y,
                    _ => y,
                };
                o[i] = r ? (byte)1 : (byte)0;
            }
            return o;
        }
        return ApplyBlocked(lod, new SelectionMask(Comb(cur.Points, stored.Points, lod.PointCount), Comb(cur.Faces, stored.Faces, lod.FaceCount)), blocked);
    }

    // ---- visibility ---------------------------------------------------------------------------------------

    static Lod DeselectBlocked(Lod lod)
    {
        if (lod.State(StateKind.Selected) is null) return lod;
        var blocked = BlockedPoints(lod);
        var sel = Selection.GetSelected(lod);
        bool changed = false;
        for (int i = 0; i < sel.Points.Length; i++)
            if (blocked[i] != 0 && sel.Points[i] != 0)
            {
                sel.Points[i] = 0;
                changed = true;
            }
        var bad = Selection.FacesTouching(lod, blocked);
        for (int f = 0; f < sel.Faces.Length; f++)
            if (bad[f] != 0 && sel.Faces[f] != 0)
            {
                sel.Faces[f] = 0;
                changed = true;
            }
        return changed ? Selection.WithSelected(lod, sel) : lod;
    }

    static VisibilityResult SetState(Lod lod, uint bit, StateKind kind, byte[]? mask, bool on)
    {
        var cur = kind == StateKind.Hidden ? Selection.HiddenPoints(lod) : Selection.LockedPoints(lod);
        int count = 0;
        for (int i = 0; i < lod.PointCount; i++)
        {
            if (mask is not null && mask[i] == 0) continue;
            if ((cur[i] != 0) != on) count++;
        }
        if (count == 0) return new VisibilityResult(lod, 0);
        var next = Selection.WithPointStateFlag(lod, bit, kind, mask, on);
        if (on) next = DeselectBlocked(next);
        return new VisibilityResult(next, count);
    }

    /// <summary>Hides the points in <paramref name="mask"/> (flag 0x01000000 + #Hidden# when present) and deselects them.</summary>
    public static VisibilityResult Hide(Lod lod, byte[] mask) => SetState(lod, PointFlags.Hidden, StateKind.Hidden, mask, true);

    /// <summary>Unhides every point.</summary>
    public static VisibilityResult UnhideAll(Lod lod) => SetState(lod, PointFlags.Hidden, StateKind.Hidden, null, false);

    /// <summary>Locks the points in <paramref name="mask"/> (flag 0x02000000 + #Lock# when present) and deselects them.</summary>
    public static VisibilityResult Lock(Lod lod, byte[] mask) => SetState(lod, PointFlags.Locked, StateKind.Lock, mask, true);

    /// <summary>Unlocks every point.</summary>
    public static VisibilityResult UnlockAll(Lod lod) => SetState(lod, PointFlags.Locked, StateKind.Lock, null, false);
}
