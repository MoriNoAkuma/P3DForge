using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using P3DForge.App.Editor;
using P3DForge.App.Services;
using P3DForge.App.Shell;
using P3DForge.App.Viewport;
using P3DForge.Core;
using P3DForge.Core.Anim;
using P3DForge.Core.Model;

namespace P3DForge.App.Panels;

/// <summary>Registers Tools ▸ Camo &amp; Damage (the Variants panel) and its automation command.</summary>
public sealed class VariantsFeature : IFeature
{
    public const string PanelId = "variants";

    public int Order => 73;

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = PanelId, Title = "Camo & Damage", Location = PanelLocation.Right, TabWith = "objects", Order = 45, Create = () => new VariantsPanel(), StartHidden = true });
        Commands.Register(
            new Command
            {
                Id = "tools.variants",
                Label = "_Camo && Damage (config.cpp)…",
                Execute = () => PanelRegistry.Focus(PanelId),
                Description = "Shows the model as its config.cpp classes do: hiddenSelections camo textures / rvmats and the healthLevels damage states; checks that every LOD has the hidden selections",
            },
            new Command
            {
                Id = "variants.set",
                Label = "Show Variant (automation)",
                Execute = () =>
                {
                    // automation: answer "class=GhillieAtt_Tan;level=2" (class "" = the model as saved)
                    var answer = Dialogs.Dlg.TakeAnswer() ?? "";
                    string? cls = null;
                    int level = 0;
                    foreach (var part in answer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var kv = part.Split('=', 2);
                        if (kv.Length != 2) continue;
                        if (kv[0] == "class") cls = kv[1];
                        if (kv[0] == "level") int.TryParse(kv[1], out level);
                    }
                    VariantSession.Show(VariantSession.Variants.FirstOrDefault(v => string.Equals(v.Name, cls, StringComparison.OrdinalIgnoreCase)), level);
                },
            });
    }
}

/// <summary>
/// The config.cpp classes of the open model (camo / damage variants) and the one the viewport shows. The viewport
/// draws <see cref="RenderLod"/> instead of the active LOD: a copy with the variant's textures / rvmats (the document
/// is never edited, Save writes the model as it is).
/// </summary>
public static class VariantSession
{
    static EditorState Ed => AppServices.Editor;

    public static List<ModelVariant> Variants { get; private set; } = [];
    public static List<VariantIssue> Issues { get; private set; } = [];
    public static List<string> Warnings { get; private set; } = [];
    public static ModelVariant? Current { get; private set; }
    public static int Level { get; private set; }
    public static string? LoadedFor { get; private set; }
    public static bool Loading { get; private set; }
    public static int ConfigFiles { get; private set; }

    public static event Action? Changed;

    static Lod? cacheIn;
    static Lod? cacheOut;
    static (ModelVariant?, int) cacheKey;

    /// <summary>The model's file for config lookups: the document, or the binarized original it was converted from.</summary>
    public static string? ModelFile => Ed.Path ?? FileService.Converted?.Original;

    /// <summary>What the viewport draws for <paramref name="lod"/> (the variant's look, or the LOD itself).</summary>
    public static Lod RenderLod(Lod lod)
    {
        if (Current is null) return lod;
        if (ReferenceEquals(lod, cacheIn) && cacheKey == (Current, Level) && cacheOut is not null) return cacheOut;
        cacheIn = lod;
        cacheKey = (Current, Level);
        cacheOut = ModelVariants.Apply(lod, Current, Level);
        return cacheOut;
    }

    public static void Show(ModelVariant? v, int level)
    {
        Current = v;
        Level = v is null ? 0 : Math.Clamp(level, 0, Math.Max(0, v.Health.Count - 1));
        cacheOut = null;
        // rvmats (damage macros, camo materials) only show in Final shading
        if (v is not null && Ed.Flag("view.shading", "textured") != "final"
            && (v.Materials.Any(m => m.Trim().Length > 0) || Level > 0 && v.Health[Level].Paths.Any(p => p.EndsWith(".rvmat", StringComparison.OrdinalIgnoreCase))))
        {
            Ed.SetFlag("view.shading", "final");
            Ed.Log("Viewport switched to Final shading: the variant's rvmats only show there (Alt+3 back to Textured)");
        }
        Changed?.Invoke();
        ViewportPlugins.Current?.Invalidate();
        if (v is not null) Ed.Log($"Showing {v.Name}{(Level > 0 ? $" at damage level {LevelName(v, Level)}" : "")} (view only — the model is not changed)");
    }

