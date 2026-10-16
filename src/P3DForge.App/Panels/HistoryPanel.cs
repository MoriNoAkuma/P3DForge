using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using P3DForge.App.Editor;
using P3DForge.App.Services;
using P3DForge.App.Shell;

namespace P3DForge.App.Panels;

/// <summary>Registers the History panel (window.history) and Help ▸ About (help.about).</summary>
public sealed class HistoryFeature : IFeature
{
    public const string PanelId = "history";

    public int Order => 71;

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = PanelId, Title = "History", Location = PanelLocation.Right, TabWith = "objects", Order = 40, Create = () => new HistoryPanel(), StartHidden = true });
        Commands.Register(
            new Command
            {
                Id = "window.history",
                Label = "_History",
                Execute = () => PanelRegistry.Focus(PanelId),
                Description = "Undo history: every step since the file was opened; click a step to go back (or forward) to it",
            },
            new Command
            {
                Id = "help.about",
                Label = "_About P3D Forge…",
                Execute = About,
                Description = "Version and where settings, autosaves and caches are kept",
            });
    }

    static void About()
    {
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "?";
        var built = System.IO.File.GetLastWriteTime(Environment.ProcessPath ?? asm.Location);
        var local = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "P3DForge");
        Dialogs.Dlg.Info("About P3D Forge",
            $"P3D Forge {version.Split('+')[0]}\nBuilt {built:yyyy-MM-dd HH:mm}\n\n" +
            "An editor for DayZ / Arma MLOD models (.p3d).\n\n" +
            $"Work drive: {AppServices.Settings.ProjectRoot}\n" +
            $"Autosave: {(Autosave.Minutes == 0 ? "off" : $"every {Autosave.Minutes} min")} → {Autosave.Dir}\n" +
            $"Caches: {local}");
    }
}

/// <summary>
/// The undo history as a list: the opened state, every undoable step (oldest first) and the redoable ones (dimmed).
/// Clicking a row undoes / redoes up to that step in one go.
/// </summary>
public sealed class HistoryPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    sealed record Row(string Text, int Steps, bool Future, bool Current);

    readonly ListBox list;
    readonly TextBlock status;
    bool loading;

    public HistoryPanel()
    {
        status = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(6, 4, 6, 4), TextWrapping = TextWrapping.Wrap };
        SetDock(status, Dock.Top);
        Children.Add(status);
        list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemContainerStyle = new Style(typeof(ListBoxItem), (Style)FindResource("CompactListBoxItem")),
        };
        list.SelectionChanged += (_, _) =>
        {
            if (loading || list.SelectedItem is not ListBoxItem { Tag: Row r } || r.Current) return;
            if (r.Steps < 0) Ed.UndoSteps(-r.Steps);
            else Ed.RedoSteps(r.Steps);
        };
        Children.Add(list);
        Ed.Changed += c =>
        {
            if (IsVisible && (c & (Change.Model | Change.Document | Change.Load | Change.Selection)) != 0) Refresh();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Refresh();
        };
        Refresh();
    }

    void Refresh()
    {
        var undo = Ed.UndoLabels;
        var redo = Ed.RedoLabels;
        var rows = new List<Row> { new($"Opened {Ed.FileName}", -undo.Count, false, undo.Count == 0) };
        for (int i = 0; i < undo.Count; i++) rows.Add(new Row(undo[i], i + 1 - undo.Count, false, i == undo.Count - 1));
        for (int i = 0; i < redo.Count; i++) rows.Add(new Row(redo[i], i + 1, true, false));
        loading = true;
        list.Items.Clear();
        ListBoxItem? current = null;
        foreach (var r in rows)
        {
            var item = new ListBoxItem
            {
                Tag = r,
                Content = new TextBlock { Text = (r.Current ? "▸ " : "   ") + r.Text, TextTrimming = TextTrimming.CharacterEllipsis },
                Opacity = r.Future ? 0.5 : 1,
                FontWeight = r.Current ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = r.Current ? "Current state" : r.Future ? $"Redo to here ({r.Steps} step{(r.Steps == 1 ? "" : "s")})" : $"Undo to here ({-r.Steps} step{(r.Steps == -1 ? "" : "s")})",
            };
            list.Items.Add(item);
            if (r.Current) current = item;
        }
        list.SelectedItem = current;
        loading = false;
        if (current is not null) list.ScrollIntoView(current);
        status.Text = $"{undo.Count} step{(undo.Count == 1 ? "" : "s")} to undo, {redo.Count} to redo (limit {Ed.UndoLimit}). Click a step to return to it." +
                      (Autosave.LastSaved is { } t ? $"\nAutosaved {t:HH:mm}." : "");
    }
}
