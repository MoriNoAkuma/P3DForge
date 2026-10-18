using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using P3DForge.App.Anim;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Scene;
using P3DForge.App.Viewport;
using P3DForge.Core;
using P3DForge.Core.Anim;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Panels;

/// <summary>
/// Tools ▸ Animations (model.cfg). Built around three steps: select the moving part → "Animate selection…" (what is it,
/// where is the hinge) → test it with the slider and save. The selected animation is shown as a card (test slider, what
/// moves, how, range with Reverse, what drives it; rarely needed fields under Advanced) and drawn in the viewport (its
/// axis in yellow, the moving part's box in cyan). Problems, all sources and the skeleton sit in collapsed sections.
/// Save rewrites only the changed classes of model.cfg.
/// </summary>
public sealed class AnimationsPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string OverlayOwner = "animations";

    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 4), FontSize = 11.5 };
    readonly Button saveButton;
    readonly Border hint;
    readonly Expander problemsBox = new() { Margin = new Thickness(4, 2, 4, 2) };
    readonly StackPanel problemsList = new() { Margin = new Thickness(4, 2, 4, 2) };
    readonly TextBox filter = new() { FontSize = 12, Margin = new Thickness(6, 2, 6, 2), ToolTip = "Filter animations by name, part or source" };
    readonly ListBox list;
    readonly StackPanel card = new() { Margin = new Thickness(6, 4, 6, 6) };
    readonly Expander sourcesBox = new() { Margin = new Thickness(4, 2, 4, 2) };
    readonly StackPanel sources = new() { Margin = new Thickness(4, 0, 4, 4) };
    readonly CheckBox previewBox;
    readonly ToggleButton playButton;
    readonly Expander skeletonBox = new() { Margin = new Thickness(4, 2, 4, 6) };
    readonly ListBox boneList;
    readonly ComboBox boneParent = new() { MinWidth = 120, FontSize = 12, IsEditable = true };
    readonly ComboBox boneToAdd = new() { MinWidth = 120, FontSize = 12, IsEditable = true };
    readonly Dictionary<string, (Slider Slider, TextBlock Value)> sliders = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer playTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    Slider? testSlider;
    double playTime;
    bool loading, stale = true, keepCard;
    /// <summary>Name of the animation the card shows.</summary>
    string? cardName;

    /// <summary>The panel (for its commands); null until it was opened once.</summary>
    public static AnimationsPanel? Instance { get; private set; }

    public AnimationsPanel()
    {
        Instance = this;
        Background = Brushes.Transparent;

        // ---- top: the main actions ----
        var top = new StackPanel();
        var bar = new WrapPanel { Margin = new Thickness(4, 4, 4, 2) };
        var animate = Btn("＋ Animate selection…", "Select the moving part in the viewport first (click it, or a named selection), then say what it is — door, wheel, lever, bolt… (also on the viewport's right-click menu)", Animate);
        animate.FontWeight = FontWeights.SemiBold;
        bar.Children.Add(animate);
        saveButton = Btn("Save model.cfg", "Write the changes to model.cfg (only the changed classes are rewritten; comments stay)", Save);
        bar.Children.Add(saveButton);
        bar.Children.Add(Btn("Reload", "Read model.cfg again (drops unsaved changes)", () =>
        {
            if (AnimSession.Dirty && !Dlg.Confirm("Reload model.cfg", "Drop the unsaved animation changes?", "Reload")) return;
            AnimSession.Refresh(force: true);
        }));
        bar.Children.Add(Btn("Open file", "Open model.cfg in the program Windows uses for it", () =>
        {
            if (AnimSession.CfgPath is { } p) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        }));
        top.Children.Add(bar);
        top.Children.Add(status);
        SetDock(top, Dock.Top);
        Children.Add(top);

        var content = new StackPanel();
        Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        // ---- getting started ----
        hint = new Border
        {
            Margin = new Thickness(6, 2, 6, 6),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("Editor.Faint"),
            Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Text = "How to animate a part:\n" +
                       "1. Click the part in the viewport (or a named selection in Named Selections).\n" +
                       "2. Press \"＋ Animate selection…\" and say what it is: door, wheel, lever, bolt…\n" +
                       "3. Move the Test slider to check it; Reverse if it goes the wrong way.\n" +
                       "4. Save model.cfg (and the model, if new points were added).",
            },
        };
        content.Children.Add(hint);

        // ---- problems ----
        problemsBox.Content = problemsList;
        content.Children.Add(problemsBox);

        // ---- animations ----
        content.Children.Add(Section("Animations"));
        filter.TextChanged += (_, _) =>
        {
            if (!loading) FillList();
        };
        content.Children.Add(filter);
        list = new ListBox
        {
            MaxHeight = 240,
            MinHeight = 40,
            Margin = new Thickness(6, 0, 6, 2),
            ItemContainerStyle = (Style)FindResource("CompactListBoxItem"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            ShowCard();
            UpdateOverlay();
        };
        content.Children.Add(list);
        content.Children.Add(card);

        // ---- all sources ----
        var srcContent = new StackPanel();
        var previewRow = new WrapPanel { Margin = new Thickness(2, 2, 2, 2) };
        previewBox = new CheckBox { Content = "Animate in the viewport", FontSize = 12, Margin = new Thickness(2, 3, 8, 3), ToolTip = "Display only — the model is not changed" };
        previewBox.Checked += (_, _) =>
        {
            if (!loading) AnimSession.SetPreview(true);
        };
        previewBox.Unchecked += (_, _) =>
        {
            if (loading) return;
            playButton!.IsChecked = false;
            AnimSession.SetPreview(false);
        };
        previewRow.Children.Add(previewBox);
        playButton = new ToggleButton { Content = "▶ Play all", FontSize = 12, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Moves every source from its start to its end and back" };
        playButton.Checked += (_, _) =>
        {
            AnimSession.SetPreview(true);
            playTime = 0;
            playTimer.Start();
        };
        playButton.Unchecked += (_, _) => playTimer.Stop();
        previewRow.Children.Add(playButton);
        previewRow.Children.Add(Btn("Reset", "All sources back to the rest pose", () =>
        {
            playButton.IsChecked = false;
            AnimSession.ResetValues();
        }));
        srcContent.Children.Add(previewRow);
        srcContent.Children.Add(sources);
        sourcesBox.Content = srcContent;
        content.Children.Add(sourcesBox);
        playTimer.Tick += (_, _) => Play();

        // ---- skeleton ----
        var skel = new StackPanel();
        boneList = new ListBox { MaxHeight = 180, MinHeight = 40, Margin = new Thickness(2, 0, 2, 2), ItemContainerStyle = (Style)FindResource("CompactListBoxItem") };
        boneList.SelectionChanged += (_, _) => ShowBoneParent();
        skel.Children.Add(boneList);
        var boneRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        boneRow.Children.Add(new TextBlock { Text = "Moves with:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) });
        boneParent.ToolTip = "The bone this one is attached to (e.g. bolthandle moves with boltbody); empty = none";
        boneParent.SelectionChanged += (_, _) => SetBoneParent(boneParent.SelectedItem as string ?? "");
        boneParent.LostKeyboardFocus += (_, _) => SetBoneParent(boneParent.Text);
        boneRow.Children.Add(boneParent);
        boneRow.Children.Add(Btn("Remove bone", "Remove the selected bone from the skeleton", RemoveBone));
        skel.Children.Add(boneRow);
        var addRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        addRow.Children.Add(new TextBlock { Text = "Add bone:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) });
        boneToAdd.ToolTip = "A named selection of the model to make a bone";
        addRow.Children.Add(boneToAdd);
        addRow.Children.Add(Btn("Add", "Add this named selection as a skeleton bone", () => AddBone(boneToAdd.Text)));
        skel.Children.Add(addRow);
        skeletonBox.Content = skel;
        content.Children.Add(skeletonBox);

        AnimSession.Changed += () =>
        {
            if (IsVisible) Refresh();
            else stale = true;
        };
        AnimSession.Posed += () =>
        {
            if (IsVisible) UpdateOverlay();
        };
        ProxySceneHost.Rebuilt += () =>
        {
            if (IsVisible) UpdateOverlay(); // a proxy model finished loading
        };
        Ed.Changed += c =>
        {
            if ((c & (Change.ActiveLod | Change.Load | Change.Document | Change.Model)) == 0) return;
            if (!IsVisible)
            {
                stale = true;
                return;
            }
            if ((c & Change.Model) != 0 && (c & (Change.ActiveLod | Change.Load | Change.Document)) == 0) UpdateOverlay();
            else Refresh();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AnimSession.Refresh();
                if (stale) Refresh();
                UpdateOverlay();
            }
            else
            {
                playButton.IsChecked = false;
                previewBox.IsChecked = false; // a hidden panel must not leave the model drawn animated
                ViewportPlugins.Current?.SetOverlayLines(OverlayOwner, null);
            }
        };
    }

    // ---- refresh ----------------------------------------------------------------------------------

    void Refresh()
    {
        stale = false;
        loading = true;
        var lod = Ed.Lod;
        int count = AnimSession.All.Count();
        string where = AnimSession.CfgPath is { } p ? ShortPath(p)
            : Ed.Path is null && Services.FileService.Converted is null ? "save the model first"
            : "no model.cfg yet — Save creates one next to the model";
        status.Text = $"{where} · class \"{AnimSession.Model}\" · {count} animation{(count == 1 ? "" : "s")}"
                      + (AnimSession.Dirty ? " · ● unsaved changes" : "");
        status.Foreground = (Brush)FindResource(AnimSession.Dirty ? "Editor.Warn" : "Editor.Dim");
        status.ToolTip = AnimSession.CfgPath;
        saveButton.IsEnabled = Ed.Path is not null && (AnimSession.Dirty || AnimSession.CfgPath is null);
        saveButton.FontWeight = AnimSession.Dirty ? FontWeights.SemiBold : FontWeights.Normal;
        hint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // problems
        problemsList.Children.Clear();
        var lines = new List<string>();
        if (AnimSession.LoadError is { } err) lines.Add(err);
        if (AnimSession.Cfg is { } cfg) lines.AddRange(cfg.Warnings.Where(w => !w.Contains("comment", StringComparison.OrdinalIgnoreCase)).Take(3));
        var rig = AnimSession.Rig;
        if (rig is not null) lines.AddRange(rig.Problems);
        var missing = MissingBones();
        foreach (var line in lines.Take(40))
        {
            var t = new TextBlock { Text = "⚠ " + line, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Editor.Warn"), Margin = new Thickness(0, 1, 0, 1) };
            var animName = line.Split(':')[0];
            if (AnimSession.All.Any(a => a.Name == animName))
            {
                t.Cursor = Cursors.Hand;
                t.ToolTip = "Click: show this animation";
                t.MouseLeftButtonUp += (_, _) => SelectByName(animName);
            }
            problemsList.Children.Add(t);
        }
        if (missing.Count > 0)
        {
            var fix = Btn($"Add the missing bones ({string.Join(", ", missing.Take(4))}{(missing.Count > 4 ? "…" : "")})", "Animated parts that are not skeleton bones are ignored by the game", AddMissingBones);
            fix.HorizontalAlignment = HorizontalAlignment.Left;
            problemsList.Children.Add(fix);
        }
        int problemCount = lines.Count + (missing.Count > 0 ? 1 : 0);
        problemsBox.Header = problemCount == 0 ? "✓ No problems found" : $"⚠ {problemCount} problem{(problemCount == 1 ? "" : "s")}";
        problemsBox.Foreground = (Brush)FindResource(problemCount == 0 ? "Editor.Ok" : "Editor.Warn");
        problemsBox.IsEnabled = problemCount > 0;
        problemsBox.IsExpanded = problemCount is > 0 and <= 3;
        problemsBox.Visibility = count == 0 && problemCount == 0 ? Visibility.Collapsed : Visibility.Visible;

        // sources
        sources.Children.Clear();
        sliders.Clear();
        previewBox.IsChecked = AnimSession.PreviewOn;
        int sourceCount = rig?.Sources.Count ?? 0;
        if (rig is not null)
            foreach (var s in rig.Sources) sources.Children.Add(SourceRow(s));
        sourcesBox.Header = $"Test all sources ({sourceCount})";
        sourcesBox.Visibility = sourceCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        filter.Visibility = count > 8 ? Visibility.Visible : Visibility.Collapsed;
        FillList();

        // bones
        var selectedBone = (boneList.SelectedItem as ListBoxItem)?.Tag as string;
        boneList.Items.Clear();
        foreach (var b in AnimSession.InheritedBones)
            boneList.Items.Add(new ListBoxItem { Content = BoneText(b) + "   (inherited)", Tag = null, Foreground = (Brush)FindResource("Editor.Dim") });
        foreach (var b in AnimSession.OwnBones)
        {
            var item = new ListBoxItem { Content = BoneText(b), Tag = b.Name };
            if (lod.Selection(b.Name) is null)
            {
                item.Content += "   — no named selection in this LOD";
                item.Foreground = (Brush)FindResource("Editor.Warn");
            }
            boneList.Items.Add(item);
            if (b.Name == selectedBone) boneList.SelectedItem = item;
        }
        skeletonBox.Header = $"Skeleton \"{AnimSession.Skeleton}\" ({AnimSession.AllBones.Count()} bones)";
        boneToAdd.ItemsSource = SelectionNames(lod).Where(n => !AnimSession.IsBone(n) && !Proxies.IsProxyName(n)).ToList();
        loading = false;
        if (!keepCard) ShowCard();
        keepCard = false;
        ShowBoneParent();
        UpdateOverlay();
    }

    void FillList()
    {
        bool wasLoading = loading;
        loading = true;
        var keep = keepCard ? cardName : (list.SelectedItem as ListBoxItem)?.Tag is AnimDef d ? d.Name : cardName;
        var f = filter.Text.Trim();
        list.Items.Clear();
        foreach (var a in AnimSession.All)
        {
            if (f.Length > 0 && !(a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || a.Selection.Contains(f, StringComparison.OrdinalIgnoreCase)
                                   || a.Source.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
            bool own = AnimSession.Own.Contains(a);
            var row = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new TextBlock
            {
                Text = $"{Icon(a)}  {a.Name}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{Friendly(a.Type)} · part {a.Selection} · driven by {a.Source}{(own ? "" : $" · from {a.Owner}")}",
                FontSize = 11,
                Foreground = (Brush)FindResource("Editor.Dim"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var item = new ListBoxItem { Content = row, Tag = a, Opacity = own ? 1 : 0.7 };
            list.Items.Add(item);
            if (a.Name == keep) list.SelectedItem = item;
        }
        if (list.SelectedItem is null && list.Items.Count > 0) list.SelectedIndex = 0;
        loading = wasLoading;
    }

    static string Icon(AnimDef a) => a.Kind switch { "rotation" => "⟳", "translation" => "⇄", "hide" => "◌", _ => "◆" };

    static readonly (string Type, string Label)[] TypeLabels =
    [
        ("rotation", "Turns around its axis"),
        ("rotationX", "Turns around the model's X"),
        ("rotationY", "Turns around the model's Y"),
        ("rotationZ", "Turns around the model's Z"),
        ("translation", "Slides along its axis"),
        ("translationX", "Slides along the model's X"),
        ("translationY", "Slides along the model's Y"),
        ("translationZ", "Slides along the model's Z"),
        ("hide", "Hides / shows"),
        ("direct", "Direct (advanced)"),
    ];

    static string Friendly(string type) => TypeLabels.FirstOrDefault(t => t.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).Label ?? type;

    static string ShortPath(string p)
    {
        var parts = p.Split('\\');
        return parts.Length <= 3 ? p : $"{parts[0]}\\…\\{parts[^2]}\\{parts[^1]}";
    }

    static string BoneText(Bone b) => b.Parent.Length > 0 ? $"{b.Name}   (moves with {b.Parent})" : b.Name;

    static List<string> SelectionNames(Lod lod) => lod.Selections.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    static Lod? MemoryLod => Ed.Model.Lods.FirstOrDefault(l => LodNames.IsMemory(l.Resolution));

    static List<string> MissingBones() =>
        AnimSession.All.Select(a => a.Selection).Where(s => s.Length > 0 && !AnimSession.IsBone(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    FrameworkElement SourceRow(AnimSource s)
    {
        double value = AnimSession.Values.TryGetValue(s.Name, out var v) ? v : AnimRig.Rest(s);
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var name = new TextBlock { Text = s.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"{s.Name}: {string.Join(", ", s.Animations)}" };
        double min = s.Min, max = s.Max > s.Min ? s.Max : s.Min + 1;
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        var text = new TextBlock { Text = Fmt(value), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        slider.ValueChanged += (_, e) =>
        {
            text.Text = Fmt(e.NewValue);
            if (loading) return;
            AnimSession.SetValue(s.Name, e.NewValue);
            if (!AnimSession.PreviewOn) AnimSession.SetPreview(true);
        };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(text, 2);
        row.Children.Add(name);
        row.Children.Add(slider);
        row.Children.Add(text);
        sliders[s.Name] = (slider, text);
        return row;
    }

    static string Fmt(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : "0.###", Inv);

    void Play()
    {
        if (AnimSession.Rig is not { } rig) return;
        playTime += playTimer.Interval.TotalSeconds;
        double k = 0.5 - 0.5 * Math.Cos(playTime * Math.PI * 2 / 3.0); // 3 s there and back
        loading = true;
        foreach (var s in rig.Sources)
        {
            double v = s.Min + (s.Max - s.Min) * k;
            AnimSession.Values[s.Name] = v;
            if (sliders.TryGetValue(s.Name, out var ui))
            {
                ui.Slider.Value = v;
                ui.Value.Text = Fmt(v);
            }
        }
        loading = false;
        AnimSession.ApplyPreview();
    }

    // ---- card -------------------------------------------------------------------------------------

    AnimDef? Selected => (list.SelectedItem as ListBoxItem)?.Tag as AnimDef;

    void ShowCard()
    {
        var a = Selected;
        cardName = a?.Name;
        card.Children.Clear();
        testSlider = null;
        if (a is null) return;
        bool own = AnimSession.Own.Contains(a);
        var lod = Ed.Lod;
        var memory = MemoryLod;
        var axisNames = a.Memory && memory is not null ? SelectionNames(memory) : SelectionNames(lod);
        var sourceNames = AnimSession.All.Select(x => x.Source).Concat(CommonSources).Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var title = new TextBlock { Text = $"{Icon(a)}  {a.Name}", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) };
        card.Children.Add(title);
        if (!own)
        {
            card.Children.Add(Note($"Inherited from the model class \"{a.Owner}\".", "Editor.Dim"));
            var o = Btn($"Override in \"{AnimSession.Model}\" to change it", "Copies this animation into this model's own Animations", () =>
            {
                AnimSession.Own.Add(a with { Owner = AnimSession.Model });
                AnimSession.Touch();
            });
            o.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(o);
        }

        // test
        var test = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        test.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        test.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var testLabel = new TextBlock { Text = "Test", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        double cur = AnimSession.Values.TryGetValue(a.Source, out var cv) ? cv : AnimRig.Rest(a);
        testSlider = new Slider { Minimum = 0, Maximum = 1, Value = AnimRig.Phase(a, cur), ToolTip = $"Moves \"{a.Source}\" from {Fmt(a.MinValue)} (start) to {Fmt(a.MaxValue)} (end) — the viewport shows the result" };
        testSlider.ValueChanged += (_, e) =>
        {
            if (loading) return;
            var latest = Find(cardName) ?? a;
            double value = latest.MinValue + e.NewValue * (latest.MaxValue - latest.MinValue);
            AnimSession.SetValue(latest.Source, value);
            if (!AnimSession.PreviewOn) AnimSession.SetPreview(true);
            if (sliders.TryGetValue(latest.Source, out var ui))
            {
                loading = true;
                ui.Slider.Value = value;
                ui.Value.Text = Fmt(value);
                loading = false;
            }
        };
        Grid.SetColumn(testSlider, 1);
        test.Children.Add(testLabel);
        test.Children.Add(testSlider);
        card.Children.Add(test);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Children.Add(form);

        // what moves
        var partRow = new DockPanel();
        var show = Btn("Select", "Select this part in the viewport", () => SelectionOps.SelectNamed(a.Selection, SelectOp.Set));
        show.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(show, Dock.Right);
        partRow.Children.Add(show);
        partRow.Children.Add(Combo(SelectionNames(lod).Where(n => !Proxies.IsProxyName(n)).ToList(), a.Selection,
            v => Edit(x => x with { Selection = v }), own, tip: "The named selection (skeleton bone) that moves"));
        AddRow(form, "What moves", partRow);

        // how
        AddRow(form, "How", Combo(TypeLabels.Select(t => t.Label).ToList(), Friendly(a.Type), v =>
        {
            var type = TypeLabels.FirstOrDefault(t => t.Label == v).Type;
            if (type is not null) Edit(x => x with { Type = type });
        }, own, editable: false, tip: "Turns: doors, wheels, levers · Slides: bolts, drawers (\"its axis\": offsets in axis lengths; model X/Y/Z: metres) · Hides"));

        // axis
        if (a.Kind is "rotation" or "translation")
        {
            bool fixedAxis = char.ToLowerInvariant(a.Type[^1]) is 'x' or 'y' or 'z';
            var axisRow = new DockPanel();
            var newAxis = Btn("New…", "Creates two points in the Memory LOD (through the part along X / Y / Z, on an edge, or from 2 selected points) and uses them as the axis", NewAxis);
            newAxis.Margin = new Thickness(4, 0, 0, 0);
            newAxis.IsEnabled = own;
            DockPanel.SetDock(newAxis, Dock.Right);
            axisRow.Children.Add(newAxis);
            axisRow.Children.Add(Combo(axisNames, a.Axis, v => Edit(x => x with { Axis = v }), own,
                tip: fixedAxis ? "Optional: a memory point the turn goes through (otherwise the model's origin)" : "Two points in the Memory LOD (the yellow line in the viewport)"));
            AddRow(form, fixedAxis ? "Through" : "Axis", axisRow);
        }

        // range
        switch (a.Kind)
        {
            case "rotation":
                AddRow(form, "Turns", Range(Deg(a.Angle0), Deg(a.Angle1), "°", v0 => Edit(x => x with { Angle0 = Rad(v0) }), v1 => Edit(x => x with { Angle1 = Rad(v1) }),
                    () => Edit(x => x with { Angle0 = -x.Angle0, Angle1 = -x.Angle1 }), own));
                break;
            case "translation":
                string unit = a.Type.Equals("translation", StringComparison.OrdinalIgnoreCase) ? "× axis" : "m";
                AddRow(form, "Moves", Range(a.Offset0, a.Offset1, unit, v0 => Edit(x => x with { Offset0 = v0 }), v1 => Edit(x => x with { Offset1 = v1 }),
                    () => Edit(x => x with { Offset0 = -x.Offset0, Offset1 = -x.Offset1 }), own));
                break;
            case "hide":
                AddRow(form, "Hidden from", Number(a.HideValue, v => Edit(x => x with { HideValue = v }), own, "Hidden when the test slider (phase 0..1) is at or past this"));
                break;
            case "direct":
                AddRow(form, "Axis pos", Text(Vec(a.AxisPos), v => Edit(x => x with { AxisPos = ParseVec(v, x.AxisPos) }), own, "x y z"));
                AddRow(form, "Axis dir", Text(Vec(a.AxisDir), v => Edit(x => x with { AxisDir = ParseVec(v, x.AxisDir) }), own, "x y z"));
                AddRow(form, "Angle (°)", Number(Deg(a.Angle), v => Edit(x => x with { Angle = Rad(v) }), own, "Degrees at the end"));
                AddRow(form, "Axis offset", Number(a.AxisOffset, v => Edit(x => x with { AxisOffset = v }), own, "Metres along the axis at the end"));
                break;
        }

        // driven by
        AddRow(form, "Driven by", Combo(sourceNames, a.Source, v => Edit(x => x with { Source = v }), own,
            tip: "The source: user (actions / scripts, building doors), or a vehicle / weapon source (doorsdriver, wheelfrontleft, steeringwheel…)"));
        AddRow(form, "From value", Number(a.MinValue, v => Edit(x => x with { MinValue = v }), own, "Source value at the start (the test slider's left end)"));
        AddRow(form, "To value", Number(a.MaxValue, v => Edit(x => x with { MaxValue = v }), own, "Source value at the end (the test slider's right end)"));

        // advanced
        var adv = new Grid();
        adv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        adv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(adv, "Name", Text(a.Name, v => Edit(x => x with { Name = SafeName(v) }), own, "Class name in model.cfg"));
        if (a.Kind is "rotation" or "translation")
        {
            AddRow(adv, "Begin / End", Text($"{a.Begin} {a.End}".Trim(), v =>
            {
                var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Edit(x => x with { Begin = parts.ElementAtOrDefault(0) ?? "", End = parts.ElementAtOrDefault(1) ?? "" });
            }, own, "Instead of Axis: two one-point memory selections \"begin end\""));
            AddRow(adv, "", Check("axis points in the Memory LOD (memory = 1)", a.Memory, v => Edit(x => x with { Memory = v }), own));
        }
        if (a.Kind == "hide")
            AddRow(adv, "Shown again at", Number(double.IsPositiveInfinity(a.UnHideValue) ? -1 : a.UnHideValue,
                v => Edit(x => x with { UnHideValue = v < 0 ? double.PositiveInfinity : v }), own, "Shown again from this phase; -1 = never"));
        AddRow(adv, "Outside the range", Combo(["clamp", "loop", "mirror"], a.SourceAddress.ToLowerInvariant().Contains("loop") ? "loop" : a.SourceAddress.ToLowerInvariant().Contains("mirror") ? "mirror" : "clamp",
            v => Edit(x => x with { SourceAddress = v }), own, editable: false, tip: "clamp: stops at the ends · loop: starts over (wheels) · mirror: goes back and forth"));
        if (a.Other.Count > 0)
            AddRow(adv, "Other", new TextBlock { Text = string.Join("; ", a.Other.Select(o => $"{o.Key} = {o.Value}")), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Editor.Dim") });
        card.Children.Add(new Expander { Header = "Advanced", Content = adv, Margin = new Thickness(0, 4, 0, 2), FontSize = 12 });

        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(Btn("Duplicate", "Copy this animation (e.g. for the other door)", Duplicate));
        if (own) actions.Children.Add(Btn("Delete", "Remove this animation", Delete));
        card.Children.Add(actions);
    }

    static readonly string[] CommonSources = ["user", "reload", "bolt", "trigger", "magazine", "hit", "damage", "isFlipped", "door", "time"];

    static double Deg(double rad) => Math.Round(rad * 180 / Math.PI, 4);
    static double Rad(double deg) => deg * Math.PI / 180;
    static string Vec(double[] v) => string.Join(" ", v.Select(x => x.ToString("0.####", Inv)));

    static double[] ParseVec(string s, double[] fallback)
    {
        var parts = s.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return fallback;
        var r = new double[3];
        for (int i = 0; i < 3; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, Inv, out r[i])) return fallback;
        return r;
    }

    static string SafeName(string s)
    {
        var n = new string(s.Trim().Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        return n.Length == 0 ? "Anim" : char.IsDigit(n[0]) ? "Anim_" + n : n;
    }

    static AnimDef? Find(string? name) => name is null ? null : AnimSession.Own.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Changes the animation on the card; the card stays (Tab keeps working) unless what it shows changes.</summary>
    void Edit(Func<AnimDef, AnimDef> change)
    {
        int i = AnimSession.Own.FindIndex(x => string.Equals(x.Name, cardName, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        var old = AnimSession.Own[i];
        var updated = change(old);
        if (old.SameAs(updated)) return;
        if (!string.Equals(old.Name, updated.Name, StringComparison.OrdinalIgnoreCase) && AnimSession.All.Any(x => string.Equals(x.Name, updated.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Dlg.Fail($"An animation called \"{updated.Name}\" already exists");
            return;
        }
        AnimSession.Own[i] = updated;
        cardName = updated.Name;
        // rebuild the card when its layout or both range values change (type, Reverse); single fields keep focus
        bool both = (old.Angle0 != updated.Angle0 && old.Angle1 != updated.Angle1) || (old.Offset0 != updated.Offset0 && old.Offset1 != updated.Offset1);
        bool reshape = old.Kind != updated.Kind || old.Type != updated.Type || old.Memory != updated.Memory || both;
        keepCard = !reshape;
        AnimSession.Touch(); // refreshes the list (selection kept by name)
    }

    FrameworkElement Range(double from, double to, string unit, Action<double> setFrom, Action<double> setTo, Action reverse, bool enabled)
    {
        var row = new DockPanel();
        var rev = Btn("⇄ Reverse", "Turns / moves the other way (both values change sign)", reverse);
        rev.IsEnabled = enabled;
        rev.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(rev, Dock.Right);
        row.Children.Add(rev);
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(new TextBlock { Text = "from", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        var a = Number(from, setFrom, enabled, $"At the start ({unit})");
        a.Width = 58;
        inner.Children.Add(a);
        inner.Children.Add(new TextBlock { Text = "to", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        var b = Number(to, setTo, enabled, $"At the end ({unit})");
        b.Width = 58;
        inner.Children.Add(b);
        inner.Children.Add(new TextBlock { Text = unit, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        row.Children.Add(inner);
        return row;
    }

    static void AddRow(Grid g, string label, FrameworkElement editor)
    {
        int r = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 2), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(l, r);
        Grid.SetRow(editor, r);
        Grid.SetColumn(editor, 1);
        editor.Margin = new Thickness(0, 2, 0, 2);
        g.Children.Add(l);
        g.Children.Add(editor);
    }

    FrameworkElement Text(string value, Action<string> commit, bool enabled, string tip)
    {
        var t = new TextBox { Text = value, Style = (Style)FindResource("NumberTextBox"), IsEnabled = enabled, ToolTip = tip };
        t.LostKeyboardFocus += (_, _) => commit(t.Text);
        t.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) commit(t.Text);
        };
        return t;
    }

    FrameworkElement Number(double value, Action<double> commit, bool enabled, string tip) =>
        Text(value.ToString("0.#######", Inv), s =>
        {
            if (ModelCfg.TryEval(s, out var v)) commit(v);
        }, enabled, tip + " (numbers, \"rad 90\" or expressions work)");

    FrameworkElement Combo(IReadOnlyList<string> items, string value, Action<string> commit, bool enabled, bool editable = true, string? tip = null)
    {
        var c = new ComboBox { ItemsSource = items, IsEditable = editable, Text = value, FontSize = 12, IsEnabled = enabled, ToolTip = tip };
        if (!editable) c.SelectedItem = items.FirstOrDefault(i => string.Equals(i, value, StringComparison.OrdinalIgnoreCase));
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedItem is string s) commit(s);
        };
        if (editable) c.LostKeyboardFocus += (_, _) => commit(c.Text.Trim());
        return c;
    }

    FrameworkElement Check(string label, bool value, Action<bool> commit, bool enabled)
    {
        var c = new CheckBox { Content = label, IsChecked = value, FontSize = 12, IsEnabled = enabled };
        c.Checked += (_, _) => commit(true);
        c.Unchecked += (_, _) => commit(false);
        return c;
    }

    // ---- viewport overlay: axis + moving part ------------------------------------------------------

    void UpdateOverlay()
    {
        var ctx = ViewportPlugins.Current;
        if (ctx is null) return;
        if (!IsVisible || Selected is not { } a)
        {
            ctx.SetOverlayLines(OverlayOwner, null);
            ctx.Invalidate();
            return;
        }
        const uint yellow = 0xFFD040FF, cyan = 0x40D0FFFF;
        var lines = new List<OverlayLine>();
        var lod = Ed.Lod;
        // the moving part's box, turned with the part while the preview animates it
        var pose = AnimSession.PreviewOn ? AnimSession.Rig?.BoneTransform(a.Selection, AnimSession.Values) ?? Matrix4x4.Identity : Matrix4x4.Identity;
        if (lod.Selection(a.Selection) is { } bone)
        {
            // the proxy models on the part's proxy triangles move with it (a door is often a proxy): the box holds the
            // model, not its triangle; the instances are already posed, so they go back to the rest pose like the points
            bool In(uint p) => p < bone.Points.Length && bone.Points[p] != 0;
            Matrix4x4.Invert(pose, out var unpose);
            var pts = new List<Vector3>();
            var triangles = new HashSet<uint>();
            var proxies = ProxyScene.Instances;
            for (int i = 0; i < proxies.Count; i++)
                if (proxies[i].Model.Lod is { } pl && ProxySceneHost.TopLevelOf(i) is { } top && In(top.A) && In(top.B) && In(top.C))
                {
                    triangles.UnionWith([top.A, top.B, top.C]);
                    var m = proxies[i].Transform * unpose;
                    for (int k = 0; k < pl.PointCount; k++)
                        pts.Add(Vector3.Transform(new Vector3(pl.Positions[k * 3], pl.Positions[k * 3 + 1], pl.Positions[k * 3 + 2]), m));
                }
            for (int i = 0; i < Math.Min(bone.Points.Length, lod.PointCount); i++)
                if (bone.Points[i] != 0 && !triangles.Contains((uint)i)) pts.Add(new Vector3(lod.Positions[i * 3], lod.Positions[i * 3 + 1], lod.Positions[i * 3 + 2]));
            if (pts.Count > 0)
            {
                var min = pts.Aggregate(Vector3.Min);
                var max = pts.Aggregate(Vector3.Max);
                Vector3 C(int k) => Vector3.Transform(new Vector3((k & 1) == 0 ? min.X : max.X, (k & 2) == 0 ? min.Y : max.Y, (k & 4) == 0 ? min.Z : max.Z), pose);
                foreach (var (i, j) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
                    lines.Add(new OverlayLine(Coords.RawToRender(C(i)), Coords.RawToRender(C(j)), cyan));
            }
        }
        // the axis (with an arrow at its end)
        var axisLod = a.Memory ? MemoryLod ?? lod : lod;
        Vector3? p0 = null, p1 = null;
        if (a.Axis.Length > 0 && axisLod.Selection(a.Axis) is { } ax)
        {
            var pts = Enumerable.Range(0, Math.Min(ax.Points.Length, axisLod.PointCount)).Where(i => ax.Points[i] != 0).Take(2)
                .Select(i => new Vector3(axisLod.Positions[i * 3], axisLod.Positions[i * 3 + 1], axisLod.Positions[i * 3 + 2])).ToList();
            if (pts.Count == 2) (p0, p1) = (pts[0], pts[1]);
        }
        else if (a.Begin.Length > 0 && a.End.Length > 0 && axisLod.Selection(a.Begin) is { } bs && axisLod.Selection(a.End) is { } es)
        {
            int bi = Array.FindIndex(bs.Points, b => b != 0), ei = Array.FindIndex(es.Points, b => b != 0);
            if (bi >= 0 && ei >= 0)
                (p0, p1) = (new Vector3(axisLod.Positions[bi * 3], axisLod.Positions[bi * 3 + 1], axisLod.Positions[bi * 3 + 2]),
                    new Vector3(axisLod.Positions[ei * 3], axisLod.Positions[ei * 3 + 1], axisLod.Positions[ei * 3 + 2]));
        }
        if (p0 is { } q0 && p1 is { } q1 && (q1 - q0).LengthSquared() > 1e-12f)
        {
            var d = q1 - q0;
            var a0 = Coords.RawToRender(q0 - d * 0.5f);
            var a1 = Coords.RawToRender(q1 + d * 0.5f);
            lines.Add(new OverlayLine(a0, a1, yellow));
            var dir = Vector3.Normalize(a1 - a0);
            var side = Vector3.Cross(dir, Math.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            side = Vector3.Normalize(side) * d.Length() * 0.15f;
            var back = a1 - dir * d.Length() * 0.3f;
            lines.Add(new OverlayLine(a1, back + side, yellow));
            lines.Add(new OverlayLine(a1, back - side, yellow));
        }
        ctx.SetOverlayLines(OverlayOwner, lines);
        ctx.Invalidate();
    }

    // ---- actions ----------------------------------------------------------------------------------

    /// <summary>"Animate selection…" (also from the viewport menu): the wizard, then its animation on the card.</summary>
    public void Animate()
    {
        if (AnimWizard.Run() is { } name)
        {
            filter.Text = "";
            SelectByName(name);
            if (testSlider is not null)
            {
                loading = true;
                testSlider.Value = 1;
                loading = false;
            }
        }
    }

    /// <summary>Two new Memory LOD points named "&lt;part&gt;_axis" for the animation on the card (one undo step).</summary>
    void NewAxis()
    {
        var a = Find(cardName);
        if (a is null) return;
        var lod = Ed.Lod;
        var sel = Selection.PeekSelected(lod);
        var picked = sel?.Points is { } sp ? Enumerable.Range(0, Math.Min(sp.Length, lod.PointCount)).Where(i => sp[i] != 0).ToList() : [];
        var form = new Dialogs.Ops.Form("New axis", $"Two points in the Memory LOD for \"{a.Name}\" (part \"{a.Selection}\").")
            .Text("name", "Name", AnimSession.UniqueName(a.Selection + "_axis"))
            .Choice("dir", "Direction", ["X — left / right", "Y — up / down", "Z — forward / back", $"from the 2 selected points of this LOD ({picked.Count} selected)"],
                picked.Count == 2 ? 3 : a.Type.EndsWith('X') ? 0 : a.Type.EndsWith('Z') ? 2 : 1)
            .Choice("at", "Through", ["the centre of the part", "left edge (−X)", "right edge (+X)", "bottom (−Y)", "top (+Y)", "back (−Z)", "front (+Z)"]);
        if (!form.Show("Create")) return;
        var name = SafeName(form.Str("name"));
        Vector3 P(int i) => new(lod.Positions[i * 3], lod.Positions[i * 3 + 1], lod.Positions[i * 3 + 2]);
        Vector3 p0, p1;
        int dirChoice = form.Index("dir");
        if (dirChoice == 3)
        {
            if (picked.Count != 2)
            {
                Dlg.Fail("Select exactly 2 points of this LOD first (or choose a direction)");
                return;
            }
            (p0, p1) = (P(picked[0]), P(picked[1]));
        }
        else
        {
            var bone = lod.Selection(a.Selection);
            var pts = bone is null ? [] : Enumerable.Range(0, Math.Min(bone.Points.Length, lod.PointCount)).Where(i => bone.Points[i] != 0).Select(P).ToList();
            if (pts.Count == 0)
            {
                Dlg.Fail($"\"{a.Selection}\" has no points in this LOD — choose the part first");
                return;
            }
            var min = pts.Aggregate(Vector3.Min);
            var max = pts.Aggregate(Vector3.Max);
            var c = (min + max) / 2;
            switch (form.Index("at"))
            {
                case 1: c.X = min.X; break;
                case 2: c.X = max.X; break;
                case 3: c.Y = min.Y; break;
                case 4: c.Y = max.Y; break;
                case 5: c.Z = min.Z; break;
                case 6: c.Z = max.Z; break;
            }
            var dir = dirChoice switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
            float half = Math.Max(0.02f, Vector3.Dot(max - min, dir) / 2);
            (p0, p1) = (c - dir * half, c + dir * half);
        }
        bool ok = false;
        Dlg.Try(() => ok = Ed.Edit($"New axis {name}", m =>
        {
            int mi = Array.FindIndex(m.Lods.ToArray(), l => LodNames.IsMemory(l.Resolution));
            if (mi < 0)
            {
                var lods = m.Lods.ToList();
                mi = LodList.SortedInsertIndex(lods, 1e15f);
                lods.Insert(mi, Lod.Empty(1e15f));
                m = m with { Lods = [.. lods] };
            }
            var frag = Fragment.Allocate(2, 0) with { Selections = [new NamedWeights(name, MeshMath.Ones(2), [])] };
            float[] xyz = [p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z];
            xyz.CopyTo(frag.Positions, 0);
            return m.WithLod(mi, MeshMath.AppendSafe(m.Lods[mi], frag, selectNew: false));
        }));
        if (!ok) return;
        Edit(x => x with { Axis = name, Begin = "", End = "", Memory = true });
        keepCard = false;
        ShowCard();
        UpdateOverlay();
        Ed.Log($"Axis \"{name}\" created in the Memory LOD for {a.Name} — save the model (Ctrl+S) to keep the points, and model.cfg for the animation");
    }

    void Duplicate()
    {
        if (Selected is not { } a) return;
        var copy = a with { Name = AnimSession.UniqueName(a.Name), Owner = AnimSession.Model };
        AnimSession.Own.Add(copy);
        AnimSession.Touch();
        SelectByName(copy.Name);
    }

    void Delete()
    {
        if (Selected is not { } a) return;
        if (!AnimSession.Own.Remove(a)) return;
        AnimSession.Touch();
    }

    void SelectByName(string name)
    {
        foreach (ListBoxItem item in list.Items)
            if (item.Tag is AnimDef d && d.Name == name)
            {
                list.SelectedItem = item;
                item.BringIntoView();
                return;
            }
    }

    // ---- bones ------------------------------------------------------------------------------------

    string? SelectedBone => (boneList.SelectedItem as ListBoxItem)?.Tag as string;

    void ShowBoneParent()
    {
        if (loading) return;
        loading = true;
        var bone = SelectedBone;
        boneParent.IsEnabled = bone is not null;
        boneParent.ItemsSource = new[] { "" }.Concat(AnimSession.AllBones.Select(b => b.Name).Where(n => !string.Equals(n, bone, StringComparison.OrdinalIgnoreCase))).ToList();
        boneParent.Text = bone is null ? "" : AnimSession.OwnBones.FirstOrDefault(b => b.Name == bone).Parent ?? "";
        loading = false;
    }

    void SetBoneParent(string parent)
    {
        if (loading || SelectedBone is not { } bone) return;
        int i = AnimSession.OwnBones.FindIndex(b => b.Name == bone);
        if (i < 0 || AnimSession.OwnBones[i].Parent == parent.Trim()) return;
        AnimSession.OwnBones[i] = AnimSession.OwnBones[i] with { Parent = parent.Trim() };
        AnimSession.Touch();
    }

    void AddBone(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || AnimSession.IsBone(name)) return;
        AnimSession.OwnBones.Add(new Bone(name, ""));
        AnimSession.Touch();
    }

    void RemoveBone()
    {
        if (SelectedBone is not { } bone) return;
        AnimSession.OwnBones.RemoveAll(b => b.Name == bone);
        for (int i = 0; i < AnimSession.OwnBones.Count; i++)
            if (string.Equals(AnimSession.OwnBones[i].Parent, bone, StringComparison.OrdinalIgnoreCase))
                AnimSession.OwnBones[i] = AnimSession.OwnBones[i] with { Parent = "" };
        AnimSession.Touch();
    }

    void AddMissingBones()
    {
        foreach (var name in MissingBones()) AnimSession.OwnBones.Add(new Bone(name, ""));
        AnimSession.Touch();
    }

    // ---- save -------------------------------------------------------------------------------------

    void Save()
    {
        if (Ed.Path is null)
        {
            Dlg.Fail("Save the model first (File ▸ Save) — model.cfg goes next to it");
            return;
        }
        if (AnimSession.CfgPath is null &&
            !Dlg.Confirm("Create model.cfg", $"There is no model.cfg in {Path.GetDirectoryName(Ed.Path)} or above.\n\nCreate one next to the model with the skeleton and animations of \"{AnimSession.Model}\"?", "Create"))
            return;
        if (MissingBones() is { Count: > 0 } missing &&
            Dlg.Confirm("Missing bones", $"These animated parts are not skeleton bones, so the game ignores their animations:\n\n{string.Join(", ", missing)}\n\nAdd them to the skeleton?", "Add bones"))
            AddMissingBones();
        try
        {
            if (AnimSession.Save() is { } written) Ed.Log($"Saved {written}");
            else Ed.Log("model.cfg is up to date — nothing to save");
        }
        catch (Exception ex)
        {
            Dlg.Fail("Cannot save model.cfg: " + ex.Message);
        }
    }

    // ---- small widgets ----------------------------------------------------------------------------

    TextBlock Section(string text) => new() { Text = text, Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(6, 8, 6, 2), FontWeight = FontWeights.SemiBold };

    TextBlock Note(string text, string brush) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource(brush), Margin = new Thickness(0, 1, 0, 1) };

    Button Btn(string text, string tip, Action action)
    {
        var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("CompactButton"), Margin = new Thickness(0, 0, 4, 2) };
        b.Click += (_, _) => action();
        return b;
    }
}
