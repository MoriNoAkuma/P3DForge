using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Viewport.Interaction;
using P3DForge.Core;
using P3DForge.Core.Ops;

namespace P3DForge.App.Panels;

/// <summary>
/// Inspector / Current Position (Shift+F3): 3D cursor and pin position, active LOD info, selection counts, centroid
/// and bounds, plus the Measuring section (Shift+F11): distance of 2 points, triangle angles of 3, length of the
/// selected edges and area of the selected faces. Selection data is recomputed on change (deferred, only while
/// visible); the cursor is polled on a light timer.
/// </summary>
public sealed class InspectorPanel : ScrollViewer
{
    static EditorState Ed => AppServices.Editor;

    /// <summary>Editor flag that expands the Measuring section (tools.measure).</summary>
    public const string MeasureFlag = "inspector.measure";

    readonly TextBlock cursor = Value(), pin = Value(), pinDist = Value();
    readonly TextBlock lodName = Value(), lodCounts = Value(), lodRes = Value();
    readonly TextBlock selCounts = Value(), centroid = Value(), min = Value(), max = Value(), size = Value();
    readonly TextBlock mDistance = Value(), mDelta = Value(), mAngles = Value(), mEdges = Value(), mArea = Value(), mHint = Value();
    readonly Expander measure;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    System.Numerics.Vector3? lastCursor;
    bool cursorShown;
    bool stale = true, queued;
    Measurement? last;

    /// <summary>The live panel instance (one per window), for commands that scroll it.</summary>
    public static InspectorPanel? Current { get; private set; }

    /// <summary>Expands the Measuring section and scrolls it into view (tools.measure).</summary>
    public void RevealMeasuring()
    {
        measure.IsExpanded = true;
        Dispatcher.BeginInvoke(() => measure.BringIntoView(), DispatcherPriority.Loaded);
    }

    public InspectorPanel()
    {
        Current = this;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Background = Dlg.Res("Editor.PanelBackground");
        var root = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        Content = root;

        root.Children.Add(Section("Position", Rows(("Cursor", cursor), ("Pin", pin), ("Cursor–pin", pinDist))));
        root.Children.Add(Section("Active LOD", Rows(("LOD", lodName), ("Resolution", lodRes), ("Contents", lodCounts))));
        root.Children.Add(Section("Selection", Rows(("Selected", selCounts), ("Centroid", centroid), ("Min", min), ("Max", max), ("Size", size))));

        var copy = new Button { Content = "Copy", Style = (Style)Application.Current.FindResource("CompactButton"), Padding = new Thickness(8, 1, 8, 1), MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), ToolTip = "Copy the measurements as text" };
        copy.Click += (_, _) => CopyText();
        var mPanel = new StackPanel();
        mPanel.Children.Add(Rows(("Distance", mDistance), ("Δ X Y Z", mDelta), ("Angles", mAngles), ("Edges", mEdges), ("Face area", mArea)));
        mHint.Foreground = Dlg.Res("Editor.Dim");
        mHint.TextWrapping = TextWrapping.Wrap;
        mHint.FontFamily = SystemFonts.MessageFontFamily;
        mPanel.Children.Add(mHint);
        mPanel.Children.Add(copy);
        measure = new Expander
        {
            Header = new TextBlock { Text = "Measuring", FontSize = 12, FontWeight = FontWeights.SemiBold },
            Content = mPanel,
            IsExpanded = Ed.Flag(MeasureFlag, true),
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(4, 2, 0, 0),
        };
        measure.Expanded += (_, _) => SyncFlag(true);
        measure.Collapsed += (_, _) => SyncFlag(false);
        root.Children.Add(measure);

