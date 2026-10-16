using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Panels;
using P3DForge.App.Shell;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>Named Properties panel + commands.</summary>
public sealed class PropertiesFeature : IFeature
{
    public int Order => 30;

    public const string PanelId = "namedProperties";

    static EditorState Ed => AppServices.Editor;

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = PanelId, Title = "Named Properties", Location = PanelLocation.Left, Order = 30, Create = () => new PropertiesPanel() });
        Commands.Register(
            new Command { Id = "window.namedProperties", Label = "Named _Properties", Hotkeys = ["Ctrl+1", "Shift+F8"], Execute = () => PanelRegistry.Focus(PanelId) },
            new Command { Id = "namedProperty.set", Label = "Set Named Property (key=value)…", Execute = SetPrompt, Description = "Sets a #Property# of the active LOD" },
            new Command { Id = "namedProperty.delete", Label = "Delete Named Property…", Execute = DeletePrompt, CanExecute = () => Ed.Lod.Properties.Any() });
    }

    /// <summary>Prompts "key=value" and sets it on the active LOD (automation: "automation.answer").</summary>
    static void SetPrompt()
    {
        static string? Validate(string s)
        {
            int eq = s.IndexOf('=');
            if (eq < 0) return "Use key=value";
            return NamedProperties.ValidateKey(s[..eq].Trim()) ?? NamedProperties.ValidateValue(s[(eq + 1)..].Trim());
        }
        var text = Dlg.Prompt("Set Named Property", "key=value:", "", Validate, "Set");
        if (text is null) return;
        int eq = text.IndexOf('=');
        string key = text[..eq].Trim(), value = text[(eq + 1)..].Trim();
        Dlg.Try(() => Ed.EditLod($"Property {key} = \"{value}\"", l => NamedProperties.Set(l, key, value)));
    }

    static void DeletePrompt()
    {
        var key = Dlg.Prompt("Delete Named Property", "Key:", Ed.Lod.Properties.FirstOrDefault()?.Key ?? "",
            k => NamedProperties.Find(Ed.Lod, k) < 0 ? $"No property \"{k}\"" : null, "Delete");
        if (key is null) return;
        Dlg.Try(() => Ed.EditLod($"Delete property \"{key}\"", l => NamedProperties.Delete(l, key)));
    }
}
