using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using P3DForge.App.Editor;
using P3DForge.Core.Ops;

namespace P3DForge.App.Dialogs.Structure;

/// <summary>What <see cref="SetMassDialog"/> asks for.</summary>
public enum SetMassMode
{
    /// <summary>A total mass distributed over the points.</summary>
    Total,
    /// <summary>The same mass on each point.</summary>
    Each,
    /// <summary>Density (kg/m³) × volume of every closed component.</summary>
    Density,
}

public sealed record SetMassRequest(SetMassMode Mode, double Value, MassDistribution Distribution);

/// <summary>
/// Object Builder "Set Mass": total mass distributed over the selected points (equally, by component volume or by face
/// area), a mass per point, or a density. Under automation the answer is read from flag <c>automation.answer</c>:
/// "<c>value</c>" (total, equal), "<c>total:value[:equal|volume|area]</c>", "<c>each:value</c>" or "<c>density:value</c>".
/// </summary>
public static class SetMassDialog
{
    static EditorState Ed => AppServices.Editor;

    static string lastValue = "100";
    static SetMassMode lastMode = SetMassMode.Total;
    static MassDistribution lastDistribution = MassDistribution.Equal;

    public static SetMassRequest? Show(string target, double currentMass)
    {
        var panel = new StackPanel { Margin = new Thickness(16), MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = target, FontSize = 12, Foreground = Dlg.Res("Editor.Dim"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        panel.Children.Add(new TextBlock { Text = $"Current mass: {currentMass.ToString("0.###", CultureInfo.InvariantCulture)} kg", FontSize = 12, Margin = new Thickness(0, 2, 0, 10) });

        var total = new RadioButton { Content = "Total mass (kg), distributed", GroupName = "mode", Margin = new Thickness(0, 2, 0, 2) };
        var each = new RadioButton { Content = "Mass of each point (kg)", GroupName = "mode", Margin = new Thickness(0, 2, 0, 2) };
        var density = new RadioButton { Content = "Density (kg/m³) × volume of closed components", GroupName = "mode", Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(total);
        var dist = new ComboBox { Margin = new Thickness(22, 2, 0, 4), FontSize = 12, MinHeight = 0, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
        dist.Items.Add("Equally over the points");
        dist.Items.Add("By component volume");
        dist.Items.Add("By face area around each point");
        dist.SelectedIndex = (int)lastDistribution;
        panel.Children.Add(dist);
        panel.Children.Add(each);
        panel.Children.Add(density);
        (lastMode switch { SetMassMode.Each => each, SetMassMode.Density => density, _ => total }).IsChecked = true;

        panel.Children.Add(Dlg.Label("Value", 10));
        var box = Dlg.TextBox(lastValue);
        panel.Children.Add(box);
        var error = Dlg.ErrorText();
        panel.Children.Add(error);

        SetMassMode Mode() => each.IsChecked == true ? SetMassMode.Each : density.IsChecked == true ? SetMassMode.Density : SetMassMode.Total;
        string? Validate(string s) =>
            !Dlg.TryParse(s, out double v) ? "Enter a number" : v < 0 ? "The value cannot be negative" : null;

        var w = Dlg.MakeWindow("Set Mass", panel);
        var (row, ok) = Dlg.OkCancel(w, () => Validate(box.Text) is null);
        panel.Children.Add(row);
        void Check()
        {
            var e = Validate(box.Text);
            Dlg.SetError(error, e);
            ok.IsEnabled = e is null;
            dist.IsEnabled = Mode() == SetMassMode.Total;
        }
        box.TextChanged += (_, _) => Check();
        foreach (var r in new[] { total, each, density }) r.Checked += (_, _) => Check();
        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
            Check();
        };

        if (Dlg.Automated)
        {
            Dlg.Capture(w, "setMass");
            var a = Dlg.TakeAnswer();
            var req = a is null ? null : Parse(a);
            Ed.Log($"[automation] Set Mass → {a ?? "(cancel)"}{(a is not null && req is null ? " — invalid" : "")}", req is null ? LogLevel.Warn : LogLevel.Info);
            return req;
        }

        if (w.ShowDialog() != true) return null;
        Dlg.TryParse(box.Text, out double value);
        lastValue = box.Text.Trim();
        lastMode = Mode();
        lastDistribution = (MassDistribution)Math.Max(0, dist.SelectedIndex);
        return new SetMassRequest(lastMode, value, lastDistribution);
    }

    /// <summary>Parses an automation answer (see the class remarks).</summary>
    public static SetMassRequest? Parse(string answer)
    {
        var parts = answer.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
            return Dlg.TryParse(parts[0], out double v1) && v1 >= 0 ? new SetMassRequest(SetMassMode.Total, v1, MassDistribution.Equal) : null;
        if (!Dlg.TryParse(parts[1], out double v) || v < 0) return null;
        var mode = parts[0].ToLowerInvariant() switch
        {
            "total" => SetMassMode.Total,
            "each" => SetMassMode.Each,
            "density" => SetMassMode.Density,
            _ => (SetMassMode?)null,
        };
        if (mode is null) return null;
        var d = parts.Length > 2 ? parts[2].ToLowerInvariant() switch
        {
            "volume" => MassDistribution.Volume,
            "area" => MassDistribution.Area,
            _ => MassDistribution.Equal,
        } : MassDistribution.Equal;
        return new SetMassRequest(mode.Value, v, d);
    }
}
