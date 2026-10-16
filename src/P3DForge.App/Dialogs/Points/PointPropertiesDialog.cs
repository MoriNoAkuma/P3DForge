using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using P3DForge.App.Dialogs.Faces;
using P3DForge.App.Editor;
using P3DForge.Core;
using P3DForge.Core.Ops;

namespace P3DForge.App.Dialogs.Points;

/// <summary>
/// Object Builder "Point Properties" (Shift+E). One point: editable X/Y/Z. Several: read-only centroid plus an
/// optional offset. Point flags (surface / land contact, fog, decal, lighting, normals, user value) with mixed
/// handling; hidden / locked counts are shown read-only. OK and Apply each make one undo step.
/// <para>Bit values follow the BI wiki and Arma 3 Object Builder: surface 1 = on, 2 = above, 4 = under, 8 = keep
/// height; lighting 0x10 shining, 0x20 always in shadow, 0x40 fully lit, 0x80 half lit; normals 0x04000000 by
/// impedance angle, 0x02000000 fixed.</para>
/// <para>Automation: "automation.answer" = "x=…;y=…;z=…" (one point) or "offset=dx,dy,dz", plus
/// "surface=normal|on|above|under|keep;fog=normal|sky|none;decal=normal|decal;lighting=…;normals=…;user=0…255".</para>
/// </summary>
public sealed class PointPropertiesDialog
{
    static EditorState Ed => AppServices.Editor;

    const uint SurfaceOn = 0x1, SurfaceAbove = 0x2, SurfaceUnder = 0x4, SurfaceKeep = 0x8; // see class remarks (Flags.cs names 2/4 the other way round)
    const uint NormalsAngle = 0x04000000, NormalsFixed = 0x02000000, NormalsMask = NormalsAngle | NormalsFixed;

