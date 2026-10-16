using System.Numerics;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>Helpers shared by the geometry-operation features (Ops*Feature).</summary>
internal static class OpsUtil
{
    public static EditorState Ed => AppServices.Editor;

    public static bool HasSelection() => Selection.PeekSelected(Ed.Lod) is { IsEmpty: false };

    public static int SelectedPointCount() => Selection.PeekSelected(Ed.Lod) is { } s ? s.PointCount : 0;

    /// <summary>Selected faces; in points mode (no faces selected) the faces whose points are all selected.</summary>
    public static byte[] TargetFaces(Lod lod)
    {
        var sel = Selection.GetSelected(lod);
        return sel.FaceCount > 0 ? sel.Faces : Selection.FacesFromPoints(lod, sel.Points);
    }

    public static bool HasTargetFaces()
    {
        var lod = Ed.Lod;
        if (Selection.PeekSelected(lod) is not { } sel) return false;
        return sel.FaceCount > 0 || (sel.PointCount >= 3 && Selection.FacesFromPoints(lod, sel.Points).AsSpan().ContainsAnyExcept((byte)0));
    }

    public static Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Object Builder "Use Pin": the pin when it is set and Use Pin is on.</summary>
    public static Vec3? UsedPin() => Ed.Pin is { } p && Ed.Flag("transform.usePin", false) ? V(p) : null;

    /// <summary>Where new primitives go: the pin when Use Pin is on, else the pin when set, else the origin.</summary>
    public static Vec3 Placement() => UsedPin() ?? (Ed.Pin is { } p ? V(p) : Vec3.Zero);

    /// <summary>Applies an op to the active LOD as one undo step; ArgumentException messages go to the log.</summary>
    public static bool Apply(string label, Func<Lod, Lod> op)
    {
        bool done = false;
        Dlg.Try(() => done = Ed.EditLod(label, op));
        return done;
    }

    public static string Plural(int n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";
}

/// <summary>
/// Edit menu: selection tools (grow/shrink/connected, faces↔points, unselect triangles, deselect faces/points,
/// selection operators with a stored selection), hide/lock and Remove Faces (D).
/// </summary>
public sealed class OpsEditFeature : IFeature
{
    public int Order => 40;
    static EditorState Ed => AppServices.Editor;

    static (SelectionMask Mask, int Lod)? stored;

    static void SetSel(SelectionMask m, string label) => Ed.SetSelection(m, label);

    static byte[]? Blocked(Lod lod) => SelectTools.BlockedPoints(lod) is var b && b.AsSpan().ContainsAnyExcept((byte)0) ? b : null;

    static void Visibility(string label, Func<Lod, VisibilityResult> op, string verb)
    {
        int count = 0;
        OpsUtil.Apply(label, lod =>
        {
            var r = op(lod);
            count = r.Count;
            return r.Lod;
        });
        Ed.Log(count > 0 ? $"{verb} {OpsUtil.Plural(count, "point")}" : $"{label}: nothing to change");
    }

