using System.Windows;
using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Selection;
using P3DForge.App.Editor;
using P3DForge.App.Panels;
using P3DForge.App.Shell;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using CoreSelection = P3DForge.Core.Ops.Selection;

namespace P3DForge.App.Features;

/// <summary>Named Selections panel + its commands (the panel's context menu calls the same actions).</summary>
public sealed class SelectionsFeature : IFeature
{
    public int Order => 20;

    public const string PanelId = "namedSelections";

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = PanelId, Title = "Named Selections", Location = PanelLocation.Left, Order = 20, Create = () => new SelectionsPanel() });

        static SelRow? T() => NamedSelectionActions.Target();
        static bool HasT() => T() is not null;
        static bool HasSel() => CoreSelection.PeekSelected(AppServices.Editor.Lod) is { IsEmpty: false };

        Commands.Register(
            new Command { Id = "window.namedSelections", Label = "Named _Selections", Hotkeys = ["Shift+F5"], Execute = () => PanelRegistry.Focus(PanelId) },
            new Command { Id = "namedSelection.new", Label = "New Named Selection…", Execute = NamedSelectionActions.New, Description = "Creates a named selection from the current selection" },
            new Command { Id = "namedSelection.delete", Label = "Delete Named Selection", Execute = () => NamedSelectionActions.Delete(T()!, confirm: false), CanExecute = HasT },
            new Command { Id = "namedSelection.deleteAllLods", Label = "Delete Named Selection (All LODs)", Execute = () => NamedSelectionActions.DeleteAllLods(T()!), CanExecute = HasT },
            new Command { Id = "namedSelection.rename", Label = "Rename Named Selection…", Execute = () => NamedSelectionActions.Rename(T()!), CanExecute = HasT },
            new Command { Id = "namedSelection.renameAllLods", Label = "Rename Named Selection (All LODs)…", Execute = () => NamedSelectionActions.RenameAllLods(T()!), CanExecute = HasT },
            new Command { Id = "namedSelection.deleteEmpty", Label = "Delete Empty Named Selections", Execute = NamedSelectionActions.DeleteEmpty },
            new Command { Id = "namedSelection.redefine", Label = "Redefine Named Selection", Execute = () => NamedSelectionActions.Redefine(T()!), CanExecute = HasT },
            new Command { Id = "namedSelection.addSelected", Label = "Add Selected to Named Selection", Execute = () => NamedSelectionActions.AddSelected(T()!), CanExecute = () => HasT() && HasSel() },
            new Command { Id = "namedSelection.removeSelected", Label = "Remove Selected from Named Selection", Execute = () => NamedSelectionActions.RemoveSelected(T()!), CanExecute = () => HasT() && HasSel() },
            new Command { Id = "namedSelection.weights", Label = "Named Selection Weights…", Execute = () => NamedSelectionActions.Weights(T()!), CanExecute = () => HasT() && HasSel() },
            new Command
            {
                Id = "namedSelection.showWeights",
                Label = "Show Weights of Named Selection",
                Execute = () => NamedSelectionActions.ToggleShowWeights(T()!),
                CanExecute = HasT,
                IsChecked = () => T() is { } r && NamedSelectionActions.ShowingWeights(r.Name),
            },
            new Command { Id = "namedSelection.copyName", Label = "Copy Named Selection Name", Execute = () => NamedSelectionActions.CopyName(T()!), CanExecute = HasT });
    }
}

/// <summary>Named-selection actions on the active LOD (shared by the panel and the commands).</summary>
public static class NamedSelectionActions
{
    static EditorState Ed => AppServices.Editor;

    /// <summary>Flag holding the name of the selection whose weights the viewport should visualize (null = off).</summary>
    public const string ShowWeightsFlag = "view.showWeights";

    /// <summary>The row the commands act on: the panel's current row.</summary>
    public static SelRow? Target() => SelectionsPanel.Instance?.Current;

    static string? NameError(Lod lod, string name, int exceptTagIndex = -1) =>
        NamedSelections.Validate(name) ?? (NamedSelections.Exists(lod, name, exceptTagIndex) ? $"A selection named \"{name}\" already exists" : null);

    /// <summary>Selects the row's contents (Set/Add/Subtract), honouring hidden/locked points like the viewport.</summary>
    public static void Select(SelRow row, SelectOp op)
    {
        var lod = Ed.Lod;
        if (NamedSelections.Find(lod, row.Name) == row.TagIndex)
        {
            SelectionOps.SelectNamed(row.Name, op);
            return;
        }
        // A duplicate name (real files have them): SelectNamed would pick the first tag, so combine here.
        var cur = CoreSelection.GetSelected(lod);
        var t = row.Tag;
        var ok = CoreSelection.SelectablePoints(lod);
        var pts = Combine(cur.Points, t.Points, op);
        var faces = Combine(cur.Faces, t.Faces, op);
        for (int i = 0; i < pts.Length; i++)
            if (ok[i] == 0) pts[i] = 0;
        Ed.SetSelection(new SelectionMask(pts, faces), $"Select \"{t.Name}\"");
    }

    static byte[] Combine(byte[] cur, byte[] mask, SelectOp op)
    {
        var o = op == SelectOp.Set ? new byte[cur.Length] : (byte[])cur.Clone();
        for (int i = 0; i < mask.Length && i < o.Length; i++)
            if (mask[i] != 0) o[i] = op == SelectOp.Subtract ? (byte)0 : (byte)1;
        return o;
    }