    public static string LevelName(ModelVariant v, int i)
    {
        string[] names = ["Pristine", "Worn", "Damaged", "Badly damaged", "Ruined"];
        var h = i < v.Health.Count ? v.Health[i].Health : double.NaN;
        var name = v.Health.Count == 5 && i < 5 ? names[i] : $"Level {i}";
        return double.IsNaN(h) ? name : $"{name} ({h:0.##})";
    }

    public static async void Load(bool force)
    {
        var file = ModelFile;
        if (!force && file == LoadedFor) return;
        if (force) GamePaths.Forget();
        LoadedFor = file;
        Current = null;
        Level = 0;
        cacheOut = null;
        Variants = [];
        Issues = [];
        Warnings = [];
        if (file is null)
        {
            Changed?.Invoke();
            return;
        }
        Loading = true;
        Changed?.Invoke();
        var root = AppServices.Settings.ProjectRoot;
        var game = AppServices.NormalizeGamePath(file);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (variants, warnings, files) = await Task.Run(() =>
        {
            var (lib, w) = GameConfigs.Load(file, root);
            return (ModelVariants.Find(lib, game), w, lib.Files.Count);
        });
        if (LoadedFor != file) return;
        Loading = false;
        Variants = variants;
        Warnings = warnings;
        ConfigFiles = files;
        Recheck();
        Ed.Log($"Camo & Damage: {variants.Count} config class{(variants.Count == 1 ? "" : "es")} use {Path.GetFileName(file)}, {Issues.Count} problem{(Issues.Count == 1 ? "" : "s")} ({files} config.cpp files read in {sw.ElapsedMilliseconds} ms)");
        foreach (var i in Issues.Where(i => i.Error).Take(5)) Ed.Log("Camo & Damage: " + i.Message, LogLevel.Warn);
        ViewportPlugins.Current?.Invalidate();
    }

    /// <summary>Re-runs the checks on the current model (after edits).</summary>
    public static void Recheck()
    {
        Issues = Variants.Count == 0 ? [] : ModelVariants.Check(Ed.Model, Variants, GamePaths.Problem);
        cacheOut = null;
        Changed?.Invoke();
    }
}

