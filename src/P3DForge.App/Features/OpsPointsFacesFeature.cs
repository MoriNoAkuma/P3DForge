using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Ops;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// Points menu (navigation, isolated, merge, merge near, flatten, snap to grid) and Faces menu (reverse, uncross,
/// triangulate, squarize, face order, recalculate normals, sharp/smooth edges).
/// </summary>
public sealed class OpsPointsFacesFeature : IFeature
{
    public int Order => 41;
    static EditorState Ed => AppServices.Editor;

    static double lastMergeDistance = 0.001, lastSnapStep = 0.1;
    static int lastFlattenPlane = 1;

    public void Install()
    {
        Commands.Register(
            Navigate("points.selectFirst", "Select _First Point", "Ctrl+Home", PointNavigation.First),
            Navigate("points.selectNext", "Select _Next Point", "Ctrl+PageDown", PointNavigation.Next),
            Navigate("points.selectPrev", "Select _Previous Point", "Ctrl+PageUp", PointNavigation.Previous),
            new Command
            {
                Id = "points.selectIsolated",
                Label = "Select _Isolated",
                Execute = () =>
                {
                    var lod = Ed.Lod;
                    var iso = PointOps.Isolated(lod, SelectTools.BlockedPoints(lod));
                    int n = MeshMath.CountSet(iso);
                    SelectionOps.SelectPoints(iso, null, SelectOp.Set, "Select isolated points");
                    Ed.Log(n > 0 ? $"{OpsUtil.Plural(n, "isolated point")} selected" : "No isolated points");
                },
            },
            new Command
            {
                Id = "points.merge",
                Label = "_Merge",
                Hotkeys = ["Shift+D", "Shift+M"],
                CanExecute = () => OpsUtil.SelectedPointCount() >= 2,
                Execute = () =>
                {
                    MergeResult? r = null;
                    var target = OpsUtil.UsedPin();
                    if (OpsUtil.Apply("Merge points", lod =>
                        {
                            r = PointOps.Merge(lod, SelectedPoints(lod), target);
                            return r.Lod;
                        }))
                        Ed.Log($"Merged {OpsUtil.Plural(r!.RemovedPoints + 1, "point")} into one{(target is null ? "" : " at the pin")}" + Removed(r));
                },
            },
            new Command
            {
                Id = "points.mergeNear",
                Label = "Merge _Near…",
                Execute = () =>
                {
                    var lod = Ed.Lod;
                    var sel = Selection.PeekSelected(lod);
                    bool selectionOnly = sel is { } s && s.PointCount >= 2;
                    var form = new Form("Merge Near", selectionOnly
                            ? $"Merges points of the selection ({OpsUtil.Plural(sel!.Value.PointCount, "point")}) closer than the distance."
                            : "Merges all points of the LOD closer than the distance (nothing is selected).")
                        .Number("distance", "Distance (m)", lastMergeDistance, 0, 1e6)
                        .Action("Detect closest pair", f =>
                        {
                            var d = PointOps.ClosestPointDistance(Ed.Lod.Positions, selectionOnly ? SelectedPoints(Ed.Lod) : null);
                            if (d is { } v) f.SetNumber("distance", v * 1.0001);
                            Ed.Log(d is { } dd ? $"Closest distinct points: {dd:0.######} m" : "No two distinct points");
                        });
                    if (!form.Show("Merge")) return;
                    lastMergeDistance = form.Num("distance");
                    MergeResult? r = null;
                    if (OpsUtil.Apply("Merge near", l =>
                        {
                            r = PointOps.MergeNear(l, selectionOnly ? SelectedPoints(l) : null, lastMergeDistance);
                            return r.Lod;
                        }))
                        Ed.Log($"Merge near {lastMergeDistance:0.######} m: {OpsUtil.Plural(r!.Groups, "group")}, {OpsUtil.Plural(r.RemovedPoints, "point")} removed" + Removed(r));
                    else Ed.Log($"Merge near {lastMergeDistance:0.######} m: nothing to merge");
                },
            },
            new Command
            {
                Id = "points.flatten",
                Label = "_Flatten Points…",
                Hotkeys = ["P"],
                CanExecute = () => OpsUtil.SelectedPointCount() >= 2,
                Execute = Flatten,
            },
            new Command
            {
                Id = "points.snapToGrid",
                Label = "_Snap to Grid…",
                CanExecute = () => OpsUtil.SelectedPointCount() >= 1,
                Execute = () =>
                {
                    var form = new Form("Snap to Grid", "Rounds the coordinates of the selected points to the grid (relative to the pin when Use Pin is on).")
                        .Number("step", "Grid step (m)", lastSnapStep, 1e-6, 1e6);
                    if (!form.Show("Snap")) return;
                    lastSnapStep = form.Num("step");
                    int moved = 0;
                    var origin = OpsUtil.UsedPin() ?? Vec3.Zero;
                    OpsUtil.Apply("Snap to grid", lod =>
                    {
                        var r = PointOps.SnapToGrid(lod, SelectedPoints(lod), lastSnapStep, origin);
                        moved = r.Moved;
                        return r.Lod;
                    });
                    Ed.Log($"Snapped {OpsUtil.Plural(moved, "point")} to a {lastSnapStep:0.######} m grid");
                },
            },
            FaceCommand("faces.reverse", "_Reverse", ["W"], "Reverse faces", (lod, m) => (FaceOps.Reverse(lod, m), MeshMath.CountSet(m)), "Reversed {0}"),
            FaceCommand("faces.uncross", "_Uncross", ["Q"], "Uncross faces", (lod, m) => FaceOps.Uncross(lod, m), "Uncrossed {0}", quadsOnly: true),
            FaceCommand("faces.triangulate", "Triangulate \\", ["\\"], "Triangulate", (lod, m) => FaceOps.Triangulate(lod, m), "Split {0} into triangles", quadsOnly: true),
            FaceCommand("faces.triangulateAlt", "Triangulate /", ["/"], "Triangulate", (lod, m) => FaceOps.Triangulate(lod, m, diagonal13: true), "Split {0} into triangles", quadsOnly: true),
            new Command
            {
                Id = "faces.squarize",
                Label = "_Squarize (triangles → quads)",
                Execute = () =>
                {
                    int merged = 0;
                    bool any = OpsUtil.HasTargetFaces();
                    OpsUtil.Apply("Squarize", lod =>
                    {
                        var r = FaceOps.Squarize(lod, any ? OpsUtil.TargetFaces(lod) : null);
                        merged = r.Merged;
                        return r.Lod;
                    });
                    Ed.Log($"Squarize{(any ? " (selection)" : "")}: {OpsUtil.Plural(merged, "quad")} created");
                },
            },
            new Command
            {
                Id = "faces.squarizeAllLods",
                Label = "Squarize _All LODs",
                Execute = () =>
                {
                    int merged = 0;
                    Dlg.Try(() => Ed.Edit("Squarize all LODs", m =>
                    {
                        var lods = m.Lods;
                        for (int i = 0; i < lods.Length; i++)
                        {
                            var r = FaceOps.Squarize(lods[i], null);
                            merged += r.Merged;
                            if (!ReferenceEquals(r.Lod, lods[i])) lods = lods.SetItem(i, r.Lod);
                        }
                        return lods == m.Lods ? m : m with { Lods = lods };
                    }));
                    Ed.Log($"Squarize all LODs: {OpsUtil.Plural(merged, "quad")} created");
                },
            },
            FaceCommand("faces.moveTop", "Move _Top", ["Ctrl+Shift+Home"], "Move faces to top", (lod, m) => (FaceOps.Move(lod, m, FaceOrder.Top), MeshMath.CountSet(m)), "Moved {0} to the top"),
            FaceCommand("faces.moveBottom", "Move _Bottom", ["Ctrl+Shift+End"], "Move faces to bottom", (lod, m) => (FaceOps.Move(lod, m, FaceOrder.Bottom), MeshMath.CountSet(m)), "Moved {0} to the bottom"),
            new Command
            {
                Id = "faces.recalcNormals",
                Label = "Recalculate _Normals",
                Hotkeys = ["F5"],
                Execute = () =>
                {
                    if (OpsUtil.Apply("Recalculate normals", Structure.RecalcNormals)) Ed.Log("Normals recalculated");
                },
            },
            SharpCommand("faces.sharpEdge", "S_harp Edge", "U", true),
            SharpCommand("faces.smoothEdge", "Sm_ooth Edge", "I", false));
    }

