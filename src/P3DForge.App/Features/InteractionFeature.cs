using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.App.Viewport;
using P3DForge.App.Viewport.Interaction;

namespace P3DForge.App.Features;

/// <summary>
/// Object Builder mouse editing in the viewport: selection modes (F2/O object, F3 faces, F4/V points, L lasso,
/// R move pin), axis locks (X/Y/Z), gizmo tools and the viewport plugin (<see cref="InteractionController"/>).
/// </summary>
public sealed class InteractionFeature : IFeature
{
    static EditorState Ed => AppServices.Editor;

    static Command Toggle(string id, string label, string flag, params string[] keys) => new()
    {
        Id = id,
        Label = label,
        Hotkeys = keys,
        IsChecked = () => Ed.Flag(flag, false),
        Execute = () => Ed.SetFlag(flag, !Ed.Flag(flag, false)),
    };

    /// <summary>Toggle persisted in settings (Extra[flag]).</summary>
    static Command Setting(string id, string label, string flag, string description) => new()
    {
        Id = id,
        Label = label,
        Description = description,
        IsChecked = () => Ed.Flag(flag, false),
        Execute = () =>
        {
            bool v = !Ed.Flag(flag, false);
            Ed.SetFlag(flag, v);
            AppServices.Settings.Set(flag, v);
        },
    };

    static void SetMode(SelectMode mode, bool objectMode)
    {
        Ed.Batch(() =>
        {
            Ed.SetSelectMode(mode);
            Ed.SetFlag(Interact.ObjectMode, objectMode);
            Ed.SetFlag(Interact.MovePin, false);
        });
    }

    static Command Tool(string id, string label, ToolId tool) => new()
    {
        Id = id,
        Label = label,
        IsChecked = () => Ed.Tool == tool,
        Execute = () => Ed.SetTool(tool),
    };

    public void Install()
    {
        Ed.SetFlag(Interact.SelectHidden, AppServices.Settings.Get(Interact.SelectHidden, false));
        Ed.SetFlag(Interact.UseWeights, AppServices.Settings.Get(Interact.UseWeights, false));

        Commands.Register(
            new Command
            {
                Id = "mode.selectObject",
                Label = "Select Object",
                Hotkeys = ["F2", "O"],
                Description = "Click / rectangle selects whole connected parts",
                IsChecked = () => Interact.IsObjectMode,
                Execute = () => SetMode(SelectMode.Faces, true),
            },
            new Command
            {
                Id = "mode.selectFaces",
                Label = "Select Faces",
                Hotkeys = ["F3"],
                IsChecked = () => Ed.SelectMode == SelectMode.Faces && !Interact.IsObjectMode,
                Execute = () => SetMode(SelectMode.Faces, false),
            },
            new Command
            {
                Id = "mode.selectPoints",
                Label = "Select Vertices",
                Hotkeys = ["F4", "V"],
                IsChecked = () => Ed.SelectMode == SelectMode.Points && !Interact.IsObjectMode,
                Execute = () => SetMode(SelectMode.Points, false),
            },
            new Command
            {
                Id = "mode.movePin",
                Label = "Move Pin",
                Hotkeys = ["R"],
                Description = "The next left click places the pin on a point, a face or the ground plane",
                IsChecked = () => Interact.IsMovePin,
                Execute = () => Ed.SetFlag(Interact.MovePin, !Interact.IsMovePin),
            },
            Toggle("mode.lasso", "Lasso", Interact.Lasso, "L"),
            Tool("tool.select", "Select", ToolId.Select),
            Tool("tool.move", "Move (gizmo)", ToolId.Move),
            Tool("tool.rotate", "Rotate (gizmo)", ToolId.Rotate),
            Tool("tool.scale", "Scale (gizmo)", ToolId.Scale),
            Toggle("view.lockX", "Lock X", Interact.LockX, "X"),
            Toggle("view.lockY", "Lock Y", Interact.LockY, "Y"),
            Toggle("view.lockZ", "Lock Z", Interact.LockZ, "Z"),
            Setting("interaction.selectHidden", "Select Occluded Points (X-ray)", Interact.SelectHidden,
                "Rectangle / lasso / click also select points hidden behind faces"),
            Setting("interaction.useWeights", "Soft Selection (use weights)", Interact.UseWeights,
                "Selection weights scale mouse and numeric transforms"),
            new Command
            {
                Id = "interaction.simulate",
                Label = "Simulate Mouse Gesture (debug)",
                Description = "Automation: replays flag 'automation.gesture' through the viewport mouse handlers",
                Execute = () =>
                {
                    var spec = Ed.Flag<string?>("automation.gesture", null);
                    var c = InteractionController.Current;
                    if (spec is null || c is null)
                    {
                        Ed.Log("interaction.simulate: no gesture or viewport", LogLevel.Warn);
                        return;
                    }
                    Ed.Log("[simulate] " + c.Simulate(spec));
                },
            });

        void Tb(string id, char glyph, string group, int order, string? tip = null) =>
            Toolbar.Add(new ToolbarItem { Command = id, Glyph = glyph.ToString(), Group = group, Order = order, Tooltip = tip });
        Tb("mode.selectObject", '\uF5EF', "mode", 60, "Select objects (F2 / O)");
        Tb("mode.selectFaces", '\uE73B', "mode", 61, "Select faces (F3)");
        Tb("mode.selectPoints", '\uE915', "mode", 62, "Select vertices (F4 / V)");
        Tb("mode.lasso", '\uEF20', "mode", 63, "Lasso (L)");
        Tb("mode.movePin", '\uE707', "mode", 64, "Move pin: click to place (R)");
        Tb("tool.select", '\uE8B0', "tool", 70, "Select tool");
        Tb("tool.move", '\uE7C2', "tool", 71, "Move gizmo (Ctrl = snap 0.1 m)");
        Tb("tool.rotate", '\uE7AD', "tool", 72, "Rotate gizmo (Ctrl = snap 15°)");
        Tb("tool.scale", '\uE740', "tool", 73, "Scale gizmo (Ctrl = snap 0.1×)");

        ViewportPlugins.Register(ctx =>
        {
            var c = new InteractionController(ctx);
            return c.Dispose;
        });
    }
}
