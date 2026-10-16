using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using P3DForge.Core.Ops;

namespace P3DForge.App.Dialogs.Faces;

/// <summary>
/// Controls shared by the Face / Point Properties dialogs: flag fields summarised over many elements. A field whose
/// value differs between the elements shows as "mixed" (no radio checked / indeterminate check box / blank text) and
/// is left unchanged unless the user edits it.
/// </summary>
public interface IFlagField
{
    /// <summary>Automation key ("lighting", "zbias", "shadow", …).</summary>
    string Key { get; }
    uint Mask { get; }
    /// <summary>True when the user changed the field (relative to the summarised value).</summary>
    bool Edited { get; }
    /// <summary>The new bits (already shifted into <see cref="Mask"/>); only meaningful when <see cref="Edited"/>.</summary>
    uint Value { get; }
    /// <summary>Automation: sets the field from text; false when the text is not understood.</summary>
    bool SetFromText(string text);
    /// <summary>Validation error of the current input (null = ok).</summary>
    string? Error { get; }
    /// <summary>Raised when the user changes the field.</summary>
    event Action? Changed;
}

/// <summary>A radio group bound to a bit field (e.g. face lighting, Z bias, point surface).</summary>
public sealed class FlagRadioGroup : IFlagField
{
    readonly (string Label, uint Value)[] options;
    readonly RadioButton[] radios;
    readonly int initialIndex;
    readonly TextBlock? hint;

    public string Key { get; }
    public uint Mask { get; }
    public GroupBox Box { get; }
    public event Action? Changed;

    public FlagRadioGroup(string key, string header, uint mask, FlagSummary summary, params (string Label, uint Value)[] options)
    {
        Key = key;
        Mask = mask;
        this.options = options;
        uint? common = summary.Common(mask);
        initialIndex = common is { } v ? Array.FindIndex(options, o => o.Value == v) : -1;
        var panel = new StackPanel { Margin = new Thickness(2, 2, 2, 0) };
        radios = new RadioButton[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            var rb = Ui.Radio(options[i].Label, i == initialIndex);
            rb.Checked += (_, _) => Changed?.Invoke();
            radios[i] = rb;
            panel.Children.Add(rb);
        }
        if (initialIndex < 0 && summary.Count > 0)
        {
            hint = Ui.Hint(common is null ? "mixed — kept unless set" : $"other (0x{common.Value:X}) — kept");
            hint.ToolTip = "The selected elements have different (or non-standard) values; nothing changes unless you pick one.";
            panel.Children.Add(hint);
        }
        Box = Ui.Group(header, panel);
    }

    int Checked
    {
        get
        {
            for (int i = 0; i < radios.Length; i++)
                if (radios[i].IsChecked == true) return i;
            return -1;
        }
    }

    public bool Edited => Checked >= 0 && Checked != initialIndex;
    public uint Value => Checked >= 0 ? options[Checked].Value : 0;
    public string? Error => null;

    public bool SetFromText(string text)
    {
        var t = Norm(text);
        for (int i = 0; i < options.Length; i++)
        {
            if (Norm(options[i].Label).StartsWith(t, StringComparison.Ordinal))
            {
                radios[i].IsChecked = true;
                return true;
            }
        }
        return false;
    }

    static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}

/// <summary>A three-state check box bound to one bit (optionally inverted: "Enable shadow" = NoShadow bit clear).</summary>
public sealed class FlagCheck : IFlagField
{
    readonly bool inverted;
    readonly bool? initial;

    public string Key { get; }
    public uint Mask { get; }
    public CheckBox Box { get; }
    public event Action? Changed;

    public FlagCheck(string key, string label, uint bit, FlagSummary summary, bool inverted = false, string? tooltip = null)
    {
        Key = key;
        Mask = bit;
        this.inverted = inverted;
        var b = summary.Bit(bit);
        initial = b is null ? null : b.Value != inverted;
        Box = new CheckBox
        {
            Content = label,
            IsThreeState = initial is null, // mixed: the user can cycle back to "unchanged"
            IsChecked = initial,
            FontSize = 12,
            MinHeight = 0,
            MinWidth = 0,
            Margin = new Thickness(2, 4, 2, 2),
            ToolTip = tooltip ?? (initial is null ? "Mixed: the indeterminate state keeps each element's value" : null),
        };
        Box.Click += (_, _) => Changed?.Invoke();
        if (initial is null && summary.Count > 0)
            Box.ToolTip = (tooltip is null ? "" : tooltip + "\n") + "Mixed: the indeterminate state keeps each element's value";
    }

