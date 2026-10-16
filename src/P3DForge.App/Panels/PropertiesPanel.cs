using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Panels;

/// <summary>
/// Named Properties window: key/value grid of the active LOD's <c>#Property#</c> tags with inline editing (value edits
/// merge into one undo step while typing), key suggestions (<see cref="PropertyHints"/>), per-row warnings and delete.
/// </summary>
public sealed class PropertiesPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    sealed class Row
    {
        public int TagIndex;
        public string Key = "";
        public string Value = "";
        public required TextBox KeyBox;
        public required ComboBox ValueBox;
        public required TextBlock Warn;
    }

    const double RowHeight = 28;

    readonly Grid grid;
    readonly TextBlock header;
    readonly Button typical;
    readonly ComboBox newKey;
    readonly ComboBox newValue;
    readonly TextBlock addError;
    readonly List<Row> rows = [];
    int shownLod = -1;
    P3DModel? shownModel;
    bool updating;
    bool stale = true;

    public PropertiesPanel()
    {
        Background = Brushes.Transparent;

        // ---- header: LOD name + count + "typical" helper
        var top = new DockPanel { Margin = new Thickness(4, 4, 4, 2) };
        typical = new Button { Content = "Add typical", Style = (Style)FindResource("CompactButton"), FontSize = 11, Padding = new Thickness(6, 0, 6, 0), MinHeight = 22 };
        typical.Click += (_, _) => AddTypical();
        SetDock(typical, Dock.Right);
        top.Children.Add(typical);
        header = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        top.Children.Add(header);
        SetDock(top, Dock.Top);
        Children.Add(top);

        // ---- add row (bottom)
        var add = new Grid { Margin = new Thickness(4, 4, 4, 6) };
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        add.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        add.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        add.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var addLabel = new TextBlock { Text = "Add property (name, value):", Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(2, 0, 0, 2) };
        Grid.SetColumnSpan(addLabel, 3);
        add.Children.Add(addLabel);
        newKey = Combo("New property name (suggestions in the list)");
        newValue = Combo("Value");
        newKey.Margin = new Thickness(0, 0, 4, 0);
        newKey.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateNewValueChoices()));
        newKey.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateNewValueChoices);
        var addButton = new Button { Content = "", Style = (Style)FindResource("IconButton"), ToolTip = "Add / set the property", Margin = new Thickness(2, 0, 0, 0) };
        addButton.Click += (_, _) => AddProperty();
        foreach (var c in new FrameworkElement[] { newKey, newValue })
            c.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    AddProperty();
                    e.Handled = true;
                }
            };
        Grid.SetColumn(newValue, 1);
        Grid.SetColumn(addButton, 2);
        Grid.SetRow(newKey, 1);
        Grid.SetRow(newValue, 1);
        Grid.SetRow(addButton, 1);
        add.Children.Add(newKey);
        add.Children.Add(newValue);
        add.Children.Add(addButton);
        addError = Dlg.ErrorText();
        addError.Margin = new Thickness(0, 4, 0, 0);
        Grid.SetRow(addError, 2);
        Grid.SetColumnSpan(addError, 3);
        add.Children.Add(addError);
        SetDock(add, Dock.Bottom);
        Children.Add(add);

        // ---- property grid
        grid = new Grid { Margin = new Thickness(4, 2, 4, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Children.Add(scroll);

        Ed.Changed += OnChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && stale) Refresh();
        };
        Refresh();
    }

    ComboBox Combo(string tip)
    {
        var c = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = false,
            MinHeight = 0,
            Height = RowHeight,
            FontSize = 12,
            Padding = new Thickness(6, 0, 6, 0),
            ToolTip = tip,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        return c;
    }

    void OnChanged(Change c)
    {
        if ((c & (Change.Model | Change.ActiveLod | Change.Document)) == 0) return;
        if (!IsVisible)
        {
            stale = true;
            return;
        }
        Refresh();
    }

    void Refresh()
    {
        stale = false;
        if (updating) return;
        var model = Ed.Model;
        var lod = Ed.Lod;
        if (ReferenceEquals(model, shownModel) && shownLod == Ed.ActiveLod) return;
        bool sameLod = shownLod == Ed.ActiveLod && shownModel is not null;
        shownModel = model;
        int count = lod.Properties.Count();
        header.Text = $"{LodNames.Name(lod.Resolution)} — {count} propert{(count == 1 ? "y" : "ies")}";
        var missing = MissingTypical(lod);
        typical.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        typical.ToolTip = "Adds: " + string.Join(", ", missing.Select(p => $"{p.Key}={p.Value}"));

        // Same keys/values in the same order → keep the editors (and the caret) and just remap tag indices.
        var props = new List<(int Index, PropertyTag Tag)>();
        for (int i = 0; i < lod.Tags.Length; i++)
            if (lod.Tags[i] is PropertyTag p) props.Add((i, p));
        bool same = sameLod && props.Count == rows.Count;
        for (int k = 0; same && k < props.Count; k++)
            same = props[k].Tag.Key == rows[k].Key && props[k].Tag.Value == rows[k].Value;
        shownLod = Ed.ActiveLod;
        if (same)
        {
            for (int k = 0; k < props.Count; k++) rows[k].TagIndex = props[k].Index;
        }
        else
        {
            Rebuild(props);
        }
        UpdateWarnings();
        newKey.ItemsSource = PropertyHints.SuggestedKeys(lod);
    }

    void Rebuild(List<(int Index, PropertyTag Tag)> props)
    {
        var focusedKey = rows.FirstOrDefault(r => r.KeyBox.IsKeyboardFocusWithin || r.ValueBox.IsKeyboardFocusWithin);
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        rows.Clear();
        if (props.Count == 0)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var empty = new TextBlock { Text = "No named properties in this LOD.", Foreground = Dlg.Res("Editor.Faint"), FontSize = 12, Margin = new Thickness(4, 6, 4, 6) };
            Grid.SetColumnSpan(empty, 4);
            grid.Children.Add(empty);
            return;
        }
        foreach (var (index, tag) in props)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keyBox = new TextBox
            {
                Text = tag.Key,
                Style = (Style)FindResource("CompactTextBox"),
                MaxLength = NamedProperties.MaxLength,
                Margin = new Thickness(0, 1, 4, 1),
                ToolTip = PropertyHints.Text(tag.Key),
                MinHeight = 0,
                Height = RowHeight,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var valueBox = Combo(PropertyHints.Text(tag.Key));
            valueBox.Margin = new Thickness(0, 1, 0, 1);
            valueBox.ItemsSource = PropertyHints.Get(tag.Key)?.Values ?? [];
            valueBox.Text = tag.Value;
            var warn = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                Foreground = Dlg.Res("Editor.Warn"),
                FontSize = 13,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var del = new Button { Content = "", Style = (Style)FindResource("IconButton"), ToolTip = "Delete this property", Height = 24 };
            var row = new Row { TagIndex = index, Key = tag.Key, Value = tag.Value, KeyBox = keyBox, ValueBox = valueBox, Warn = warn };
            keyBox.LostKeyboardFocus += (_, _) => CommitKey(row);
            keyBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    CommitKey(row);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    keyBox.Text = row.Key;
                    e.Handled = true;
                }
            };
            valueBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => CommitValue(row)));
            valueBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(() => CommitValue(row));
            del.Click += (_, _) => Dlg.Try(() => Ed.EditLod($"Delete property \"{row.Key}\"", l => NamedProperties.DeleteAt(l, row.TagIndex)));
            Grid.SetRow(keyBox, r);
            Grid.SetRow(valueBox, r);
            Grid.SetColumn(valueBox, 1);
            Grid.SetRow(warn, r);
            Grid.SetColumn(warn, 2);
            Grid.SetRow(del, r);
            Grid.SetColumn(del, 3);
            grid.Children.Add(keyBox);
            grid.Children.Add(valueBox);
            grid.Children.Add(warn);
            grid.Children.Add(del);
            rows.Add(row);
        }
        if (focusedKey is not null && rows.FirstOrDefault(r => r.Key == focusedKey.Key) is { } again)
            Dispatcher.BeginInvoke(() => again.ValueBox.Focus());
    }

    void UpdateWarnings()
    {
        var lod = Ed.Lod;
        foreach (var r in rows)
        {
            var w = PropertyHints.Warnings(lod, r.TagIndex);
            r.Warn.Visibility = w.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            r.Warn.ToolTip = w.Count > 0 ? string.Join("\n", w) : null;
        }
    }

    // ---- edits -----------------------------------------------------------------------------------

    void Apply(string label, Func<Lod, Lod> fn, string? mergeKey = null)
    {
        updating = true;
        try
        {
            Ed.EditLod(label, fn, new EditOptions { MergeKey = mergeKey });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Dlg.Fail(ex.Message);
            shownModel = null; // force a rebuild from the (unchanged) model
            shownLod = -1;
        }
        finally
        {
            updating = false;
        }
        Refresh();
    }

    void CommitValue(Row row)
    {
        var text = row.ValueBox.Text ?? "";
        if (text == row.Value) return;
        var err = NamedProperties.ValidateValue(text);
        row.ValueBox.BorderBrush = err is null ? null : Dlg.Res("Editor.Error");
        row.ValueBox.ToolTip = err ?? PropertyHints.Text(row.Key);
        if (err is not null) return;
        row.Value = text;
        Apply($"Property {row.Key} = \"{text}\"", l => NamedProperties.SetValue(l, row.TagIndex, text), $"prop:{Ed.ActiveLod}:{row.TagIndex}");
    }

    void CommitKey(Row row)
    {
        var text = row.KeyBox.Text ?? "";
        if (text == row.Key) return;
        var lod = Ed.Lod;
        string? err = NamedProperties.ValidateKey(text);
        if (err is null)
            for (int j = 0; j < lod.Tags.Length; j++)
                if (j != row.TagIndex && lod.Tags[j] is PropertyTag p && string.Equals(p.Key, text, StringComparison.OrdinalIgnoreCase))
                    err = $"Property \"{text}\" already exists";
        if (err is not null)
        {
            Dlg.Fail(err);
            row.KeyBox.Text = row.Key;
            return;
        }
        var old = row.Key;
        row.Key = text;
        row.KeyBox.ToolTip = PropertyHints.Text(text);
        row.ValueBox.ItemsSource = PropertyHints.Get(text)?.Values ?? [];
        Apply($"Rename property \"{old}\" → \"{text}\"", l => NamedProperties.Rename(l, row.TagIndex, text));
    }

    void UpdateNewValueChoices()
    {
        var key = newKey.Text ?? "";
        newValue.ItemsSource = PropertyHints.Get(key)?.Values ?? [];
        newKey.ToolTip = key.Length > 0 ? PropertyHints.Text(key) : "New property name (suggestions in the list)";
        Dlg.SetError(addError, null);
    }

    void AddProperty()
    {
        var key = (newKey.Text ?? "").Trim();
        var value = (newValue.Text ?? "").Trim();
        var err = NamedProperties.ValidateKey(key) ?? NamedProperties.ValidateValue(value);
        Dlg.SetError(addError, err);
        if (err is not null) return;
        bool exists = NamedProperties.Find(Ed.Lod, key) >= 0;
        Apply(exists ? $"Property {key} = \"{value}\"" : $"Add property {key} = \"{value}\"", l => NamedProperties.Set(l, key, value));
        if (PropertyHints.ValueProblem(key, value) is { } p) Ed.Log($"Property {key}: {p}", LogLevel.Warn);
        newKey.Text = "";
        newValue.Text = "";
        newKey.Focus();
    }

    static List<(string Key, string Value)> MissingTypical(Lod lod) =>
        PropertyHints.Typical(lod.Resolution).Where(p => NamedProperties.Find(lod, p.Key) < 0).ToList();

    void AddTypical()
    {
        var missing = MissingTypical(Ed.Lod);
        if (missing.Count == 0) return;
        Apply("Add typical properties", l =>
        {
            foreach (var (k, v) in missing) l = NamedProperties.Set(l, k, v);
            return l;
        });
    }
}
