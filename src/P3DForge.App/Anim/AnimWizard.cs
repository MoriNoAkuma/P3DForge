using System.Globalization;
using System.Numerics;
using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Ops;
using P3DForge.App.Editor;
using P3DForge.Core;
using P3DForge.Core.Anim;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Anim;

/// <summary>
/// Animate Selection…: the selected part (or a named selection) + "what is it" → named selection, skeleton bone,
/// two axis points in the Memory LOD (on the hinge edge or through the centre) and a model.cfg animation with a sensible
/// range — shown at once in the preview. One undo step for the model; the animation itself is saved with model.cfg.
/// </summary>
public static class AnimWizard
{
    static EditorState Ed => AppServices.Editor;

    enum Kind { Door, Wheel, Lever, Slide, Hide }

    static readonly string[] KindLabels =
    [
        "Door, hatch, lid — swings on a hinge",
        "Wheel, rotor, fan — spins around its centre",
        "Lever, trigger, switch — tilts a little",
        "Bolt, slide, drawer — moves along a line",
        "Disappears / appears (hide)",
    ];

    static readonly string[] AxisLabels = ["Auto (by the kind of part)", "X — left / right", "Y — up / down", "Z — forward / back"];

    static readonly string[] AtLabels =
        ["Auto (by the kind of part)", "Centre of the part", "Left edge (−X)", "Right edge (+X)", "Bottom (−Y)", "Top (+Y)", "Back (−Z)", "Front (+Z)"];

    /// <summary>Runs the dialog; returns the new animation's name (null when cancelled).</summary>
    public static string? Run()
    {
        if (Ed.Path is null && Services.FileService.Converted is null)
        {
            Dlg.Fail("Save the model first — its model.cfg is found (or created) next to it");
            return null;
        }
        AnimSession.Refresh();
        var lod = Ed.Lod;
        var sel = Selection.PeekSelected(lod);
        int selected = sel?.PointCount ?? 0;
        // the selection may be exactly an existing named selection
        string? match = null;
        if (selected > 0)
            foreach (var s in lod.Selections)
                if (!Proxies.IsProxyName(s.Name) && SamePoints(s.Points, sel!.Value.Points)) match = s.Name;
        var memory = Ed.Model.Lods.FirstOrDefault(l => LodNames.IsMemory(l.Resolution));
        var axes = memory?.Selections.Where(s => s.Points.Count(b => b != 0) >= 2).Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        var bones = AnimSession.AllBones.Select(b => b.Name).ToList();
        var sources = AnimSession.All.Select(a => a.Source).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var intro = selected > 0
            ? $"{selected:N0} selected points in LOD {LodNames.Name(lod.Resolution)}" + (match is null ? " — they become a new named selection." : $" = the named selection \"{match}\".")
            : "Nothing is selected — type the name of an existing named selection (or close this, click the part in the viewport and try again).";
        var form = new Form("Animate Selection", intro + "\nThe part must have the same named selection in every LOD that should move (LOD ▸ Copy Selection to LODs).")
            .Text("bone", "Part (named selection)", match ?? "", v => ValidateBone(v, lod, selected))
            .Choice("kind", "What is it?", KindLabels)
            .Choice("axis", "Turns / moves along", [.. AxisLabels, .. axes.Select(a => "existing axis: " + a)])
            .Choice("at", "Axis goes through", AtLabels)
            .Text("amount", "How far", "auto", v => v.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) || ModelCfg.TryEval(v, out _) ? null : "a number, or auto")
            .Text("source", "Driven by (source)", sources.Contains("user", StringComparer.OrdinalIgnoreCase) || sources.Count == 0 ? "user" : sources[0])
            .Choice("parent", "Moves with", ["(nothing — moves on its own)", .. bones])
            .Text("name", "Animation name", "", v => v.Trim().Length == 0 || ValidName(v) ? null : "letters, digits and _ only");
        if (!form.Show("Animate")) return null;

