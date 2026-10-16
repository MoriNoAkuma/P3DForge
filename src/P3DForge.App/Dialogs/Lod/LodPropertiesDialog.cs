using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Dialogs.Lod;

/// <summary>
/// LOD resolution dialog (New LOD / LOD Properties): LOD type + index/value, live name preview, rejects resolutions
/// already used by another LOD.
/// </summary>
public static class LodPropertiesDialog
{
    sealed record Choice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    static readonly Choice[] Choices = [.. LodNames.Types.Select(t => new Choice(t.Id, t.Name)), new Choice("custom", "Custom value…")];

    static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Asks for a resolution. <paramref name="exceptIndex"/> is the LOD being edited (its own resolution is allowed).
    /// Returns null when cancelled. Automation: "automation.answer" = a raw number, "typeId" or "typeId:value".
    /// </summary>
    public static float? Ask(string title, P3DModel model, float initial, int exceptIndex = -1, string okText = "OK")
    {
        string? Problem(float r) => LodList.Find(model, r, exceptIndex) >= 0 ? $"A LOD \"{LodNames.Name(r)}\" already exists" : null;

        var (typeId, value) = LodList.ChoiceFromResolution(initial);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Dlg.Label("LOD type:"));
        var type = new ComboBox { ItemsSource = Choices, MinWidth = 300, FontSize = 12, SelectedItem = Choices.First(c => c.Id == typeId) };
        panel.Children.Add(type);
        var valueLabel = Dlg.Label("Value:", 10);
        panel.Children.Add(valueLabel);
        var box = Dlg.TextBox(Fmt(value));
        panel.Children.Add(box);
        var preview = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontSize = 12, Foreground = Dlg.Res("Editor.Dim") };
        panel.Children.Add(preview);
        var error = Dlg.ErrorText();
        panel.Children.Add(error);

        var w = Dlg.MakeWindow(title, panel);
        float result = initial;
        Button? okButton = null;
        bool Evaluate()
        {
            var c = (Choice)type.SelectedItem;
            var t = LodNames.Types.FirstOrDefault(x => x.Id == c.Id);
            bool needsValue = c.Id == "custom" || t is { IsFamily: true };
            box.IsEnabled = needsValue;
            valueLabel.Text = c.Id == "custom" ? "Raw resolution value:" : c.Id == "visual" ? "Resolution:" : t is { IsFamily: true } ? $"Index (0 … {t.RangeEnd!.Value - t.Base - 1:0}):" : "Value (not used by this type):";
            string? err;
            float r = 0;
            if (needsValue && !Dlg.TryParse(box.Text, out var v)) err = "Enter a number";
            else
            {
                Dlg.TryParse(box.Text, out v);
                (r, err) = LodList.ResolutionFromChoice(c.Id, needsValue ? v : 0);
                err ??= Problem(r);
            }
            preview.Text = err is null ? $"→ {LodNames.Name(r)}   (resolution {((double)r).ToString("G9", CultureInfo.InvariantCulture)})" : "";
            Dlg.SetError(error, err);
            if (okButton is not null) okButton.IsEnabled = err is null;
            if (err is null) result = r;
            return err is null;
        }
        type.SelectionChanged += (_, _) => Evaluate();
        box.TextChanged += (_, _) => Evaluate();
        var (buttons, ok) = Dlg.OkCancel(w, Evaluate, okText);
        okButton = ok;
        panel.Children.Add(buttons);
        w.Loaded += (_, _) =>
        {
            Evaluate();
            if (box.IsEnabled)
            {
                box.Focus();
                box.SelectAll();
            }
            else type.Focus();
        };
        if (Dlg.Automated)
        {
            Dlg.Capture(w, "lod-properties");
            var a = Dlg.TakeAnswer();
            (float Resolution, string? Error) res = a is null ? (0, "no automation.answer")
                : Dlg.TryParse(a, out var raw) ? ((float)raw, null)
                : a.Split(':') is var p && p.Length == 2 && Dlg.TryParse(p[1], out var v) ? LodList.ResolutionFromChoice(p[0], v)
                : LodList.ResolutionFromChoice(a, 0);
            var err = res.Error ?? Problem(res.Resolution);
            if (err is not null)
            {
                Dlg.Fail($"[automation] {title}: {err}");
                return null;
            }
            return res.Resolution;
        }

        return w.ShowDialog() == true ? result : null;
    }
}
