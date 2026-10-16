using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Lod;
using P3DForge.App.Editor;
using P3DForge.App.Panels;
using P3DForge.App.Shell;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>LOD list panel + LOD commands (Object Builder "LOD" menu / LODs window).</summary>
public sealed class LodFeature : IFeature
{
    public int Order => 10;

    public const string PanelId = "lods";

    /// <summary>UI flag: index (int) of the LOD the viewport should draw at the background, or null.</summary>
    public const string BackgroundFlag = "view.backgroundLod";

    static EditorState Ed => AppServices.Editor;

    /// <summary>Index of the background LOD, or -1.</summary>
    public static int BackgroundLod => Ed.Flag<object?>(BackgroundFlag, null) switch
    {
        int i when i >= 0 && i < Ed.Model.Lods.Length => i,
        double d when d >= 0 && d < Ed.Model.Lods.Length => (int)d,
        _ => -1,
    };

    /// <summary>UI flag (default on): with no background LOD chosen, the first LOD is drawn as a yellow frame in the others (Object Builder).</summary>
    public const string FirstLodFlag = "view.firstLodBackground";

    /// <summary>The LOD actually drawn at the background: the chosen one, else the first LOD when <see cref="FirstLodFlag"/> is on; -1 for none.</summary>
    public static int EffectiveBackgroundLod => BackgroundLod >= 0 ? BackgroundLod : Ed.Flag(FirstLodFlag, true) && Ed.Model.Lods.Length > 1 ? 0 : -1;

    static void SetBackground(int index) => Ed.SetFlag(BackgroundFlag, index >= 0 ? index : null);