        var bone = form.Str("bone").Trim();
        var kind = (Kind)form.Index("kind");
        int axisChoice = form.Index("axis");
        int atChoice = form.Index("at");
        var amountText = form.Str("amount").Trim();
        var source = form.Str("source").Trim();
        if (source.Length == 0) source = "user";
        int parentChoice = form.Index("parent");
        var parent = parentChoice == 0 ? "" : bones[parentChoice - 1];
        var name = form.Str("name").Trim();
        if (name.Length == 0) name = AnimSession.UniqueName(bone + "_" + kind.ToString().ToLowerInvariant());
        else if (AnimSession.All.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) name = AnimSession.UniqueName(name);

        // the part's points: its named selection, else the current selection
        var boneSel = lod.Selection(bone);
        byte[] points = boneSel?.Points ?? sel?.Points ?? [];
        var pts = Enumerable.Range(0, Math.Min(points.Length, lod.PointCount)).Where(i => points[i] != 0)
            .Select(i => new Vector3(lod.Positions[i * 3], lod.Positions[i * 3 + 1], lod.Positions[i * 3 + 2])).ToList();
        if (pts.Count == 0)
        {
            Dlg.Fail($"\"{bone}\" has no points in LOD {LodNames.Name(lod.Resolution)}");
            return null;
        }
        var min = pts.Aggregate(Vector3.Min);
        var max = pts.Aggregate(Vector3.Max);
        var centre = (min + max) / 2;

        int dirIndex = axisChoice is >= 1 and <= 3 ? axisChoice - 1 : kind switch { Kind.Door => 1, Kind.Slide => 2, _ => 0 };
        string? existingAxis = axisChoice >= AxisLabels.Length ? axes[axisChoice - AxisLabels.Length] : null;
        // Auto: a memory axis already named after the part ("door2_axis", "osa_door2") is the one the author meant
        if (axisChoice == 0 && kind != Kind.Hide)
            existingAxis = axes.FirstOrDefault(n => n.Contains(bone, StringComparison.OrdinalIgnoreCase)
                                                    && (n.Contains("axis", StringComparison.OrdinalIgnoreCase) || n.Contains("osa", StringComparison.OrdinalIgnoreCase)));
        var dir = dirIndex switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
        // where the axis line passes: the centre, or an edge of the part's box (a door's hinge side)
        int at = atChoice >= 1 ? atChoice : kind == Kind.Door ? dirIndex switch { 1 => 7, 0 => 6, _ => 2 } : 1;
        var pivot = centre;
        switch (at)
        {
            case 2: pivot.X = min.X; break;
            case 3: pivot.X = max.X; break;
            case 4: pivot.Y = min.Y; break;
            case 5: pivot.Y = max.Y; break;
            case 6: pivot.Z = min.Z; break;
            case 7: pivot.Z = max.Z; break;
        }
        double amount = amountText.Equals("auto", StringComparison.OrdinalIgnoreCase) || !ModelCfg.TryEval(amountText, out var a0)
            ? kind switch { Kind.Door => 90, Kind.Wheel => 360, Kind.Lever => 25, Kind.Slide => Math.Max(0.02, Vector3.Dot(max - min, dir)), _ => 0 }
            : a0;
        float extent = Math.Max(0.05f, Vector3.Dot(max - min, dir));
        Vector3 p0, p1;
        if (kind == Kind.Slide)
        {
            // an axis exactly as long as the move: offset 1 = one axis length
            p0 = centre;
            p1 = centre + dir * (float)Math.Max(0.001, Math.Abs(amount)) * Math.Sign(amount == 0 ? 1 : amount);
        }
        else
        {
            p0 = pivot - dir * extent / 2;
            p1 = pivot + dir * extent / 2;
        }
        string axisName = existingAxis ?? (kind == Kind.Hide ? "" : UniqueAxisName(memory, bone + "_axis"));

