using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using P3DForge.App.Editor;
using P3DForge.App.Features;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Validation;

namespace P3DForge.App.Panels;

/// <summary>
/// Validation window (Tools › Validate Model): DayZ model rules (<see cref="Validator.Rules"/>) run on a background
/// thread over an immutable snapshot; issues grouped by LOD, errors first. Double-click / Enter switches to the LOD and
/// selects the offending points/faces (and frames them when "Frame" is on). File existence checks are cached.
/// </summary>
public sealed class ValidationPanel : DockPanel
{
    public const string PanelId = "validation";
    static EditorState Ed => AppServices.Editor;

    // ---- results shared by every panel instance
    static List<ValidationIssue> results = [];
    static P3DModel? resultsModel;
    static string status = "Not validated yet — press Validate";
    static bool running;
    static CancellationTokenSource? cts;
    static event Action? ResultsChanged;
    static readonly ConcurrentDictionary<string, (bool Exists, long Ticks)> existsCache = new(StringComparer.OrdinalIgnoreCase);

    readonly TreeView tree;
    readonly List<TextBlock> messages = [];

    /// <summary>Wrapping width of issue messages (TreeViewItem headers are measured with unlimited width).</summary>
    double MessageWidth() => Math.Max(120, tree.ActualWidth - 64);
    readonly TextBlock summary;
    readonly TextBlock stale;
    readonly CheckBox showInfo;
    readonly CheckBox frame;
    readonly Button validate;
    readonly Button fixAll;

    public ValidationPanel()
    {
        Background = Brushes.Transparent;
        var bar = new WrapPanel { Margin = new Thickness(4, 4, 4, 2) }; // wraps in a narrow dock
        validate = new Button
        {
            Content = "Validate",
            Style = (Style)FindResource("CompactButton"),
            FontSize = 12,
            Padding = new Thickness(12, 0, 12, 0),
            MinHeight = 24,
            ToolTip = "Run the DayZ model checks on the whole model (Tools › Validate Model)",
        };
        validate.Click += (_, _) => Validate();
        bar.Children.Add(validate);
        showInfo = new CheckBox { Content = "Info", FontSize = 12, Margin = new Thickness(10, 3, 0, 0), IsChecked = Ed.Flag("validation.showInfo", true), ToolTip = "Show informational hints" };
        showInfo.Click += (_, _) =>
        {
            Ed.SetFlag("validation.showInfo", showInfo.IsChecked == true);
            Rebuild();
        };
        bar.Children.Add(showInfo);
        frame = new CheckBox { Content = "Frame", FontSize = 12, Margin = new Thickness(10, 3, 0, 0), IsChecked = Ed.Flag("validation.frame", true), ToolTip = "Zoom the views to the selected problem on double-click" };
        frame.Click += (_, _) => Ed.SetFlag("validation.frame", frame.IsChecked == true);
        bar.Children.Add(frame);
        fixAll = new Button
        {
            Content = "Fix all",
            Style = (Style)FindResource("CompactButton"),
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = "Apply every automatic fix (sharp shadow edges, clear shadow textures, bone weights to 100 %, extra UV sets) in one undo step",
            Visibility = Visibility.Collapsed,
        };
        fixAll.Click += (_, _) => ApplyFixes(results.Where(i => i.CanFix).ToList());
        bar.Children.Add(fixAll);
        summary = new TextBlock { FontSize = 12, Margin = new Thickness(12, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        stale = new TextBlock { FontSize = 12, Margin = new Thickness(12, 4, 0, 0), Foreground = (Brush)FindResource("Editor.Warn"), Text = "model changed — validate again", Visibility = Visibility.Collapsed };
        SetDock(stale, Dock.Right);
        bar.Children.Add(stale);
        bar.Children.Add(summary);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        tree = new TreeView { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 2, 0, 0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled); // long messages wrap
        tree.SizeChanged += (_, _) =>
        {
            double w = MessageWidth();
            foreach (var m in messages) m.MaxWidth = w;
        };
        tree.MouseDoubleClick += (_, e) =>
        {
            if (tree.SelectedItem is TreeViewItem { Tag: ValidationIssue issue })
            {
                GoTo(issue);
                e.Handled = true;
            }
        };
        tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && tree.SelectedItem is TreeViewItem { Tag: ValidationIssue issue })
            {
                GoTo(issue);
                e.Handled = true;
            }
        };
        Children.Add(tree);

        ResultsChanged += Rebuild;
        Ed.Changed += c =>
        {
            if ((c & (Change.Model | Change.Document)) != 0) UpdateStale();
            // Automation hook: setFlag ["validation.goto", n] acts like a double-click on the n-th issue.
            if ((c & Change.Ui) != 0 && Dialogs.Dlg.Automated && Ed.Flag<object?>("validation.goto", null) is double n)
            {
                Ed.SetFlag("validation.goto", null);
                if (n >= 0 && n < results.Count) GoTo(results[(int)n]);
            }
        };
        Rebuild();
    }

    // ------------------------------------------------------------------ running

