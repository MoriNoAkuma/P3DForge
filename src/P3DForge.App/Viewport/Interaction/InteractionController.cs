using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using P3DForge.App.Editor;
using P3DForge.Core.Ops;

namespace P3DForge.App.Viewport.Interaction;

/// <summary>A mouse gesture in progress (LMB selection, RMB transform, pin placement, gizmo drag).</summary>
abstract class Gesture(InteractionController owner, IViewHandle view, MouseButton button, Point start)
{
    public InteractionController Owner { get; } = owner;
    public IViewHandle View { get; } = view;
    public MouseButton Button { get; } = button;
    public Point Start { get; } = start;
    /// <summary>Moved beyond the click threshold.</summary>
    public bool Dragging { get; protected set; }

    protected bool PastThreshold(Point p) => Dragging || (p - Start).Length >= 3;

    /// <summary>Mouse moved (VIEW point local to <see cref="View"/>).</summary>
    public abstract void Move(Point p);
    /// <summary>Button released (gesture finished).</summary>
    public abstract void Up(Point p);
    /// <summary>Esc / lost capture / document changed: undo any preview.</summary>
    public abstract void Cancel();
}

/// <summary>
/// Object Builder mouse editing for one viewport: LMB selection (click / rectangle / lasso), RMB transforms
/// (move / copy / rotate / scale with axis locks and live preview), RMB-click context menu, pin placement,
/// gizmo tools, the 3D cursor (<see cref="CursorState"/>) and a small status hint. The gesture entry points
/// (<see cref="Down"/>, <see cref="Move"/>, <see cref="Up"/>) take a view + VIEW-local point so automation can
/// replay gestures through the same code (<see cref="Simulate"/>).
/// </summary>
public sealed class InteractionController
{
    static EditorState Ed => AppServices.Editor;

    /// <summary>The controller of the current viewport.</summary>
    public static InteractionController? Current { get; private set; }

    readonly IViewportContext ctx;
    readonly FrameworkElement input;
    readonly Border hint;
    readonly TextBlock hintText;
    readonly DispatcherTimer pickTimer;
    readonly PinOverlay pin;
    readonly GizmoOverlay gizmo;
    Gesture? gesture;
    string? status;
    (IViewHandle View, Point P)? pendingPick;
    UIElement? keyHost;
    bool releasing;

    public IViewportContext Context => ctx;
    public bool Busy => gesture is not null;