    public void Install()
    {
        bool facesMode() => Ed.SelectMode == SelectMode.Faces;
        Commands.Register(
            new Command
            {
                Id = "edit.growSelection",
                Label = "_Grow Selected",
                CanExecute = OpsUtil.HasSelection,
                Execute = () => SetSel(SelectTools.Grow(Ed.Lod, Ed.GetSelection(), facesMode(), Blocked(Ed.Lod)), "Grow selection"),
            },
            new Command
            {
                Id = "edit.shrinkSelection",
                Label = "_Shrink Selected",
                CanExecute = OpsUtil.HasSelection,
                Execute = () => SetSel(SelectTools.Shrink(Ed.Lod, Ed.GetSelection(), facesMode(), Blocked(Ed.Lod)), "Shrink selection"),
            },
            new Command
            {
                Id = "edit.selectConnected",
                Label = "Select _Connected",
                CanExecute = OpsUtil.HasSelection,
                Execute = () => SetSel(SelectTools.Connected(Ed.Lod, Ed.GetSelection(), Blocked(Ed.Lod)), "Select connected"),
            },
            new Command
            {
                Id = "edit.facesFromPoints",
                Label = "Select _Faces from Points",
                CanExecute = OpsUtil.HasSelection,
                Execute = () => SetSel(SelectTools.FacesFromPoints(Ed.Lod, Ed.GetSelection()), "Faces from points"),
            },
            new Command
            {
                Id = "edit.pointsFromFaces",
                Label = "Select _Points from Faces",
                CanExecute = OpsUtil.HasSelection,
                Execute = () => SetSel(SelectTools.PointsFromFaces(Ed.Lod, Ed.GetSelection()), "Points from faces"),
            },
            new Command
            {
                Id = "edit.unselectTriangles",
                Label = "Unselect _Triangles",
                CanExecute = OpsUtil.HasSelection,
                Execute = () => SetSel(SelectTools.UnselectTriangles(Ed.Lod, Ed.GetSelection()), "Unselect triangles"),
            },
            new Command
            {
                Id = "edit.deselectFaces",
                Label = "Deselect F_aces",
                CanExecute = OpsUtil.HasSelection,
                Execute = () => SetSel(SelectTools.DeselectFaces(Ed.GetSelection()), "Deselect faces"),
            },
            new Command
            {
                Id = "edit.deselectPoints",
                Label = "Deselect _Vertices",
                CanExecute = OpsUtil.HasSelection,
                Execute = () => SetSel(SelectTools.DeselectPoints(Ed.GetSelection()), "Deselect vertices"),
            },
            new Command
            {
                Id = "edit.storeSelection",
                Label = "_Copy (Store Selection)",
                CanExecute = OpsUtil.HasSelection,
                Execute = () =>
                {
                    var s = Ed.GetSelection();
                    stored = (s, Ed.ActiveLod);
                    Ed.Log($"Stored selection: {OpsUtil.Plural(s.PointCount, "point")}, {OpsUtil.Plural(s.FaceCount, "face")}");
                },
            },
            StoredOp("edit.recallSelection", "_Recall", StoredSelectionOp.Recall),
            StoredOp("edit.unionStored", "_Union", StoredSelectionOp.Union),
            StoredOp("edit.complementStored", "C_omplement", StoredSelectionOp.Complement),
            StoredOp("edit.intersectStored", "_Intersection", StoredSelectionOp.Intersect),
            new Command
            {
                Id = "edit.hideSelection",
                Label = "_Hide Selection",
                Hotkeys = ["Ctrl+H"],
                CanExecute = OpsUtil.HasSelection,
                Execute = () =>
                {
                    var s = Ed.GetSelection();
                    var pts = Selection.PointsFromFaces(Ed.Lod, s.Faces, (byte[])s.Points.Clone());
                    Visibility("Hide", lod => SelectTools.Hide(lod, pts), "Hidden");
                },
            },
            new Command { Id = "edit.unhideAll", Label = "_Unhide", Hotkeys = ["Ctrl+Shift+H"], Execute = () => Visibility("Unhide", SelectTools.UnhideAll, "Unhidden") },
            new Command
            {
                Id = "edit.lockSelection",
                Label = "_Lock Selection",
                Hotkeys = ["Ctrl+L"],
                CanExecute = OpsUtil.HasSelection,
                Execute = () =>
                {
                    var s = Ed.GetSelection();
                    var pts = Selection.PointsFromFaces(Ed.Lod, s.Faces, (byte[])s.Points.Clone());
                    Visibility("Lock", lod => SelectTools.Lock(lod, pts), "Locked");
                },
            },
            new Command { Id = "edit.unlockAll", Label = "U_nlock", Hotkeys = ["Ctrl+Shift+L"], Execute = () => Visibility("Unlock", SelectTools.UnlockAll, "Unlocked") },
            new Command
            {
                Id = "edit.deleteFaces",
                Label = "Remove _Faces (keep points)",
                Hotkeys = ["D"],
                CanExecute = OpsUtil.HasTargetFaces,
                Execute = () =>
                {
                    int n = 0;
                    if (OpsUtil.Apply("Remove faces", lod =>
                        {
                            var faces = OpsUtil.TargetFaces(lod);
                            n = MeshMath.CountSet(faces);
                            return n == 0 ? lod : FaceOps.Remove(lod, faces);
                        }))
                        Ed.Log($"Removed {OpsUtil.Plural(n, "face")} (points kept)");
                },
            });
    }

    static Command StoredOp(string id, string label, StoredSelectionOp op) => new()
    {
        Id = id,
        Label = label,
        CanExecute = () => stored is not null,
        Execute = () =>
        {
            if (stored is not { } st) return;
            var lod = Ed.Lod;
            if (st.Lod != Ed.ActiveLod) Ed.Log("The stored selection comes from another LOD; indices are used as they are", LogLevel.Warn);
            SetSel(SelectTools.CombineWithStored(lod, Ed.GetSelection(), st.Mask, op, Blocked(lod)), $"Selection operator: {op}");
        },
    };
}
