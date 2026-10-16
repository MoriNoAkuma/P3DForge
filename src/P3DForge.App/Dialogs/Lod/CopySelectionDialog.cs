using System.Windows;
using System.Windows.Controls;
using P3DForge.Core;
using P3DForge.Core.Model;

namespace P3DForge.App.Dialogs.Lod;

/// <summary>"Copy Selection to LODs…": pick the target LODs (every LOD except the source).</summary>
public static class CopySelectionDialog
{
    /// <summary>Returns the chosen LOD indices, or null when cancelled. Automation: "automation.answer" = "1,3".</summary>
    public static int[]? Ask(P3DModel model, int source, int points, int faces)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Dlg.Label($"Copy {points:N0} point{(points == 1 ? "" : "s")} and {faces:N0} face{(faces == 1 ? "" : "s")} of LOD {LodNames.Name(model.Lods[source].Resolution)} to:"));
        var boxes = new List<(CheckBox Box, int Index)>();
        var stack = new StackPanel { Margin = new Thickness(4, 4, 0, 0) };
        for (int i = 0; i < model.Lods.Length; i++)
        {
            if (i == source) continue;
            var l = model.Lods[i];
            var cb = new CheckBox { Content = $"{LodNames.Name(l.Resolution)}   ({l.PointCount:N0} pts, {l.FaceCount:N0} faces)", FontSize = 12, Margin = new Thickness(0, 1, 0, 1), MinHeight = 0 };
            boxes.Add((cb, i));
            stack.Children.Add(cb);
        }
        panel.Children.Add(new ScrollViewer { Content = stack, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var all = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var selAll = Dlg.Button("All");
        selAll.Margin = new Thickness(0);
        selAll.Click += (_, _) => boxes.ForEach(b => b.Box.IsChecked = true);
        var none = Dlg.Button("None");
        none.Click += (_, _) => boxes.ForEach(b => b.Box.IsChecked = false);
        all.Children.Add(selAll);
        all.Children.Add(none);
        panel.Children.Add(all);
        var error = Dlg.ErrorText();
        panel.Children.Add(error);
        var w = Dlg.MakeWindow("Copy Selection to LODs", panel, 380);
        var (buttons, _) = Dlg.OkCancel(w, () =>
        {
            bool any = boxes.Any(b => b.Box.IsChecked == true);
            Dlg.SetError(error, any ? null : "Check at least one LOD");
            return any;
        }, "Copy");
        panel.Children.Add(buttons);
        if (Dlg.Automated)
        {
            Dlg.Capture(w, "copy-selection");
            var a = Dlg.TakeAnswer();
            if (a is null)
            {
                Dlg.Fail("[automation] copy selection: no automation.answer");
                return null;
            }
            var list = new List<int>();
            foreach (var part in a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, out int i) && i >= 0 && i < model.Lods.Length && i != source) list.Add(i);
            return list.ToArray();
        }

        return w.ShowDialog() == true ? boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Index).ToArray() : null;
    }
}
