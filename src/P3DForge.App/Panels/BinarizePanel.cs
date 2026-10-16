using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using P3DForge.App.Editor;
using P3DForge.App.Services;
using P3DForge.App.Shell;
using P3DForge.Core;
using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.App.Panels;

/// <summary>Registers Tools ▸ Binarize Test (panel + command).</summary>
public sealed class BinarizeFeature : IFeature
{
    public const string PanelId = "binarize";

    public int Order => 73;

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = PanelId, Title = "Binarize", Location = PanelLocation.Right, TabWith = "objects", Order = 46, Create = () => new BinarizePanel(), StartHidden = true });
        Commands.Register(new Command
        {
            Id = "tools.binarize",
            Label = "_Binarize Test…",
            Hotkeys = ["Ctrl+Shift+B"],
            Execute = () =>
            {
                PanelRegistry.Focus(PanelId);
                BinarizePanel.Start();
            },
            Description = "Runs DayZ Tools binarize on a temporary copy of the model (unsaved edits included) and lists its errors and warnings; click one to go to the LOD and points",
        });
    }
}

/// <summary>
/// Binarize test: runs binarize.exe on a copy (see <see cref="Binarize.RunAsync"/>), lists what it reports (click =
/// go to the LOD / select the vertices), and sums up the binarized result per LOD (faces, vertices after binarize).
/// </summary>
public sealed class BinarizePanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    static BinarizeRun? last;
    static P3DModel? lastModel;
    static bool running;
    static DateTime startedAt;
    static CancellationTokenSource? cts;
    static event Action? StateChanged;

    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 2), FontSize = 12 };
    readonly StackPanel messages = new() { Margin = new Thickness(6, 0, 6, 4) };
    readonly StackPanel lods = new() { Margin = new Thickness(6, 0, 6, 4) };
    readonly Button run, cancel, openLog, openFolder, viewResult;
    readonly DispatcherTimer clock;

    public BinarizePanel()
    {
        Background = Brushes.Transparent;
        var content = new StackPanel();
        Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var bar = new WrapPanel { Margin = new Thickness(4, 4, 4, 0) };
        run = Btn("Run binarize", "Binarize a temporary copy of the model as it is now (Ctrl+Shift+B)", Start);
        cancel = Btn("Cancel", "Stop binarize", () => cts?.Cancel());
        openLog = Btn("Log", "Open binarize's full output", () => Open(last?.LogFile));
        openFolder = Btn("Folder", "Open the temporary folder (source copy, model.cfg, result)", () => Open(last?.WorkDir));
        viewResult = Btn("Open result", "Open the binarized model (read only) instead of the current document", ViewResult);
        foreach (var b in new[] { run, cancel, openLog, openFolder, viewResult }) bar.Children.Add(b);
        content.Children.Add(bar);
        content.Children.Add(status);
        content.Children.Add(Section("Binarize reports"));
        content.Children.Add(messages);
        content.Children.Add(Section("Result per LOD"));
        content.Children.Add(lods);
        content.Children.Add(new TextBlock
        {
            Text = "Binarize reads textures and rvmats from the work drive (P:) and model.cfg from the model's folders. The model file is not changed; the copy and the result are in %LOCALAPPDATA%\\P3DForge\\binarize.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = (Brush)FindResource("Editor.Faint"),
            Margin = new Thickness(6, 8, 6, 6),
        });
        clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) =>
        {
            if (running) status.Text = $"Binarizing… {(DateTime.Now - startedAt):m\\:ss} (a heavy model takes a minute or two)";
            else clock.Stop();
        };
        StateChanged += () => Dispatcher.BeginInvoke(Build);
        Build();
    }

    /// <summary>Starts a run (ignored while one is running).</summary>
    public static async void Start()
    {
        if (running) return;
        var tool = Binarize.ToolPath();
        if (tool is null)
        {
            Dialogs.Dlg.Fail("binarize.exe was not found — install DayZ Tools (Steam) or set it in File ▸ Preferences ▸ Tools");
            return;
        }
        var model = Ed.Model;
        var path = Ed.Path;
        var name = Ed.Path is not null ? Path.GetFileName(Ed.Path) : Ed.FileName;
        running = true;
        startedAt = DateTime.Now;
        cts = new CancellationTokenSource();
        StateChanged?.Invoke();
        Ed.Log($"Binarize test of {name} started ({Path.GetFileName(tool)})");
        try
        {
            last = await Binarize.RunAsync(tool, model, path, FileService.Converted?.ModelCfg, name, AppServices.Settings.ProjectRoot, cts.Token);
            lastModel = model;
            int e = last.Messages.Count(m => m.Error), w = last.Messages.Count - e;
            Ed.Log(last.Cancelled ? "Binarize test cancelled"
                : $"Binarize test: {e} error{(e == 1 ? "" : "s")}, {w} warning{(w == 1 ? "" : "s")} in {last.Time.TotalSeconds:0} s" +
                  (last.Output is { } o ? $" → {new FileInfo(o).Length / 1024:N0} KB ODOL" : " — no output") +
                  $" (exit code {last.ExitCode}, {LogLines(last.LogFile)} log lines)", e > 0 ? LogLevel.Warn : LogLevel.Info);
        }
        catch (Exception ex)
        {
            Ed.Log($"Binarize test failed: {ex.Message}", LogLevel.Error);
            last = null;
        }
        finally
        {
            running = false;
            StateChanged?.Invoke();
        }
    }

    static int LogLines(string file)
    {
        try
        {
            return File.ReadLines(file).Count();
        }
        catch
        {
            return 0;
        }
    }

    void Build()
    {
        run.IsEnabled = !running;
        cancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        openLog.IsEnabled = openFolder.IsEnabled = last is not null && !running;
        viewResult.IsEnabled = last?.Output is not null && !running;
        messages.Children.Clear();
        lods.Children.Clear();
        if (running)
        {
            status.Text = "Binarizing…";
            status.Foreground = (Brush)FindResource("Editor.Dim");
            clock.Start();
            return;
        }
        if (last is null)
        {
            status.Text = Binarize.ToolPath() is { } t
                ? $"Runs {t} on a copy of the open model. Press Run binarize."
                : "binarize.exe not found — install DayZ Tools or set it in File ▸ Preferences ▸ Tools.";
            status.Foreground = (Brush)FindResource(Binarize.ToolPath() is null ? "Editor.Warn" : "Editor.Dim");
            return;
        }
        int errors = last.Messages.Count(m => m.Error);
        status.Text = last.Cancelled ? "Cancelled."
            : $"{(last.Output is not null ? "Binarized" : "Failed")} in {last.Time.TotalSeconds:0} s · {errors} error{(errors == 1 ? "" : "s")}, {last.Messages.Count - errors} warning{(last.Messages.Count - errors == 1 ? "" : "s")}" +
              (last.Output is { } o ? $" · {new FileInfo(o).Length / 1024:N0} KB" : "") +
              (lastModel is not null && !ReferenceEquals(lastModel, Ed.Model) ? "\nThe model changed since this run — run again for current results." : "");
        status.Foreground = (Brush)FindResource(errors > 0 || last.Output is null ? "Editor.Warn" : "Editor.Ok");
        if (last.Messages.Count == 0) messages.Children.Add(new TextBlock { Text = "Nothing reported.", FontSize = 12, Foreground = (Brush)FindResource("Editor.Ok") });
        foreach (var m in last.Messages.OrderByDescending(m => m.Error).Take(80))
        {
            var where = m.Lod >= 0 && lastModel is not null && m.Lod < lastModel.Lods.Length ? LodNames.Name(lastModel.Lods[m.Lod].Resolution) + ": " : "";
            var row = new Button
            {
                Content = new TextBlock { Text = (m.Error ? "✖ " : "⚠ ") + where + m.Text + (m.Count > 1 && m.Points is null ? $"  (×{m.Count})" : ""), TextWrapping = TextWrapping.Wrap },
                Style = (Style)FindResource("CompactButton"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 2),
                Foreground = (Brush)FindResource(m.Error ? "Editor.Error" : "Editor.Warn"),
                ToolTip = m.Lod >= 0 ? "Go to that LOD" + (m.Points is not null ? " and select the vertices" : "") : null,
            };
            row.Click += (_, _) => GoTo(m);
            messages.Children.Add(row);
        }
        if (last.Result is { } r && lastModel is not null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            void Cell(int row, int col, string text, bool head = false)
            {
                var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(col == 0 ? 0 : 10, 0, 0, 1), HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
                if (head) t.Foreground = (Brush)FindResource("Editor.Dim");
                Grid.SetRow(t, row);
                Grid.SetColumn(t, col);
                grid.Children.Add(t);
            }
            grid.RowDefinitions.Add(new RowDefinition());
            Cell(0, 0, "LOD", true);
            Cell(0, 1, "faces", true);
            Cell(0, 2, "vertices", true);
            for (int i = 0; i < r.Lods.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                var l = r.Lods[i];
                Cell(i + 1, 0, LodNames.Name(l.Resolution));
                Cell(i + 1, 1, l.FaceCount.ToString("N0"));
                Cell(i + 1, 2, l.PointCount.ToString("N0"));
            }
            lods.Children.Add(grid);
            lods.Children.Add(new TextBlock
            {
                Text = "Vertices after binarize: a point with several normals or UVs becomes several vertices — far more vertices than points means many hard edges / UV seams.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = (Brush)FindResource("Editor.Faint"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
    }

    static void GoTo(BinarizeMessage m)
    {
        if (m.Lod < 0 || m.Lod >= Ed.Model.Lods.Length) return;
        Ed.SetActiveLod(m.Lod);
        if (m.Points is { Length: > 0 } pts)
        {
            var valid = pts.Where(p => p >= 0 && p < Ed.Lod.PointCount).ToArray();
            if (valid.Length > 0)
            {
                SelectionOps.SelectPoints(null, valid, SelectOp.Set, "Select: binarize vertices");
                if (Commands.IsEnabled("view.frameSelection")) Commands.Run("view.frameSelection");
            }
        }
    }

    static void ViewResult()
    {
        if (last?.Output is not { } o || !File.Exists(o)) return;
        if (!FileService.ConfirmDiscard("open the binarized result")) return;
        FileService.Open(o);
    }

    static void Open(string? path)
    {
        if (path is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ed.Log($"Cannot open {path}: {ex.Message}", LogLevel.Warn);
        }
    }

    TextBlock Section(string text) => new() { Text = text, Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(6, 8, 6, 2), FontWeight = FontWeights.SemiBold };

    Button Btn(string text, string tip, Action action)
    {
        var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("CompactButton"), Margin = new Thickness(0, 0, 4, 2) };
        b.Click += (_, _) => action();
        return b;
    }
}