    public InteractionController(IViewportContext ctx)
    {
        this.ctx = ctx;
        input = ctx.InputElement;
        input.MouseDown += OnMouseDown;
        input.MouseMove += OnMouseMove;
        input.MouseUp += OnMouseUp;
        input.LostMouseCapture += OnLostCapture;
        hintText = new TextBlock { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C)) };
        hint = new Border
        {
            Child = hintText,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x18, 0x1A, 0x1E)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 3),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetRight(hint, 10);
        Canvas.SetBottom(hint, 10);
        ctx.OverlayCanvas.Children.Add(hint);
        pickTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        pickTimer.Tick += (_, _) => PickCursor();
        pin = new PinOverlay(ctx);
        gizmo = new GizmoOverlay(this);
        Ed.Changed += OnEditorChanged;
        Current = this;
        UpdateHint();
    }

    public void Dispose()
    {
        Cancel();
        input.MouseDown -= OnMouseDown;
        input.MouseMove -= OnMouseMove;
        input.MouseUp -= OnMouseUp;
        input.LostMouseCapture -= OnLostCapture;
        Ed.Changed -= OnEditorChanged;
        pickTimer.Stop();
        pin.Dispose();
        gizmo.Dispose();
        ctx.OverlayCanvas.Children.Remove(hint);
        if (ReferenceEquals(Current, this)) Current = null;
    }

    internal GizmoOverlay Gizmo => gizmo;

    void OnEditorChanged(Change c)
    {
        if ((c & (Change.Ui | Change.Pin)) != 0) UpdateHint();
    }

    // ---- WPF events → gesture entry points --------------------------------------------------------

    static Point Local(IViewHandle v, Point control) => new(control.X - v.Rect.X, control.Y - v.Rect.Y);

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (gesture is not null)
        {
            e.Handled = true; // second button during a gesture: ignore
            return;
        }
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Alt) || e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        var at = ctx.ViewAt(e.GetPosition(input));
        if (at is null) return;
        if (!Down(at.Value.View, at.Value.ViewPoint, e.ChangedButton, mods)) return;
        input.CaptureMouse();
        e.Handled = true;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        var cp = e.GetPosition(input);
        if (gesture is not null)
        {
            Move(Local(gesture.View, cp));
            e.Handled = true;
            return;
        }
        if (e.MiddleButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.LeftButton == MouseButtonState.Pressed) return;
        var at = ctx.ViewAt(cp);
        if (at is not null) Hover(at.Value.View, at.Value.ViewPoint);
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (gesture is null || e.ChangedButton != gesture.Button) return;
        var p = Local(gesture.View, e.GetPosition(input));
        e.Handled = true;
        Up(p);
    }

    void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (!releasing && gesture is not null && !Simulating) Cancel();
    }

    void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || gesture is null) return;
        Cancel();
        e.Handled = true;
    }

    // ---- gesture entry points (also used by automation) ----------------------------------------------

    /// <summary>Starts a gesture for a button press at a VIEW-local point. Returns false when the press is not ours.</summary>
    public bool Down(IViewHandle view, Point p, MouseButton button, ModifierKeys mods)
    {
        if (gesture is not null || mods.HasFlag(ModifierKeys.Alt)) return false;
        Gesture? g = null;
        if (button == MouseButton.Left)
        {
            if (Interact.IsMovePin) g = new PinGesture(this, view, p);
            else if (Ed.Tool != ToolId.Select && gizmo.HitTest(view, p) is { } handle) g = new GizmoGesture(this, view, p, handle, mods);
            else g = new SelectGesture(this, view, p, mods);
        }
        else if (button == MouseButton.Right) g = new TransformGesture(this, view, p, mods);
        if (g is null) return false;
        gesture = g;
        keyHost = Window.GetWindow(input) ?? (UIElement)input;
        keyHost.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKey), true);
        return true;
    }

    /// <summary>Mouse moved during a gesture (VIEW point local to the gesture's view).</summary>
    public void Move(Point p) => gesture?.Move(p);

    /// <summary>Finishes the current gesture.</summary>
    public void Up(Point p)
    {
        var g = gesture;
        if (g is null) return;
        End();
        g.Up(p);
        SetStatus(null);
    }

    /// <summary>Cancels the current gesture (Esc).</summary>
    public void Cancel()
    {
        var g = gesture;
        if (g is null) return;
        End();
        g.Cancel();
        SetStatus(null);
    }

    void End()
    {
        gesture = null;
        keyHost?.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKey));
        keyHost = null;
        if (input.IsMouseCaptured)
        {
            releasing = true;
            input.ReleaseMouseCapture();
            releasing = false;
        }
    }

    /// <summary>Mouse over a view without buttons: maintains <see cref="CursorState"/> and the gizmo hover.</summary>
    public void Hover(IViewHandle view, Point p)
    {
        CursorState.Raw = Interact.PlanePoint(view, p) is { } q ? Interact.ToVector(q) : CursorState.Raw;
        pendingPick = (view, p);
        pickTimer.Stop();
        pickTimer.Start();
        gizmo.Hover(view, p);
    }

    /// <summary>Debounced: the point on the face under the cursor (more precise than the plane).</summary>
    void PickCursor()
    {
        pickTimer.Stop();
        if (pendingPick is not { } pp || gesture is not null) return;
        pendingPick = null;
        if (ctx.PickFace(pp.View, pp.P) is { } hit) CursorState.Raw = hit.RawPosition;
    }

    // ---- hint / status --------------------------------------------------------------------------------

    /// <summary>Gesture feedback ("Move Δ …", "Rotate 15°") shown next to the lock state.</summary>
    internal void SetStatus(string? text)
    {
        if (status == text) return;
        status = text;
        UpdateHint();
    }

    public string HintText => hintText.Text;

    void UpdateHint()
    {
        var parts = new List<string>();
        var (x, y, z) = Interact.Locks;
        if (x || y || z) parts.Add("Lock " + string.Join(" ", new[] { x ? "X" : null, y ? "Y" : null, z ? "Z" : null }.Where(s => s is not null)));
        if (Interact.IsMovePin) parts.Add("Move pin: click to place");
        else if (Interact.IsLasso) parts.Add("Lasso");
        if (Interact.SoftSelection) parts.Add("Soft");
        if (status is not null) parts.Add(status);
        hintText.Text = string.Join("  ·  ", parts);
        hint.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- automation ---------------------------------------------------------------------------------

    /// <summary>Control point of a gizmo handle in the active view (axis char x/y/z, c = centre).</summary>
    Point GizmoPoint(char axis)
    {
        var v = ctx.ActiveView;
        if (gizmo.Center is not { } c) return new Point(double.NaN, double.NaN);
        double len = GizmoOverlay.Length(v, c);
        Vec3 at = c;
        if (axis != 'c')
        {
            int a = axis - 'x';
            at = Ed.Tool == ToolId.Rotate
                ? c + (Transforms.Axis((a + 1) % 3) + Transforms.Axis((a + 2) % 3)) * (len * Math.Sqrt(0.5)) // only on ring a
                : c + Transforms.Axis(a) * (len * 0.7);
        }
        var sp = new float[2];
        v.ProjectRaw([(float)at.X, (float)at.Y, (float)at.Z], sp);
        return new Point(sp[0] + v.Rect.X, sp[1] + v.Rect.Y);
    }

    public bool Simulating { get; private set; }
    Point heldAt;

    /// <summary>
    /// Replays a gesture given in viewport-control coordinates, synchronously and with intermediate moves:
    /// "click left none 500,350", "drag left ctrl 400,300 520,380", "drag right ctrl+shift 600,400 650,400",
    /// "drag left none 100,100 300,120 250,300" (several points = lasso path), "move 400,300" (hover),
    /// a trailing "esc" cancels before release, a trailing "hold" keeps the button down (for screenshots of the live
    /// preview / rubber band) until a later "release" (or "esc"). Points may be given in percent of the control ("60%,45%").
    /// </summary>
    public string Simulate(string spec)
    {
        var t = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (t.Length == 1 && t[0] is "release" or "esc")
        {
            if (gesture is null) return "no gesture";
            if (t[0] == "esc") Cancel();
            else Up(heldAt);
            return t[0];
        }
        if (t.Length < 2) return "bad gesture";
        // "x,y" in control units, "60%,40%" relative to the control size, "@gx|@gy|@gz|@gc" = a gizmo handle of the
        // active view, "+dx,dy" relative to the previous point
        Point prev = default;
        Point Pt(string s)
        {
            if (s.StartsWith("@g") && s.Length == 3) return prev = GizmoPoint(s[2]);
            bool rel = s.StartsWith('+');
            if (rel) s = s[1..];
            var xy = s.Split(',');
            double C(string v, double size) => v.EndsWith('%')
                ? double.Parse(v[..^1], CultureInfo.InvariantCulture) / 100 * size
                : double.Parse(v, CultureInfo.InvariantCulture);
            var q = new Point(C(xy[0], input.ActualWidth), C(xy[1], input.ActualHeight));
            return prev = rel ? new Point(prev.X + q.X, prev.Y + q.Y) : q;
        }
        if (t[0] == "move")
        {
            var at0 = ctx.ViewAt(Pt(t[1]));
            if (at0 is null) return "move: outside views";
            Hover(at0.Value.View, at0.Value.ViewPoint);
            PickCursor();
            return $"cursor {CursorState.Raw}";
        }
        if (t.Length < 4) return "bad gesture";
        var button = t[1] == "right" ? MouseButton.Right : MouseButton.Left;
        var mods = ModifierKeys.None;
        foreach (var m in t[2].Split('+'))
            mods |= m switch { "ctrl" => ModifierKeys.Control, "shift" => ModifierKeys.Shift, "alt" => ModifierKeys.Alt, _ => ModifierKeys.None };
        bool esc = t[^1] == "esc", hold = t[^1] == "hold";
        var pts = t.Skip(3).Take(t.Length - 3 - (esc || hold ? 1 : 0)).Select(Pt).ToList();
        var at = ctx.ViewAt(pts[0]);
        if (at is null) return "start outside views";
        var view = at.Value.View;
        Simulating = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int moves = 0;
        try
        {
            if (!Down(view, at.Value.ViewPoint, button, mods)) return "not handled";
            if (t[0] == "drag")
            {
                for (int i = 1; i < pts.Count; i++)
                    for (int s = 1; s <= 8; s++)
                    {
                        var a = pts[i - 1];
                        var b = pts[i];
                        Move(Local(view, new Point(a.X + (b.X - a.X) * s / 8, a.Y + (b.Y - a.Y) * s / 8)));
                        moves++;
                    }
            }
            var hintNow = hintText.Text;
            if (hold)
            {
                heldAt = Local(view, pts[^1]);
                return $"{spec} → held: {hintNow} [{sw.ElapsedMilliseconds} ms, {moves} moves]";
            }
            if (esc) Cancel();
            else Up(Local(view, pts[^1]));
            return $"{spec} → {(hintNow.Length > 0 ? hintNow : "ok")} [{sw.ElapsedMilliseconds} ms, {moves} moves]";
        }
        finally
        {
            Simulating = false;
        }
    }
}
