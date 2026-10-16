using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using P3DForge.App.Editor;

namespace P3DForge.App.Dialogs.Ops;

/// <summary>
/// A small parameter dialog built from typed fields (numbers, integers, text, choices, check boxes) with validation.
/// <para>
/// Automation: the dialog is never shown; the "automation.answer" flag may hold "key=value;key=value" overrides
/// (choices by option text or index, booleans as true/false/1/0); fields without an override keep their defaults.
/// </para>
/// </summary>
public sealed class Form
{
    abstract class Field(string key, string label)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public abstract FrameworkElement Editor { get; }
        /// <summary>Parses the editor content; returns an error or null.</summary>
        public abstract string? Validate();
        public abstract void SetText(string text);
    }

    sealed class NumberField(string key, string label, double value, double min, double max, bool integer) : Field(key, label)
    {
        readonly TextBox box = new()
        {
            Text = Format(value, integer),
            Style = (Style)Application.Current.FindResource("NumberTextBox"),
            MinWidth = 120,
        };
        public double Value { get; private set; } = value;
        public override FrameworkElement Editor => box;

        static string Format(double v, bool integer) => integer ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.######", CultureInfo.InvariantCulture);

        public override string? Validate()
        {
            if (!Dlg.TryParse(box.Text, out double v)) return $"{Label}: enter a number";
            if (integer && v != Math.Floor(v)) return $"{Label}: enter a whole number";
            if (v < min || v > max) return $"{Label}: must be between {Format(min, integer)} and {Format(max, integer)}";
            Value = v;
            return null;
        }

        public override void SetText(string text) => box.Text = text;
        public void Set(double v) => box.Text = Format(v, integer);
    }

    sealed class TextField(string key, string label, string value, Func<string, string?>? validate) : Field(key, label)
    {
        readonly TextBox box = new() { Text = value, Style = (Style)Application.Current.FindResource("CompactTextBox"), MinWidth = 300 };
        public string Value => box.Text.Trim();
        public override FrameworkElement Editor => box;
        public override string? Validate() => validate?.Invoke(Value);
        public override void SetText(string text) => box.Text = text;
    }

    sealed class ChoiceField(string key, string label, string[] options, int selected) : Field(key, label)
    {
        readonly ComboBox combo = new() { ItemsSource = options, SelectedIndex = selected, MinWidth = 160, FontSize = 12 };
        public int Index => Math.Max(0, combo.SelectedIndex);
        public override FrameworkElement Editor => combo;
        public override string? Validate() => null;

        public override void SetText(string text)
        {
            int i = Array.FindIndex(options, o => o.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (i < 0 && int.TryParse(text, out int n) && n >= 0 && n < options.Length) i = n;
            if (i >= 0) combo.SelectedIndex = i;
        }
    }

    sealed class CheckField(string key, string label, bool value) : Field(key, label)
    {
        readonly CheckBox box = new() { Content = label, IsChecked = value, FontSize = 12, MinHeight = 0 };
        public bool Value => box.IsChecked == true;
        public override FrameworkElement Editor => box;
        public override string? Validate() => null;
        public override void SetText(string text) => box.IsChecked = text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    readonly string title;
    readonly List<Field> fields = [];
    readonly Grid grid = new();
    readonly StackPanel panel = new() { Margin = new Thickness(16) };
    readonly TextBlock error = Dlg.ErrorText();
    Func<Form, string?>? check;

    public Form(string title, string? intro = null)
    {
        this.title = title;
        if (intro is not null) panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(grid);
    }

    Form Add(Field f, bool labelled = true)
    {
        fields.Add(f);
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (labelled)
        {
            var l = new TextBlock { Text = f.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3) };
            Grid.SetRow(l, row);
            grid.Children.Add(l);
        }
        f.Editor.Margin = new Thickness(0, 3, 0, 3);
        f.Editor.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(f.Editor, row);
        Grid.SetColumn(f.Editor, labelled ? 1 : 0);
        if (!labelled) Grid.SetColumnSpan(f.Editor, 2);
        grid.Children.Add(f.Editor);
        return this;
    }

    public Form Number(string key, string label, double value, double min = double.MinValue, double max = double.MaxValue) =>
        Add(new NumberField(key, label, value, min, max, false));

    public Form Integer(string key, string label, int value, int min, int max) => Add(new NumberField(key, label, value, min, max, true));

    public Form Text(string key, string label, string value, Func<string, string?>? validate = null) => Add(new TextField(key, label, value, validate));

    public Form Choice(string key, string label, string[] options, int selected = 0) => Add(new ChoiceField(key, label, options, selected));

    public Form Check(string key, string label, bool value) => Add(new CheckField(key, label, value), labelled: false);

    /// <summary>An extra button below the fields (e.g. "Detect"); the callback may change field values via <see cref="SetNumber"/>.</summary>
    public Form Action(string text, Action<Form> onClick)
    {
        var b = Dlg.Button(text);
        b.Margin = new Thickness(0, 6, 0, 0);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.Click += (_, _) => onClick(this);
        panel.Children.Add(b);
        return this;
    }

    /// <summary>Cross-field validation run on OK (after every field parsed); returns an error or null.</summary>
    public Form Validate(Func<Form, string?> f)
    {
        check = f;
        return this;
    }

    T Get<T>(string key) where T : Field => fields.OfType<T>().FirstOrDefault(f => f.Key == key) ?? throw new KeyNotFoundException(key);

    public double Num(string key) => Get<NumberField>(key).Value;
    public int Int(string key) => (int)Get<NumberField>(key).Value;
    public string Str(string key) => Get<TextField>(key).Value;
    public int Index(string key) => Get<ChoiceField>(key).Index;
    public bool Bool(string key) => Get<CheckField>(key).Value;
    public void SetNumber(string key, double v) => Get<NumberField>(key).Set(v);
    /// <summary>Sets any field from text (numbers invariant culture, choices by text or index, booleans true/false).</summary>
    public void Set(string key, string text) => fields.First(f => f.Key == key).SetText(text);

    string? ValidateAll()
    {
        foreach (var f in fields)
            if (f.Validate() is { } e) return e;
        return check?.Invoke(this);
    }

    /// <summary>Shows the dialog; true when confirmed with valid values (read them with Num/Int/Str/Index/Bool).</summary>
    public bool Show(string okText = "OK")
    {
        panel.Children.Add(error);
        var w = Dlg.MakeWindow(title, panel, 320);
        var (row, _) = Dlg.OkCancel(w, () =>
        {
            var e = ValidateAll();
            Dlg.SetError(error, e);
            return e is null;
        }, okText);
        panel.Children.Add(row);
        w.Loaded += (_, _) =>
        {
            if (fields.FirstOrDefault()?.Editor is TextBox tb)
            {
                tb.Focus();
                tb.SelectAll();
            }
        };
        if (Dlg.Automated)
        {
            Dlg.Capture(w, "form-" + new string(title.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant());
            if (Dlg.TakeAnswer() is { } answer)
                foreach (var part in answer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = part[..eq].Trim();
                    fields.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.SetText(part[(eq + 1)..].Trim());
                }
            var err = ValidateAll();
            AppServices.Editor.Log($"[automation] form \"{title}\"{(err is null ? "" : " — " + err)}", err is null ? LogLevel.Info : LogLevel.Warn);
            return err is null;
        }
        return w.ShowDialog() == true;
    }
}
