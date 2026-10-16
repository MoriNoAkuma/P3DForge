using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using P3DForge.App.Editor;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Viewport.Interaction;

/// <summary>
/// LMB selection: click picks a point (Points mode), a face (Faces mode) or a connected part (Object mode);
/// drag = rectangle (or lasso when Lasso mode is on). Ctrl/Shift = add, Ctrl+Shift = subtract, none = replace.
/// </summary>
sealed class SelectGesture : Gesture
{
    static EditorState Ed => AppServices.Editor;
    static readonly Brush BandStroke = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C)));
    static readonly Brush BandFill = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xF2, 0xC9, 0x4C)));

    readonly SelectOp op;
    readonly bool lasso;
    readonly List<Point> path = [];
    Point current;
    Shape? band;

    public SelectGesture(InteractionController owner, IViewHandle view, Point start, ModifierKeys mods) : base(owner, view, MouseButton.Left, start)
    {
        op = OpFor(mods);
        lasso = Interact.IsLasso;
        current = start;
        path.Add(start);
    }

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>OB modifiers: Ctrl = add, Ctrl+Shift = subtract, Shift = add (modern habit), none = replace.</summary>
    public static SelectOp OpFor(ModifierKeys mods) =>
        mods.HasFlag(ModifierKeys.Control) && mods.HasFlag(ModifierKeys.Shift) ? SelectOp.Subtract
        : mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Shift) ? SelectOp.Add
        : SelectOp.Set;

    public override void Move(Point p)
    {
        if (!PastThreshold(p)) return;
        Dragging = true;
        current = p;
        if (lasso && (p - path[^1]).Length >= 2) path.Add(p);
        UpdateBand();
        Owner.SetStatus(lasso ? null : $"{Math.Abs(p.X - Start.X):0} × {Math.Abs(p.Y - Start.Y):0}");
    }

    public override void Up(Point p)
    {
        RemoveBand();
        if (!Dragging)
        {
            Click(p);
            return;
        }
        current = p;
        IReadOnlyList<Point> poly;
        if (lasso)
        {
            path.Add(p);
            poly = path;
        }
        else poly = [Start, new Point(p.X, Start.Y), p, new Point(Start.X, p.Y)];
        if (poly.Count < 3) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var idx = PointsInside(poly);
        long pick = sw.ElapsedMilliseconds;
        ApplyPoints(idx, lasso ? "Lasso select" : "Rectangle select");
        if (Owner.Simulating) Ed.Log($"[perf] {(lasso ? "lasso" : "rectangle")}: {idx.Length} pts, pick {pick} ms, apply {sw.ElapsedMilliseconds - pick} ms");
    }

    public override void Cancel() => RemoveBand();

    /// <summary>Points inside a VIEW polygon; occluded ones dropped through a depth buffer unless X-ray / wireframe.</summary>
    int[] PointsInside(IReadOnlyList<Point> poly)
    {
        var idx = Owner.Context.PointsInPolygon(View, poly, false);
        if (idx.Length == 0 || !Interact.VisibleOnly) return idx;
        var lod = Ed.Lod;
        return new DepthVisibility(View, lod, lod.Positions).Filter(idx);
    }

    // ---- selection rules ----------------------------------------------------------------------------

    void Click(Point p)
    {
        var ctx = Owner.Context;
        var lod = Ed.Lod;
        if (Interact.IsObjectMode || Ed.SelectMode == SelectMode.Faces)
        {
            var hit = ctx.PickFace(View, p);
            if (PickProxy(p, hit?.Distance)) return;
            if (hit is null)
            {
                if (op == SelectOp.Set) SelectionOps.Clear();
                return;
            }
            if (Interact.IsObjectMode)
            {
                var faces = new byte[lod.FaceCount];
                faces[hit.Value.Face] = 1;
                SelectConnected(lod, new SelectionMask(new byte[lod.PointCount], faces), "Select object");
            }
            else SelectionOps.SelectFaces(null, [hit.Value.Face], op, "Select face");
            return;
        }
        int i = ctx.PickPoint(View, p, 8, Interact.VisibleOnly);
        if (i < 0)
        {
            if (PickProxy(p, null)) return;
            if (op == SelectOp.Set) SelectionOps.Clear();
            return;
        }
        SelectionOps.SelectPoints(null, [i], op, "Select point");
    }

    /// <summary>
    /// Displayed proxy models are pickable (Objects): a click on one that is nearer than <paramref name="nearest"/>
    /// (the face hit of the active LOD; null = nothing hit) selects its proxy triangle with the click's modifiers.
    /// </summary>
    bool PickProxy(Point p, float? nearest)
    {
        if (Scene.ProxyViewport.Pick(View, p) is not { } hit || (nearest is { } d && d <= hit.Distance)) return false;
        return Scene.ProxySceneHost.Select(hit.Index, op);
    }

    void ApplyPoints(int[] idx, string label)
    {
        var lod = Ed.Lod;
        if (Interact.IsObjectMode)
        {
            var pts = new byte[lod.PointCount];
            foreach (int i in idx) pts[i] = 1;
            SelectConnected(lod, new SelectionMask(pts, new byte[lod.FaceCount]), label);
        }
        else if (Ed.SelectMode == SelectMode.Faces)
        {
            var pts = new byte[lod.PointCount];
            foreach (int i in idx) pts[i] = 1;
            SelectionOps.SelectFaces(Selection.FacesFromPoints(lod, pts), null, op, label);
        }
        else SelectionOps.SelectPoints(null, idx, op, label);
    }

    /// <summary>Object mode: everything connected (through shared points) to the seed, hidden/locked points block propagation.</summary>
    void SelectConnected(Lod lod, SelectionMask seed, string label)
    {
        if (seed.IsEmpty)
        {
            if (op == SelectOp.Set) SelectionOps.Clear();
            return;
        }
        var conn = SelectTools.Connected(lod, seed, SelectTools.BlockedPoints(lod));
        SelectionOps.SelectPoints(conn.Points, null, op, label);
    }

    // ---- rubber band ----------------------------------------------------------------------------------

    void UpdateBand()
    {
        var canvas = Owner.Context.OverlayCanvas;
        var o = View.Rect.TopLeft;
        if (band is null)
        {
            band = lasso
                ? new Polygon { Stroke = BandStroke, Fill = BandFill, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false }
                : new Rectangle { Stroke = BandStroke, Fill = BandFill, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false };
            canvas.Children.Add(band);
        }
        if (band is Polygon pg)
        {
            var pc = new PointCollection(path.Count + 1);
            foreach (var q in path) pc.Add(new Point(q.X + o.X, q.Y + o.Y));
            pc.Add(new Point(current.X + o.X, current.Y + o.Y));
            pg.Points = pc;
        }
        else
        {
            double x = Math.Min(Start.X, current.X), y = Math.Min(Start.Y, current.Y);
            Canvas.SetLeft(band, x + o.X);
            Canvas.SetTop(band, y + o.Y);
            band.Width = Math.Abs(current.X - Start.X);
            band.Height = Math.Abs(current.Y - Start.Y);
        }
    }

    void RemoveBand()
    {
        if (band is null) return;
        Owner.Context.OverlayCanvas.Children.Remove(band);
        band = null;
    }
}

