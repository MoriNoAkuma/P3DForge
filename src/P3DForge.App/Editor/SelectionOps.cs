using P3DForge.Core.Ops;

namespace P3DForge.App.Editor;

public enum SelectOp
{
    Set,
    Add,
    Subtract,
    Toggle,
}

/// <summary>
/// High-level selection changes shared by the viewport, panels and commands so every entry point follows
/// the same rules: hidden/locked points are never selected; in Points mode a face is selected iff all its
/// points are; in Faces mode selecting faces also selects their points.
/// </summary>
public static class SelectionOps
{
    static EditorState E => AppServices.Editor;

    static byte[] Combine(byte[] cur, IReadOnlyList<int>? indices, byte[]? mask, SelectOp op)
    {
        var o = op == SelectOp.Set ? new byte[cur.Length] : (byte[])cur.Clone();
        void Apply(int i)
        {
            if (op == SelectOp.Subtract) o[i] = 0;
            else if (op == SelectOp.Toggle) o[i] = cur[i] != 0 ? (byte)0 : (byte)1;
            else o[i] = 1;
        }
        if (mask is not null)
        {
            for (int i = 0; i < mask.Length; i++)
                if (mask[i] != 0) Apply(i);
        }
        else if (indices is not null)
        {
            foreach (int i in indices) Apply(i);
        }
        return o;
    }

    /// <summary>Changes the point selection of the active LOD by a mask or a list of point indices.</summary>
    public static void SelectPoints(byte[]? mask, IReadOnlyList<int>? indices, SelectOp op, string label = "Select points")
    {
        var lod = E.Lod;
        var cur = Selection.GetSelected(lod);
        var next = Combine(cur.Points, indices, mask, op);
        var ok = Selection.SelectablePoints(lod);
        for (int i = 0; i < next.Length; i++)
            if (ok[i] == 0) next[i] = 0;
        E.SetSelection(new SelectionMask(next, Selection.FacesFromPoints(lod, next)), label);
    }

    /// <summary>Changes the face selection of the active LOD by a mask or a list of face indices.</summary>
    public static void SelectFaces(byte[]? mask, IReadOnlyList<int>? indices, SelectOp op, string label = "Select faces")
    {
        var lod = E.Lod;
        var cur = Selection.GetSelected(lod);
        var faces = Combine(cur.Faces, indices, mask, op);
        var ok = Selection.SelectablePoints(lod);
        var blocked = new byte[ok.Length];
        for (int i = 0; i < ok.Length; i++) blocked[i] = ok[i] == 0 ? (byte)1 : (byte)0;
        var blockedFaces = Selection.FacesTouching(lod, blocked);
        for (int f = 0; f < faces.Length; f++)
            if (blockedFaces[f] != 0) faces[f] = 0;
        var pts = op == SelectOp.Add ? (byte[])cur.Points.Clone() : new byte[lod.PointCount];
        Selection.PointsFromFaces(lod, faces, pts);
        if (op is SelectOp.Subtract or SelectOp.Toggle)
        {
            // Keep points that were selected on their own (not through a face).
            var viaFaces = Selection.PointsFromFaces(lod, cur.Faces);
            for (int i = 0; i < pts.Length; i++)
                if (cur.Points[i] != 0 && viaFaces[i] == 0) pts[i] = 1;
        }
        for (int i = 0; i < pts.Length; i++)
            if (ok[i] == 0) pts[i] = 0;
        E.SetSelection(new SelectionMask(pts, faces), label);
    }

    /// <summary>Selects the contents of a named selection of the active LOD. Returns false if it does not exist.</summary>
    public static bool SelectNamed(string name, SelectOp op)
    {
        var lod = E.Lod;
        var s = lod.Selection(name);
        if (s is null) return false;
        var cur = Selection.GetSelected(lod);
        var pts = Combine(cur.Points, null, s.Points, op);
        var faces = Combine(cur.Faces, null, s.Faces, op);
        var ok = Selection.SelectablePoints(lod);
        for (int i = 0; i < pts.Length; i++)
            if (ok[i] == 0) pts[i] = 0;
        E.SetSelection(new SelectionMask(pts, faces), $"Select \"{s.Name}\"");
        return true;
    }

    public static void Clear() => E.SetSelection(SelectionMask.Empty(E.Lod), "Deselect");
}
