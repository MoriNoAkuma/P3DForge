using System.Windows;
using System.Windows.Input;
using P3DForge.App.Editor;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Viewport.Interaction;

/// <summary>Gizmo handle: axis 0..2 = X/Y/Z, 3 = free (move in the view plane / uniform scale / rotate about the view axis).</summary>
readonly record struct GizmoHandle(ToolId Tool, int Axis);

/// <summary>
/// Screen-sized Move / Rotate / Scale handles (tools tool.move / tool.rotate / tool.scale) drawn with overlay lines
/// at the pivot (pin with Use Pin, else the selection centre). Handles are world-aligned; their length is
/// <see cref="SizePx"/> pixels in the active view (refreshed after camera moves).
/// </summary>
sealed class GizmoOverlay
{
    const string Key = "gizmo";
    public const double SizePx = 90;
    const double HitPx = 8;
    static readonly uint[] AxisColor = [0xE84A4AFFu, 0x4AC84AFFu, 0x4A7CF0FFu, 0xD8D8D8FFu];
    const uint HotColor = 0xFFD640FFu;
    static EditorState Ed => AppServices.Editor;

    readonly InteractionController owner;
    readonly MouseEventHandler onMove;
    readonly MouseWheelEventHandler onWheel;
    Lod? cachedLod;
    (bool UsePin, System.Numerics.Vector3? Pin) cachedPin;
    Vec3? center;
    GizmoHandle? hot;
    /// <summary>During a gizmo drag: centre shown displaced by the preview.</summary>
    Vec3? dragCenter;
    (ToolId Tool, Vec3 C, double L, GizmoHandle? Hot)? drawn;

    public GizmoOverlay(InteractionController owner)
    {
        this.owner = owner;
        Ed.Changed += OnChanged;
        onMove = (_, _) => RefreshScale();
        onWheel = (_, _) => owner.Context.InputElement.Dispatcher.BeginInvoke(RefreshScale, System.Windows.Threading.DispatcherPriority.Background);
        owner.Context.InputElement.AddHandler(UIElement.MouseMoveEvent, onMove, true);
        owner.Context.InputElement.AddHandler(UIElement.MouseWheelEvent, onWheel, true);
        Refresh();
    }

    public void Dispose()
    {
        Ed.Changed -= OnChanged;
        owner.Context.InputElement.RemoveHandler(UIElement.MouseMoveEvent, onMove);
        owner.Context.InputElement.RemoveHandler(UIElement.MouseWheelEvent, onWheel);
        owner.Context.SetOverlayLines(Key, null);
    }

    void OnChanged(Change c)
    {
        if ((c & (Change.Ui | Change.Selection | Change.Model | Change.ActiveLod | Change.Pin | Change.Load)) != 0) Refresh();
    }

    bool Active => Ed.Tool != ToolId.Select;

    /// <summary>Gizmo centre (RAW) for the current selection, null when hidden.</summary>
    public Vec3? Center
    {
        get
        {
            if (!Active) return null;
            var lod = Ed.Lod;
            var pin = (Ed.Flag(Interact.UsePin, false), Ed.Pin);
            if (!ReferenceEquals(lod, cachedLod) || pin != cachedPin)
            {
                cachedLod = lod;
                cachedPin = pin;
                var mask = Interact.TransformMask(lod);
                center = mask is null ? null : Interact.Pivot(lod, mask);
            }
            return center;
        }
    }

    /// <summary>Handle length in world units for a view at the gizmo centre.</summary>
    public static double Length(IViewHandle v, Vec3 c) => v.UnitsPerPixel(Interact.RawToRender(c)) * SizePx;

    void RefreshScale()
    {
        if (drawn is null || !Active) return;
        Refresh();
    }

    public void Refresh()
    {
        var ctx = owner.Context;
        var c = dragCenter ?? Center;
        if (c is not { } cc)
        {
            if (drawn is not null) ctx.SetOverlayLines(Key, null);
            drawn = null;
            return;
        }
        double len = Length(ctx.ActiveView, cc);
        if (!double.IsFinite(len) || len <= 0) return;
        if (drawn is { } d && d.Tool == Ed.Tool && d.C == cc && d.Hot == hot && Math.Abs(d.L - len) <= len * 0.01) return;
        drawn = (Ed.Tool, cc, len, hot);
        var lines = new List<OverlayLine>();
        foreach (var (handle, poly) in Handles(ctx.ActiveView, Ed.Tool, cc, len))
        {
            uint col = hot == handle ? HotColor : AxisColor[handle.Axis];
            for (int i = 1; i < poly.Count; i++) lines.Add(new OverlayLine(Interact.RawToRender(poly[i - 1]), Interact.RawToRender(poly[i]), col));
        }
        ctx.SetOverlayLines(Key, lines);
    }

