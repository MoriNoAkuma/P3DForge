using System.Diagnostics;
using System.IO;
using System.Windows;
using P3DForge.App.Editor;
using P3DForge.App.Services;
using P3DForge.App.Shell;
using P3DForge.Core.Model;

namespace P3DForge.App.Features;

/// <summary>File menu: new, open, recent, save, save as, revert, show in Explorer, new window, exit.</summary>
public sealed class FileFeature : IFeature
{
    public int Order => 0;
    static EditorState Ed => AppServices.Editor;

    public void Install()
    {
        Commands.Register(
            new Command
            {
                Id = "file.new",
                Label = "_New",
                Hotkeys = ["Ctrl+N"],
                Execute = () =>
                {
                    if (!FileService.ConfirmDiscard("create a new model")) return;
                    FileService.ForgetConverted();
                    Ed.Load(P3DModel.Empty(), null);
                    Ed.Log("New model");
                },
            },
            new Command { Id = "file.open", Label = "_Open…", Hotkeys = ["Ctrl+O"], Execute = FileService.OpenWithDialog },
            new Command { Id = "file.save", Label = "_Save", Hotkeys = ["Ctrl+S"], Execute = () => FileService.Save() },
            new Command { Id = "file.saveAs", Label = "Save _As…", Hotkeys = ["Ctrl+Shift+S"], Execute = () => FileService.SaveAs() },
            new Command
            {
                Id = "file.revert",
                Label = "Revert to Saved",
                CanExecute = () => Ed.Path is not null && Ed.Dirty,
                Execute = () =>
                {
                    if (Ed.Path is null) return;
                    if (MessageBox.Show("Discard all changes and reload the file from disk?", "Revert", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                        FileService.Open(Ed.Path);
                },
            },
            new Command
            {
                Id = "file.reload",
                Label = "Reload from Disk",
                Hotkeys = ["Ctrl+Shift+R"],
                Description = "Reloads the model from disk (asks when there are unsaved changes) and the proxy models changed on disk",
                CanExecute = () => Ed.Path is not null,
                Execute = ReloadFromDisk,
            },
            new Command
            {
                Id = "file.reveal",
                Label = "Show in Explorer",
                CanExecute = () => Ed.Path is not null,
                Execute = () => Process.Start("explorer.exe", $"/select,\"{Ed.Path}\""),
            },
            new Command
            {
                Id = "file.newWindow",
                Label = "New _Window",
                Hotkeys = ["Ctrl+Shift+N"],
                Execute = () => Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }),
            },
            new Command { Id = "file.exit", Label = "E_xit", Hotkeys = ["Alt+F4"], Execute = () => Application.Current.MainWindow?.Close() });

        DynamicMenus.Register("recentFiles", () => AppServices.Settings.RecentFiles.Select(path => (path, (Action)(() =>
        {
            if (!File.Exists(path))
            {
                Ed.Log($"File not found: {path}", LogLevel.Warn);
                return;
            }
            if (FileService.ConfirmDiscard("open another file")) FileService.Open(path);
        }))));

        Toolbar.Add(new ToolbarItem { Command = "file.new", Glyph = "", Group = "file", Order = 1 });
        Toolbar.Add(new ToolbarItem { Command = "file.open", Glyph = "", Group = "file", Order = 2 });
        Toolbar.Add(new ToolbarItem { Command = "file.save", Glyph = "", Group = "file", Order = 3 });
    }

    /// <summary>
    /// file.reload: re-reads the document from disk (confirming when modified) and keeps the active LOD; the proxy
    /// models changed on disk are reloaded by the proxy scene on the document load.
    /// </summary>
    static void ReloadFromDisk()
    {
        if (Ed.Path is not { } path) return;
        if (!File.Exists(path))
        {
            Ed.Log($"File not found: {path}", LogLevel.Warn);
            return;
        }
        if (Ed.Dirty && !Dialogs.Dlg.Confirm("Reload from Disk", $"Discard the unsaved changes to {Ed.FileName} and reload it from disk?", "Reload")) return;
        int lod = Ed.ActiveLod;
        if (!FileService.Open(path)) return;
        if (lod < Ed.Model.Lods.Length) Ed.SetActiveLod(lod);
    }
}
