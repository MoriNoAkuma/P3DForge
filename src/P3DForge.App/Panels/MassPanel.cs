using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using P3DForge.App.Editor;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Panels;

/// <summary>
/// Mass window (Object Builder "Mass", Shift+F7): mass of the model (Geometry LOD), of the active LOD and of the
/// selection, centre of mass and inertia, per-named-selection masses (double-click selects), Set Mass / Clear buttons
/// and the centre-of-mass marker toggle (flag <c>mass.showCenter</c>).
/// </summary>
public sealed class MassPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    readonly TextBlock header;
    readonly Grid summary;
    readonly ListBox list;
    readonly TextBlock listHeader;
    readonly CheckBox showCenter;
    bool stale = true;
    P3DModel? shownModel;
    int shownLod = -1;

    public MassPanel()
    {
        Background = Brushes.Transparent;
        header = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(6, 6, 6, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        SetDock(header, Dock.Top);
        Children.Add(header);

        summary = new Grid { Margin = new Thickness(6, 2, 6, 4) };
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetDock(summary, Dock.Top);
        Children.Add(summary);

        var buttons = new WrapPanel { Margin = new Thickness(4, 2, 4, 4) };
        buttons.Children.Add(CommandButton("Set Mass…", "mass.set"));
        buttons.Children.Add(CommandButton("Clear", "mass.clear"));
        showCenter = new CheckBox
        {
            Content = "Show centre",
            FontSize = 12,
            Margin = new Thickness(8, 4, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Draws a cross at the centre of mass of the active LOD in the viewport",
            IsChecked = Ed.Flag("mass.showCenter", false),
        };
        showCenter.Checked += (_, _) => Ed.SetFlag("mass.showCenter", true);
        showCenter.Unchecked += (_, _) => Ed.SetFlag("mass.showCenter", false);
        buttons.Children.Add(showCenter);
        SetDock(buttons, Dock.Top);
        Children.Add(buttons);

        listHeader = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(6, 6, 6, 2), Text = "Named selections" };
        SetDock(listHeader, Dock.Top);
        Children.Add(listHeader);

        list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemContainerStyle = (Style)FindResource("CompactListBoxItem"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, System.Windows.Controls.ScrollBarVisibility.Disabled);
        list.MouseDoubleClick += (_, e) =>
        {
            if (list.SelectedItem is FrameworkElement { Tag: string name })
                SelectionOps.SelectNamed(name, Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? SelectOp.Add : SelectOp.Set);
        };
        Children.Add(list);

        Ed.Changed += OnChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && stale) Refresh(true);
        };
        Refresh(true);
    }

    Button CommandButton(string text, string id)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource("CompactButton"),
            FontSize = 12,
            Padding = new Thickness(10, 0, 10, 0),
            MinHeight = 24,
            Margin = new Thickness(2, 2, 2, 2),
            ToolTip = Commands.Get(id)?.Description,
        };
        b.Click += (_, _) => Commands.Run(id);
        return b;
    }

    void OnChanged(Change c)
    {
        if ((c & (Change.Model | Change.ActiveLod | Change.Document | Change.Selection | Change.Ui)) == 0) return;
        if ((c & Change.Ui) != 0) showCenter.IsChecked = Ed.Flag("mass.showCenter", false);
        if (!IsVisible)
        {
            stale = true;
            return;
        }
        Refresh(!ReferenceEquals(shownModel, Ed.Model) || shownLod != Ed.ActiveLod);
    }

    static string Kg(double v) => v.ToString(v >= 1000 ? "#,0.#" : "0.###", CultureInfo.InvariantCulture) + " kg";

    static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    void Row(string label, string value, Brush? brush = null, string? tip = null)
    {
        int r = summary.RowDefinitions.Count;
        summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("Editor.Dim"), Margin = new Thickness(0, 1, 10, 1) };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            Margin = new Thickness(0, 1, 0, 1),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = tip ?? value,
        };
        if (brush is not null) v.Foreground = brush;
        Grid.SetRow(l, r);
        Grid.SetRow(v, r);
        Grid.SetColumn(v, 1);
        summary.Children.Add(l);
        summary.Children.Add(v);
    }

    void Refresh(bool full)
    {
        stale = false;
        var model = Ed.Model;
        var lod = Ed.Lod;
        summary.Children.Clear();
        summary.RowDefinitions.Clear();

        int g = Mass.GeometryLodIndex(model);
        header.Text = $"Mass — {LodNames.Name(lod.Resolution)}";
        if (g < 0)
            Row("Model", "no Geometry LOD", (Brush)FindResource("Editor.Warn"), "The engine reads the mass from the Geometry LOD");
        else
        {
            var geo = model.Lods[g];
            double gm = Mass.Total(geo);
            Row("Model (Geometry)", geo.MassTag is null ? "no mass" : Kg(gm), gm > 0 ? null : (Brush)FindResource("Editor.Warn"));
        }

        var props = Mass.Properties(lod);
        int withMass = Mass.PointsWithMass(lod);
        Row("Active LOD", lod.MassTag is null ? "no #Mass#" : $"{Kg(props.Total)} on {withMass:N0} of {lod.PointCount:N0} points");
        var sel = Selection.PeekSelected(lod);
        if (sel is { PointCount: > 0 } s)
            Row("Selection", $"{Kg(Mass.Total(lod, s.Points))} on {s.PointCount:N0} points");
        if (props.HasMass)
        {
            var c = props.Center;
            Row("Centre of mass", $"{Num(c.X)}, {Num(c.Y)}, {Num(c.Z)}", null, "RAW model coordinates (x right, y up, z forward)");
            Row("Inertia (diag.)", $"{Num(props.Ixx)}, {Num(props.Iyy)}, {Num(props.Izz)}", null,
                $"About the centre of mass, kg·m²\nIxy {Num(props.Ixy)}  Ixz {Num(props.Ixz)}  Iyz {Num(props.Iyz)}");
        }

        if (!full) return;
        shownModel = model;
        shownLod = Ed.ActiveLod;
        list.Items.Clear();
        if (lod.MassTag is null)
        {
            listHeader.Text = "Named selections";
            return;
        }
        var per = Mass.PerSelection(lod);
        listHeader.Text = $"Named selections ({per.Length})";
        foreach (var (name, mass, points) in per)
        {
            if (points == 0) continue;
            var row = new DockPanel { Tag = name, ToolTip = $"{name}: {Kg(mass)} on {points:N0} points — double-click to select" };
            var v = new TextBlock { Text = Kg(mass), FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, Margin = new Thickness(8, 0, 2, 0) };
            if (mass == 0) v.Foreground = (Brush)FindResource("Editor.Faint");
            SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(new TextBlock { Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            list.Items.Add(row);
        }
    }
}