    public static void New()
    {
        var lod = Ed.Lod;
        var name = Dlg.Prompt("New Named Selection", "Name:", NamedSelections.UniqueName(lod, "Selection"), n => NameError(Ed.Lod, n), "Create");
        if (name is null) return;
        var mask = Ed.GetSelection();
        Dlg.Try(() =>
        {
            if (Ed.EditLod($"New selection \"{name}\"", l => NamedSelections.Create(l, name, mask)))
                Ed.SetFlag(SelectionsPanel.FocusFlag, name);
        });
    }

    public static void Delete(SelRow row, bool confirm)
    {
        if (confirm && !Dlg.Confirm("Delete Named Selection", $"Delete the named selection \"{row.Name}\" from this LOD?", "Delete")) return;
        Dlg.Try(() => Ed.EditLod($"Delete selection \"{row.Name}\"", l => NamedSelections.DeleteAt(l, row.TagIndex)));
    }

    public static void DeleteAllLods(SelRow row)
    {
        int n = NamedSelections.LodsWith(Ed.Model, row.Name).Length;
        if (!Dlg.Confirm("Delete Named Selection (All LODs)", $"Delete the named selection \"{row.Name}\" from {n} LOD{(n == 1 ? "" : "s")}?", "Delete")) return;
        Dlg.Try(() => Ed.Edit($"Delete selection \"{row.Name}\" (all LODs)", m => NamedSelections.DeleteAll(m, row.Name)));
    }

    public static void Rename(SelRow row)
    {
        var name = Dlg.Prompt("Rename Named Selection", $"New name for \"{row.Name}\":", row.Name, n => NameError(Ed.Lod, n, row.TagIndex), "Rename");
        if (name is null || name == row.Name) return;
        Dlg.Try(() =>
        {
            if (Ed.EditLod($"Rename selection \"{row.Name}\" → \"{name}\"", l => NamedSelections.Rename(l, row.TagIndex, name)))
                Ed.SetFlag(SelectionsPanel.FocusFlag, name);
        });
    }

    public static void RenameAllLods(SelRow row)
    {
        var old = row.Name;
        string? Validate(string n)
        {
            if (NamedSelections.Validate(n) is { } err) return err;
            var m = Ed.Model;
            for (int i = 0; i < m.Lods.Length; i++)
            {
                int idx = NamedSelections.Find(m.Lods[i], old);
                if (idx >= 0 && NamedSelections.Exists(m.Lods[i], n, idx))
                    return $"\"{n}\" already exists in LOD {P3DForge.Core.LodNames.Name(m.Lods[i].Resolution)}";
            }
            return null;
        }
        int count = NamedSelections.LodsWith(Ed.Model, old).Length;
        var name = Dlg.Prompt("Rename Named Selection (All LODs)", $"New name for \"{old}\" in {count} LOD{(count == 1 ? "" : "s")}:", old, Validate, "Rename");
        if (name is null || name == old) return;
        Dlg.Try(() =>
        {
            if (Ed.Edit($"Rename selection \"{old}\" → \"{name}\" (all LODs)", m => NamedSelections.RenameAll(m, old, name)))
                Ed.SetFlag(SelectionsPanel.FocusFlag, name);
        });
    }

    public static void DeleteEmpty()
    {
        int before = Ed.Lod.Selections.Count();
        Dlg.Try(() =>
        {
            if (Ed.EditLod("Delete empty selections", NamedSelections.DeleteEmpty))
                Ed.Log($"Deleted {before - Ed.Lod.Selections.Count()} empty named selection(s)");
            else Ed.Log("No empty named selections");
        });
    }

    public static void Redefine(SelRow row)
    {
        var mask = Ed.GetSelection();
        Dlg.Try(() => Ed.EditLod($"Redefine selection \"{row.Name}\"", l => NamedSelections.Redefine(l, row.TagIndex, mask)));
    }

    public static void AddSelected(SelRow row)
    {
        var mask = Ed.GetSelection();
        Dlg.Try(() => Ed.EditLod($"Add to selection \"{row.Name}\"", l => NamedSelections.Add(l, row.TagIndex, mask)));
    }

    public static void RemoveSelected(SelRow row)
    {
        var mask = Ed.GetSelection();
        Dlg.Try(() => Ed.EditLod($"Remove from selection \"{row.Name}\"", l => NamedSelections.Remove(l, row.TagIndex, mask)));
    }

    public static void Weights(SelRow row)
    {
        var mask = Ed.GetSelection();
        if (mask.PointCount == 0)
        {
            Dlg.Fail("Weights: select the points whose weight should change first");
            return;
        }
        var w = WeightsDialog.Ask(row, mask.Points);
        if (w is null) return;
        Dlg.Try(() => Ed.EditLod($"Weights of \"{row.Name}\" = {w.Value:0.###}", l => NamedSelections.SetWeight(l, row.TagIndex, mask.Points, w.Value)));
    }

    public static bool ShowingWeights(string name) =>
        string.Equals(Ed.Flag<string?>(ShowWeightsFlag, null), name, StringComparison.OrdinalIgnoreCase);

    public static void ToggleShowWeights(SelRow row) => Ed.SetFlag(ShowWeightsFlag, ShowingWeights(row.Name) ? null : row.Name);

    public static void CopyName(SelRow row)
    {
        try
        {
            Clipboard.SetText(row.Name);
            Ed.Log($"Copied \"{row.Name}\"");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            Dlg.Fail("Clipboard is busy: " + ex.Message);
        }
    }
}
