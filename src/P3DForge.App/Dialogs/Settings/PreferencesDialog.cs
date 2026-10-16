using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using P3DForge.App.Editor;

namespace P3DForge.App.Dialogs.Preferences;

/// <summary>
/// File ▸ Preferences: the persisted <see cref="Editor.Settings"/> properties (work drive, viewport background, grid,
/// texture size limit, undo limit, .bak on save) with OK / Cancel / Apply. Applying saves settings.json, updates the
/// live editor (undo limit, grid toggle) and bumps the UI flag "settings.revision" so views repaint.
/// Automation: the window is never shown; "automation.answer" may hold overrides
/// ("root=D:/x;background=#303030;grid=false;textureMax=2048;undo=150;backup=true"), then the values are applied.
/// </summary>
public static class PreferencesDialog
{
    static EditorState Ed => AppServices.Editor;

    static readonly int[] TextureSizes = [256, 512, 1024, 2048, 4096, 8192];

    /// <summary>Validated values of the dialog.</summary>
    public sealed record Values(string ProjectRoot, string Background, bool ShowGrid, int TextureMaxSize, int UndoLimit, bool BackupOnSave, string Deodol = "",
        int AutosaveMinutes = Services.Autosave.DefaultMinutes, string Binarize = "");

    public static Values Current()
    {
        var s = AppServices.Settings;
        return new Values(s.ProjectRoot, s.ViewportBackground, s.ShowGrid, s.TextureMaxSize, s.UndoLimit, s.BackupOnSave,
            s.Get<string?>(Services.Deodol.SettingKey, null) ?? "", Services.Autosave.Minutes, s.Get<string?>(Services.Binarize.SettingKey, null) ?? "");
    }