/// <summary>Move Pin mode (R): the click places the pin on the clicked point, face or ground/view plane (one-shot).</summary>
sealed class PinGesture(InteractionController owner, IViewHandle view, Point start) : Gesture(owner, view, MouseButton.Left, start)
{
    static EditorState Ed => AppServices.Editor;

    public override void Move(Point p)
    {
        if (PastThreshold(p)) Dragging = true;
    }

    public override void Up(Point p)
    {
        var ctx = Owner.Context;
        var lod = Ed.Lod;
        System.Numerics.Vector3? at = null;
        int i = ctx.PickPoint(View, p, 8, Interact.VisibleOnly);
        if (i >= 0) at = new(lod.Positions[i * 3], lod.Positions[i * 3 + 1], lod.Positions[i * 3 + 2]);
        else if (ctx.PickFace(View, p) is { } hit) at = hit.RawPosition;
        else if (Interact.PlanePoint(View, p) is { } q) at = Interact.ToVector(q);
        Ed.SetFlag(Interact.MovePin, false);
        if (at is not { } v) return;
        Ed.SetPin(v);
        Ed.Log($"Pin at {Interact.Fmt(Interact.ToVec(v))}{(i >= 0 ? $" (point {i})" : "")}");
    }

    public override void Cancel() { }
}