    public bool Edited => Box.IsChecked is { } v && v != initial;
    public uint Value => Box.IsChecked == true != inverted ? Mask : 0;
    public string? Error => null;

    public bool SetFromText(string text)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "on" or "1" or "true" or "yes" or "enable" or "enabled":
                Box.IsChecked = true;
                return true;
            case "off" or "0" or "false" or "no" or "disable" or "disabled":
                Box.IsChecked = false;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>A numeric text box bound to an integer bit field (user value); blank when mixed.</summary>
public sealed class FlagNumber : IFlagField
{
    readonly int shift;
    readonly uint max;
    readonly string initialText;

    public string Key { get; }
    public uint Mask { get; }
    public TextBox Box { get; }
    public event Action? Changed;

    public FlagNumber(string key, uint mask, int shift, FlagSummary summary)
    {
        Key = key;
        Mask = mask;
        this.shift = shift;
        max = mask >> shift;
        uint? common = summary.Common(mask);
        initialText = common is { } v ? (v >> shift).ToString(CultureInfo.InvariantCulture) : "";
        Box = Ui.Text(initialText, 56);
        Box.ToolTip = common is null && summary.Count > 0 ? $"Mixed — leave blank to keep (0…{max})" : $"0…{max}";
        Box.TextChanged += (_, _) => Changed?.Invoke();
    }

    public bool Edited => Box.Text.Trim().Length > 0 && Box.Text.Trim() != initialText && Error is null;
    public uint Value => uint.TryParse(Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? (v << shift) & Mask : 0;

    public string? Error
    {
        get
        {
            var t = Box.Text.Trim();
            if (t.Length == 0) return null;
            return uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v <= max ? null : $"User value must be an integer 0…{max}.";
        }
    }

    public bool SetFromText(string text)
    {
        Box.Text = text.Trim();
        return Error is null;
    }
}

/// <summary>Small control factories (compact sizes; the Fluent theme defaults are too large for dense dialogs).</summary>
public static class Ui
{
    public static RadioButton Radio(string label, bool isChecked) => new()
    {
        Content = label,
        IsChecked = isChecked,
        FontSize = 12,
        MinHeight = 0,
        MinWidth = 0,
        Margin = new Thickness(0, -4, 8, -4), // the Fluent template is ~32 px tall; overlap the padding instead of clipping
    };

    public static GroupBox Group(string header, UIElement content) => new()
    {
        Header = new TextBlock { Text = header, FontSize = 12, FontWeight = FontWeights.SemiBold },
        Content = content,
        Padding = new Thickness(6, 2, 6, 6),
        Margin = new Thickness(0, 0, 8, 8),
        BorderBrush = Dlg.Res("Editor.Faint"),
        BorderThickness = new Thickness(1),
    };

    public static TextBlock Hint(string text, double maxWidth = 220) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Dlg.Res("Editor.Dim"),
        Margin = new Thickness(0, 3, 0, 0),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = maxWidth,
    };

    /// <summary>A caption; "_X" text becomes an access-key Label focusing <paramref name="target"/> (Alt+X).</summary>
    public static FrameworkElement Caption(string text, double width = 0, UIElement? target = null)
    {
        if (target is not null && text.Contains('_'))
            return new Label
            {
                Content = text,
                Target = target,
                FontSize = 12,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Width = width > 0 ? width : double.NaN,
            };
        return new TextBlock
        {
            Text = text.Replace("_", ""),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Width = width > 0 ? width : double.NaN,
        };
    }

    public static TextBox Text(string text, double width) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("NumberTextBox"),
        Width = width,
        Margin = new Thickness(0, 1, 6, 1),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBox Mono(string text, double width)
    {
        var t = Text(text, width);
        t.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        return t;
    }

    public static Button SmallButton(string text, string? tooltip = null) => new()
    {
        Content = text,
        Style = (Style)Application.Current.FindResource("CompactButton"),
        Margin = new Thickness(0, 1, 4, 1),
        Padding = new Thickness(8, 1, 8, 1),
        MinWidth = 0,
        ToolTip = tooltip,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>Parses "key=value;key=value" automation answers (keys lower case).</summary>
    public static Dictionary<string, string> ParseAnswer(string? answer)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(answer)) return d;
        foreach (var part in answer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) d[part] = "";
            else d[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return d;
    }
}