    /// <summary>Highlights a handle (the dragged one) regardless of the mouse position.</summary>
    public void SetHot(GizmoHandle? h)
    {
        if (h == hot) return;
        hot = h;
        Refresh();
    }

    /// <summary>Displaces the drawn gizmo while dragging (null = back to the selection).</summary>
    public void ShowDragCenter(Vec3? c)
    {
        dragCenter = c;
        if (c is null) cachedLod = null;
        Refresh();
    }

    /// <summary>Handle geometry (RAW polylines) for hit testing and drawing.</summary>
    static IEnumerable<(GizmoHandle Handle, List<Vec3> Poly)> Handles(IViewHandle view, ToolId tool, Vec3 c, double len)
    {
        var vd = Interact.ViewDir(view);
        var up0 = Math.Abs(vd.Y) > 0.95 ? new Vec3(0, 0, 1) : new Vec3(0, 1, 0);
        var right = Vec3.Cross(up0, vd).Normalized() ?? new Vec3(1, 0, 0);
        var up = Vec3.Cross(vd, right).Normalized() ?? new Vec3(0, 1, 0);
        for (int a = 0; a < 3; a++)
        {
            var ax = Transforms.Axis(a);
            var u = Transforms.Axis((a + 1) % 3);
            var v = Transforms.Axis((a + 2) % 3);
            switch (tool)
            {
                case ToolId.Move:
                {
                    var tip = c + ax * len;
                    // arrow head in the plane facing the camera as much as possible
                    var side = Vec3.Cross(ax, vd).Normalized() ?? u;
                    yield return (new GizmoHandle(tool, a), [c + ax * (len * 0.12), tip, tip - ax * (len * 0.16) + side * (len * 0.06), tip, tip - ax * (len * 0.16) - side * (len * 0.06)]);
                    break;
                }
                case ToolId.Scale:
                {
                    var tip = c + ax * len;
                    double s = len * 0.05;
                    yield return (new GizmoHandle(tool, a), [c + ax * (len * 0.12), tip, tip + right * s, tip + up * s, tip - right * s, tip - up * s, tip + right * s]);
                    break;
                }
                case ToolId.Rotate:
                {
                    var ring = new List<Vec3>(49);
                    for (int i = 0; i <= 48; i++)
                    {
                        double t = i * Math.PI * 2 / 48;
                        ring.Add(c + (u * Math.Cos(t) + v * Math.Sin(t)) * len);
                    }
                    yield return (new GizmoHandle(tool, a), ring);
                    break;
                }
            }
        }
        double q = len * 0.1;
        switch (tool)
        {
            case ToolId.Move:
            case ToolId.Scale:
                yield return (new GizmoHandle(tool, 3), [c + (right + up) * q, c + (up - right) * q, c - (right + up) * q, c + (right - up) * q, c + (right + up) * q]);
                break;
            case ToolId.Rotate:
            {
                var ring = new List<Vec3>(65);
                for (int i = 0; i <= 64; i++)
                {
                    double t = i * Math.PI * 2 / 64;
                    ring.Add(c + (right * Math.Cos(t) + up * Math.Sin(t)) * (len * 1.18));
                }
                yield return (new GizmoHandle(tool, 3), ring);
                break;
            }
        }
    }