    static string Removed(MergeResult r) =>
        (r.RemovedFaces > 0 ? $", {OpsUtil.Plural(r.RemovedFaces, "degenerate face")} removed" : "") +
        (r.CollapsedQuads > 0 ? $", {OpsUtil.Plural(r.CollapsedQuads, "quad")} became triangles" : "");

    /// <summary>Selected points plus the points of selected faces.</summary>
    static byte[] SelectedPoints(Lod lod)
    {
        var s = Selection.GetSelected(lod);
        return Selection.PointsFromFaces(lod, s.Faces, s.Points);
    }

    static Command Navigate(string id, string label, string hotkey, PointNavigation dir) => new()
    {
        Id = id,
        Label = label,
        Hotkeys = [hotkey],
        Execute = () =>
        {
            var lod = Ed.Lod;
            var cur = Selection.PeekSelected(lod)?.Points ?? new byte[lod.PointCount];
            int p = PointOps.Navigate(cur, SelectTools.BlockedPoints(lod), dir);
            if (p < 0)
            {
                Ed.Log("No more points");
                return;
            }
            SelectionOps.SelectPoints(null, [p], SelectOp.Set, $"Select point {p}");
            var v = Vec3.At(lod.Positions, p);
            Ed.Log($"Point {p}: {v}");
        },
    };

