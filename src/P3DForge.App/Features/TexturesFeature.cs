using P3DForge.App.Dialogs.Textures;
using P3DForge.App.Editor;
using P3DForge.App.Panels.Textures;
using P3DForge.App.Shell;

namespace P3DForge.App.Features;

/// <summary>
/// Texture tools: the Textures panel (window.textures, Ctrl+Shift+T), "Convert to PAA…", "Convert Folder to PAA…" and
/// "Export PAA to PNG/TGA…". Cross-feature signals (UI flags): "textures.openPath" (set by others: open that texture
/// in the panel) and "textures.changed" (set here after every write: the saved path, for viewport reloads).
/// </summary>
public sealed class TexturesFeature : IFeature
{
    public int Order => 70;

    public const string PanelId = "textures";

    static EditorState Ed => AppServices.Editor;

    string? lastOpenRequest;

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = PanelId, Title = "Textures", Location = PanelLocation.Right, TabWith = "objects", Order = 40, Create = () => new TexturesPanel(), StartHidden = true });
        Commands.Register(
            new Command
            {
                Id = "window.textures",
                Label = "_Textures (PAA tools)",
                Hotkeys = ["Ctrl+Shift+T"],
                Execute = () => PanelRegistry.Focus(PanelId),
                Description = "Texture tools: open .paa / images, channel preview, adjustments, save .paa / .png",
            },
            new Command
            {
                Id = "textures.open",
                Label = "Open _Texture…",
                Execute = () =>
                {
                    PanelRegistry.Focus(PanelId);
                    TexturesPanel.Current?.OpenWithDialog();
                },
                Description = "Opens a .paa / .png / .tga / .jpg in the texture tools",
            },
            new Command
            {
                Id = "textures.save",
                Label = "Save Texture",
                Execute = () => TexturesPanel.Current?.Save(null),
                CanExecute = () => TexturesPanel.Current?.CanSave == true,
                Description = "Overwrites the texture open in the texture tools (.bak when backups are on)",
            },
            new Command
            {
                Id = "textures.saveAs",
                Label = "Save Texture As…",
                Execute = () => TexturesPanel.Current?.SaveAsCommand(),
                CanExecute = () => TexturesPanel.Current?.CanSave == true,
                Description = "Saves the texture tools image as .paa (format by suffix), .png or .tga",
            },
            new Command
            {
                Id = "textures.reset",
                Label = "Reset Texture Adjustments",
                Execute = () => TexturesPanel.Current?.ResetCommand(),
                CanExecute = () => TexturesPanel.Current?.CanSave == true,
            },
            new Command
            {
                Id = "textures.convertToPaa",
                Label = "_Convert to PAA…",
                Execute = ConvertDialog.ConvertFiles,
                Description = "Converts PNG / TGA / JPG / BMP images to .paa (format, swizzle and mips by suffix like ImageToPAA)",
            },
            new Command
            {
                Id = "textures.convertFolder",
                Label = "Convert _Folder to PAA…",
                Execute = ConvertDialog.ConvertFolder,
                Description = "Converts every image of a folder (optionally with subfolders) to .paa in the background",
            },
            new Command
            {
                Id = "textures.exportPaa",
                Label = "_Export PAA to PNG/TGA…",
                Execute = ConvertDialog.ExportPaa,
                Description = "Writes the largest mip of .paa textures as .png or .tga",
            });

        Ed.Changed += c =>
        {
            if ((c & Change.Ui) == 0) return;
            var p = Ed.Flag<string?>(TexturesPanel.OpenFlag, null);
            if (string.IsNullOrWhiteSpace(p) || p == lastOpenRequest) return;
            lastOpenRequest = p;
            PanelRegistry.Focus(PanelId); // creates the panel on first use; the panel itself loads the flag's path
        };
    }
}
