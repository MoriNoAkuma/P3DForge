using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using P3DForge.App.Editor;
using P3DForge.App.Viewport.Interaction;
using P3DForge.Core.Ops;

namespace P3DForge.App.Dialogs.Transform;

public enum NumericTransform
{
    Move,
    Rotate,
    Scale,
}

public enum TransformPivot
{
    Pin,
    Center,
    Origin,
}

/// <summary>
/// Result of the numeric Move/Rotate/Scale dialogs. <see cref="Values"/> = offset (Move) or per-axis factors (Scale);
/// <see cref="Axis"/> 0..2 = X/Y/Z, 3 = view axis (Rotate); <see cref="Angle"/> in degrees.
/// </summary>
public sealed record TransformRequest(NumericTransform Kind, Vec3 Values, int Axis, double Angle, TransformPivot Pivot, bool AllLods);

/// <summary>
/// Points &gt; Transform 3D &gt; Move… / Rotate… / Scale… (Object Builder semantics, values remembered between uses).
/// Under automation the answer comes from flag "automation.answer":
/// move "dx dy dz [all]", rotate "x|y|z|view deg [pin|center|origin] [all]", scale "f | sx sy sz [pin|center|origin] [all]".
/// </summary>
public static class TransformDialog
{
    static EditorState Ed => AppServices.Editor;

    static Vec3 lastMove = Vec3.Zero;
    static int lastAxis = 1;
    static double lastAngle = 90;
    static Vec3 lastScale = new(1, 1, 1);
    static bool lastUniform = true;
    static TransformPivot? lastPivot;
    static bool lastAll;

