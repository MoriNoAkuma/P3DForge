using System.Windows;
using System.Windows.Input;
using P3DForge.App.Editor;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Viewport.Interaction;

enum MouseTransform
{
    Move,
    Copy,
    Rotate,
    Scale,
}

/// <summary>
/// Object Builder RMB drags: move (none), copy &amp; move (Shift), rotate (Ctrl), scale (Ctrl+Shift). Live preview via
/// <see cref="IViewportContext.SetPreviewPositions"/>; one undo step on release; Esc cancels; a click without drag
/// opens the context menu. Axis locks (X/Y/Z) are read live, so they can be toggled during the drag.
/// </summary>
sealed class TransformGesture : Gesture
{
    static EditorState Ed => AppServices.Editor;

    /// <summary>Degrees of rotation per pixel of horizontal mouse motion.</summary>
    public const double DegreesPerPixel = 0.5;
    /// <summary>Scale factor = exp(dx · ScalePerPixel).</summary>
    public const double ScalePerPixel = 0.005;

    readonly MouseTransform kind;
    int lodIndex;
    Lod? baseLod;
    byte[]? mask;
    Vec3 center, pivot;
    bool useWeights, copied, dead;
    float[]? bufA, bufB;
    bool flip;
    double[] matrix = Transforms.Identity();

    public TransformGesture(InteractionController owner, IViewHandle view, Point start, ModifierKeys mods) : base(owner, view, MouseButton.Right, start)
    {
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        kind = ctrl && shift ? MouseTransform.Scale : ctrl ? MouseTransform.Rotate : shift ? MouseTransform.Copy : MouseTransform.Move;
    }

    public static string Label(MouseTransform k) => k switch
    {
        MouseTransform.Copy => "Copy & Move",
        MouseTransform.Rotate => "Rotate",
        MouseTransform.Scale => "Scale",
        _ => "Move",
    };

    bool Begin()
    {
        lodIndex = Ed.ActiveLod;
        var lod = Ed.Lod;
        var m = Interact.TransformMask(lod);
        if (m is null)
        {
            Ed.Log("Nothing selected", LogLevel.Warn);
            return false;
        }
        if (kind == MouseTransform.Copy)
        {
            var sel = Selection.GetSelected(lod);
            // temporary: the copy exists during the drag so the preview is exact; replaced by one "Copy & Move" step on release
            Ed.EditLod("Copy", l => Structure.Append(l, Structure.Extract(l, m, sel.Faces), selectNew: true), new EditOptions { LodIndex = lodIndex, KeepNormals = true });
            copied = true;
            lod = Ed.Model.Lods[lodIndex];
            m = Interact.TransformMask(lod);
            if (m is null) return false;
        }
        baseLod = lod;
        mask = m;
        useWeights = Interact.SoftSelection;
        center = Transforms.Center(lod, m) ?? Vec3.Zero;
        pivot = Interact.Pivot(lod, m);
        bufA = new float[lod.Positions.Length];
        bufB = new float[lod.Positions.Length];
        return true;
    }

    public override void Move(Point p)
    {
        if (dead || !PastThreshold(p)) return;
        if (!Dragging)
        {
            Dragging = true;
            if (!Begin())
            {
                dead = true;
                return;
            }
        }
        if (!ReferenceEquals(Ed.Model.Lods.ElementAtOrDefault(lodIndex), baseLod))
        {
            // the document changed under the drag (undo, LOD switch …)
            Owner.Cancel();
            return;
        }
        matrix = Compute(p, out var text);
        Owner.SetStatus(text);
        var buf = (flip = !flip) ? bufA! : bufB!;
        Transforms.TransformPositions(baseLod!.Positions, buf, mask, matrix, useWeights);
        Owner.Context.SetPreviewPositions(lodIndex, buf);
    }

    double[] Compute(Point p, out string text)
    {
        var (lx, ly, lz) = Interact.Locks;
        double dx = p.X - Start.X;
        switch (kind)
        {
            case MouseTransform.Rotate:
            {
                var axis = Transforms.RotationAxis(Interact.ViewDir(View), lx, ly, lz);
                double deg = -dx * DegreesPerPixel; // right = clockwise on screen
                text = $"Rotate {-deg:0.#}°{(PinUsed ? " (pin)" : "")}";
                return Transforms.RotateAbout(pivot, axis, deg * Math.PI / 180);
            }
            case MouseTransform.Scale:
            {
                double f = Math.Exp(dx * ScalePerPixel);
                var s = Transforms.ScaleFactors(f, lx, ly, lz);
                text = $"Scale ×{f:0.###}{(PinUsed ? " (pin)" : "")}";
                return Transforms.ScaleAbout(pivot, s.X, s.Y, s.Z);
            }
            default:
            {
                var d = Transforms.DragDelta(center, Interact.ViewDir(View), Interact.RawRay(View, Start), Interact.RawRay(View, p), lx, ly, lz);
                text = $"{(kind == MouseTransform.Copy ? "Copy" : "Move")} Δ {Interact.Fmt(d)}";
                return Transforms.Translate(d);
            }
        }
    }

    static bool PinUsed => Interact.PinActive;

    public override void Up(Point p)
    {
        if (!Dragging)
        {
            ViewportContextMenu.Show(Owner.Context, View, p);
            return;
        }
        if (dead) return;
        Owner.Context.SetPreviewPositions(lodIndex, null);
        if (!ReferenceEquals(Ed.Model.Lods.ElementAtOrDefault(lodIndex), baseLod)) return;
        var m = matrix;
        var w = useWeights;
        var opts = new EditOptions { LodIndex = lodIndex };
        if (kind == MouseTransform.Copy)
        {
            // replace the temporary copy by a single undo step
            Ed.Undo();
            var lod = Ed.Model.Lods[lodIndex];
            var src = Interact.TransformMask(lod);
            if (src is null) return;
            var faces = Selection.GetSelected(lod).Faces;
            Ed.EditLod("Copy & Move", l =>
            {
                var c = Structure.Append(l, Structure.Extract(l, src, faces), selectNew: true);
                var sel = Interact.TransformMask(c);
                return sel is null ? c : Transforms.Apply(c, sel, m, w);
            }, opts);
            return;
        }
        if (Transforms.IsIdentity(m)) return;
        var mk = mask!;
        Ed.EditLod(Label(kind), l => Transforms.Apply(l, mk, m, w), opts);
    }

    public override void Cancel()
    {
        if (!Dragging || dead) return;
        Owner.Context.SetPreviewPositions(lodIndex, null);
        if (copied && ReferenceEquals(Ed.Model.Lods.ElementAtOrDefault(lodIndex), baseLod) && Ed.UndoLabel == "Copy") Ed.Undo();
        Ed.Log($"{Label(kind)} cancelled");
    }
}