        var anim = new AnimDef
        {
            Name = name,
            Type = kind switch { Kind.Slide => "translation", Kind.Hide => "hide", _ => "rotation" },
            Source = source,
            Selection = bone,
            Axis = axisName,
            MinValue = 0,
            MaxValue = 1,
            Angle0 = 0,
            Angle1 = kind is Kind.Slide or Kind.Hide ? 0 : amount * Math.PI / 180,
            Offset1 = kind == Kind.Slide ? 1 : 0,
            HideValue = 0.5,
            SourceAddress = kind == Kind.Wheel ? "loop" : "clamp",
            Owner = AnimSession.Model,
        };

        bool needSelection = boneSel is null;
        bool needAxis = existingAxis is null && kind != Kind.Hide;
        if (needSelection || needAxis)
        {
            var mask = sel;
            bool ok = false;
            Dlg.Try(() => ok = Ed.Edit($"Animate {bone}", m =>
            {
                if (needSelection && mask is not null)
                    m = m.WithLod(Ed.ActiveLod, NamedSelections.Create(m.Lods[Ed.ActiveLod], bone, mask.Value));
                if (needAxis)
                {
                    int mi = Array.FindIndex(m.Lods.ToArray(), l => LodNames.IsMemory(l.Resolution));
                    if (mi < 0)
                    {
                        var lods = m.Lods.ToList();
                        mi = LodList.SortedInsertIndex(lods, 1e15f);
                        lods.Insert(mi, Lod.Empty(1e15f));
                        m = m with { Lods = [.. lods] };
                    }
                    var frag = Fragment.Allocate(2, 0) with { Selections = [new NamedWeights(axisName, MeshMath.Ones(2), [])] };
                    float[] xyz = [p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z];
                    xyz.CopyTo(frag.Positions, 0);
                    m = m.WithLod(mi, MeshMath.AppendSafe(m.Lods[mi], frag, selectNew: false));
                }
                return m;
            }));
            if (!ok) return null;
        }

        // skeleton: the part becomes a bone (moving with its parent)
        int bi = AnimSession.OwnBones.FindIndex(b => string.Equals(b.Name, bone, StringComparison.OrdinalIgnoreCase));
        if (bi >= 0)
        {
            if (parent.Length > 0) AnimSession.OwnBones[bi] = AnimSession.OwnBones[bi] with { Parent = parent };
        }
        else if (!AnimSession.IsBone(bone)) AnimSession.OwnBones.Add(new Bone(bone, parent));
        AnimSession.Own.Add(anim);
        AnimSession.Touch();
        AnimSession.SetValue(source, 1); // show it "open"
        AnimSession.SetPreview(true);
        Ed.Log($"Animation \"{name}\" ({KindLabels[(int)kind].Split(" —")[0].ToLowerInvariant()}) created for \"{bone}\"" +
               (needAxis ? $" with the axis \"{axisName}\" in the Memory LOD" : existingAxis is not null ? $" on the existing axis \"{existingAxis}\"" : "") +
               " — test it with the slider, then Save model.cfg (and the model, Ctrl+S, for the new points / selection)");
        return name;
    }

    static string? ValidateBone(string v, Lod lod, int selected)
    {
        v = v.Trim();
        if (v.Length == 0) return "name the part (it becomes a named selection and a skeleton bone)";
        if (!ValidName(v)) return "letters, digits and _ only (no spaces)";
        if (lod.Selection(v) is null && selected == 0) return $"\"{v}\" is not a named selection here — select the part in the viewport first";
        return null;
    }

    static bool ValidName(string v) => v.Trim().All(c => char.IsLetterOrDigit(c) || c == '_') && !char.IsDigit(v.Trim()[0]);

    static string UniqueAxisName(Lod? memory, string stem)
    {
        if (memory is null || memory.Selection(stem) is null) return stem;
        for (int i = 2; ; i++)
            if (memory.Selection($"{stem}_{i}") is null) return $"{stem}_{i}";
    }

    static bool SamePoints(byte[] a, byte[] b)
    {
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if ((i < a.Length && a[i] != 0) != (i < b.Length && b[i] != 0)) return false;
        return true;
    }

    /// <summary>"90°", "0.1 m" — for labels.</summary>
    public static string Deg(double rad) => (rad * 180 / Math.PI).ToString("0.#", CultureInfo.InvariantCulture) + "°";
}