    readonly int lodIndex;
    readonly byte[] points;
    readonly int pointCount;
    PointSummary summary;
    readonly bool single;
    readonly TextBox[] coord = new TextBox[3];
    readonly string[] coordInitial = new string[3];
    readonly TextBlock centroid = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 2) };
    readonly List<IFlagField> fields = [];
    readonly TextBlock error = Dlg.ErrorText();
    readonly Window window;
    Button? ok;

    public static void Show()
    {
        var lod = Ed.Lod;
        var sel = Ed.GetSelection();
        var s = Attributes.SummarizePoints(lod, sel.Points);
        if (s.Count == 0)
        {
            Dlg.Fail("Point Properties: select one or more points first.");
            return;
        }
        new PointPropertiesDialog(Ed.ActiveLod, sel.Points, s).Run();
    }

    static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    PointPropertiesDialog(int lodIndex, byte[] points, PointSummary summary)
    {
        this.lodIndex = lodIndex;
        this.points = points;
        pointCount = points.Length;
        this.summary = summary;
        single = summary.Count == 1;
        var flags = summary.Flags;

        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
        var head = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        head.Inlines.Add(new System.Windows.Documents.Run($"{summary.Count:N0}") { FontWeight = FontWeights.SemiBold });
        head.Inlines.Add(new System.Windows.Documents.Run(single ? $" point selected (point #{summary.First})" : " points selected"));
        root.Children.Add(head);

        // ---- position ----
        var pos = new StackPanel();
        if (single)
        {
            var p = summary.Centroid;
            double[] v = [p.X, p.Y, p.Z];
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            string[] names = ["_X", "_Y", "_Z"];
            for (int i = 0; i < 3; i++)
            {
                coordInitial[i] = F(v[i]);
                coord[i] = Ui.Mono(coordInitial[i], 96);
                row.Children.Add(Ui.Caption(names[i], 0, coord[i]));
                row.Children.Add(coord[i]);
            }
            pos.Children.Add(row);
        }
        else
        {
            pos.Children.Add(centroid);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(Ui.Caption("Offset  X"));
            string[] names = ["X", "Y", "Z"];
            for (int i = 0; i < 3; i++)
            {
                coordInitial[i] = "0";
                coord[i] = Ui.Mono("0", 80);
                if (i > 0) row.Children.Add(Ui.Caption(names[i], 0, coord[i]));
                row.Children.Add(coord[i]);
            }
            pos.Children.Add(row);
            pos.Children.Add(Ui.Hint("Moves every selected point by the offset (metres).", 400));
        }
        root.Children.Add(Ui.Group(single ? "Position" : "Position (centroid)", pos));
        UpdateCentroid();

        // ---- flags ----
        var groups = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 640 };
        var surface = new FlagRadioGroup("surface", "Surface", PointFlags.LandMask, flags,
            ("Normal", 0), ("On surface", SurfaceOn), ("Above surface", SurfaceAbove), ("Under surface", SurfaceUnder), ("Keep height (fence)", SurfaceKeep));
        var fog = new FlagRadioGroup("fog", "Fog", PointFlags.FogMask, flags, ("Normal", 0), ("Sky", PointFlags.SkyFog), ("None", PointFlags.NoFog));
        var decal = new FlagRadioGroup("decal", "Decal", PointFlags.DecalMask, flags, ("Normal", 0), ("Decal", PointFlags.Decal));
        var lighting = new FlagRadioGroup("lighting", "Lighting (obsolete)", PointFlags.LightMask, flags,
            ("Normal", 0), ("Shining", 0x10), ("Always in shadow", 0x20), ("Half lighted", 0x80), ("Fully lighted", 0x40));
        var normals = new FlagRadioGroup("normals", "Normals (weighted by)", NormalsMask, flags,
            ("Face dimensions", 0), ("Impedance angle", NormalsAngle), ("Fixed (locked normal)", NormalsFixed));
        normals.Box.ToolTip = "Fixed = flag 0x02000000. Note: P3D Forge currently also treats that bit as 'locked' (not selectable).";
        foreach (var g in new[] { surface, fog, decal, lighting, normals })
        {
            fields.Add(g);
            groups.Children.Add(g.Box);
        }
        var user = new FlagNumber("user", PointFlags.UserMask, PointFlags.UserShift, flags);
        fields.Add(user);
        var other = new StackPanel();
        other.Children.Add(Ui.Row(Ui.Caption("_User value", 0, user.Box), user.Box));
        var state = Ui.Hint($"Hidden: {summary.Hidden:N0}   Locked: {summary.Locked:N0}");
        state.ToolTip = "Read-only here — use Points › Hide / Lock commands.";
        other.Children.Add(state);
        var raw = Ui.Hint(!flags.IsMixed(uint.MaxValue) ? $"flags 0x{flags.And:X8}" : $"flags vary (AND 0x{flags.And:X8}, OR 0x{flags.Or:X8})");
        raw.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        other.Children.Add(raw);
        groups.Children.Add(Ui.Group("Other", other));
        root.Children.Add(groups);
        root.Children.Add(error);

        window = Dlg.MakeWindow("Point Properties", root, 460);
        var (buttons, okButton) = Dlg.OkCancel(window, Apply);
        ok = okButton;
        var apply = Dlg.Button("_Apply");
        apply.Click += (_, _) => Apply();
        buttons.Children.Add(apply);
        root.Children.Add(buttons);

        foreach (var f in fields) f.Changed += () => Validate();
        foreach (var c in coord) c.TextChanged += (_, _) => Validate();
        window.Loaded += (_, _) =>
        {
            coord[0].Focus();
            coord[0].SelectAll();
        };
    }

    void UpdateCentroid()
    {
        if (single) return;
        var c = summary.Centroid;
        var size = summary.Max - summary.Min;
        static string P(double v) => F(v).PadLeft(11);
        centroid.Text = $"Centroid  X{P(c.X)}  Y{P(c.Y)}  Z{P(c.Z)}\nSize      X{P(size.X)}  Y{P(size.Y)}  Z{P(size.Z)}";
        centroid.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
    }

    void Run()
    {
        if (Dlg.Automated)
        {
            var answer = Dlg.TakeAnswer();
            if (answer is not null) ApplyAnswer(answer);
            Dlg.Capture(window, "point-properties");
            if (answer is null)
            {
                Ed.Log("[automation] Point Properties → cancel (no automation.answer)");
                return;
            }
            bool applied = Apply();
            Ed.Log($"[automation] Point Properties \"{answer}\" → {(applied ? "applied" : "rejected: " + error.Text)}", applied ? LogLevel.Info : LogLevel.Warn);
            return;
        }
        window.ShowDialog();
    }

    void ApplyAnswer(string answer)
    {
        foreach (var (k, v) in Ui.ParseAnswer(answer))
        {
            switch (k.ToLowerInvariant())
            {
                case "x": coord[0].Text = v; break;
                case "y": coord[1].Text = v; break;
                case "z": coord[2].Text = v; break;
                case "offset":
                {
                    var p = v.Split(',');
                    for (int i = 0; i < 3 && i < p.Length; i++) coord[i].Text = p[i].Trim();
                    break;
                }
                default:
                    if (fields.FirstOrDefault(f => f.Key.Equals(k, StringComparison.OrdinalIgnoreCase)) is { } f)
                    {
                        if (!f.SetFromText(v)) Ed.Log($"[automation] Point Properties: bad value {k}={v}", LogLevel.Warn);
                    }
                    else Ed.Log($"[automation] Point Properties: unknown key '{k}'", LogLevel.Warn);
                    break;
            }
        }
    }

    string? Validate()
    {
        string? e = null;
        var axis = "XYZ";
        for (int i = 0; i < 3; i++)
            if (!Dlg.TryParse(coord[i].Text, out _)) e ??= $"{axis[i]} must be a number.";
        foreach (var f in fields) e ??= f.Error;
        Dlg.SetError(error, e);
        if (ok is not null) ok.IsEnabled = e is null;
        return e;
    }

    bool Apply()
    {
        if (Validate() is not null) return false;
        var v = new double[3];
        bool moved = false;
        for (int i = 0; i < 3; i++)
        {
            Dlg.TryParse(coord[i].Text, out v[i]);
            if (coord[i].Text.Trim() != coordInitial[i]) moved = true;
        }
        var d = new Vec3(v[0], v[1], v[2]);
        if (!single && d == Vec3.Zero) moved = false;
        var edited = fields.Where(f => f.Edited).ToList();
        if (!moved && edited.Count == 0) return true;
        if (lodIndex >= Ed.Model.Lods.Length || Ed.Model.Lods[lodIndex].PointCount != pointCount)
        {
            Dlg.SetError(error, "The LOD changed since the dialog opened; reopen Point Properties.");
            return false;
        }
        var what = new List<string>();
        if (moved) what.Add(single ? "position" : "offset");
        foreach (var f in edited) what.Add(f.Key);
        int first = summary.First;
        bool changed = false;
        Dlg.Try(() => changed = Ed.EditLod($"Point properties ({string.Join(", ", what)}; {summary.Count:N0} pts)", lod =>
        {
            var l = lod;
            if (moved) l = single ? Attributes.SetPointPosition(l, first, d) : Attributes.OffsetPoints(l, points, d);
            foreach (var f in edited) l = Attributes.SetPointFlags(l, points, f.Mask, f.Value);
            return l;
        }, new EditOptions { LodIndex = lodIndex }));
        if (!changed) return true;
        Ed.Log($"Point properties: {string.Join(", ", what)} set on {summary.Count:N0} point(s)");
        // re-baseline so OK after Apply does not move the points again
        summary = Attributes.SummarizePoints(Ed.Model.Lods[lodIndex], points);
        for (int i = 0; i < 3; i++)
        {
            coordInitial[i] = single ? coord[i].Text.Trim() : "0";
            if (!single) coord[i].Text = "0";
        }
        UpdateCentroid();
        return true;
    }
}