    /// <summary>Handle under a VIEW point (screen distance ≤ 8 px), the centre square winning ties.</summary>
    public GizmoHandle? HitTest(IViewHandle view, Point p)
    {
        if (Center is not { } c || dragCenter is not null) return null;
        double len = Length(owner.Context.ActiveView, c);
        GizmoHandle? best = null;
        double bestD = HitPx;
        foreach (var (handle, poly) in Handles(view, Ed.Tool, c, len))
        {
            var raw = new float[poly.Count * 3];
            for (int i = 0; i < poly.Count; i++)
            {
                raw[i * 3] = (float)poly[i].X;
                raw[i * 3 + 1] = (float)poly[i].Y;
                raw[i * 3 + 2] = (float)poly[i].Z;
            }
            var s = new float[poly.Count * 2];
            view.ProjectRaw(raw, s);
            double d = double.MaxValue;
            if (handle.Axis == 3 && Ed.Tool != ToolId.Rotate)
            {
                // inside the centre square
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                for (int i = 0; i < poly.Count; i++)
                {
                    minX = Math.Min(minX, s[i * 2]);
                    maxX = Math.Max(maxX, s[i * 2]);
                    minY = Math.Min(minY, s[i * 2 + 1]);
                    maxY = Math.Max(maxY, s[i * 2 + 1]);
                }
                if (p.X >= minX - 3 && p.X <= maxX + 3 && p.Y >= minY - 3 && p.Y <= maxY + 3) d = 0;
            }
            else
            {
                for (int i = 1; i < poly.Count; i++)
                    d = Math.Min(d, SegmentDistance(p, s[i * 2 - 2], s[i * 2 - 1], s[i * 2], s[i * 2 + 1]));
            }
            if (d <= bestD)
            {
                bestD = d;
                best = handle;
                if (d == 0) break;
            }
        }
        return best;
    }

    static double SegmentDistance(Point p, double ax, double ay, double bx, double by)
    {
        if (double.IsNaN(ax) || double.IsNaN(bx)) return double.MaxValue;
        double dx = bx - ax, dy = by - ay;
        double l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((p.X - ax) * dx + (p.Y - ay) * dy) / l2, 0, 1);
        double x = ax + dx * t - p.X, y = ay + dy * t - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>Highlights the handle under the mouse.</summary>
    public void Hover(IViewHandle view, Point p)
    {
        if (!Active)
        {
            hot = null;
            return;
        }
        var h = HitTest(view, p);
        if (h == hot) return;
        hot = h;
        Refresh();
    }
}

/// <summary>Dragging a gizmo handle: live preview, one undo step on release, Ctrl snaps (0.1 m / 15° / 0.1×).</summary>
sealed class GizmoGesture : Gesture
{
    static EditorState Ed => AppServices.Editor;
    public const double SnapMove = 0.1, SnapAngle = 15, SnapScale = 0.1;

    readonly GizmoHandle handle;
    readonly bool snap;
    readonly int lodIndex;
    readonly Lod baseLod;
    readonly byte[]? mask;
    readonly Vec3 center;
    readonly double len;
    readonly bool useWeights;
    readonly float[] bufA, bufB;
    bool flip;
    double[] matrix = Transforms.Identity();
    double prevAngle, totalAngle;

    public GizmoGesture(InteractionController owner, IViewHandle view, Point start, GizmoHandle handle, ModifierKeys mods)
        : base(owner, view, MouseButton.Left, start)
    {
        this.handle = handle;
        snap = mods.HasFlag(ModifierKeys.Control);
        lodIndex = Ed.ActiveLod;
        baseLod = Ed.Lod;
        mask = Interact.TransformMask(baseLod);
        center = owner.Gizmo.Center ?? Vec3.Zero;
        len = GizmoOverlay.Length(owner.Context.ActiveView, center);
        useWeights = Interact.SoftSelection;
        bufA = new float[baseLod.Positions.Length];
        bufB = new float[baseLod.Positions.Length];
        prevAngle = ScreenAngle(start);
        owner.Gizmo.SetHot(handle);
    }

    double ScreenAngle(Point p)
    {
        var c = Project(center);
        return Math.Atan2(p.Y - c.Y, p.X - c.X);
    }

    Point Project(Vec3 raw)
    {
        var s = new float[2];
        View.ProjectRaw([(float)raw.X, (float)raw.Y, (float)raw.Z], s);
        return new Point(s[0], s[1]);
    }

    static double Snap(double v, double step) => Math.Round(v / step) * step;

