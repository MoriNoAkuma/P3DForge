using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using P3DForge.App.Panels;
using P3DForge.Core;

namespace P3DForge.App.Dialogs.Selection;

/// <summary>"Weights…" of a named selection: one weight (0…1) for the selected points.</summary>
public static class WeightsDialog
{
    /// <summary>Returns the chosen weight, or null when cancelled. Automation: "automation.answer" = "0.5".</summary>
    public static float? Ask(SelRow row, byte[] selectedPoints)
    {
        // Current weight of the selected points inside the selection (shown as the initial value).
        double sum = 0;
        int n = 0, inside = 0;
        for (int i = 0; i < selectedPoints.Length && i < row.Tag.Points.Length; i++)
        {
            if (selectedPoints[i] == 0) continue;
            n++;
            byte b = row.Tag.Points[i];
            if (b == 0) continue;
            inside++;
            sum += Weights.Decode(b);
        }
        double initial = inside > 0 ? sum / inside : 1;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Dlg.Label($"Selection \"{row.Name}\": {n:N0} selected point{(n == 1 ? "" : "s")} ({inside:N0} already in it)."));
        panel.Children.Add(Dlg.Label("Weight (0 removes the points, 1 = full):", 8));
        var line = new DockPanel();
        var box = Dlg.TextBox(initial.ToString("0.###", CultureInfo.InvariantCulture));
        box.MinWidth = 80;
        box.VerticalAlignment = VerticalAlignment.Center;
        box.Width = 80;
        DockPanel.SetDock(box, Dock.Right);
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = initial, SmallChange = 0.01, LargeChange = 0.1, MinWidth = 220, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(box);
        line.Children.Add(slider);
        panel.Children.Add(line);
        var error = Dlg.ErrorText();
        panel.Children.Add(error);

        var w = Dlg.MakeWindow("Named Selection Weights", panel, 380);
        double value = initial;
        bool syncing = false;
        Button? okButton = null;
        bool Parse()
        {
            bool ok = Dlg.TryParse(box.Text, out var v) && v >= 0 && v <= 1;
            Dlg.SetError(error, ok ? null : "Enter a number between 0 and 1");
            if (okButton is not null) okButton.IsEnabled = ok;
            if (ok) value = v;
            return ok;
        }
        box.TextChanged += (_, _) =>
        {
            if (syncing || !Parse()) return;
            syncing = true;
            slider.Value = value;
            syncing = false;
        };
        slider.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            value = Math.Round(slider.Value, 3);
            box.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            syncing = false;
        };
        var (buttons, ok) = Dlg.OkCancel(w, Parse);
        okButton = ok;
        panel.Children.Add(buttons);
        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        if (Dlg.Automated)
        {
            Dlg.Capture(w, "weights");
            var a = Dlg.TakeAnswer();
            if (a is null || !Dlg.TryParse(a, out var v) || v < 0 || v > 1)
            {
                Dlg.Fail($"[automation] weights: bad or missing automation.answer \"{a}\"");
                return null;
            }
            return (float)v;
        }

        return w.ShowDialog() == true ? (float)value : null;
    }
}
