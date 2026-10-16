using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Panels.Materials;
using P3DForge.App.Shell;
using P3DForge.Core.Materials;

namespace P3DForge.App.Features;

/// <summary>
/// Materials: material editor (Shift+F12), material library (Ctrl+Shift+F12), assign / select faces by material,
/// new material from the Super template. Keeps <see cref="MaterialStore.Shared"/> pointed at Settings.ProjectRoot,
/// re-checks material files when the app is activated and asks before quitting with unsaved material edits.
/// </summary>
public sealed class MaterialsFeature : IFeature
{
    public int Order => 72;

    static EditorState Ed => AppServices.Editor;
    static MaterialEditorPanel? EditorPanel => MaterialEditorPanel.Current;

    public void Install()
    {
        MaterialStore.Shared.ProjectRootProvider = () => AppServices.Settings.ProjectRoot;

        PanelRegistry.Register(new PanelDescriptor
        {
            Id = MaterialActions.EditorPanelId,
            Title = "Material Editor",
            Location = PanelLocation.Right,
            TabWith = "objects",
            Order = 30,
            Create = () => new MaterialEditorPanel(),
            StartHidden = true,
        });
        PanelRegistry.Register(new PanelDescriptor
        {
            Id = MaterialActions.LibraryPanelId,
            Title = "Material Library",
            Location = PanelLocation.Right,
            TabWith = "objects",
            Order = 31,
            Create = () => new MaterialLibraryPanel(),
            StartHidden = true,
        });

        Commands.Register(
            new Command
            {
                Id = "window.material",
                Label = "Material _Editor (rvmat, live preview)",
                Hotkeys = ["Ctrl+Shift+M"],
                Execute = OpenEditor,
                Description = "Edit the .rvmat of the selected faces (shaders, colours, flags, stages, UV transforms) with live preview",
            },
            new Command
            {
                Id = "window.materialLibrary",
                Label = "Material _Library",
                Hotkeys = ["Ctrl+Shift+F12"],
                Execute = () => PanelRegistry.Focus(MaterialActions.LibraryPanelId),
                Description = "Browse the project's .rvmat files with preview spheres; double-click assigns to the selected faces",
            },
            new Command
            {
                Id = "material.editSelected",
                Label = "_Edit Material (rvmat) of Selection…",
                Execute = () =>
                {
                    var m = MaterialActions.SelectedFacesMaterial();
                    if (m is null)
                    {
                        Ed.Log("The selected faces have no material", LogLevel.Warn);
                        return;
                    }
                    MaterialActions.OpenInEditor(m);
                },
                CanExecute = () => Ed.Model.Lods.Length > 0,
                Description = "Opens the material of the selected faces in the material editor",
            },
            new Command
            {
                Id = "material.assign",
                Label = "_Assign Edited Material to Selection",
                Execute = () =>
                {
                    if (EditorPanel?.MaterialPath is { } p) MaterialActions.AssignToSelectedFaces(p);
                    else Ed.Log("Open a material in the material editor first", LogLevel.Warn);
                },
                CanExecute = () => Ed.Model.Lods.Length > 0 && EditorPanel?.MaterialPath is not null,
                Description = "Sets the material shown in the material editor on the selected faces",
            },
            new Command
            {
                Id = "material.selectFaces",
                Label = "_Select Faces Using Edited Material",
                Execute = () =>
                {
                    if (EditorPanel?.MaterialPath is { } p) MaterialActions.SelectFacesUsing(p);
                },
                CanExecute = () => Ed.Model.Lods.Length > 0 && EditorPanel?.MaterialPath is not null,
                Description = "Selects the faces of the active LOD that use the material shown in the material editor",
            },
            new Command
            {
                Id = "material.new",
                Label = "_New Material…",
                Execute = () =>
                {
                    PanelRegistry.Focus(MaterialActions.EditorPanelId);
                    Dispatch(() => EditorPanel?.NewMaterial());
                },
                Description = "Creates a DayZ Super material (stage textures follow the selected faces' texture names)",
            },
            new Command { Id = "material.save", Label = "Save Material", Execute = () => EditorPanel?.Save(), CanExecute = () => EditorPanel?.MaterialPath is not null },
            new Command { Id = "material.saveAs", Label = "Save Material As…", Execute = () => EditorPanel?.SaveAs(), CanExecute = () => EditorPanel?.MaterialPath is not null },
            new Command { Id = "material.revert", Label = "Revert Material", Execute = () => EditorPanel?.Revert(), CanExecute = () => EditorPanel?.IsEdited == true },
            new Command { Id = "material.reload", Label = "Reload Material", Execute = () => EditorPanel?.Reload(), CanExecute = () => EditorPanel?.MaterialPath is not null });

        var app = Application.Current;
        if (app is null) return;
        app.Activated += (_, _) =>
        {
            try
            {
                MaterialStore.Shared.CheckForChanges();
            }
            catch (Exception ex)
            {
                Ed.Log($"Material check: {ex.Message}", LogLevel.Warn);
            }
        };
        app.Dispatcher.BeginInvoke(() =>
        {
            if (app.MainWindow is { } w) w.Closing += OnMainWindowClosing;
        }, DispatcherPriority.Loaded);
    }

    static void OpenEditor()
    {
        MaterialActions.EnsureFinalView();
        var created = EditorPanel is null;
        PanelRegistry.Focus(MaterialActions.EditorPanelId);
        Dispatch(() =>
        {
            if (EditorPanel is not { } p) return;
            // preselect the selected faces' material when opening (not when the panel was already showing one)
            if (created || p.MaterialPath is null || !p.IsVisible)
            {
                var m = MaterialActions.SelectedFacesMaterial();
                if (m is not null) p.Open(m);
                else if (p.MaterialPath is null) p.OpenDefault();
            }
        });
    }

    static void Dispatch(Action a) => Application.Current?.Dispatcher.BeginInvoke(a, DispatcherPriority.Background);

    static void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel) return;
        var edited = MaterialStore.Shared.OverrideKeys;
        if (edited.Count == 0) return;
        var names = string.Join("\n", edited.Take(8).Select(System.IO.Path.GetFileName));
        if (!Dlg.Confirm("Unsaved materials", $"{edited.Count} material(s) have unsaved edits:\n{names}\n\nQuit and discard them?", "Discard and quit"))
            e.Cancel = true;
    }
}
