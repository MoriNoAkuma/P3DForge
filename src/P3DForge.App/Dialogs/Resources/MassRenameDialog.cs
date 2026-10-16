using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using P3DForge.App.Dialogs.Faces;
using P3DForge.App.Editor;
using P3DForge.Core.Ops;

namespace P3DForge.App.Dialogs.Resources;

/// <summary>
/// Object Builder "Mass Texture &amp; Material Renaming" (Edit menu): every texture / material path of the model
/// (all LODs), a find text ("can be mask"), the new name, match mode and a live preview of the renames with face /
/// LOD counts. Rename = one undo step over all LODs; Undo reverts the last rename made here.
/// <para>Automation: "automation.answer" = "find=…;replace=…[;match=substring|exact|prefix|mask][;textures=off]
/// [;materials=off][;case=on]" (unset → closed without renaming).</para>
/// </summary>
public sealed class MassRenameDialog
{
    static EditorState Ed => AppServices.Editor;
    const string UndoPrefix = "Rename paths";

    sealed class PreviewRow
    {
        public required string From { get; init; }
        public required string To { get; init; }
        public required string Detail { get; init; }
    }

    readonly Window window;
    readonly TextBox find, replace;
    readonly ComboBox match;
    readonly CheckBox textures, materials, caseSensitive;
    readonly ObservableCollection<FileRow> paths = [];
    readonly ObservableCollection<PreviewRow> previewRows = [];
    readonly TextBlock summary = new() { FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    readonly Button rename, undo;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    List<PathChange> changes = [];

    static readonly (string Label, PathMatch Mode)[] Modes =
    [
        ("Contains (replace every occurrence)", PathMatch.Substring),
        ("Whole path", PathMatch.Exact),
        ("Starts with (folder prefix)", PathMatch.Prefix),
        ("Mask (* and ?)", PathMatch.Wildcard),
    ];

    /// <summary>Opens the dialog; <paramref name="initial"/> pre-fills the find text (whole-path match).</summary>
    public static void Show(string? initial = null, ResourceKind? kind = null) => new MassRenameDialog(initial, kind).Run();

    MassRenameDialog(string? initial, ResourceKind? kind)
    {
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(UIElement e, int row)
        {
            Grid.SetRow(e, row);
            root.Children.Add(e);
        }

        Put(new TextBlock { Text = "All textures and materials (click to fill the name to rename)", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) }, 0);
        var list = new ListBox
        {
            ItemsSource = paths,
            ItemTemplate = RowTemplates.Row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = (Style)Application.Current.FindResource("CompactListBoxItem"),
            BorderBrush = Dlg.Res("Editor.Faint"),
            BorderThickness = new Thickness(1),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        Put(list, 1);

        var form = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
        find = Ui.Mono(initial ?? "", 600);
        replace = Ui.Mono(initial ?? "", 600);
        form.Children.Add(Ui.Caption("_Name to rename (can be mask):", 0, find));
        form.Children.Add(find);
        form.Children.Add(Ui.Caption("Ne_w name:", 0, replace));
        form.Children.Add(replace);
        match = new ComboBox { FontSize = 12, MinHeight = 0, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 12, 0) };
        foreach (var m in Modes) match.Items.Add(m.Label);
        match.SelectedIndex = initial is null ? 0 : 1;
        textures = new CheckBox { Content = "_Textures", IsChecked = kind != ResourceKind.Material, FontSize = 12, MinHeight = 0, MinWidth = 0, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        materials = new CheckBox { Content = "_Materials", IsChecked = kind != ResourceKind.Texture, FontSize = 12, MinHeight = 0, MinWidth = 0, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        caseSensitive = new CheckBox { Content = "Match _case", IsChecked = false, FontSize = 12, MinHeight = 0, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        var opts = Ui.Row(Ui.Caption("Match"), match, textures, materials, caseSensitive);
        opts.Margin = new Thickness(0, 8, 0, 0);
        form.Children.Add(opts);
        Put(form, 2);

        var previewPanel = new DockPanel();
        var previewHead = new TextBlock { Text = "Preview", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(previewHead, Dock.Top);
        previewPanel.Children.Add(previewHead);
        DockPanel.SetDock(summary, Dock.Bottom);
        previewPanel.Children.Add(summary);
        var previewList = new ListBox
        {
            ItemsSource = previewRows,
            ItemTemplate = PreviewTemplate(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = (Style)Application.Current.FindResource("CompactListBoxItem"),
            BorderBrush = Dlg.Res("Editor.Faint"),
            BorderThickness = new Thickness(1),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(previewList, ScrollBarVisibility.Disabled);
        previewPanel.Children.Add(previewList);
        Put(previewPanel, 3);

        window = Dlg.MakeWindow("Mass Texture & Material Renaming", root);
        window.SizeToContent = SizeToContent.Manual;
        window.ResizeMode = ResizeMode.CanResizeWithGrip;
        window.Width = 760;
        window.Height = 680;

        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        undo = Dlg.Button("_Undo");
        undo.Margin = new Thickness(0);
        undo.ToolTip = "Undo the last rename made with this dialog";
        undo.Click += (_, _) =>
        {
            if (Ed.UndoLabel?.StartsWith(UndoPrefix, StringComparison.Ordinal) == true) Ed.Undo();
            RefreshPaths();
            UpdatePreview();
        };
        DockPanel.SetDock(undo, Dock.Left);
        buttons.Children.Add(undo);
        var close = Dlg.Button("_Close", isCancel: true);
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        rename = Dlg.Button("_Rename", isDefault: true);
        rename.Click += (_, _) => Rename();
        DockPanel.SetDock(rename, Dock.Right);
        buttons.Children.Add(rename);
        Put(buttons, 4);

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not FileRow r) return;
            find.Text = r.Path;
            replace.Text = r.Path;
            if (Mode is PathMatch.Substring) match.SelectedIndex = 1;
            replace.Focus();
            replace.CaretIndex = replace.Text.Length;
        };
        find.TextChanged += (_, _) => Kick();
        replace.TextChanged += (_, _) => Kick();
        match.SelectionChanged += (_, _) => Kick();
        foreach (var c in new[] { textures, materials, caseSensitive })
        {
            c.Checked += (_, _) => Kick();
            c.Unchecked += (_, _) => Kick();
        }
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpdatePreview();
        };
        window.Closed += (_, _) => timer.Stop();
        window.Loaded += (_, _) =>
        {
            (initial is null ? find : replace).Focus();
            replace.SelectAll();
        };
        RefreshPaths();
        UpdatePreview();
    }

    PathMatch Mode => Modes[Math.Max(0, match.SelectedIndex)].Mode;

    ReplaceOptions Options => new()
    {
        Textures = textures.IsChecked == true,
        Materials = materials.IsChecked == true,
        CaseInsensitive = caseSensitive.IsChecked != true,
        Match = Mode,
    };

    void Kick()
    {
        timer.Stop();
        timer.Start();
    }

    void RefreshPaths()
    {
        paths.Clear();
        foreach (var u in Attributes.Usage(Ed.Model))
            paths.Add(new FileRow
            {
                Name = u.Path,
                Path = u.Path,
                Detail = $"{(u.Kind == ResourceKind.Texture ? "texture" : "material")} · {u.TotalFaces:N0} faces · {u.LodCount} LOD{(u.LodCount == 1 ? "" : "s")}",
                Glyph = u.Kind == ResourceKind.Texture ? "" : "",
                WantsThumb = u.Kind == ResourceKind.Texture,
                ToolTip = u.Path,
            });
        undo.IsEnabled = Ed.UndoLabel?.StartsWith(UndoPrefix, StringComparison.Ordinal) == true;
    }

    void UpdatePreview()
    {
        previewRows.Clear();
        var f = find.Text.Trim();
        changes = f.Length == 0 ? [] : Attributes.PreviewReplace(Ed.Model, f, replace.Text.Trim(), Options);
        int faces = 0;
        foreach (var c in changes)
        {
            faces += c.Faces;
            previewRows.Add(new PreviewRow
            {
                From = c.From,
                To = c.To.Length == 0 ? "(empty)" : c.To,
                Detail = $"{(c.Kind == ResourceKind.Texture ? "texture" : "material")} · {c.Faces:N0} faces · {c.Lods} LOD{(c.Lods == 1 ? "" : "s")}",
            });
        }
        summary.Text = f.Length == 0 ? "Type the name to rename." : changes.Count == 0 ? "No path matches." : $"{changes.Count} path(s) will be renamed on {faces:N0} faces.";
        summary.Foreground = Dlg.Res(changes.Count == 0 && f.Length > 0 ? "Editor.Warn" : "Editor.Dim");
        rename.IsEnabled = changes.Count > 0;
    }

    bool Rename()
    {
        timer.Stop();
        UpdatePreview();
        if (changes.Count == 0) return false;
        var f = find.Text.Trim();
        var r = replace.Text.Trim();
        var o = Options;
        int count = 0;
        bool ok = false;
        Dlg.Try(() => ok = Ed.Edit($"{UndoPrefix} \"{f}\" → \"{r}\"", m =>
        {
            var (model, n) = Attributes.ReplacePaths(m, f, r, o);
            count = n;
            return model;
        }, new EditOptions { KeepNormals = true }));
        if (ok) Ed.Log($"Renamed {changes.Count} path(s) ({count:N0} face references): \"{f}\" → \"{r}\"");
        RefreshPaths();
        UpdatePreview();
        return ok;
    }

    void Run()
    {
        if (!Dlg.Automated)
        {
            window.ShowDialog();
            return;
        }
        var answer = Dlg.TakeAnswer();
        var a = Ui.ParseAnswer(answer);
        if (a.TryGetValue("find", out var fv)) find.Text = fv;
        if (a.TryGetValue("replace", out var rv)) replace.Text = rv;
        if (a.TryGetValue("match", out var mv))
            match.SelectedIndex = mv.ToLowerInvariant() switch { "exact" or "whole" => 1, "prefix" => 2, "mask" or "wildcard" => 3, _ => 0 };
        if (a.TryGetValue("textures", out var tv)) textures.IsChecked = tv is "on" or "1" or "true";
        if (a.TryGetValue("materials", out var mav)) materials.IsChecked = mav is "on" or "1" or "true";
        if (a.TryGetValue("case", out var cv)) caseSensitive.IsChecked = cv is "on" or "1" or "true";
        UpdatePreview();
        window.ShowActivated = false;
        window.Show();
        ResourcePicker.Pump(800);
        Dlg.Capture(window, "mass-rename");
        if (answer is null)
        {
            Ed.Log("[automation] Mass renaming → closed (no automation.answer)");
            return;
        }
        bool done = Rename();
        Ed.Log($"[automation] Mass renaming \"{answer}\" → {(done ? "renamed" : "nothing renamed")}", done ? LogLevel.Info : LogLevel.Warn);
    }

    static DataTemplate PreviewTemplate() => (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <StackPanel Margin="0,1,0,1">
            <TextBlock Text="{Binding From}" FontSize="11.5" Opacity="0.65" TextDecorations="Strikethrough" TextTrimming="CharacterEllipsis"/>
            <TextBlock FontSize="12" TextTrimming="CharacterEllipsis"><Run Text="→ "/><Run Text="{Binding To, Mode=OneWay}"/></TextBlock>
            <TextBlock Text="{Binding Detail}" FontSize="10.5" Opacity="0.55"/>
          </StackPanel>
        </DataTemplate>
        """);
}
