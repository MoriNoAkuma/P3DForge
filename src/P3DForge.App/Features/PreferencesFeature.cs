using P3DForge.App.Dialogs.Preferences;
using P3DForge.App.Editor;
using P3DForge.App.Shell;

namespace P3DForge.App.Features;

/// <summary>File ▸ Preferences… (file.preferences): work drive, viewport background / grid, texture size limit, undo limit, backups.</summary>
public sealed class PreferencesFeature : IFeature
{
    public int Order => 72;

    public void Install()
    {
        Commands.Register(new Command
        {
            Id = "file.preferences",
            Label = "_Preferences…",
            Execute = PreferencesDialog.Show,
            Description = "Work drive (project root), viewport background and grid, texture size limit, undo limit, .bak backups",
        });
    }
}
