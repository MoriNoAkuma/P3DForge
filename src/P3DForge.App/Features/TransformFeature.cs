using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Transform;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.App.Viewport;
using P3DForge.App.Viewport.Interaction;
using P3DForge.Core;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// Pin (C center pin, Shift+C use pin, pin to origin), numeric Move/Rotate/Scale dialogs (selection or all LODs),
/// Mirror X/Y/Z (around the pin or the selection centre, faces reversed) and Center All.
/// </summary>
public sealed class TransformFeature : IFeature
{
    static EditorState Ed => AppServices.Editor;

    public void Install()
    {
        Commands.Register(
            new Command
            {
                Id = "transform.pinToSelection",
                Label = "Center Pin",
                Hotkeys = ["C"],
                Description = "Pin at the centre of the selection (of the whole LOD when nothing is selected)",
                Execute = CenterPin,
            },
            new Command
            {
                Id = "transform.usePin",
                Label = "Use Pin",
                Hotkeys = ["Shift+C"],
                Description = "Rotate / scale / mirror / insert point use the pin",
                IsChecked = () => Ed.Flag(Interact.UsePin, false),
                Execute = () =>
                {
                    bool on = !Ed.Flag(Interact.UsePin, false);
                    Ed.Batch(() =>
                    {
                        if (on && Ed.Pin is null) CenterPin();
                        Ed.SetFlag(Interact.UsePin, on);
                    });
                },
            },
            new Command
            {
                Id = "transform.pinToOrigin",
                Label = "Pin to Origin",
                Execute = () => Ed.SetPin(System.Numerics.Vector3.Zero),
            },
            new Command { Id = "transform.move", Label = "Move…", Execute = () => Numeric(NumericTransform.Move) },
            new Command { Id = "transform.rotate", Label = "Rotate…", Execute = () => Numeric(NumericTransform.Rotate) },
            new Command { Id = "transform.scale", Label = "Scale…", Execute = () => Numeric(NumericTransform.Scale) },
            Mirror(0),
            Mirror(1),
            Mirror(2),
            new Command
            {
                Id = "transform.centerAll",
                Label = "Center All",
                Description = "Move every LOD so the Geometry LOD (or LOD 0) bounding-box centre is at the origin",
                Execute = CenterAll,
            });
        Toolbar.Add(new ToolbarItem { Command = "transform.usePin", Glyph = "\uE718", Group = "mode", Order = 65, Tooltip = "Use pin (Shift+C)" });
    }

    static void CenterPin()
    {
        var lod = Ed.Lod;
        var mask = Interact.TransformMask(lod);
        var c = Transforms.Center(lod, mask) ?? Transforms.Center(lod, null) ?? Vec3.Zero;
        Ed.SetPin(Interact.ToVector(c));
        Ed.Log($"Pin at {Interact.Fmt(c)}{(mask is null ? " (LOD centre)" : "")}");
    }

    static Command Mirror(int axis) => new()
    {
        Id = "transform.mirror" + "XYZ"[axis],
        Label = "Mirror " + "XYZ"[axis],
        Description = "Mirror the selection across the plane through the pin (Use Pin) or the selection centre",
        Execute = () =>
        {
            var lod = Ed.Lod;
            var mask = Interact.TransformMask(lod);
            if (mask is null)
            {
                Dlg.Fail("Mirror: nothing selected");
                return;
            }
            var c = Interact.Pivot(lod, mask);
            var m = Transforms.MirrorAbout(c, axis);
            Ed.EditLod("Mirror " + "XYZ"[axis], l => Transforms.Apply(l, mask, m));
        },
    };

    static void Numeric(NumericTransform kind)
    {
        var r = TransformDialog.Show(kind);
        if (r is not null) Dlg.Try(() => Apply(r));
    }

    /// <summary>Applies a numeric transform request (also used by tests/automation through the dialog).</summary>
    public static void Apply(TransformRequest r)
    {
        var lod = Ed.Lod;
        var mask = r.AllLods ? null : Interact.TransformMask(lod);
        if (!r.AllLods && mask is null)
        {
            Dlg.Fail($"{r.Kind}: nothing selected (or tick \"Apply to all LODs\")");
            return;
        }
        Vec3 pivot;
        if (r.Pivot == TransformPivot.Pin && Ed.Pin is { } pin) pivot = Interact.ToVec(pin);
        else if (r.Pivot == TransformPivot.Origin) pivot = Vec3.Zero;
        else
        {
            if (r.Pivot == TransformPivot.Pin) Ed.Log("No pin set — using the selection centre", LogLevel.Warn);
            pivot = Transforms.Center(lod, mask) ?? Transforms.Center(lod, null) ?? Vec3.Zero;
        }

        double[] m;
        string label;
        bool rigid; // translation / positive uniform scale: normals stay valid when applied to whole LODs
        switch (r.Kind)
        {
            case NumericTransform.Move:
                m = Transforms.Translate(r.Values);
                label = $"Move {Interact.Fmt(r.Values)}";
                rigid = true;
                break;
            case NumericTransform.Rotate:
            {
                Vec3 axis = r.Axis < 3
                    ? Transforms.Axis(r.Axis)
                    : ViewportPlugins.Current?.ActiveView is { } v ? Interact.ViewDir(v) : new Vec3(0, 0, 1);
                m = Transforms.RotateAbout(pivot, axis, r.Angle * Math.PI / 180);
                label = $"Rotate {(r.Axis < 3 ? "XYZ"[r.Axis].ToString() : "view")} {Interact.Fmt(r.Angle)}°";
                rigid = false;
                break;
            }
            default:
                m = Transforms.ScaleAbout(pivot, r.Values.X, r.Values.Y, r.Values.Z);
                label = r.Values.X == r.Values.Y && r.Values.Y == r.Values.Z ? $"Scale ×{Interact.Fmt(r.Values.X)}" : $"Scale {Interact.Fmt(r.Values)}";
                rigid = r.Values.X == r.Values.Y && r.Values.Y == r.Values.Z && r.Values.X > 0;
                break;
        }
        if (Transforms.IsIdentity(m)) return;
        if (r.AllLods) Ed.Edit(label + " (all LODs)", model => Transforms.ApplyAll(model, m), new EditOptions { KeepNormals = rigid });
        else
        {
            bool w = Interact.SoftSelection;
            Ed.EditLod(label, l => Transforms.Apply(l, mask, m, w));
        }
    }

    static void CenterAll()
    {
        var model = Ed.Model;
        var off = Transforms.CenterAllOffset(model);
        if (off is not { } d)
        {
            Dlg.Fail("Center All: the model has no points");
            return;
        }
        if (d.Length < 1e-7)
        {
            Ed.Log("Center All: already centred");
            return;
        }
        var refName = LodNames.Name(model.Lods[Transforms.CenterReferenceLod(model)].Resolution);
        if (!Dlg.Confirm("Center All", $"Move all {model.Lods.Length} LODs by ({Interact.Fmt(d)}) so the bounding-box centre of the {refName} LOD is at the origin?", "Center"))
            return;
        Ed.Edit("Center All", mm => Transforms.ApplyAll(mm, Transforms.Translate(d)), new EditOptions { KeepNormals = true });
    }
}
