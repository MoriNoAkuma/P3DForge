using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using P3DForge.App.Dialogs.Textures;
using P3DForge.App.Editor;
using P3DForge.App.Features;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using Point = System.Windows.Point;

namespace P3DForge.App.Panels;

public enum UvTool
{
    Move,
    Rotate,
    Scale,
}

/// <summary>
/// UV editor (Surfaces ▸ UV Editor, Ctrl+Shift+U): the UVs of the selected faces (all faces when none are selected) of
/// the active UV set over their texture. Click / drag a rectangle to select UV points (Shift adds, Ctrl removes;
/// double-click selects the island), drag a selected point to move / rotate / scale them (W / E / R), wheel zooms,
/// middle or right drag pans, F frames the selection, Home the 0–1 square. One undo step per drag or button.
/// </summary>
public sealed class UvEditorPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    readonly ComboBox setBox;
    readonly TextBlock status;
    readonly TextBlock textureLabel;
    readonly UvCanvas canvas;
    readonly ToggleButton[] toolButtons;
    bool stale = true, updatingSets;

    public UvEditorPanel()
    {
        Background = Brushes.Transparent;
        canvas = new UvCanvas();

        // Row 1: UV set, tools
        var bar = new WrapPanel { Margin = new Thickness(4, 4, 4, 0) };
        bar.Children.Add(new TextBlock { Text = "UV set", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) });
        setBox = new ComboBox { MinWidth = 70, FontSize = 12, Margin = new Thickness(0, 0, 2, 0), ToolTip = "UV set to edit (0 = the one in the faces, shown by the Textured view)" };
        setBox.SelectionChanged += (_, _) =>
        {
            if (updatingSets || setBox.SelectedItem is not ComboBoxItem { Tag: int set }) return;
            UvFeature.ActiveSet = set;
        };
        bar.Children.Add(setBox);
        bar.Children.Add(IconButton("", "uv.addSet", "Add a UV set (copy of this one)"));
        bar.Children.Add(IconButton("", "uv.deleteSet", "Delete this UV set"));
        bar.Children.Add(Gap());
        toolButtons =
        [
            ToolButton("", UvTool.Move, "Move (W) — drag a selected point"),
            ToolButton("", UvTool.Rotate, "Rotate (E) — drag around the selection centre; Shift snaps to 15°"),
            ToolButton("", UvTool.Scale, "Scale (R) — drag away from / towards the selection centre; Shift: one axis"),
        ];
        foreach (var b in toolButtons) bar.Children.Add(b);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        // Row 2: one-click operations on the selected UV points
        var ops = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };
        ops.Children.Add(Button("⟲ 90°", "Rotate the selected UVs 90° counter-clockwise", () => canvas.Apply("Rotate UVs", c => Uv.Rotation(-90, c))));
        ops.Children.Add(Button("⟳ 90°", "Rotate the selected UVs 90° clockwise", () => canvas.Apply("Rotate UVs", c => Uv.Rotation(90, c))));
        ops.Children.Add(Button("Flip U", "Mirror the selected UVs left ↔ right", () => canvas.Apply("Flip UVs", c => Uv.Scale(-1, 1, c))));
        ops.Children.Add(Button("Flip V", "Mirror the selected UVs top ↔ bottom", () => canvas.Apply("Flip UVs", c => Uv.Scale(1, -1, c))));
        ops.Children.Add(Button("Fit 0–1", "Scale and move the selected UVs to fill the texture (aspect kept)", canvas.FitSelection));
        var planar = Button("Planar ▾", "Planar mapping of the faces of the selected UVs", () => { });
        planar.Click += (_, _) => ShowPlanarMenu(planar);
        ops.Children.Add(planar);
        ops.Children.Add(Button("Island", "Select every UV point connected to the selection (double-click a point does the same)", canvas.SelectIslands));
        ops.Children.Add(Button("All", "Select all UV points (Ctrl+A)", canvas.SelectAll));
        SetDock(ops, Dock.Top);
        Children.Add(ops);

        textureLabel = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), TextTrimming = TextTrimming.CharacterEllipsis };
        SetDock(textureLabel, Dock.Top);
        Children.Add(textureLabel);

        status = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), TextWrapping = TextWrapping.Wrap };
        SetDock(status, Dock.Bottom);
        Children.Add(status);

        Children.Add(new Border { BorderBrush = (Brush)FindResource("Editor.Faint"), BorderThickness = new Thickness(0, 1, 0, 1), Child = canvas, ClipToBounds = true });

        canvas.StateChanged += UpdateStatus;
        canvas.ToolChanged += UpdateToolButtons;
        UpdateToolButtons();
        PanelKeys.Register(this, canvas.HandleKey);
        Ed.Changed += OnChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && stale) Reload(keepSelection: false);
        };
    }

    void OnChanged(Change c)
    {
        const Change content = Change.Model | Change.Selection | Change.ActiveLod | Change.Load | Change.Document;
        // UI flags change often (view options…); only a different active UV set matters here.
        if ((c & content) == 0 && ((c & Change.Ui) == 0 || UvFeature.ActiveSet == canvas.Set)) return;
        if (!IsVisible)
        {
            stale = true;
            return;
        }
        // Edits made here keep the UV selection; a new face selection, LOD or document starts over.
        Reload(keepSelection: (c & (Change.Selection | Change.ActiveLod | Change.Load | Change.Document)) == 0);
    }

    void Reload(bool keepSelection)
    {
        stale = false;
        var lod = Ed.Lod;
        var sets = Uv.Sets(lod);
        int active = UvFeature.ActiveSet;
        updatingSets = true;
        setBox.Items.Clear();
        foreach (int s in sets)
        {
            var item = new ComboBoxItem { Content = s == 0 ? "0 (faces)" : s.ToString(), Tag = s };
            setBox.Items.Add(item);
            if (s == active) setBox.SelectedItem = item;
        }
        updatingSets = false;
        canvas.Load(lod, active, keepSelection);
        textureLabel.Text = canvas.TexturePath is { Length: > 0 } t ? t : "(no texture on these faces)";
        textureLabel.ToolTip = canvas.TextureError ?? canvas.TexturePath;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var faces = canvas.FaceCount;
        var sel = canvas.SelectedCount;
        status.Text = (canvas.AllFaces ? $"All {faces:N0} faces (none selected in the viewport)" : $"{faces:N0} selected faces")
                      + $" · {sel:N0} UV points selected · set {canvas.Set}"
                      + "\nClick / drag: select · drag a point: " + canvas.Tool.ToString().ToLowerInvariant()
                      + " · double-click: island · wheel: zoom · middle / right drag: pan · F / Home: frame";
    }

    void UpdateToolButtons()
    {
        foreach (var b in toolButtons) b.IsChecked = (UvTool)b.Tag == canvas.Tool;
        UpdateStatus();
    }

    void ShowPlanarMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        void Add(string header, string tip, Func<Lod, float[], byte[], float[]> fn)
        {
            var mi = new MenuItem { Header = header, ToolTip = tip };
            mi.Click += (_, _) => canvas.ApplyToFaces(header, fn);
            menu.Items.Add(mi);
        }
        Add("From the current view", "Project as seen in the active viewport", (lod, uv, f) => UvFeature.ViewAxes() is { } ax ? Uv.Planar(lod, uv, f, ax.Right, ax.Up) : uv);
        Add("Along X (side)", "Project along the model's X axis", (lod, uv, f) => Uv.PlanarAxis(lod, uv, f, 0));
        Add("Along Y (top)", "Project along the model's Y axis", (lod, uv, f) => Uv.PlanarAxis(lod, uv, f, 1));
        Add("Along Z (front)", "Project along the model's Z axis", (lod, uv, f) => Uv.PlanarAxis(lod, uv, f, 2));
        menu.IsOpen = true;
    }

    // ---- small widgets -------------------------------------------------------------------------

    static FrameworkElement Gap() => new Border { Width = 8 };

    Button Button(string text, string tip, Action action)
    {
        var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("CompactButton"), Margin = new Thickness(0, 0, 4, 2), Focusable = false };
        b.Click += (_, _) => action();
        return b;
    }

    Button IconButton(string glyph, string command, string tip)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("IconButton"), Focusable = false };
        b.Click += (_, _) => Commands.Run(command);
        return b;
    }

    ToggleButton ToolButton(string glyph, UvTool tool, string tip)
    {
        var b = new ToggleButton
        {
            Content = glyph,
            Tag = tool,
            ToolTip = tip,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 14,
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            Focusable = false,
        };
        b.Click += (_, _) => canvas.Tool = tool;
        return b;
    }
}