    /// <summary>Validates the current model on a background thread (a running validation is cancelled).</summary>
    public static async void Validate()
    {
        cts?.Cancel();
        var my = new CancellationTokenSource();
        cts = my;
        var model = Ed.Model;
        string root = AppServices.Settings.ProjectRoot;
        var modelFile = Ed.Path ?? Services.FileService.Converted?.Original;
        var (skeleton, animated) = Services.DayZContext.Bones();
        running = true;
        status = "Validating…";
        ResultsChanged?.Invoke();
        var sw = Stopwatch.StartNew();
        try
        {
            Services.DayZContext.Info info;
            var issues = await Task.Run(() =>
            {
                // kind and hidden selections from config.cpp (the DayZ rules depend on them)
                info = Services.DayZContext.Find(modelFile, root);
                var found = Validator.Run(new ValidationContext
                {
                    Model = model,
                    Resolve = p => AppServices.ResolveProjectPath(p, root),
                    Exists = FileExists,
                    Cancel = my.Token,
                    Kind = info.Kind,
                    KindSource = info.Source,
                    SkeletonBones = skeleton,
                    AnimatedBones = animated,
                    HiddenSelections = info.HiddenSelections,
                });
                found.Insert(0, new ValidationIssue(Severity.Info, "dayz.kind", info.Kind == ModelKind.Unknown
                    ? "Object kind unknown (no config.cpp class with model = this file) — the weapon / item / vehicle / building checks are skipped"
                    : $"Checked as a {info.Kind.ToString().ToLowerInvariant()} ({info.Source})" +
                      (info.HiddenSelections.Count > 0 ? $"; hidden selections {string.Join(", ", info.HiddenSelections)}" : "") +
                      (animated.Count > 0 ? $"; {animated.Count} animated part{(animated.Count == 1 ? "" : "s")} in model.cfg" : "")));
                return found;
            }, my.Token);
            if (my.IsCancellationRequested) return;
            results = issues;
            resultsModel = model;
            long ms = sw.ElapsedMilliseconds;
            status = $"{Validator.Summary(issues)} · {ms} ms";
            int errors = issues.Count(i => i.Severity == Severity.Error);
            Ed.Log($"Validate: {Validator.Summary(issues)} [{ms} ms]", errors > 0 ? LogLevel.Warn : LogLevel.Info);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            status = "Validation failed: " + ex.Message;
            Ed.Log(status, LogLevel.Error);
        }
        finally
        {
            if (ReferenceEquals(cts, my)) running = false;
        }
        ResultsChanged?.Invoke();
    }

    /// <summary>File.Exists with a 30 s cache (validation runs often; network drives are slow).</summary>
    static bool FileExists(string abs)
    {
        long now = Environment.TickCount64;
        if (existsCache.TryGetValue(abs, out var e) && now - e.Ticks < 30_000) return e.Exists;
        bool exists;
        try { exists = File.Exists(abs); }
        catch (Exception) { exists = false; }
        existsCache[abs] = (exists, now);
        return exists;
    }

    /// <summary>The latest results (for automation / other features).</summary>
    public static IReadOnlyList<ValidationIssue> Results => results;

    // ------------------------------------------------------------------ UI