/// <summary>
/// Camo &amp; Damage: the config.cpp classes that show this model (hiddenSelections textures / rvmats) as a list to
/// preview, the healthLevels damage states of the chosen class, and problems (a hidden selection missing in a LOD,
/// missing files, damage entries no face uses). Clicking a problem goes to that LOD and selection.
/// </summary>
public sealed class VariantsPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 2), FontSize = 12 };
    readonly StackPanel classes = new() { Margin = new Thickness(6, 0, 6, 2) };
    readonly WrapPanel levels = new() { Margin = new Thickness(6, 0, 6, 2) };
    readonly TextBlock levelInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 2), FontSize = 11.5 };
    readonly StackPanel problems = new() { Margin = new Thickness(6, 0, 6, 4) };
    readonly TextBlock problemsHeader;
    bool building;

    public VariantsPanel()
    {
        Background = Brushes.Transparent;
        var content = new StackPanel();
        Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var bar = new WrapPanel { Margin = new Thickness(4, 4, 4, 0) };
        bar.Children.Add(Btn("Reload", "Read the config.cpp files again", () => VariantSession.Load(force: true)));
        bar.Children.Add(Btn("Open config.cpp", "Opens the config.cpp of the selected class", OpenConfig));
        bar.Children.Add(Btn("Model as saved", "Show the model's own textures again", () => VariantSession.Show(null, 0)));
        content.Children.Add(bar);
        content.Children.Add(new TextBlock
        {
            Text = "How the game dresses this model: every config.cpp class that uses it can put its own textures / rvmats on the " +
                   "model's hidden selections (camo, colour variants), and healthLevels swap them as the item gets damaged. " +
                   "Pick a class to see it in the viewport, then a damage state. Checks below: what would not work in game.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("Editor.Dim"),
            Margin = new Thickness(6, 2, 6, 2),
        });
        content.Children.Add(status);
        content.Children.Add(Section("Variant (class in config.cpp)"));
        content.Children.Add(classes);
        content.Children.Add(Section("Damage (healthLevels)"));
        content.Children.Add(levels);
        content.Children.Add(levelInfo);
        problemsHeader = Section("Problems");
        content.Children.Add(problemsHeader);
        content.Children.Add(problems);
        content.Children.Add(new TextBlock
        {
            Text = "View only: the viewport shows the class's textures and rvmats (Final shading shows the rvmats). The model is not changed.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = (Brush)FindResource("Editor.Faint"),
            Margin = new Thickness(6, 8, 6, 6),
        });
        VariantSession.Changed += () => Dispatcher.BeginInvoke(Build);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            VariantSession.Load(force: false);
            if (VariantSession.Variants.Count > 0) VariantSession.Recheck(); // edits made while hidden
        };
        Ed.Changed += c =>
        {
            if ((c & (Change.Document | Change.Load)) != 0 && IsVisible) VariantSession.Load(force: false);
            else if ((c & Change.Model) != 0 && IsVisible && VariantSession.Variants.Count > 0) VariantSession.Recheck();
        };
        Build();
    }

    void Build()
    {
        if (building) return;
        building = true;
        try
        {
            BuildCore();
        }
        finally
        {
            building = false;
        }
    }

    void BuildCore()
    {
        var vs = VariantSession.Variants;
        classes.Children.Clear();
        levels.Children.Clear();
        problems.Children.Clear();
        levelInfo.Text = "";
        if (VariantSession.ModelFile is null)
        {
            status.Text = "Save the model first — its config.cpp is looked up around it.";
            Warn(status, true);
            return;
        }
        if (VariantSession.Loading)
        {
            status.Text = "Reading config.cpp files…";
            Warn(status, false);
            return;
        }
        if (vs.Count == 0)
        {
            status.Text = $"No class in the config.cpp files ({VariantSession.ConfigFiles} read) shows {Path.GetFileName(VariantSession.ModelFile)} " +
                          "(model = …, or ClothingTypes male / female = … for worn clothing). The model is looked up by its path on the work drive, else by its file name.";
            if (VariantSession.Warnings.Count > 0) status.Text += "\n⚠ " + string.Join("\n⚠ ", VariantSession.Warnings.Take(3));
            Warn(status, true);
            problemsHeader.Visibility = Visibility.Collapsed;
            return;
        }
        var withLook = vs.Count(v => v.Selections.Count > 0);
        status.Text = $"{vs.Count} class{(vs.Count == 1 ? "" : "es")} use this model · {withLook} with hidden selections · {VariantSession.ConfigFiles} config files";
        Warn(status, false);

        var cur = VariantSession.Current;
        classes.Children.Add(Radio("Model as saved", "The model's own textures and materials", cur is null, () => VariantSession.Show(null, 0)));
        foreach (var v in vs)
        {
            var parts = new List<string>();
            for (int i = 0; i < v.Selections.Count; i++)
            {
                var t = i < v.Textures.Count ? Path.GetFileName(v.Textures[i]) : "";
                var m = i < v.Materials.Count ? Path.GetFileName(v.Materials[i]) : "";
                var what = string.Join(" + ", new[] { t, m }.Where(x => x.Length > 0));
                if (what.Length > 0) parts.Add($"{v.Selections[i]} → {what}");
            }
            var label = v.Name + (v.Scope == 2 ? "" : "  (base class, not spawned in game)") + (parts.Count > 0 ? "\n    " + string.Join("; ", parts) : v.Selections.Count > 0 ? "\n    (no textures of its own)" : "\n    (no hidden selections)");
            var tip = $"{v.Section} ▸ {v.Name} in {v.Class.File}" + (v.Class.Class.Base is { } b ? $"\nclass {v.Name}: {b}" : "");
            classes.Children.Add(Radio(label, tip, ReferenceEquals(cur, v), () => VariantSession.Show(v, VariantSession.Level)));
        }

        if (cur is null || cur.Health.Count == 0)
        {
            levelInfo.Text = cur is null ? "Choose a class to preview its damage states." : $"{cur.Name} has no healthLevels (no damage textures).";
        }
        else if (cur.Health.All(h => h.Paths.Count == 0))
        {
            levelInfo.Text = $"{cur.Name}: healthLevels name no textures / rvmats — in game it looks the same at every damage level (nothing to preview).";
        }
        else
        {
            for (int i = 0; i < cur.Health.Count; i++)
            {
                int k = i;
                var t = new ToggleButton
                {
                    Content = VariantSession.LevelName(cur, i).Split(" (")[0],
                    ToolTip = VariantSession.LevelName(cur, i) + "\n" + string.Join("\n", cur.Health[i].Paths),
                    IsChecked = VariantSession.Level == i,
                    MinHeight = 24,
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 4, 4),
                    FontSize = 12,
                };
                t.Click += (_, _) => VariantSession.Show(cur, k);
                levels.Children.Add(t);
            }
            var paths = cur.Health[VariantSession.Level].Paths;
            levelInfo.Text = VariantSession.Level == 0 ? "Pristine: the hidden selection textures above." : string.Join("\n", paths.Select(p => "• " + p));
        }

        var issues = VariantSession.Issues;
        problemsHeader.Visibility = Visibility.Visible;
        problemsHeader.Text = issues.Count == 0 ? "Problems: none" : $"Problems ({issues.Count})";
        foreach (var issue in issues.OrderByDescending(i => i.Error).Take(60))
        {
            var row = new Button
            {
                Content = new TextBlock { Text = (issue.Error ? "✖ " : "⚠ ") + issue.Message, TextWrapping = TextWrapping.Wrap },
                Style = (Style)FindResource("CompactButton"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 2),
                Foreground = (Brush)FindResource(issue.Error ? "Editor.Error" : "Editor.Warn"),
                ToolTip = issue.Lod >= 0 ? "Go to that LOD" + (issue.Selection is not null ? " and select the hidden selection (if it exists)" : "") : "Open the config.cpp",
            };
            row.Click += (_, _) => GoTo(issue);
            problems.Children.Add(row);
        }
        if (issues.Count > 60) problems.Children.Add(new TextBlock { Text = $"… {issues.Count - 60} more", FontSize = 11.5 });
    }

    static void GoTo(VariantIssue issue)
    {
        if (issue.Lod < 0)
        {
            if (VariantSession.Current?.Class.File is { } f) Reveal(f);
            return;
        }
        Ed.SetActiveLod(issue.Lod);
        if (issue.Selection is { } s && Ed.Lod.Selection(s) is not null) SelectionOps.SelectNamed(s, SelectOp.Set);
    }

    void OpenConfig()
    {
        var file = (VariantSession.Current ?? VariantSession.Variants.FirstOrDefault())?.Class.File;
        if (file is null)
        {
            Dialogs.Dlg.Fail("No config.cpp class uses this model");
            return;
        }
        Reveal(file);
    }

    static void Reveal(string file)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ed.Log($"Cannot open {file}: {ex.Message}", LogLevel.Warn);
        }
    }

    RadioButton Radio(string text, string tip, bool on, Action pick)
    {
        var r = new RadioButton
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            GroupName = "variant",
            IsChecked = on,
            ToolTip = tip,
            Margin = new Thickness(0, 1, 0, 3),
            FontSize = 12,
        };
        r.Checked += (_, _) =>
        {
            if (!building) pick();
        };
        return r;
    }

    void Warn(TextBlock t, bool warn) => t.Foreground = (Brush)FindResource(warn ? "Editor.Warn" : "Editor.Dim");

    TextBlock Section(string text) => new() { Text = text, Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(6, 8, 6, 2), FontWeight = FontWeights.SemiBold };

    Button Btn(string text, string tip, Action action)
    {
        var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("CompactButton"), Margin = new Thickness(0, 0, 4, 2) };
        b.Click += (_, _) => action();
        return b;
    }
}