/// <summary>Drawing and mouse / key handling of the UV editor.</summary>
sealed class UvCanvas : FrameworkElement
{
    static EditorState Ed => AppServices.Editor;
    const double HitRadius = 7;

    Lod? lod;
    byte[]? faceMask; // null = every face
    int[] slots = [];
    float[] uv = [];
    float[]? dragOrigin;
    readonly HashSet<int> selected = [];
    double zoom = 240, panU = -0.1, panV = -0.1; // pixels per UV unit; UV at the top-left corner
    bool framed;

    enum DragKind { None, Rect, Transform, Pan }
    DragKind drag;
    Point dragStart, dragNow, pivotScreen;
    Vector2 pivot;
    UvTool tool;

    BitmapSource? texture;
    static readonly Dictionary<string, BitmapSource?> textureCache = new(StringComparer.OrdinalIgnoreCase);

    public event Action? StateChanged;
    public event Action? ToolChanged;

    public UvCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        SizeChanged += (_, _) =>
        {
            if (!framed && ActualWidth > 0) FrameUnit();
            Redraw();
        };
    }

    public int Set { get; private set; }
    public bool AllFaces => faceMask is null;
    public int FaceCount => faceMask is null ? lod?.FaceCount ?? 0 : MeshMath.CountSet(faceMask);
    public int SelectedCount => selected.Count;
    public string? TexturePath { get; private set; }
    public string? TextureError { get; private set; }

    public UvTool Tool
    {
        get => tool;
        set
        {
            tool = value;
            ToolChanged?.Invoke();
        }
    }

    // ---- data -----------------------------------------------------------------------------------

    public void Load(Lod l, int set, bool keepSelection)
    {
        var mask = OpsUtil.TargetFaces(l);
        byte[]? newMask = MeshMath.CountSet(mask) > 0 ? mask : null;
        bool sameFaces = lod is not null && l.FaceCount == lod.FaceCount && set == Set
                         && (newMask is null ? faceMask is null : faceMask is not null && newMask.AsSpan().SequenceEqual(faceMask));
        lod = l;
        Set = set;
        faceMask = newMask;
        slots = Uv.SlotsOf(l, faceMask);
        uv = Uv.Get(l, set);
        drag = DragKind.None;
        dragOrigin = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!keepSelection || !sameFaces) selected.Clear();
        else selected.RemoveWhere(s => s / 4 >= l.FaceCount);
        LoadTexture();
        Redraw();
        StateChanged?.Invoke();
    }

    /// <summary>The texture most of the shown faces use, decoded once per path (largest mip, at most 1024 px).</summary>
    void LoadTexture()
    {
        TextureError = null;
        texture = null;
        if (lod is null) return;
        var counts = new Dictionary<uint, int>();
        for (int f = 0; f < lod.FaceCount; f++)
            if (faceMask is null || faceMask[f] != 0)
                counts[lod.FaceTexture[f]] = counts.GetValueOrDefault(lod.FaceTexture[f]) + 1;
        var best = counts.Count == 0 ? 0u : counts.MaxBy(p => p.Value).Key;
        var path = lod.Strings[(int)best];
        TexturePath = path;
        if (path.Length == 0 || path.StartsWith('#')) return;
        if (textureCache.TryGetValue(path, out var cached))
        {
            texture = cached;
            if (cached is null) TextureError = "Texture not found or not readable: " + path;
            return;
        }
        var abs = AppServices.ResolveProjectPath(path);
        if (abs is not null && !File.Exists(abs) && Path.ChangeExtension(abs, ".paa") is var paa && File.Exists(paa)) abs = paa;
        if (abs is null || !File.Exists(abs))
        {
            textureCache[path] = null;
            TextureError = "Texture not found: " + path;
            return;
        }
        var requested = path;
        _ = Task.Run(() =>
        {
            BitmapSource? bmp = null;
            try
            {
                bmp = TextureIO.ToBitmap(TextureIO.Fit(TextureIO.Load(abs).Image, 1024));
            }
            catch
            {
                // unreadable: drawn as a checker
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (textureCache.Count > 16) textureCache.Clear();
                textureCache[requested] = bmp;
                if (TexturePath != requested) return;
                texture = bmp;
                if (bmp is null) TextureError = "Texture not readable: " + requested;
                Redraw();
            });
        });
    }

    // ---- operations -----------------------------------------------------------------------------

    /// <summary>The selected UV points, or every shown one when nothing is selected.</summary>
    IReadOnlyCollection<int> Targets => selected.Count > 0 ? selected : slots;

    public void Apply(string label, Func<Vector2, Matrix3x2> matrix)
    {
        if (lod is null || slots.Length == 0) return;
        var targets = Targets;
        Commit(label, Uv.Transform(uv, targets, matrix(Uv.Center(uv, targets))));
    }

    public void FitSelection()
    {
        if (lod is null || slots.Length == 0) return;
        Commit("Fit UVs", Uv.Fit(uv, Targets, keepAspect: true));
    }

    /// <summary>A face-based operation (planar mapping) on the faces of the selected UV points (or every shown face).</summary>
    public void ApplyToFaces(string label, Func<Lod, float[], byte[], float[]> fn)
    {
        if (lod is null || slots.Length == 0) return;
        var faces = new byte[lod.FaceCount];
        foreach (int s in Targets) faces[s / 4] = 1;
        Commit("Planar mapping " + label.ToLowerInvariant(), fn(lod, uv, faces));
        foreach (int s in Uv.SlotsOf(lod, faces)) selected.Add(s);
        StateChanged?.Invoke();
    }

    void Commit(string label, float[] next)
    {
        if (lod is null) return;
        var snapshot = lod;
        int set = Set;
        if (next.AsSpan().SequenceEqual(Uv.Get(snapshot, set))) return;
        Dialogs.Dlg.Try(() => Ed.EditLod(label, l => ReferenceEquals(l, snapshot) ? Uv.Put(l, set, next) : l));
    }

    public void SelectAll()
    {
        selected.Clear();
        selected.UnionWith(slots);
        Redraw();
        StateChanged?.Invoke();
    }

    public void SelectIslands()
    {
        if (lod is null) return;
        var faces = new HashSet<int>();
        foreach (int s in selected) faces.Add(s / 4);
        if (faces.Count == 0) return;
        var all = new HashSet<int>();
        foreach (int f in faces)
            if (!all.Contains(f)) all.UnionWith(Uv.Island(lod, uv, f, faceMask));
        foreach (int f in all)
            for (int j = 0; j < lod.FaceVertCount[f]; j++) selected.Add(f * 4 + j);
        Redraw();
        StateChanged?.Invoke();
    }

    // ---- view -----------------------------------------------------------------------------------

    Point ToScreen(float u, float v) => new((u - panU) * zoom, (v - panV) * zoom);
    Point ToScreen(int slot) => ToScreen(uv[slot * 2], uv[slot * 2 + 1]);

    void FrameUnit() => Frame(0, 0, 1, 1);

    void Frame(double u0, double v0, double u1, double v1)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        framed = true;
        double w = Math.Max(u1 - u0, 1e-3), h = Math.Max(v1 - v0, 1e-3);
        zoom = Math.Clamp(Math.Min(ActualWidth / (w * 1.15), ActualHeight / (h * 1.15)), 10, 200000);
        panU = (u0 + u1) / 2 - ActualWidth / 2 / zoom;
        panV = (v0 + v1) / 2 - ActualHeight / 2 / zoom;
        Redraw();
    }

    void FrameSelection()
    {
        if (Uv.Bounds(uv, Targets) is { } b) Frame(b.MinU, b.MinV, b.MaxU, b.MaxV);
        else FrameUnit();
    }

    // ---- drawing --------------------------------------------------------------------------------

    static readonly Brush Background1 = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x22)));
    static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 1);
    static readonly Pen UnitPen = FrozenPen(Color.FromRgb(0x4F, 0x9C, 0xFF), 1.5);
    static readonly Pen RectPen = FrozenPen(Color.FromRgb(0xFF, 0x9D, 0x3B), 1);
    static readonly Brush Checker = MakeChecker();

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    static Pen FrozenPen(Color c, double w) => Frozen(new Pen(new SolidColorBrush(c), w));

    static Brush MakeChecker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x45)), null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x2C, 0x2F, 0x36)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 1, 1)), new RectangleGeometry(new Rect(1, 1, 1, 1)) } }));
        return Frozen(new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 0.125, 0.125), ViewportUnits = BrushMappingMode.RelativeToBoundingBox });
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background1, null, new Rect(RenderSize));
        var unit = new Rect(ToScreen(0, 0), ToScreen(1, 1));
        if (texture is not null)
        {
            // Neighbouring tiles faintly: UVs outside 0–1 repeat the texture.
            for (int du = -1; du <= 1; du++)
                for (int dv = -1; dv <= 1; dv++)
                {
                    bool centre = du == 0 && dv == 0;
                    if (!centre) dc.PushOpacity(0.22);
                    dc.DrawImage(texture, new Rect(unit.X + du * unit.Width, unit.Y + dv * unit.Height, unit.Width, unit.Height));
                    if (!centre) dc.Pop();
                }
        }
        else dc.DrawRectangle(Checker, null, unit);
        for (int i = 1; i < 4; i++)
        {
            double x = unit.X + unit.Width * i / 4, y = unit.Y + unit.Height * i / 4;
            dc.DrawLine(GridPen, new Point(x, unit.Top), new Point(x, unit.Bottom));
            dc.DrawLine(GridPen, new Point(unit.Left, y), new Point(unit.Right, y));
        }
        dc.DrawRectangle(null, UnitPen, unit);
        if (lod is null) return;

        // Faces and selected points: rasterised in software (WPF vector figures are far too slow for thousands of faces).
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)), h = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        if (rasterDirty || raster.Width != w || raster.Height != h)
        {
            rasterDirty = false;
#if DEBUG
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DrawRaster(w, h, dpi.DpiScaleX, dpi.DpiScaleY);
            if (sw.Elapsed.TotalMilliseconds > 20) Ed.Log($"[perf] UV raster {w}×{h}: {sw.Elapsed.TotalMilliseconds:0.0} ms ({FaceCount:N0} faces, {selected.Count:N0} points)");
#else
            DrawRaster(w, h, dpi.DpiScaleX, dpi.DpiScaleY);
#endif
        }
        if (raster.Bitmap is { } bmp) dc.DrawImage(bmp, new Rect(0, 0, w / dpi.DpiScaleX, h / dpi.DpiScaleY));

        if (drag == DragKind.Rect) dc.DrawRectangle(null, RectPen, new Rect(dragStart, dragNow));
        if (drag == DragKind.Transform && tool != UvTool.Move)
        {
            dc.DrawEllipse(null, RectPen, pivotScreen, 4, 4);
            dc.DrawLine(RectPen, pivotScreen, dragNow);
        }
    }

    readonly UvRaster raster = new();
    bool rasterDirty = true;

    /// <summary>Redraws everything; the faces are rasterised again.</summary>
    void Redraw()
    {
        rasterDirty = true;
        InvalidateVisual();
    }

    void DrawRaster(int w, int h, double sx, double sy)
    {
        raster.Begin(w, h, sx, sy);
        if (lod is null)
        {
            raster.End();
            return;
        }
        double kx = zoom * sx, ky = zoom * sy, ox = -panU * kx, oy = -panV * ky;
        var u = uv;
        float X(int slot) => (float)(u[slot * 2] * kx + ox);
        float Y(int slot) => (float)(u[slot * 2 + 1] * ky + oy);
        int fill = UvRaster.Premultiplied(0x50, 0xFF, 0x9D, 0x3B), edge = UvRaster.Premultiplied(0xD0, 0xE8, 0xEC, 0xF2);
        int point = UvRaster.Premultiplied(0xFF, 0xFF, 0x9D, 0x3B);
        if (selected.Count > 0)
            for (int f = 0; f < lod.FaceCount; f++)
            {
                if (faceMask is not null && faceMask[f] == 0) continue;
                int n = lod.FaceVertCount[f], b = f * 4;
                bool all = true;
                for (int j = 0; j < n && all; j++) all = selected.Contains(b + j);
                if (!all) continue;
                for (int j = 1; j + 1 < n; j++) raster.FillTriangle(X(b), Y(b), X(b + j), Y(b + j), X(b + j + 1), Y(b + j + 1), fill);
            }
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faceMask is not null && faceMask[f] == 0) continue;
            int n = lod.FaceVertCount[f], b = f * 4;
            for (int j = 0; j < n; j++)
            {
                int k = b + (j + 1) % n;
                raster.Line(X(b + j), Y(b + j), X(k), Y(k), edge);
            }
        }
        int r = (int)Math.Round(2 * sx);
        foreach (int slot in selected) raster.Square(X(slot), Y(slot), r, point);
        raster.End();
    }

    // ---- input ----------------------------------------------------------------------------------

    int HitSlot(Point p)
    {
        int best = -1;
        double bestD = HitRadius * HitRadius;
        foreach (int s in slots)
        {
            var c = ToScreen(s);
            double d = (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y);
            if (d < bestD)
            {
                bestD = d;
                best = s;
            }
        }
        return best;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        dragStart = dragNow = p;
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            drag = DragKind.Pan;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left || lod is null) return;
        var mods = Keyboard.Modifiers;
        int hit = HitSlot(p);
        if (hit >= 0 && e.ClickCount == 2)
        {
            if ((mods & (ModifierKeys.Shift | ModifierKeys.Control)) == 0) selected.Clear();
            selected.UnionWith(Uv.Welded(lod, uv, [hit], slots));
            SelectIslands();
            e.Handled = true;
            return;
        }
        if (hit >= 0)
        {
            var group = Uv.Welded(lod, uv, [hit], slots);
            if ((mods & ModifierKeys.Control) != 0)
            {
                selected.ExceptWith(group);
                Redraw();
                StateChanged?.Invoke();
                e.Handled = true;
                return;
            }
            if (!selected.Contains(hit))
            {
                if ((mods & ModifierKeys.Shift) == 0) selected.Clear();
                selected.UnionWith(group);
                StateChanged?.Invoke();
            }
            drag = DragKind.Transform;
            dragOrigin = uv;
            pivot = Uv.Center(uv, selected);
            pivotScreen = ToScreen(pivot.X, pivot.Y);
        }
        else drag = DragKind.Rect;
        CaptureMouse();
        Redraw();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag == DragKind.None) return;
        var p = e.GetPosition(this);
        var last = dragNow;
        dragNow = p;
        switch (drag)
        {
            case DragKind.Pan:
                panU -= (p.X - last.X) / zoom;
                panV -= (p.Y - last.Y) / zoom;
                break;
            case DragKind.Transform when dragOrigin is not null:
                uv = Uv.Transform(dragOrigin, selected, DragMatrix(p));
                break;
            case DragKind.Rect:
                InvalidateVisual(); // only the rectangle changes
                return;
        }
        Redraw();
    }

    Matrix3x2 DragMatrix(Point p)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var d = p - dragStart;
        switch (tool)
        {
            case UvTool.Rotate:
            {
                var a = dragStart - pivotScreen;
                var b = p - pivotScreen;
                double deg = (Math.Atan2(b.Y, b.X) - Math.Atan2(a.Y, a.X)) * 180 / Math.PI;
                if (shift) deg = Math.Round(deg / 15) * 15;
                return Uv.Rotation((float)deg, pivot);
            }
            case UvTool.Scale:
            {
                var a = dragStart - pivotScreen;
                var b = p - pivotScreen;
                if (shift)
                {
                    // one axis: the one the drag started along
                    bool horizontal = Math.Abs(a.X) >= Math.Abs(a.Y);
                    double ra = horizontal ? a.X : a.Y, rb = horizontal ? b.X : b.Y;
                    float s = Math.Abs(ra) < 1 ? 1 : (float)(rb / ra);
                    return horizontal ? Uv.Scale(s, 1, pivot) : Uv.Scale(1, s, pivot);
                }
                float k = a.Length < 1 ? 1 : (float)(b.Length / a.Length);
                return Uv.Scale(k, k, pivot);
            }
            default:
            {
                double du = d.X / zoom, dv = d.Y / zoom;
                if (shift)
                {
                    if (Math.Abs(d.X) >= Math.Abs(d.Y)) dv = 0;
                    else du = 0;
                }
                return Matrix3x2.CreateTranslation((float)du, (float)dv);
            }
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (drag == DragKind.None) return;
        var kind = drag;
        drag = DragKind.None;
        ReleaseMouseCapture();
        if (kind == DragKind.Rect && lod is not null)
        {
            var r = new Rect(dragStart, dragNow);
            var mods = Keyboard.Modifiers;
            if ((mods & (ModifierKeys.Shift | ModifierKeys.Control)) == 0) selected.Clear();
            foreach (int s in slots)
                if (r.Contains(ToScreen(s)))
                {
                    if ((mods & ModifierKeys.Control) != 0) selected.Remove(s);
                    else selected.Add(s);
                }
            StateChanged?.Invoke();
        }
        else if (kind == DragKind.Transform && dragOrigin is not null)
        {
            var origin = dragOrigin;
            dragOrigin = null;
            if (!ReferenceEquals(uv, origin)) Commit(tool switch { UvTool.Rotate => "Rotate UVs", UvTool.Scale => "Scale UVs", _ => "Move UVs" }, uv);
        }
        Redraw();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        double u = panU + p.X / zoom, v = panV + p.Y / zoom;
        zoom = Math.Clamp(zoom * Math.Pow(1.2, e.Delta / 120.0), 10, 200000);
        panU = u - p.X / zoom;
        panV = v - p.Y / zoom;
        framed = true;
        Redraw();
        e.Handled = true;
    }

    /// <summary>Plain keys while the UV editor has focus (registered with <see cref="PanelKeys"/>).</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.W when mods == ModifierKeys.None:
                Tool = UvTool.Move;
                return true;
            case Key.E when mods == ModifierKeys.None:
                Tool = UvTool.Rotate;
                return true;
            case Key.R when mods == ModifierKeys.None:
                Tool = UvTool.Scale;
                return true;
            case Key.F when mods == ModifierKeys.None:
                FrameSelection();
                return true;
            case Key.Home when mods == ModifierKeys.None:
                FrameUnit();
                return true;
            case Key.A when mods == ModifierKeys.Control:
                SelectAll();
                return true;
            case Key.Escape:
                if (drag == DragKind.Transform && dragOrigin is not null)
                {
                    uv = dragOrigin;
                    dragOrigin = null;
                    drag = DragKind.None;
                    ReleaseMouseCapture();
                }
                else selected.Clear();
                Redraw();
                StateChanged?.Invoke();
                return true;
        }
        return false;
    }
}