    public override void Move(Point p)
    {
        if (mask is null || !PastThreshold(p)) return;
        Dragging = true;
        if (!ReferenceEquals(Ed.Model.Lods.ElementAtOrDefault(lodIndex), baseLod))
        {
            Owner.Cancel();
            return;
        }
        string text;
        var vd = Interact.ViewDir(View);
        switch (handle.Tool)
        {
            case ToolId.Move:
            {
                Vec3 d;
                if (handle.Axis < 3)
                {
                    var ax = Transforms.Axis(handle.Axis);
                    var (o0, d0) = Interact.RawRay(View, Start);
                    var (o1, d1) = Interact.RawRay(View, p);
                    double t0 = Transforms.ClosestOnLine(center, ax, o0, d0), t1 = Transforms.ClosestOnLine(center, ax, o1, d1);
                    double t = double.IsFinite(t0) && double.IsFinite(t1) ? t1 - t0 : 0;
                    if (snap) t = Snap(t, SnapMove);
                    d = ax * t;
                }
                else
                {
                    d = Transforms.DragDelta(center, vd, Interact.RawRay(View, Start), Interact.RawRay(View, p), false, false, false);
                    if (snap) d = new Vec3(Snap(d.X, SnapMove), Snap(d.Y, SnapMove), Snap(d.Z, SnapMove));
                }
                matrix = Transforms.Translate(d);
                text = $"Move Δ {Interact.Fmt(d)}";
                Owner.Gizmo.ShowDragCenter(center + d);
                break;
            }
            case ToolId.Rotate:
            {
                double a = ScreenAngle(p);
                double step = a - prevAngle;
                if (step > Math.PI) step -= 2 * Math.PI;
                if (step < -Math.PI) step += 2 * Math.PI;
                prevAngle = a;
                totalAngle += step;
                var axis = handle.Axis < 3 ? Transforms.Axis(handle.Axis) : vd;
                // screen angles grow clockwise (y down); a positive rotation about an axis pointing away from the
                // viewer turns counter-clockwise on screen → negate so the selection follows the mouse
                double sign = Vec3.Dot(axis, vd) >= 0 ? -1 : 1;
                double deg = totalAngle * 180 / Math.PI * sign;
                if (snap) deg = Snap(deg, SnapAngle);
                matrix = Transforms.RotateAbout(center, axis, deg * Math.PI / 180);
                text = $"Rotate {(handle.Axis < 3 ? "XYZ"[handle.Axis] + " " : "")}{Interact.Fmt(deg)}°";
                break;
            }
            default:
            {
                double f;
                if (handle.Axis < 3)
                {
                    var c = Project(center);
                    var tip = Project(center + Transforms.Axis(handle.Axis) * len);
                    double ux = tip.X - c.X, uy = tip.Y - c.Y, l = Math.Sqrt(ux * ux + uy * uy);
                    f = l < 1 ? 1 : 1 + ((p.X - Start.X) * ux + (p.Y - Start.Y) * uy) / (l * l);
                }
                else f = Math.Exp(((p.X - Start.X) - (p.Y - Start.Y)) * 0.01);
                if (snap) f = Snap(f, SnapScale);
                f = Math.Max(f, 0.01);
                var s = handle.Axis < 3 ? new Vec3(handle.Axis == 0 ? f : 1, handle.Axis == 1 ? f : 1, handle.Axis == 2 ? f : 1) : new Vec3(f, f, f);
                matrix = Transforms.ScaleAbout(center, s.X, s.Y, s.Z);
                text = $"Scale {(handle.Axis < 3 ? "XYZ"[handle.Axis] + " " : "")}×{Interact.Fmt(f)}";
                break;
            }
        }
        Owner.SetStatus(text + (snap ? " (snap)" : ""));
        var buf = (flip = !flip) ? bufA : bufB;
        Transforms.TransformPositions(baseLod.Positions, buf, mask, matrix, useWeights);
        Owner.Context.SetPreviewPositions(lodIndex, buf);
    }

    public override void Up(Point p)
    {
        Owner.Gizmo.ShowDragCenter(null);
        if (!Dragging || mask is null) return;
        Owner.Context.SetPreviewPositions(lodIndex, null);
        if (!ReferenceEquals(Ed.Model.Lods.ElementAtOrDefault(lodIndex), baseLod) || Transforms.IsIdentity(matrix)) return;
        var m = matrix;
        var mk = mask;
        bool w = useWeights;
        string label = handle.Tool switch { ToolId.Move => "Move", ToolId.Rotate => "Rotate", _ => "Scale" };
        Ed.EditLod(label, l => Transforms.Apply(l, mk, m, w), new EditOptions { LodIndex = lodIndex });
    }

    public override void Cancel()
    {
        Owner.Gizmo.ShowDragCenter(null);
        if (Dragging) Owner.Context.SetPreviewPositions(lodIndex, null);
    }
}
