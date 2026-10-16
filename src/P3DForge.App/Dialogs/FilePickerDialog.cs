using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace P3DForge.App.Dialogs;

/// <summary>
/// Picks one path from a long list (game animation files…) by typing: every word must appear in the path. Under
/// automation the answer (flag "automation.answer") is the filter and the first match is taken.
/// </summary>
public static class FilePickerDialog
{
    static List<string> Filter(IReadOnlyList<string> items, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return items.Where(i => words.All(w => i.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => words.Length > 0 && System.IO.Path.GetFileName(i).Contains(words[^1], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => i.Length).ToList();
    }

    public static string? Pick(string title, IReadOnlyList<string> items, string current)
    {
        if (Dlg.Automated)
        {
            var q = Dlg.TakeAnswer();
            return q is null ? null : Filter(items, q).FirstOrDefault();
        }
        var faint = (Brush)Application.Current.FindResource("Editor.Faint");
        var box = new TextBox { FontSize = 14, Margin = new Thickness(0, 0, 0, 8), Text = current.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(current.Replace('/', '\\')) : "" };
        var hint = new TextBlock
        {
            Text = $"{items.Count:N0} files. Type parts of the name (e.g. \"cz527 states\"). ↑↓ choose · Enter take · Esc close",
            Foreground = faint,
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 6),
        };
        var list = new ListBox { Height = 420, FontSize = 12 };
        var panel = new DockPanel { Margin = new Thickness(12), Width = 680 };
        DockPanel.SetDock(box, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(list);
        var w = Dlg.MakeWindow(title, panel, 700);
        string? chosen = null;
        void Refill()
        {
            list.Items.Clear();
            foreach (var i in Filter(items, box.Text).Take(300)) list.Items.Add(i);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        void Accept()
        {
            if (list.SelectedItem is not string s) return;
            chosen = s;
            w.DialogResult = true;
        }
        box.TextChanged += (_, _) => Refill();
        box.PreviewKeyDown += (_, k) =>
        {
            switch (k.Key)
            {
                case Key.Down when list.SelectedIndex < list.Items.Count - 1:
                    list.SelectedIndex++;
                    list.ScrollIntoView(list.SelectedItem);
                    k.Handled = true;
                    break;
                case Key.Up when list.SelectedIndex > 0:
                    list.SelectedIndex--;
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
        return w.ShowDialog() == true ? chosen : null;
    }
}