/// <summary>
/// A software rasteriser for the UV editor: clipped Bresenham lines, triangles filled by horizontal spans and point
/// squares into one premultiplied BGRA bitmap. Cost follows the faces and the covered pixels, not WPF's per-figure
/// tessellation, so tens of thousands of faces stay interactive.
/// </summary>
sealed class UvRaster
{
    int[] px = [];
    WriteableBitmap? bitmap;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public BitmapSource? Bitmap => bitmap;

    public static int Premultiplied(int a, int r, int g, int b) => (a << 24) | (r * a / 255 << 16) | (g * a / 255 << 8) | (b * a / 255);

    public void Begin(int w, int h, double scaleX, double scaleY)
    {
        if (bitmap is null || w != Width || h != Height || Math.Abs(bitmap.DpiX - 96 * scaleX) > 0.01)
        {
            Width = w;
            Height = h;
            px = new int[w * h];
            bitmap = new WriteableBitmap(w, h, 96 * scaleX, 96 * scaleY, PixelFormats.Pbgra32, null);
        }
        else Array.Clear(px);
    }

    public void End() => bitmap?.WritePixels(new Int32Rect(0, 0, Width, Height), px, Width * 4, 0);

    public void Line(float x0, float y0, float x1, float y1, int color)
    {
        if (!Clip(ref x0, ref y0, ref x1, ref y1)) return;
        int ax = (int)x0, ay = (int)y0, bx = (int)x1, by = (int)y1;
        int dx = Math.Abs(bx - ax), dy = -Math.Abs(by - ay), sx = ax < bx ? 1 : -1, sy = ay < by ? 1 : -1, err = dx + dy;
        int w = Width;
        while (true)
        {
            px[ay * w + ax] = color;
            if (ax == bx && ay == by) return;
            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                ax += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                ay += sy;
            }
        }
    }

    /// <summary>Liang–Barsky clip to the pixel area; false when the segment is outside.</summary>
    bool Clip(ref float x0, ref float y0, ref float x1, ref float y1)
    {
        if (!(float.IsFinite(x0) && float.IsFinite(y0) && float.IsFinite(x1) && float.IsFinite(y1))) return false;
        float xmax = Width - 1, ymax = Height - 1, t0 = 0, t1 = 1, dx = x1 - x0, dy = y1 - y0;
        if (!Edge(-dx, x0, ref t0, ref t1) || !Edge(dx, xmax - x0, ref t0, ref t1)
            || !Edge(-dy, y0, ref t0, ref t1) || !Edge(dy, ymax - y0, ref t0, ref t1))
            return false;
        float nx0 = x0 + t0 * dx, ny0 = y0 + t0 * dy, nx1 = x0 + t1 * dx, ny1 = y0 + t1 * dy;
        x0 = Math.Clamp(nx0, 0, xmax);
        y0 = Math.Clamp(ny0, 0, ymax);
        x1 = Math.Clamp(nx1, 0, xmax);
        y1 = Math.Clamp(ny1, 0, ymax);
        return true;
    }

    static bool Edge(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0) return q >= 0;
        float r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    public void FillTriangle(float ax, float ay, float bx, float by, float cx, float cy, int color)
    {
        if (!(float.IsFinite(ax) && float.IsFinite(ay) && float.IsFinite(bx) && float.IsFinite(by) && float.IsFinite(cx) && float.IsFinite(cy))) return;
        int y0 = Math.Max(0, (int)MathF.Ceiling(MathF.Min(ay, MathF.Min(by, cy)) - 0.5f));
        int y1 = Math.Min(Height - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy)) - 0.5f) - 1);
        for (int y = y0; y <= y1; y++)
        {
            float yc = y + 0.5f, xl = float.MaxValue, xr = float.MinValue;
            Cross(ax, ay, bx, by, yc, ref xl, ref xr);
            Cross(bx, by, cx, cy, yc, ref xl, ref xr);
            Cross(cx, cy, ax, ay, yc, ref xl, ref xr);
            if (xl > xr) continue;
            int x0 = Math.Max(0, (int)MathF.Ceiling(xl - 0.5f)), x1 = Math.Min(Width - 1, (int)MathF.Ceiling(xr - 0.5f) - 1);
            if (x1 >= x0) px.AsSpan(y * Width + x0, x1 - x0 + 1).Fill(color);
        }
    }

    static void Cross(float ax, float ay, float bx, float by, float y, ref float xl, ref float xr)
    {
        if ((y < ay) == (y < by)) return; // the row does not cross this edge
        float x = ax + (y - ay) * (bx - ax) / (by - ay);
        if (x < xl) xl = x;
        if (x > xr) xr = x;
    }

    public void Square(float x, float y, int r, int color)
    {
        if (!(float.IsFinite(x) && float.IsFinite(y)) || x < -r || y < -r || x > Width + r || y > Height + r) return;
        int cx = (int)x, cy = (int)y;
        int x0 = Math.Max(0, cx - r), x1 = Math.Min(Width - 1, cx + r), y0 = Math.Max(0, cy - r), y1 = Math.Min(Height - 1, cy + r);
        if (x1 < x0) return;
        for (int yy = y0; yy <= y1; yy++) px.AsSpan(yy * Width + x0, x1 - x0 + 1).Fill(color);
    }
}