    static Command FaceCommand(string id, string label, string[] hotkeys, string undoLabel, Func<Lod, byte[], (Lod Lod, int Count)> op, string message, bool quadsOnly = false) => new()
    {
        Id = id,
        Label = label,
        Hotkeys = hotkeys,
        CanExecute = OpsUtil.HasTargetFaces,
        Execute = () =>
        {
            int count = 0;
            OpsUtil.Apply(undoLabel, lod =>
            {
                var r = op(lod, OpsUtil.TargetFaces(lod));
                count = r.Count;
                return r.Lod;
            });
            Ed.Log(count > 0 ? string.Format(message, OpsUtil.Plural(count, quadsOnly ? "quad" : "face")) : $"{undoLabel}: nothing to change");
        },
    };

    static Command SharpCommand(string id, string label, string hotkey, bool sharp) => new()
    {
        Id = id,
        Label = label,
        Hotkeys = [hotkey],
        CanExecute = () => OpsUtil.SelectedPointCount() >= 2,
        Execute = () =>
        {
            int changed = 0;
            OpsUtil.Apply(sharp ? "Sharp edges" : "Smooth edges", lod =>
            {
                var r = FaceOps.SetSharpEdges(lod, SelectedPoints(lod), sharp);
                changed = r.Changed;
                return r.Lod;
            });
            Ed.Log(changed > 0 ? $"{OpsUtil.Plural(changed, "edge")} marked {(sharp ? "sharp" : "smooth")}" : "No edges changed");
        },
    };

    static void Flatten()
    {
        string[] planes = ["X (YZ plane)", "Y (XZ plane, horizontal)", "Z (XY plane)", "Best-fit plane"];
        bool pin = OpsUtil.UsedPin() is not null;
        var form = new Form("Flatten Points", "Projects the selected points onto a plane through " + (pin ? "the pin." : "their centre."))
            .Choice("plane", "Normal", planes, lastFlattenPlane);
        if (!form.Show("Flatten")) return;
        lastFlattenPlane = form.Index("plane");
        var origin = OpsUtil.UsedPin();
        OpsUtil.Apply("Flatten points", lod =>
        {
            var mask = SelectedPoints(lod);
            Vec3 normal;
            switch (lastFlattenPlane)
            {
                case 0: normal = new Vec3(1, 0, 0); break;
                case 1: normal = new Vec3(0, 1, 0); break;
                case 2: normal = new Vec3(0, 0, 1); break;
                default:
                    if (MeshMath.FitPlane(lod.Positions, mask) is not { } fit) throw new ArgumentException("Need at least 3 non-collinear points for a best-fit plane");
                    normal = fit.Normal;
                    origin ??= fit.Centroid;
                    break;
            }
            return PointOps.Flatten(lod, mask, normal, origin);
        });
        Ed.Log($"Flattened {OpsUtil.Plural(OpsUtil.SelectedPointCount(), "point")} onto the {planes[lastFlattenPlane]}");
    }
}
