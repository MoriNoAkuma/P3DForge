using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Shell;

namespace P3DForge.App.Features;

/// <summary>
/// "Find a command" (Ctrl+Shift+P / F1): type a few letters ("texture", "merge", "lod") and see every matching command
/// with its menu location, hotkey and a one-line explanation; Enter runs it. Also the hotkey cheat sheet (Help ▸ Hotkeys).
/// Automation: automation.answer holds the query; the first available match runs.
/// </summary>
public sealed class CommandPaletteFeature : IFeature
{
    public int Order => 90;
    static EditorState Ed => AppServices.Editor;

    sealed record Entry(string Id, string Label, string Where, string Hotkey, string Description, string Haystack);

    public void Install()
    {
        Commands.Register(
            new Command
            {
                Id = "app.commandPalette",
                Label = "_Find a Command…",
                Hotkeys = ["Ctrl+Shift+P", "F1"],
                Description = "Search every command by name and run it",
                Execute = () => Show(""),
            },
            new Command
            {
                Id = "help.hotkeys",
                Label = "_Hotkeys and Commands",
                Description = "Every command with its hotkey and menu location",
                Execute = () => Show(""),
            });
    }

    static string Clean(string label) => Commands.PlainText(label).TrimEnd('…', '.').Trim();

    /// <summary>Menu location of every command id, e.g. "Surfaces" or "Points ▸ Transform 3D".</summary>
    static Dictionary<string, string> MenuPaths()
    {
        var map = new Dictionary<string, string>();
        void Walk(string path, IEnumerable<MenuEntry> items)
        {
            foreach (var e in items)
                switch (e)
                {
                    case CommandEntry c:
                        map.TryAdd(c.Command, path);
                        break;
                    case SubmenuEntry s:
                        Walk(path + " ▸ " + Clean(s.Title), s.Items);
                        break;
                }
        }
        foreach (var (title, items) in MenuModel.Menus) Walk(Clean(title), items);
        return map;
    }

    static List<Entry> Entries()
    {
        var paths = MenuPaths();
        var list = new List<Entry>();
        foreach (var c in Commands.All)
        {
            if (c.Id.StartsWith("automation.", StringComparison.Ordinal)) continue;
            var label = Clean(c.Label);
            var where = paths.GetValueOrDefault(c.Id, "");
            var hotkey = string.Join(", ", c.Hotkeys);
            var desc = c.Description ?? "";
            list.Add(new Entry(c.Id, label, where, hotkey, desc, $"{label} {c.Id} {where} {desc} {hotkey}".ToLowerInvariant()));
        }
        return list;
    }

    /// <summary>Entries matching every word of <paramref name="query"/>; label matches first, then available ones, then A–Z.</summary>
    static List<Entry> Filter(List<Entry> all, string query)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return all
            .Where(e => words.All(w => e.Haystack.Contains(w, StringComparison.Ordinal)))
            .OrderBy(e => words.Length == 0 ? 0 : e.Label.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : e.Label.Contains(words[0], StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(e => Commands.IsEnabled(e.Id) ? 0 : 1)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static void Show(string initial)
    {
        var all = Entries();
        if (Dlg.Automated)
        {
            var q = Dlg.TakeAnswer() ?? initial;
            var hits = Filter(all, q);
            Ed.Log($"[automation] palette \"{q}\": {string.Join(" ", hits.Take(8).Select(h => h.Id))}");
            if (q.Length > 0 && hits.FirstOrDefault(h => Commands.IsEnabled(h.Id)) is { } run) Commands.Run(run.Id);
            return;
        }

        var faint = (Brush)Application.Current.FindResource("Editor.Faint");
        var box = new TextBox { Text = initial, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) };
        var hint = new TextBlock
        {
            Text = "Type what you want to do (e.g. texture, merge, lod, mirror). ↑↓ choose · Enter run · Esc close",
            Foreground = faint,
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 6),
        };
        var list = new ListBox { Height = 440, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var panel = new DockPanel { Margin = new Thickness(12), Width = 620 };
        DockPanel.SetDock(box, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(list);
        var w = Dlg.MakeWindow("Find a Command", panel, 640);

        FrameworkElement Row(Entry e)
        {
            bool enabled = Commands.IsEnabled(e.Id);
            var title = new TextBlock { Text = e.Label, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var keys = new TextBlock { Text = e.Hotkey, FontSize = 12, Foreground = faint, Margin = new Thickness(12, 0, 0, 0) };
            var top = new DockPanel();
            DockPanel.SetDock(keys, Dock.Right);
            top.Children.Add(keys);
            top.Children.Add(title);
            var info = (e.Where.Length > 0 ? e.Where + " menu" : "") + (e.Description.Length > 0 ? (e.Where.Length > 0 ? " — " : "") + e.Description : "");
            if (!enabled) info = "(not available right now — e.g. needs a selection or another LOD) " + info;
            var sub = new TextBlock { Text = info, FontSize = 11, Foreground = faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            var stack = new StackPanel { Margin = new Thickness(2, 3, 2, 3), Opacity = enabled ? 1 : 0.55, Tag = e };
            stack.Children.Add(top);
            if (info.Length > 0) stack.Children.Add(sub);
            return stack;
        }

        void Refill()
        {
            list.Items.Clear();
            foreach (var e in Filter(all, box.Text).Take(200)) list.Items.Add(Row(e));
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        string? chosen = null;
        void Accept()
        {
            if (list.SelectedItem is not FrameworkElement { Tag: Entry e }) return;
            chosen = e.Id;
            w.DialogResult = true;
        }

        box.TextChanged += (_, _) => Refill();
        box.PreviewKeyDown += (_, k) =>
        {
            switch (k.Key)
            {
                case Key.Down:
                    if (list.SelectedIndex < list.Items.Count - 1) list.SelectedIndex++;
                    list.ScrollIntoView(list.SelectedItem);
                    k.Handled = true;
                    break;
                case Key.Up:
                    if (list.SelectedIndex > 0) list.SelectedIndex--;
                    list.ScrollIntoView(list.SelectedItem);
                    k.Handled = true;
                    break;
                case Key.Enter:
                    Accept();
                    k.Handled = true;
                    break;
            }
        };
        list.MouseDoubleClick += (_, _) => Accept();
        list.KeyDown += (_, k) =>
        {
            if (k.Key == Key.Enter) Accept();
        };
        w.KeyDown += (_, k) =>
        {
            if (k.Key == Key.Escape) w.DialogResult = false;
        };
        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        Refill();
        if (w.ShowDialog() != true || chosen is null) return;
        if (!Commands.IsEnabled(chosen))
        {
            Ed.Log($"\"{Clean(Commands.Get(chosen)?.Label ?? chosen)}\" is not available right now (it usually needs a selection first).", LogLevel.Warn);
            return;
        }
        // Run after the palette has closed so dialogs it opens get the focus.
        Application.Current.Dispatcher.BeginInvoke(() => Commands.Run(chosen));
    }
}
