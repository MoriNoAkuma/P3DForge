using System.Numerics;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Panels;
using P3DForge.App.Shell;
using P3DForge.App.Viewport;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// UV sets and mapping: Surfaces ▸ UV Mapping (planar from the view / along X, Y, Z, fit, flip, rotate), Surfaces ▸
/// UV Sets (switch, add, delete) and the UV Editor panel (Ctrl+Shift+U). Commands work on the selected faces (or, after
/// asking, every face) in the active UV set (flag <see cref="ActiveSetFlag"/>; set 0 is what the Textured view shows).
/// </summary>
public sealed class UvFeature : IFeature
{
    public const string PanelId = "uvEditor";
    public const string ActiveSetFlag = "uv.activeSet";
    public int Order => 46;
    static EditorState Ed => AppServices.Editor;

    /// <summary>The UV set edited by the UV commands and the UV editor; falls back to 0 when the LOD does not have it.</summary>
    public static int ActiveSet
    {
        get
        {
            int s = Ed.Flag(ActiveSetFlag, 0);
            return Array.IndexOf(Uv.Sets(Ed.Lod), s) >= 0 ? s : 0;
        }
        set => Ed.SetFlag(ActiveSetFlag, value);
    }

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor
        {
            Id = PanelId,
            Title = "UV Editor",
            Location = PanelLocation.Right,
            Order = 40,
            StartHidden = true,
            TabWith = "objects",
            Create = () => new UvEditorPanel(),
        });
        Commands.Register(
            new Command
            {
                Id = "uv.editor",
                Label = "_UV Editor",
                Hotkeys = ["Ctrl+Shift+U"],
                Description = "Edit the UVs of the selected faces over their texture: move, rotate, scale, flip, fit, planar mapping",
                Execute = () => PanelRegistry.Focus(PanelId),
            },
            new Command
            {
                Id = "uv.activeSet",
                Label = "_Switch Active UV Set",
                Description = "Cycles the UV set that the UV commands and the UV editor work on (set 0 is the one the Textured view shows)",
                CanExecute = () => Uv.Sets(Ed.Lod).Length > 1,
                Execute = () =>
                {
                    var sets = Uv.Sets(Ed.Lod);
                    ActiveSet = sets[(Array.IndexOf(sets, ActiveSet) + 1) % sets.Length];
                    Ed.Log($"Active UV set: {ActiveSet}");
                },
            },
            new Command
            {
                Id = "uv.addSet",
                Label = "_Add UV Set (copy of the active set)",
                Description = "Adds a UV set (e.g. set 1 for _as / _mc stages with uvSource=\"tex1\") as a copy of the active set and makes it active",
                CanExecute = () => Ed.Lod.FaceCount > 0,
                Execute = () =>
                {
                    int from = ActiveSet, added = 0;
                    if (!Ed.EditLod("Add UV set", lod => Uv.AddSet(lod, from, out added))) return;
                    ActiveSet = added;
                    Ed.Log($"UV set {added} added (copy of set {from}) and made active");
                },
            },
            new Command
            {
                Id = "uv.deleteSet",
                Label = "_Delete Active UV Set",
                Description = "Removes the active UV set (set 0 is stored in the faces and cannot be removed)",
                CanExecute = () => ActiveSet > 0,
                Execute = () =>
                {
                    int set = ActiveSet;
                    if (!Dlg.Confirm("Delete UV Set", $"Delete UV set {set} of this LOD?\n\nMaterials whose stages use uvSource=\"tex{set}\" will lose their mapping.", "Delete"))
                        return;
                    if (!Ed.EditLod($"Delete UV set {set}", lod => Uv.DeleteSet(lod, set))) return;
                    ActiveSet = 0;
                    Ed.Log($"UV set {set} deleted");
                },
            },
            Map("uv.planarView", "Planar Mapping from _View", "Projects the selected faces from the current view direction and fits the result to 0–1",
                (lod, uv, faces) => ViewAxes() is { } ax ? Uv.Planar(lod, uv, faces, ax.Right, ax.Up) : uv),
            Map("uv.planarX", "Planar Mapping along _X", "Projects the selected faces along the model's X axis (side view) and fits to 0–1",
                (lod, uv, faces) => Uv.PlanarAxis(lod, uv, faces, 0)),
            Map("uv.planarY", "Planar Mapping along _Y", "Projects the selected faces along the model's Y axis (top view) and fits to 0–1",
                (lod, uv, faces) => Uv.PlanarAxis(lod, uv, faces, 1)),
            Map("uv.planarZ", "Planar Mapping along _Z", "Projects the selected faces along the model's Z axis (front view) and fits to 0–1",
                (lod, uv, faces) => Uv.PlanarAxis(lod, uv, faces, 2)),
            Map("uv.fit", "_Fit UVs to 0–1", "Scales and moves the UVs of the selected faces so they fill the texture (aspect kept)",
                (lod, uv, faces) => Uv.Fit(uv, Uv.SlotsOf(lod, faces), keepAspect: true)),
            Map("uv.flipU", "Flip U (_horizontal)", "Mirrors the UVs of the selected faces left ↔ right",
                (lod, uv, faces) => Around(lod, uv, faces, c => Uv.Scale(-1, 1, c))),
            Map("uv.flipV", "Flip V (v_ertical)", "Mirrors the UVs of the selected faces top ↔ bottom",
                (lod, uv, faces) => Around(lod, uv, faces, c => Uv.Scale(1, -1, c))),
            Map("uv.rotate90", "_Rotate UVs 90°", "Rotates the UVs of the selected faces by 90° clockwise",
                (lod, uv, faces) => Around(lod, uv, faces, c => Uv.Rotation(90, c))));
    }

    static float[] Around(Lod lod, float[] uv, byte[] faces, Func<Vector2, Matrix3x2> m)
    {
        var slots = Uv.SlotsOf(lod, faces);
        return Uv.Transform(uv, slots, m(Uv.Center(uv, slots)));
    }

    static Command Map(string id, string label, string description, Func<Lod, float[], byte[], float[]> fn) => new()
    {
        Id = id,
        Label = label,
        Description = description,
        CanExecute = () => Ed.Lod.FaceCount > 0,
        Execute = () =>
        {
            if (TargetFaces(label.Replace("_", "")) is not { } faces) return;
            int set = ActiveSet;
            string name = label.Replace("_", "");
            if (ApplyToSet(name, set, (lod, uv) => fn(lod, uv, faces)))
                Ed.Log($"{name}: {OpsUtil.Plural(MeshMath.CountSet(faces), "face")} (UV set {set})");
        },
    };

    /// <summary>One undo step that replaces the UVs of a set; false when nothing changed.</summary>
    internal static bool ApplyToSet(string label, int set, Func<Lod, float[], float[]> fn)
    {
        bool changed = false;
        Dlg.Try(() => changed = Ed.EditLod(label, lod => Uv.Put(lod, set, fn(lod, Uv.Get(lod, set)))));
        return changed;
    }

    /// <summary>Selected faces; with nothing selected, asks to use every face of the LOD (null = cancelled).</summary>
    internal static byte[]? TargetFaces(string action)
    {
        var lod = Ed.Lod;
        if (lod.FaceCount == 0)
        {
            Dlg.Fail("This LOD has no faces.");
            return null;
        }
        var faces = OpsUtil.TargetFaces(lod);
        if (MeshMath.CountSet(faces) > 0) return faces;
        if (!Dlg.Confirm(action, $"No faces are selected.\n\nApply \"{action}\" to all {lod.FaceCount:N0} faces of this LOD?\n\n" +
                "To change a part of the model only, select its faces first.", "All faces"))
            return null;
        return MeshMath.Ones(lod.FaceCount);
    }

    /// <summary>Right and up of the active view as RAW model directions, or null without a viewport.</summary>
    internal static (Vector3 Right, Vector3 Up)? ViewAxes()
    {
        if (ViewportPlugins.Current is not ViewportView vp) return null;
        var v = vp.ActivePane.Camera.View;
        // RENDER (x, y, −z) → RAW
        return (new Vector3(v.M11, v.M21, -v.M31), new Vector3(v.M12, v.M22, -v.M32));
    }
}