    /// <summary>Stops drawing a background LOD (also the automatic first-LOD frame).</summary>
    public static void ClearBackground()
    {
        SetBackground(-1);
        Ed.SetFlag(FirstLodFlag, false);
    }

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = PanelId, Title = "LODs", Location = PanelLocation.Left, Order = 10, Create = () => new LodsPanel() });
        Commands.Register(
            new Command { Id = "window.lods", Label = "_LODs", Hotkeys = ["Shift+F6"], Execute = () => PanelRegistry.Focus(PanelId) },
            new Command { Id = "lod.new", Label = "New LOD…", Execute = New },
            new Command { Id = "lod.duplicate", Label = "Duplicate LOD", Execute = Duplicate },
            new Command { Id = "lod.delete", Label = "Delete LOD", Execute = Delete, CanExecute = () => Ed.Model.Lods.Length > 1 },
            new Command { Id = "lod.properties", Label = "LOD Properties (resolution)…", Execute = Properties },
            new Command { Id = "lod.copySelectionTo", Label = "Copy Selection to LODs…", Execute = CopySelectionTo, CanExecute = () => Ed.Model.Lods.Length > 1 && Selection.PeekSelected(Ed.Lod) is { IsEmpty: false } },
            new Command { Id = "lod.sort", Label = "Sort LODs", Execute = Sort, CanExecute = () => !LodList.IsSorted(Ed.Model) },
            new Command { Id = "lod.prev", Label = "Previous LOD", Hotkeys = ["["], Execute = () => Ed.SetActiveLod(Ed.ActiveLod - 1), CanExecute = () => Ed.ActiveLod > 0 },
            new Command { Id = "lod.next", Label = "Next LOD", Hotkeys = ["]"], Execute = () => Ed.SetActiveLod(Ed.ActiveLod + 1), CanExecute = () => Ed.ActiveLod < Ed.Model.Lods.Length - 1 },
            new Command
            {
                Id = "lod.showAtBackground",
                Label = "Show LOD at Background",
                Execute = () =>
                {
                    if (EffectiveBackgroundLod != Ed.ActiveLod) SetBackground(Ed.ActiveLod);
                    else if (Ed.ActiveLod == 0) ClearBackground();
                    else SetBackground(-1);
                },
                IsChecked = () => EffectiveBackgroundLod == Ed.ActiveLod,
            },
            new Command
            {
                Id = "view.firstLodBackground",
                Label = "Show _First LOD at Background (yellow frame)",
                Description = "When no background LOD is chosen, the first LOD is drawn as yellow lines in the other LODs",
                IsChecked = () => Ed.Flag(FirstLodFlag, true),
                Execute = () => Ed.SetFlag(FirstLodFlag, !Ed.Flag(FirstLodFlag, true)),
            });
        Ed.Changed += c =>
        {
            if ((c & Change.Document) == 0) return;
            // Per-document view flags do not carry over to a newly loaded document.
            if ((c & Change.Load) != 0)
            {
                if (Ed.Flag<object?>(BackgroundFlag, null) is not null) SetBackground(-1);
                if (Ed.Flag<string?>(NamedSelectionActions.ShowWeightsFlag, null) is not null) Ed.SetFlag(NamedSelectionActions.ShowWeightsFlag, null);
            }
            else if (Ed.Flag<object?>(BackgroundFlag, null) is not null && BackgroundLod < 0) SetBackground(-1);
        };
    }

    /// <summary>Keeps the background-LOD flag pointing at the same LOD after the list was reordered.</summary>
    static void RemapBackground(Func<int, int> map)
    {
        int bg = BackgroundLod;
        if (bg >= 0) SetBackground(map(bg));
    }

    static void New()
    {
        var model = Ed.Model;
        var r = LodPropertiesDialog.Ask("New LOD", model, LodList.DefaultNewResolution(model), okText: "Create");
        if (r is null) return;
        Dlg.Try(() =>
        {
            int index = -1;
            if (!Ed.Edit($"New LOD {LodNames.Name(r.Value)}", m =>
                {
                    var (next, i) = LodList.Insert(m, Lod.Empty(r.Value), Ed.ActiveLod);
                    index = i;
                    return next;
                })) return;
            RemapBackground(b => b >= index ? b + 1 : b);
            Ed.SetActiveLod(index);
        });
    }

    static void Duplicate()
    {
        var src = Ed.Lod;
        Dlg.Try(() =>
        {
            float r = LodList.FreeResolutionLike(Ed.Model, src.Resolution);
            int index = -1;
            if (!Ed.Edit($"Duplicate LOD {LodNames.Name(src.Resolution)} → {LodNames.Name(r)}", m =>
                {
                    var (next, i) = LodList.Insert(m, src with { Resolution = r }, Ed.ActiveLod);
                    index = i;
                    return next;
                })) return;
            RemapBackground(b => b >= index ? b + 1 : b);
            Ed.SetActiveLod(index);
        });
    }

    static void Delete()
    {
        var lod = Ed.Lod;
        int index = Ed.ActiveLod;
        if (Ed.Model.Lods.Length <= 1)
        {
            Dlg.Fail("A model must keep at least one LOD");
            return;
        }
        bool empty = lod.PointCount == 0 && lod.FaceCount == 0 && !lod.Selections.Any() && !lod.Properties.Any();
        if (!empty && !Dlg.Confirm("Delete LOD", $"Delete LOD {LodNames.Name(lod.Resolution)} ({lod.PointCount:N0} points, {lod.FaceCount:N0} faces)?", "Delete")) return;
        Dlg.Try(() =>
        {
            if (!Ed.Edit($"Delete LOD {LodNames.Name(lod.Resolution)}", m => LodList.Remove(m, index))) return;
            RemapBackground(b => b == index ? -1 : b > index ? b - 1 : b);
        });
    }

    static void Properties()
    {
        var lod = Ed.Lod;
        int index = Ed.ActiveLod;
        var r = LodPropertiesDialog.Ask($"LOD Properties — {LodNames.Name(lod.Resolution)}", Ed.Model, lod.Resolution, index);
        if (r is null || LodList.SameResolution(r.Value, lod.Resolution)) return;
        Dlg.Try(() =>
        {
            int at = index;
            if (!Ed.Edit($"LOD {LodNames.Name(lod.Resolution)} → {LodNames.Name(r.Value)}", m =>
                {
                    var (next, i) = LodList.SetResolution(m, index, r.Value);
                    at = i;
                    return next;
                })) return;
            if (at != index)
                RemapBackground(b => b == index ? at : (b > index && b <= at) ? b - 1 : (b < index && b >= at) ? b + 1 : b);
            Ed.SetActiveLod(at);
        });
    }

    static void CopySelectionTo()
    {
        var model = Ed.Model;
        int source = Ed.ActiveLod;
        var mask = Ed.GetSelection();
        if (mask.IsEmpty)
        {
            Dlg.Fail("Copy Selection to LODs: nothing is selected");
            return;
        }
        var faces = mask.FaceCount;
        var pts = SelectionMask.CountNonZero(Selection.Combine(mask.Points, Structure.PointsUsedByFaces(Ed.Lod, mask.Faces), MaskOp.Add));
        var targets = CopySelectionDialog.Ask(model, source, pts, faces);
        if (targets is null || targets.Length == 0) return;
        Dlg.Try(() =>
        {
            if (Ed.Edit($"Copy selection to {targets.Length} LOD{(targets.Length == 1 ? "" : "s")}", m => LodList.CopySelectionTo(m, source, targets, mask)))
                Ed.Log($"Copied {pts:N0} points / {faces:N0} faces to {string.Join(", ", targets.Select(t => LodNames.Name(Ed.Model.Lods[t].Resolution)))}");
        });
    }

    static void Sort()
    {
        var before = Ed.Model;
        int active = Ed.ActiveLod;
        int newActive = active;
        Dlg.Try(() =>
        {
            if (!Ed.Edit("Sort LODs", m =>
                {
                    var (next, a) = LodList.Sort(m, active);
                    newActive = a;
                    return next;
                })) return;
            var after = Ed.Model;
            RemapBackground(b => after.Lods.IndexOf(before.Lods[b], 0, ReferenceEqualityComparer.Instance));
            Ed.SetActiveLod(newActive);
        });
    }
}
