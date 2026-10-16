using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>Core edit commands: undo/redo, select all/none/invert, delete.</summary>
public sealed class EditFeature : IFeature
{
    public int Order => 0;
    static EditorState Ed => AppServices.Editor;

    static bool HasSelection() => Selection.PeekSelected(Ed.Lod) is { IsEmpty: false };

    public void Install()
    {
        Commands.Register(
            new Command { Id = "edit.undo", Label = "_Undo", Hotkeys = ["Ctrl+Z", "Alt+Backspace"], CanExecute = () => Ed.CanUndo, Execute = Ed.Undo },
            new Command { Id = "edit.redo", Label = "_Redo", Hotkeys = ["Ctrl+Y", "Ctrl+Shift+Z"], CanExecute = () => Ed.CanRedo, Execute = Ed.Redo },
            new Command
            {
                Id = "edit.selectAll",
                Label = "Select _All",
                Hotkeys = ["Ctrl+A"],
                Execute = () =>
                {
                    var pts = Selection.SelectablePoints(Ed.Lod);
                    Ed.SetSelection(new SelectionMask(pts, Selection.FacesFromPoints(Ed.Lod, pts)), "Select all");
                },
            },
            new Command { Id = "edit.deselect", Label = "_Deselect", Hotkeys = ["Ctrl+D"], CanExecute = HasSelection, Execute = SelectionOps.Clear },
            new Command
            {
                Id = "edit.invertSelection",
                Label = "_Invert Selection",
                Hotkeys = ["Ctrl+I"],
                Execute = () =>
                {
                    var lod = Ed.Lod;
                    var cur = Selection.GetSelected(lod);
                    if (Ed.SelectMode == SelectMode.Faces)
                    {
                        var faces = new byte[lod.FaceCount];
                        for (int f = 0; f < faces.Length; f++) faces[f] = cur.Faces[f] == 0 ? (byte)1 : (byte)0;
                        SelectionOps.SelectFaces(faces, null, SelectOp.Set, "Invert selection");
                    }
                    else
                    {
                        var pts = new byte[lod.PointCount];
                        for (int i = 0; i < pts.Length; i++) pts[i] = cur.Points[i] == 0 ? (byte)1 : (byte)0;
                        SelectionOps.SelectPoints(pts, null, SelectOp.Set, "Invert selection");
                    }
                },
            },
            new Command
            {
                Id = "edit.delete",
                Label = "Del_ete",
                Hotkeys = ["Delete"],
                CanExecute = HasSelection,
                Execute = () =>
                {
                    var sel = Ed.GetSelection();
                    int np = sel.PointCount, nf = sel.FaceCount;
                    Ed.EditLod("Delete", lod => Structure.DeleteSelection(lod, sel.Points, sel.Faces, Ed.SelectMode == SelectMode.Faces));
                    Ed.Log($"Deleted selection ({np} points, {nf} faces)");
                },
            });

        Toolbar.Add(new ToolbarItem { Command = "edit.undo", Glyph = "", Group = "file", Order = 5 });
        Toolbar.Add(new ToolbarItem { Command = "edit.redo", Glyph = "", Group = "file", Order = 6 });
    }
}