        timer.Tick += (_, _) => UpdateCursor();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                timer.Start();
                UpdateCursor();
                if (stale) Queue();
            }
            else timer.Stop();
        };
        Ed.Changed += c =>
        {
            if ((c & Change.Ui) != 0 && measure.IsExpanded != Ed.Flag(MeasureFlag, true)) measure.IsExpanded = Ed.Flag(MeasureFlag, true);
            if ((c & Change.Pin) != 0) UpdateCursor(force: true);
            if ((c & (Change.Selection | Change.Model | Change.ActiveLod | Change.Load | Change.Document)) == 0) return;
            stale = true;
            if (IsVisible) Queue();
        };
        Queue();
    }

    void SyncFlag(bool expanded)
    {
        if (Ed.Flag(MeasureFlag, true) != expanded) Ed.SetFlag(MeasureFlag, expanded);
        if (expanded && stale) Queue();
    }

    // ---- layout helpers -----------------------------------------------------------------------------------

    static TextBlock Value() => new()
    {
        FontSize = 12,
        FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 0, 1),
    };

    static UIElement Section(string title, UIElement content)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        p.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
        p.Children.Add(content);
        return p;
    }

    static Grid Rows(params (string Label, TextBlock Value)[] rows)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = rows[i].Label, FontSize = 12, Foreground = Dlg.Res("Editor.Dim"), Margin = new Thickness(0, 1, 6, 1) };
            Grid.SetRow(l, i);
            g.Children.Add(l);
            Grid.SetRow(rows[i].Value, i);
            Grid.SetColumn(rows[i].Value, 1);
            g.Children.Add(rows[i].Value);
        }
        return g;
    }

    static string N(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    static string V(Vec3 v) => $"{N(v.X)}  {N(v.Y)}  {N(v.Z)}";
    static string V(System.Numerics.Vector3 v) => $"{N(v.X)}  {N(v.Y)}  {N(v.Z)}";

    // ---- cursor ---------------------------------------------------------------------------------------

    void UpdateCursor(bool force = false)
    {
        var c = CursorState.Raw;
        if (!force && cursorShown && c == lastCursor) return;
        lastCursor = c;
        cursorShown = true;
        cursor.Text = c is { } v ? V(v) : "— (move the mouse over a view)";
        var p = Ed.Pin;
        pin.Text = p is { } q ? V(q) : "— (not set)";
        pinDist.Text = c is { } a && p is { } b ? N(System.Numerics.Vector3.Distance(a, b)) + " m" : "—";
    }

    // ---- selection / LOD ----------------------------------------------------------------------------------

    void Queue()
    {
        if (queued) return;
        queued = true;
        Dispatcher.BeginInvoke(() =>
        {
            queued = false;
            if (!stale || !IsVisible) return;
            stale = false;
            Recompute();
        }, DispatcherPriority.Background);
    }

    void Recompute()
    {
        var lod = Ed.Lod;
        lodName.Text = LodNames.Name(lod.Resolution) + $"  (#{Ed.ActiveLod + 1} of {Ed.Model.Lods.Length})";
        lodRes.Text = lod.Resolution.ToString("G9", CultureInfo.InvariantCulture);
        int sels = 0;
        foreach (var _ in lod.Selections) sels++;
        lodCounts.Text = $"{lod.PointCount:N0} points, {lod.FaceCount:N0} faces\n{sels:N0} named selections";

        var sel = Selection.PeekSelected(lod);
        if (sel is not { } s || s.IsEmpty)
        {
            last = null;
            selCounts.Text = "nothing";
            centroid.Text = min.Text = max.Text = size.Text = "—";
            mDistance.Text = mDelta.Text = mAngles.Text = mEdges.Text = mArea.Text = "—";
            mHint.Text = "Select 2 points for a distance, 3 for angles, an edge path for its length, faces for their area.";
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = Attributes.Measure(lod, s.Points, s.Faces);
        if (Dlg.Automated) Ed.Log($"[perf] inspector measure: {sw.ElapsedMilliseconds} ms");
        last = m;
        selCounts.Text = $"{m.Points:N0} points, {m.Faces:N0} faces";
        centroid.Text = m.Centroid is { } c ? V(c) : "—";
        min.Text = m.Min is { } a ? V(a) : "—";
        max.Text = m.Max is { } b ? V(b) : "—";
        size.Text = m.Size is { } z ? V(z) : "—";

        mDistance.Text = m.Distance is { } d ? $"{N(d)} m  (#{m.FirstPoints[0]} → #{m.FirstPoints[1]})" : "— (select 2 points)";
        mDelta.Text = m.Delta is { } dv ? V(dv) : "—";
        mAngles.Text = m.Angles is { } ang
            ? $"#{m.FirstPoints[0]} {ang[0].ToString("0.##", CultureInfo.InvariantCulture)}°  #{m.FirstPoints[1]} {ang[1].ToString("0.##", CultureInfo.InvariantCulture)}°  #{m.FirstPoints[2]} {ang[2].ToString("0.##", CultureInfo.InvariantCulture)}°"
            : "— (select 3 points)";
        mEdges.Text = m.Edges > 0 ? $"{m.Edges:N0} edge{(m.Edges == 1 ? "" : "s")}, length {N(m.EdgeLength)} m" : "—";
        mArea.Text = m.Faces > 0 ? $"{N(m.Area)} m²  ({m.Faces:N0} face{(m.Faces == 1 ? "" : "s")})" : "—";
        mHint.Text = "";
    }

    void CopyText()
    {
        if (stale) Recompute();
        var sb = new StringBuilder();
        sb.AppendLine($"LOD {lodName.Text}");
        sb.AppendLine($"Selected: {selCounts.Text}");
        if (last is { } m)
        {
            if (m.Centroid is { } c) sb.AppendLine($"Centroid: {V(c)}");
            if (m.Size is { } z) sb.AppendLine($"Size: {V(z)}");
            if (m.Distance is { } d) sb.AppendLine($"Distance: {N(d)} m (delta {V(m.Delta!.Value)})");
            if (m.Angles is { } a) sb.AppendLine($"Angles: {string.Join(", ", a.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture) + "°"))}");
            if (m.Edges > 0) sb.AppendLine($"Edges: {m.Edges} length {N(m.EdgeLength)} m");
            if (m.Faces > 0) sb.AppendLine($"Face area: {N(m.Area)} m² ({m.Faces} faces)");
        }
        try
        {
            Clipboard.SetText(sb.ToString());
            Ed.Log("Measurements copied");
        }
        catch (Exception ex)
        {
            Dlg.Fail("Clipboard: " + ex.Message);
        }
    }
}