    /// <summary>Error text, or null when the values can be applied.</summary>
    public static string? Validate(Values v)
    {
        if (string.IsNullOrWhiteSpace(v.ProjectRoot)) return "The work drive (project root) is empty.";
        if (v.ProjectRoot.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "The work drive path contains invalid characters.";
        if (ParseColor(v.Background) is null) return $"\"{v.Background}\" is not a colour (use #RRGGBB).";
        if (v.UndoLimit is < 10 or > 10000) return "Undo limit must be between 10 and 10000.";
        if (Array.IndexOf(TextureSizes, v.TextureMaxSize) < 0) return "Unsupported texture size limit.";
        if (v.Deodol.Trim().Length > 0 && !File.Exists(v.Deodol.Trim())) return $"The converter \"{v.Deodol.Trim()}\" does not exist.";
        if (v.Binarize.Trim().Length > 0 && !File.Exists(v.Binarize.Trim())) return $"Binarize \"{v.Binarize.Trim()}\" does not exist.";
        if (v.AutosaveMinutes is < 0 or > 120) return "Autosave interval must be 0 (off) … 120 minutes.";
        return null;
    }

    static Color? ParseColor(string text)
    {
        try
        {
            return ColorConverter.ConvertFromString(text.Trim()) is Color c ? c : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    static string NormalizeRoot(string root)
    {
        root = root.Trim().Replace('/', '\\');
        if (root.Length == 2 && root[1] == ':') root += "\\"; // "P:" → "P:\"
        return root;
    }

    static int applyCount;

    /// <summary>Stores <paramref name="v"/> in the settings, saves them and updates the live editor.</summary>
    public static void Apply(Values v)
    {
        var s = AppServices.Settings;
        bool gridChanged = s.ShowGrid != v.ShowGrid;
        s.ProjectRoot = NormalizeRoot(v.ProjectRoot);
        s.ViewportBackground = ParseColor(v.Background) is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : s.ViewportBackground;
        s.ShowGrid = v.ShowGrid;
        s.TextureMaxSize = v.TextureMaxSize;
        s.UndoLimit = v.UndoLimit;
        s.BackupOnSave = v.BackupOnSave;
        s.Set<string?>(Services.Deodol.SettingKey, v.Deodol.Trim().Length == 0 ? null : v.Deodol.Trim());
        s.Set<string?>(Services.Binarize.SettingKey, v.Binarize.Trim().Length == 0 ? null : v.Binarize.Trim());
        s.Set(Services.Autosave.MinutesKey, v.AutosaveMinutes);
        s.Save();
        Ed.UndoLimit = v.UndoLimit;
        if (gridChanged) Ed.SetFlag("view.showGrid", v.ShowGrid);
        Ed.SetFlag("settings.revision", ++applyCount);
        Ed.Log($"Preferences applied (work drive {s.ProjectRoot}, textures ≤ {s.TextureMaxSize}px, undo {s.UndoLimit}, backups {(s.BackupOnSave ? "on" : "off")})");
    }

    // ---- dialog ----------------------------------------------------------------------------------------------

    public static void Show()
    {
        if (Dlg.Automated)
        {
            var v = WithAnswer(Current(), Dlg.TakeAnswer());
            if (Validate(v) is { } err) Dlg.Fail("Preferences: " + err);
            else Apply(v);
            if (Environment.GetEnvironmentVariable("P3DFORGE_DIALOG_SHOTS") is { Length: > 0 }) Build(out _).Capture();
            return;
        }
        Build(out var window);
        window.ShowDialog();
    }

    static Values WithAnswer(Values v, string? answer)
    {
        if (answer is null) return v;
        foreach (var part in answer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq].Trim().ToLowerInvariant(), val = part[(eq + 1)..].Trim();
            v = key switch
            {
                "root" => v with { ProjectRoot = val },
                "background" => v with { Background = val },
                "grid" => v with { ShowGrid = bool.TryParse(val, out var g) && g },
                "texturemax" => v with { TextureMaxSize = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : -1 },
                "undo" => v with { UndoLimit = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : -1 },
                "backup" => v with { BackupOnSave = bool.TryParse(val, out var b) && b },
                "deodol" => v with { Deodol = val },
                "binarize" => v with { Binarize = val },
                "autosave" => v with { AutosaveMinutes = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : -1 },
                _ => v,
            };
        }
        return v;
    }

    /// <summary>The dialog's controls (kept together so OK / Apply read one place).</summary>
    sealed class Ui
    {
        public required Window Window;
        public required TextBox Root, Background, Undo, Autosave, Binarize;
        public required TextBlock RootState, Error;
        public required Border Swatch;
        public required CheckBox Grid, Backup;
        public required ComboBox TextureMax;
        public required TextBox Deodol;

        public Values Read() => new(
            Root.Text,
            Background.Text,
            Grid.IsChecked == true,
            TextureSizes[Math.Max(0, TextureMax.SelectedIndex)],
            int.TryParse(Undo.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : -1,
            Backup.IsChecked == true,
            Deodol.Text,
            int.TryParse(Autosave.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : -1,
            Binarize.Text);

        public bool TryApply()
        {
            var v = Read();
            var err = Validate(v);
            Dlg.SetError(Error, err);
            if (err is not null) return false;
            Apply(v);
            return true;
        }

        public void Capture() => Dlg.Capture(Window, "preferences");
    }

    static Ui Build(out Window window)
    {
        var s = AppServices.Settings;
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14), MinWidth = 460 };

        // work drive
        root.Children.Add(Section("Work drive"));
        var rootBox = Dlg.TextBox(s.ProjectRoot);
        rootBox.MinWidth = 300;
        var browse = Dlg.Button("Browse…");
        var rootRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        rootRow.Children.Add(browse);
        rootRow.Children.Add(rootBox);
        root.Children.Add(Row("Project root (P:\\)", rootRow, "Folder that model-relative paths (\\dz\\…, data\\x_co.paa) resolve against"));
        var rootState = new TextBlock { FontSize = 11.5, Margin = new Thickness(150, 0, 0, 4) };
        root.Children.Add(rootState);

        // viewport
        root.Children.Add(Section("Viewport"));
        var bgBox = Dlg.TextBox(s.ViewportBackground);
        bgBox.Width = 110;
        bgBox.MinWidth = 0;
        var swatch = new Border { Width = 40, Height = 22, Margin = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(1), BorderBrush = Dlg.Res("Editor.Faint"), CornerRadius = new CornerRadius(3) };
        var bgRow = new StackPanel { Orientation = Orientation.Horizontal };
        bgRow.Children.Add(bgBox);
        bgRow.Children.Add(swatch);
        foreach (var (name, hex) in new[] { ("Default", "#2A2D33"), ("Dark", "#1A1A1A"), ("Grey", "#5A5F66"), ("Light", "#C8CCD2") })
        {
            var b = new Button { Content = name, Style = (Style)Application.Current.FindResource("CompactButton"), Margin = new Thickness(6, 0, 0, 0), ToolTip = hex };
            b.Click += (_, _) => bgBox.Text = hex;
            bgRow.Children.Add(b);
        }
        root.Children.Add(Row("Background", bgRow, "Viewport clear colour (#RRGGBB)"));
        var grid = new CheckBox { Content = "Show the ground grid in new sessions", IsChecked = s.ShowGrid, FontSize = 12 };
        root.Children.Add(Row("Grid", grid, null));
        var texMax = new ComboBox { Width = 110, FontSize = 12 };
        foreach (var t in TextureSizes) texMax.Items.Add($"{t} px");
        int sizeIndex = Array.IndexOf(TextureSizes, s.TextureMaxSize);
        texMax.SelectedIndex = sizeIndex >= 0 ? sizeIndex : 2;
        root.Children.Add(Row("Texture size limit", texMax, "Largest mip the viewport uploads per texture (lower = less GPU memory, faster loads)"));

        // editing
        root.Children.Add(Section("Editing"));
        var undo = Dlg.TextBox(s.UndoLimit.ToString(CultureInfo.InvariantCulture));
        undo.Width = 110;
        undo.MinWidth = 0;
        root.Children.Add(Row("Undo limit", undo, "Undo steps kept in memory (10…10000)"));
        var backup = new CheckBox { Content = "Keep a .bak copy when overwriting files (models, textures)", IsChecked = s.BackupOnSave, FontSize = 12 };
        root.Children.Add(Row("Backups", backup, null));
        var autosave = Dlg.TextBox(Services.Autosave.Minutes.ToString(CultureInfo.InvariantCulture));
        autosave.Width = 110;
        autosave.MinWidth = 0;
        root.Children.Add(Row("Autosave (minutes)", autosave, $"Copy of unsaved changes for crash recovery, kept in {Services.Autosave.Dir} (0 = off). The model file itself is only written by Save"));

        // tools
        root.Children.Add(Section("Tools"));
        var deodolBox = Dlg.TextBox(s.Get<string?>(Services.Deodol.SettingKey, null) ?? "");
        deodolBox.MinWidth = 300;
        var deodolBrowse = Dlg.Button("Browse…");
        var deodolRow = new DockPanel();
        DockPanel.SetDock(deodolBrowse, Dock.Right);
        deodolRow.Children.Add(deodolBrowse);
        deodolRow.Children.Add(deodolBox);
        root.Children.Add(Row("ODOL converter", deodolRow,
            $"Converts binarized models to editable MLOD when you open them (e.g. {Services.Deodol.DefaultName}). Empty = {Services.Deodol.DefaultName} in the work drive's root, if it is there"));
        var deodolState = new TextBlock { FontSize = 11.5, Margin = new Thickness(150, 0, 0, 4) };
        root.Children.Add(deodolState);
        deodolBrowse.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Title = "ODOL converter", Filter = "Programs (*.exe)|*.exe" };
            if (d.ShowDialog(Window.GetWindow(deodolBox)) == true) deodolBox.Text = d.FileName;
        };
        void UpdateDeodol()
        {
            var t = deodolBox.Text.Trim();
            var effective = t.Length > 0 ? (File.Exists(t) ? t : null) : Path.Combine(NormalizeRoot(rootBox.Text), Services.Deodol.DefaultName) is var g && File.Exists(g) ? g : null;
            deodolState.Text = effective is not null ? $"✓ {effective}" : t.Length > 0 ? "⚠ file not found" : $"not set — {Services.Deodol.DefaultName} is not in the work drive's root (binarized models open read only)";
            deodolState.Foreground = Dlg.Res(effective is not null ? "Editor.Ok" : "Editor.Warn");
        }
        deodolBox.TextChanged += (_, _) => UpdateDeodol();
        rootBox.TextChanged += (_, _) => UpdateDeodol();
        UpdateDeodol();

        var binBox = Dlg.TextBox(s.Get<string?>(Services.Binarize.SettingKey, null) ?? "");
        binBox.MinWidth = 300;
        var binBrowse = Dlg.Button("Browse…");
        var binRow = new DockPanel();
        DockPanel.SetDock(binBrowse, Dock.Right);
        binRow.Children.Add(binBrowse);
        binRow.Children.Add(binBox);
        root.Children.Add(Row("Binarize", binRow, "DayZ Tools binarize.exe for Tools ▸ Binarize Test. Empty = found through the DayZ Tools installation"));
        var binState = new TextBlock { FontSize = 11.5, Margin = new Thickness(150, 0, 0, 4) };
        root.Children.Add(binState);
        binBrowse.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Title = "binarize.exe (DayZ Tools ▸ Bin ▸ Binarize)", Filter = "Programs (*.exe)|*.exe" };
            if (d.ShowDialog(Window.GetWindow(binBox)) == true) binBox.Text = d.FileName;
        };
        void UpdateBin()
        {
            var t = binBox.Text.Trim();
            var effective = t.Length > 0 ? (File.Exists(t) ? t : null) : Services.Binarize.Installed();
            binState.Text = effective is not null ? $"✓ {effective}" : t.Length > 0 ? "⚠ file not found" : "not found — install DayZ Tools (Steam) or browse to binarize.exe";
            binState.Foreground = Dlg.Res(effective is not null ? "Editor.Ok" : "Editor.Warn");
        }
        binBox.TextChanged += (_, _) => UpdateBin();
        UpdateBin();

        var error = Dlg.ErrorText();
        root.Children.Add(error);
        root.Children.Add(new TextBlock { Text = "Settings file: " + Editor.Settings.FilePath, FontSize = 11, Foreground = Dlg.Res("Editor.Faint"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });

        var w = Dlg.MakeWindow("Preferences", root, 520);
        window = w;
        var ui = new Ui { Window = w, Root = rootBox, Background = bgBox, Undo = undo, RootState = rootState, Error = error, Swatch = swatch, Grid = grid, Backup = backup, TextureMax = texMax, Deodol = deodolBox, Autosave = autosave, Binarize = binBox };
        var (buttons, _) = Dlg.OkCancel(w, ui.TryApply);
        var apply = Dlg.Button("Apply");
        apply.Click += (_, _) => ui.TryApply();
        buttons.Children.Add(apply);
        root.Children.Add(buttons);

        void UpdateRoot()
        {
            var r = NormalizeRoot(rootBox.Text);
            bool exists = r.Length > 0 && Directory.Exists(r);
            rootState.Text = r.Length == 0 ? "" : exists ? "✓ folder exists" : "⚠ folder not found (paths will not resolve until it exists)";
            rootState.Foreground = Dlg.Res(exists ? "Editor.Ok" : "Editor.Warn");
        }
        void UpdateSwatch()
        {
            var c = ParseColor(bgBox.Text);
            swatch.Background = c is { } col ? new SolidColorBrush(col) : Brushes.Transparent;
            swatch.BorderBrush = Dlg.Res(c is null ? "Editor.Error" : "Editor.Faint");
        }
        rootBox.TextChanged += (_, _) => UpdateRoot();
        bgBox.TextChanged += (_, _) => UpdateSwatch();
        browse.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = "Work drive (project root)" };
            var cur = NormalizeRoot(rootBox.Text);
            if (Directory.Exists(cur)) d.InitialDirectory = cur;
            if (d.ShowDialog(w) == true) rootBox.Text = d.FolderName;
        };
        UpdateRoot();
        UpdateSwatch();
        return ui;
    }

    static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 4),
    };

    static Grid Row(string label, UIElement editor, string? tip)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        g.Children.Add(l);
        if (editor is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            if (fe is not DockPanel) fe.HorizontalAlignment = HorizontalAlignment.Left;
            if (tip is not null) fe.ToolTip ??= tip;
        }
        Grid.SetColumn(editor, 1);
        g.Children.Add(editor);
        return g;
    }
}