    static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static TransformRequest? Show(NumericTransform kind)
    {
        var pivotDefault = lastPivot ?? (Interact.PinActive ? TransformPivot.Pin : TransformPivot.Center);
        var panel = new StackPanel { Margin = new Thickness(16), MinWidth = 320 };
        var error = Dlg.ErrorText();
        var boxes = new List<TextBox>();
        CheckBox? uniform = null;
        var axisButtons = new List<RadioButton>();
        var pivotButtons = new List<RadioButton>();

        TextBox Row(Panel parent, string label, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var t = new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            DockPanel.SetDock(t, Dock.Left);
            row.Children.Add(t);
            var b = Dlg.TextBox(value);
            b.MinWidth = 160;
            row.Children.Add(b);
            parent.Children.Add(row);
            boxes.Add(b);
            return b;
        }

        void Radios(string label, string[] names, int selected, List<RadioButton> into, string group)
        {
            panel.Children.Add(Dlg.Label(label, 6));
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            for (int i = 0; i < names.Length; i++)
            {
                var rb = new RadioButton { Content = names[i], GroupName = group, IsChecked = i == selected, Margin = new Thickness(0, 0, 14, 0), MinWidth = 0 };
                into.Add(rb);
                wrap.Children.Add(rb);
            }
            panel.Children.Add(wrap);
        }

        string title;
        switch (kind)
        {
            case NumericTransform.Move:
                title = "Move";
                panel.Children.Add(Dlg.Label("Offset (m)"));
                Row(panel, "X", F(lastMove.X));
                Row(panel, "Y", F(lastMove.Y));
                Row(panel, "Z", F(lastMove.Z));
                break;
            case NumericTransform.Rotate:
                title = "Rotate";
                Radios("Axis", ["X", "Y", "Z", "View"], lastAxis, axisButtons, "axis");
                Row(panel, "Angle (°)", F(lastAngle));
                break;
            default:
                title = "Scale";
                uniform = new CheckBox { Content = "Uniform", IsChecked = lastUniform, Margin = new Thickness(0, 0, 0, 8) };
                panel.Children.Add(uniform);
                Row(panel, "X / all", F(lastScale.X));
                Row(panel, "Y", F(lastScale.Y));
                Row(panel, "Z", F(lastScale.Z));
                void Sync()
                {
                    boxes[1].IsEnabled = boxes[2].IsEnabled = uniform.IsChecked != true;
                }
                uniform.Checked += (_, _) => Sync();
                uniform.Unchecked += (_, _) => Sync();
                Sync();
                break;
        }
        if (kind != NumericTransform.Move)
            Radios("Around", [Ed.Pin is null ? "Pin (not set)" : "Pin", "Selection centre", "Origin"], (int)pivotDefault, pivotButtons, "pivot");
        var all = new CheckBox { Content = "Apply to all LODs (every point)", IsChecked = lastAll, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(all);
        panel.Children.Add(error);

        TransformRequest? Read(out string? err)
        {
            err = null;
            var v = new double[boxes.Count];
            for (int i = 0; i < boxes.Count; i++)
            {
                if (!boxes[i].IsEnabled) continue;
                if (!Dlg.TryParse(boxes[i].Text, out v[i]))
                {
                    err = $"\"{boxes[i].Text}\" is not a number";
                    return null;
                }
            }
            var pivot = (TransformPivot)Math.Max(0, pivotButtons.FindIndex(b => b.IsChecked == true));
            bool allLods = all.IsChecked == true;
            switch (kind)
            {
                case NumericTransform.Move:
                    return new TransformRequest(kind, new Vec3(v[0], v[1], v[2]), 0, 0, TransformPivot.Origin, allLods);
                case NumericTransform.Rotate:
                    return new TransformRequest(kind, Vec3.Zero, Math.Max(0, axisButtons.FindIndex(b => b.IsChecked == true)), v[0], pivot, allLods);
                default:
                    var s = uniform!.IsChecked == true ? new Vec3(v[0], v[0], v[0]) : new Vec3(v[0], v[1], v[2]);
                    err = ValidateScale(s);
                    return err is null ? new TransformRequest(kind, s, 0, 0, pivot, allLods) : null;
            }
        }

        var w = Dlg.MakeWindow(title, panel);
        TransformRequest? result = null;
        var (row, ok) = Dlg.OkCancel(w, () =>
        {
            result = Read(out var e);
            Dlg.SetError(error, e);
            return result is not null;
        });
        panel.Children.Add(row);
        foreach (var b in boxes)
            b.TextChanged += (_, _) =>
            {
                Read(out var e);
                Dlg.SetError(error, e);
                ok.IsEnabled = e is null;
            };
        w.Loaded += (_, _) =>
        {
            var first = boxes.FirstOrDefault();
            first?.Focus();
            first?.SelectAll();
        };

        if (Dlg.Automated)
        {
            Dlg.Capture(w, "transform-" + kind.ToString().ToLowerInvariant());
            var a = Dlg.TakeAnswer();
            result = a is null ? null : Parse(kind, a, out var err);
            Ed.Log($"[automation] {title} → {a ?? "(cancel)"}{(result is null && a is not null ? " — invalid" : "")}");
        }
        else if (w.ShowDialog() != true) result = null;

        if (result is not null) Remember(result, uniform?.IsChecked == true);
        return result;
    }

    static string? ValidateScale(Vec3 s) =>
        Math.Abs(s.X) < 1e-9 || Math.Abs(s.Y) < 1e-9 || Math.Abs(s.Z) < 1e-9 ? "Scale factors must not be 0" : null;

    static void Remember(TransformRequest r, bool uniform)
    {
        lastAll = r.AllLods;
        switch (r.Kind)
        {
            case NumericTransform.Move:
                lastMove = r.Values;
                break;
            case NumericTransform.Rotate:
                lastAxis = r.Axis;
                lastAngle = r.Angle;
                lastPivot = r.Pivot;
                break;
            default:
                lastScale = r.Values;
                lastUniform = uniform || (r.Values.X == r.Values.Y && r.Values.Y == r.Values.Z);
                lastPivot = r.Pivot;
                break;
        }
    }

    /// <summary>Parses an automation answer (see the class summary). Returns null when invalid.</summary>
    public static TransformRequest? Parse(NumericTransform kind, string answer, out string? error)
    {
        error = null;
        var t = answer.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool allLods = t.Remove("all");
        var pivot = TransformPivot.Center;
        foreach (var (name, p) in new[] { ("pin", TransformPivot.Pin), ("center", TransformPivot.Center), ("centre", TransformPivot.Center), ("origin", TransformPivot.Origin) })
            if (t.Remove(name)) pivot = p;
        var nums = new List<double>();
        int axis = -1;
        foreach (var s in t)
        {
            int ax = s.ToLowerInvariant() switch { "x" => 0, "y" => 1, "z" => 2, "view" => 3, _ => -1 };
            if (ax >= 0) axis = ax;
            else if (Dlg.TryParse(s, out double v)) nums.Add(v);
            else
            {
                error = $"bad token '{s}'";
                return null;
            }
        }
        switch (kind)
        {
            case NumericTransform.Move when nums.Count == 3:
                return new TransformRequest(kind, new Vec3(nums[0], nums[1], nums[2]), 0, 0, TransformPivot.Origin, allLods);
            case NumericTransform.Rotate when nums.Count == 1 && axis >= 0:
                return new TransformRequest(kind, Vec3.Zero, axis, nums[0], pivot, allLods);
            case NumericTransform.Scale when nums.Count is 1 or 3:
                var s = nums.Count == 1 ? new Vec3(nums[0], nums[0], nums[0]) : new Vec3(nums[0], nums[1], nums[2]);
                error = ValidateScale(s);
                return error is null ? new TransformRequest(kind, s, 0, 0, pivot, allLods) : null;
            default:
                error = "wrong number of values";
                return null;
        }
    }
}