    void UpdateStale() =>
        stale.Visibility = resultsModel is not null && !SameContent(resultsModel, Ed.Model) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The same model for the checks: geometry, faces, surfaces and tags unchanged — selecting, hiding or switching
    /// LODs does not make the results stale (they create a new model object, but the issues still point right).
    /// </summary>
    static bool SameContent(P3DModel a, P3DModel b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Lods.Length != b.Lods.Length) return false;
        for (int i = 0; i < a.Lods.Length; i++) if (!SameContent(a.Lods[i], b.Lods[i])) return false;
        return true;
    }

    static bool SameContent(Lod a, Lod b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Resolution != b.Resolution || !ReferenceEquals(a.Positions, b.Positions) || !ReferenceEquals(a.FacePoints, b.FacePoints)
            || !ReferenceEquals(a.FaceUV, b.FaceUV) || !ReferenceEquals(a.FaceTexture, b.FaceTexture) || !ReferenceEquals(a.FaceMaterial, b.FaceMaterial)
            || a.Strings != b.Strings) return false;
        var ta = a.Tags.Where(t => t is not StateTag).ToList();
        var tb = b.Tags.Where(t => t is not StateTag).ToList();
        return ta.Count == tb.Count && ta.Zip(tb).All(p => ReferenceEquals(p.First, p.Second));
    }

    void Rebuild()
    {
        summary.Text = status;
        validate.IsEnabled = !running;
        int fixable = resultsModel is not null && SameContent(resultsModel, Ed.Model) ? results.Count(i => i.CanFix) : 0;
        fixAll.Visibility = fixable > 1 ? Visibility.Visible : Visibility.Collapsed;
        fixAll.Content = $"Fix all ({fixable})";
        UpdateStale();
        tree.Items.Clear();
        messages.Clear();
        if (resultsModel is null) return;
        bool info = showInfo.IsChecked == true;
        var groups = results.Where(i => info || i.Severity != Severity.Info).GroupBy(i => i.LodIndex).OrderBy(g => g.Key);
        foreach (var g in groups)
        {
            int e = g.Count(i => i.Severity == Severity.Error), w = g.Count(i => i.Severity == Severity.Warning), n = g.Count() - e - w;
            string name = g.Key < 0 ? "Model" : g.Key < resultsModel.Lods.Length ? $"{LodNames.Name(resultsModel.Lods[g.Key].Resolution)}  (LOD #{g.Key + 1})" : $"LOD #{g.Key + 1}";
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold });
            head.Children.Add(new TextBlock
            {
                Text = "   " + string.Join(", ", new[] { e > 0 ? $"{e} error{(e == 1 ? "" : "s")}" : null, w > 0 ? $"{w} warning{(w == 1 ? "" : "s")}" : null, n > 0 ? $"{n} info" : null }.Where(s => s is not null)),
                FontSize = 12,
                Foreground = (Brush)FindResource(e > 0 ? "Editor.Error" : w > 0 ? "Editor.Warn" : "Editor.Dim"),
            });
            var group = new TreeViewItem { Header = head, IsExpanded = true };
            foreach (var issue in g) group.Items.Add(IssueItem(issue));
            tree.Items.Add(group);
        }
        if (tree.Items.Count == 0)
            tree.Items.Add(new TreeViewItem { Header = new TextBlock { Text = results.Count == 0 ? "No issues found." : "Only informational hints (enable Info).", FontSize = 12, Foreground = (Brush)FindResource("Editor.Ok") } });
    }

    TreeViewItem IssueItem(ValidationIssue issue)
    {
        var (glyph, brush) = issue.Severity switch
        {
            Severity.Error => ("", "Editor.Error"),
            Severity.Warning => ("", "Editor.Warn"),
            _ => ("", "Editor.Dim"),
        };
        var row = new DockPanel();
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12,
            Foreground = (Brush)FindResource(brush),
            Margin = new Thickness(0, 2, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        if (issue.CanFix)
        {
            var fix = new Button
            {
                Content = "Fix",
                Style = (Style)FindResource("CompactButton"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = ValidationFixes.Label(issue.Fix!) + " (one undo step), then validate again",
            };
            fix.Click += (_, e) =>
            {
                ApplyFixes([issue]);
                e.Handled = true;
            };
            SetDock(fix, Dock.Right);
            row.Children.Add(fix);
        }
        var msg = new TextBlock { Text = issue.Message, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = MessageWidth() };
        messages.Add(msg);
        row.Children.Add(msg);
        var tip = issue.Hint is null ? issue.Rule : $"{issue.Hint}\n[{issue.Rule}]";
        if (issue.CanSelect) tip += "\nDouble-click to select (switches LOD)";
        return new TreeViewItem { Header = row, Tag = issue, ToolTip = tip };
    }

    /// <summary>Applies automatic fixes (one undo step) and validates again.</summary>
    static void ApplyFixes(IReadOnlyList<ValidationIssue> issues)
    {
        if (issues.Count == 0) return;
        if (resultsModel is not null && !SameContent(resultsModel, Ed.Model))
        {
            // edited since the check: the issues may point at other points / faces now
            Dialogs.Dlg.Info("Fix", "The model was edited since the check, so the found problems may point at other places now." +
                Environment.NewLine + Environment.NewLine + "It is checked again — press Fix once more when the new results are there.");
            Validate();
            return;
        }
        var label = issues.Count == 1 ? "Fix: " + ValidationFixes.Label(issues[0].Fix!) : $"Fix {issues.Count} validation issues";
        if (Ed.Edit(label, m => issues.Aggregate(m, ValidationFixes.Apply))) Ed.Log(label);
        Validate();
    }

    /// <summary>Switches to the issue's LOD and selects its points/faces.</summary>
    public static void GoTo(ValidationIssue issue)
    {
        var model = Ed.Model;
        if (issue.LodIndex < 0 || issue.LodIndex >= model.Lods.Length) return;
        if (resultsModel is not null && issue.LodIndex < resultsModel.Lods.Length && !SameContent(resultsModel.Lods[issue.LodIndex], model.Lods[issue.LodIndex]))
            Ed.Log("Validation: this LOD changed since the check — validate again for exact elements", LogLevel.Warn);
        if (Ed.ActiveLod != issue.LodIndex) Ed.SetActiveLod(issue.LodIndex);
        if (!issue.CanSelect) return;
        var lod = Ed.Lod;
        var pts = new byte[lod.PointCount];
        var faces = new byte[lod.FaceCount];
        if (issue.Points is not null)
            foreach (int p in issue.Points) if (p >= 0 && p < pts.Length) pts[p] = 1;
        if (issue.Faces is not null)
            foreach (int f in issue.Faces) if (f >= 0 && f < faces.Length) faces[f] = 1;
        StructureWork.Select(lod, pts, faces, "Select: " + issue.Rule);
        if (Ed.Flag("validation.frame", true) && Commands.IsEnabled("view.frameSelection")) Commands.Run("view.frameSelection");
    }
}
