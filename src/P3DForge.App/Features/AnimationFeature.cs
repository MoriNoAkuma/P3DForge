using P3DForge.App.Anim;
using P3DForge.App.Editor;
using P3DForge.App.Panels;
using P3DForge.App.Shell;

namespace P3DForge.App.Features;

/// <summary>
/// model.cfg animations (Tools ▸ Animations, Ctrl+Shift+A: preview with sliders, edit, save) and DayZ weapon
/// animation wiring (Tools ▸ Weapon Animations: .asi / IK .anm / states .anm, boneRemap check, writes the script call
/// and weaponStateAnim).
/// </summary>
public sealed class AnimationFeature : IFeature
{
    public const string PanelId = "animations";
    public const string WeaponPanelId = "weaponAnimations";
    public int Order => 47;
    static EditorState Ed => AppServices.Editor;

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor
        {
            Id = PanelId,
            Title = "Animations",
            Location = PanelLocation.Right,
            Order = 41,
            StartHidden = true,
            TabWith = "objects",
            Create = () => new AnimationsPanel(),
        });
        PanelRegistry.Register(new PanelDescriptor
        {
            Id = WeaponPanelId,
            Title = "Weapon Animations",
            Location = PanelLocation.Right,
            Order = 42,
            StartHidden = true,
            TabWith = "objects",
            Create = () => new WeaponAnimationsPanel(),
        });
        Commands.Register(
            new Command
            {
                Id = "tools.animations",
                Label = "_Animations (model.cfg)…",
                Hotkeys = ["Ctrl+Shift+A"],
                Description = "model.cfg animations of this model: preview them with sliders, edit bones / axes / angles, save",
                Execute = () => PanelRegistry.Focus(PanelId),
            },
            new Command
            {
                Id = "anim.animateSelection",
                Label = "_Animate Selection…",
                Description = "Makes the selected part move: door, wheel, lever, bolt or hide — creates its named selection, skeleton bone, axis and model.cfg animation",
                CanExecute = () => Core.Ops.Selection.PeekSelected(Ed.Lod) is { IsEmpty: false },
                Execute = () =>
                {
                    PanelRegistry.Focus(PanelId);
                    AnimationsPanel.Instance?.Animate();
                },
            },
            new Command
            {
                Id = "tools.weaponAnimations",
                Label = "_Weapon Animations (DayZ .asi / .anm)…",
                Description = "Which .asi, IK .anm and states .anm the weapon uses, the boneRemap → model bone check, and writing them to the script / config.cpp",
                Execute = () => PanelRegistry.Focus(WeaponPanelId),
            },
            new Command
            {
                Id = "anim.preview",
                Label = "Animation _Preview",
                Description = "Shows the model.cfg animations in the viewport at the values of the Animations panel",
                IsChecked = () => AnimSession.PreviewOn,
                Execute = () =>
                {
                    AnimSession.Refresh();
                    AnimSession.SetPreview(!AnimSession.PreviewOn);
                },
            },
            new Command
            {
                Id = "weaponAnims.writeScript",
                Label = "Weapon Animations: Write to Script",
                Description = "Adds / updates AddItemInHandsProfileIK for the weapon class of this model (Weapon Animations panel)",
                Execute = () => Panel()?.WriteScript(),
            },
            new Command
            {
                Id = "weaponAnims.writeConfig",
                Label = "Weapon Animations: Write weaponStateAnim",
                Description = "Sets weaponStateAnim of the weapon class of this model in its config.cpp (Weapon Animations panel)",
                Execute = () => Panel()?.WriteConfig(),
            },
            new Command
            {
                Id = "weaponAnims.setFiles",
                Label = "Weapon Animations: Set Files (automation)",
                Description = "Automation: flag automation.answer = \"asi=…;ik=…;states=…\" or \"like=svd\"; \"preview=states;frame=1\" plays one",
                Execute = () => Panel()?.SetFiles(Dialogs.Dlg.TakeAnswer() ?? ""),
            },
            new Command
            {
                Id = "anim.setValues",
                Label = "Set Animation Source Values (automation)",
                Description = "Automation: flag automation.answer = \"source=value;source=value\" (\"*=max\" / \"*=min\" for all); turns the preview on",
                Execute = () =>
                {
                    AnimSession.Refresh();
                    var spec = Dialogs.Dlg.TakeAnswer() ?? "";
                    foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int eq = part.IndexOf('=');
                        if (eq <= 0) continue;
                        string name = part[..eq].Trim(), v = part[(eq + 1)..].Trim();
                        foreach (var s in AnimSession.Rig?.Sources ?? [])
                            if (name == "*" || string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                                AnimSession.Values[s.Name] = v == "max" ? s.Max : v == "min" ? s.Min
                                    : double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : s.Min;
                    }
                    AnimSession.SetPreview(true);
                },
            });
        Ed.Changed += AnimSession.OnEditorChanged;
    }

    static WeaponAnimationsPanel? Panel()
    {
        PanelRegistry.Focus(WeaponPanelId);
        return WeaponAnimationsPanel.Instance;
    }
}
