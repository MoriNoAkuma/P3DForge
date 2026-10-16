using P3DForge.App.Dialogs.Resources;
using P3DForge.App.Editor;
using P3DForge.App.Panels;
using P3DForge.App.Shell;

namespace P3DForge.App.Features;

/// <summary>
/// Inspector panels: Current Position / Inspector (Shift+F3) with Measuring (Shift+F11), Resource Library
/// (Shift+F10) and Mass Texture &amp; Material Renaming (Edit menu).
/// </summary>
public sealed class InspectorPanelsFeature : IFeature
{
    public int Order => 61;

    public const string InspectorPanelId = "inspector";
    public const string ResourcesPanelId = "resources";

    static EditorState Ed => AppServices.Editor;

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = InspectorPanelId, Title = "Inspector", Location = PanelLocation.Right, Order = 10, Create = () => new InspectorPanel() });
        PanelRegistry.Register(new PanelDescriptor { Id = ResourcesPanelId, Title = "Resource Library", Location = PanelLocation.Right, TabWith = "objects", Order = 20, Create = () => new ResourcesPanel(), StartHidden = true });
        Commands.Register(
            new Command
            {
                Id = "window.currentPosition",
                Label = "Current _Position / Inspector",
                Hotkeys = ["Shift+F3"],
                Execute = () => PanelRegistry.Focus(InspectorPanelId),
                Description = "Cursor / pin position, active LOD, selection centroid and bounds",
            },
            new Command
            {
                Id = "tools.measure",
                Label = "_Measuring",
                Hotkeys = ["Shift+F11"],
                Execute = () =>
                {
                    Ed.SetFlag(InspectorPanel.MeasureFlag, true);
                    PanelRegistry.Focus(InspectorPanelId);
                    InspectorPanel.Current?.RevealMeasuring();
                },
                Description = "Distance (2 points), angles (3 points), edge length and face area of the selection",
            },
            new Command
            {
                Id = "window.resources",
                Label = "_Resource Library (textures/materials/proxies)",
                Hotkeys = ["Shift+F10"],
                Execute = () => PanelRegistry.Focus(ResourcesPanelId),
                Description = "Textures, materials and proxies of the model + project browser",
            },
            new Command
            {
                Id = "edit.findReplacePaths",
                Label = "Mass Texture & Material _Renaming…",
                Execute = () => MassRenameDialog.Show(),
                CanExecute = () => Ed.Model.Lods.Length > 0,
                Description = "Renames texture / material paths in every LOD (mask, prefix or substring) with a preview",
            });
    }
}
