using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using P3DForge.App.Anim;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Scene;
using P3DForge.App.Services;
using P3DForge.App.Viewport;
using P3DForge.Core.Anim;
using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.App.Panels;

/// <summary>
/// Tools ▸ Weapon Animations (DayZ): the weapon classes of config.cpp that use this model (with their inheritance),
/// the .asi / IK .anm / states .anm they are registered with (ModItemRegisterCallbacks ▸ AddItemInHandsProfileIK in
/// the mod's scripts, weaponStateAnim in config.cpp), presets taken from the game's own weapons, a check that every
/// bone of the states animation reaches a skeleton bone and named selection of the model through boneRemap, and
/// buttons that write the call / the property (after showing exactly what changes), and a viewport preview that plays
/// the states .anm or one of the .asi's animations on the model (display only).
/// </summary>
public sealed class WeaponAnimationsPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 2), FontSize = 12 };
    readonly ComboBox classBox = new() { FontSize = 12, Margin = new Thickness(6, 0, 6, 2) };
    readonly TextBlock classInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 6, 2), FontSize = 12 };
    readonly ComboBox presetBox = new() { FontSize = 12, Margin = new Thickness(0, 0, 0, 2), ToolTip = "Fill the three files the way a game weapon does it" };
    readonly TextBox asiBox, ikBox, statesBox;
    readonly TextBlock linkInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 6, 2), FontSize = 12 };
    readonly StackPanel bones = new() { Margin = new Thickness(6, 2, 6, 4) };
    readonly ComboBox previewBox = new() { FontSize = 12, Margin = new Thickness(6, 0, 6, 2), ToolTip = "Plays an animation on the model through boneRemap — display only, the model is not changed" };
    readonly Slider frameSlider = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), IsEnabled = false };
    readonly ToggleButton playButton = new() { Content = "▶ Play", FontSize = 12, Padding = new Thickness(8, 1, 8, 1), IsEnabled = false, ToolTip = "Plays the animation in a loop" };
    readonly TextBlock frameText = new() { FontSize = 12, Width = 64, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
    readonly DispatcherTimer playTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    AnmClip? clip;
    List<(string ModelBone, string AnmBone)> clipLinks = [];
    int previewLod = -1;
    ConfigLibrary? library;
    List<HandsLink> links = [];
    string? loadedFor;
    bool loading;

    /// <summary>The panel (for its commands); null until it was opened once.</summary>
    public static WeaponAnimationsPanel? Instance { get; private set; }

    public WeaponAnimationsPanel()
    {
        Instance = this;
        Background = Brushes.Transparent;
        var content = new StackPanel();
        Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        content.Children.Add(status);
        content.Children.Add(Section("Weapon class (config.cpp)"));
        classBox.SelectionChanged += (_, _) =>
        {
            if (!loading) ShowClass();
        };
        content.Children.Add(classBox);
        content.Children.Add(classInfo);

        content.Children.Add(Section("Hands animation (script) and weapon states"));
        var form = new Grid { Margin = new Thickness(6, 0, 6, 2) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        presetBox.SelectionChanged += (_, _) => ApplyPreset();
        AddRow(form, "Like", presetBox, null);
        asiBox = Field();
        ikBox = Field();
        statesBox = Field();
        AddRow(form, ".asi", asiBox, () => Pick(asiBox, AnimFiles.Asi, "Animation set (.asi) — how the player holds and uses the weapon"));
        AddRow(form, "IK .anm", ikBox, () => Pick(ikBox, AnimFiles.Ik, "Hand IK pose (.anm)"));
        AddRow(form, "States .anm", statesBox, () => Pick(statesBox, AnimFiles.States, "Weapon states (.anm) — moves bolt, trigger, magazine… (weaponStateAnim)"));
        content.Children.Add(form);
        content.Children.Add(linkInfo);
        var buttons = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };
        buttons.Children.Add(Btn("Write to script", "Adds or updates pType.AddItemInHandsProfileIK(…) for this class in the mod's ModItemRegisterCallbacks script", WriteScript));
        buttons.Children.Add(Btn("Write weaponStateAnim", "Sets weaponStateAnim of this class in its config.cpp to the states .anm", WriteConfig));
        buttons.Children.Add(Btn("Reload", "Read config.cpp files, scripts and game configs again", () => Load(force: true)));
        content.Children.Add(buttons);

        content.Children.Add(Section("Bones: states .anm → boneRemap → model"));
        content.Children.Add(bones);

        content.Children.Add(Section("Preview in the viewport"));
        content.Children.Add(previewBox);
        var player = new DockPanel { Margin = new Thickness(6, 0, 6, 6) };
        DockPanel.SetDock(playButton, Dock.Left);
        DockPanel.SetDock(frameText, Dock.Right);
        player.Children.Add(playButton);
        player.Children.Add(frameText);
        player.Children.Add(frameSlider);
        content.Children.Add(player);
        previewBox.SelectionChanged += (_, _) =>
        {
            if (!loading) PickClip();
        };
        frameSlider.ValueChanged += (_, _) => ShowPose();
        playButton.Checked += (_, _) => playTimer.Start();
        playButton.Unchecked += (_, _) => playTimer.Stop();
        playTimer.Tick += (_, _) =>
        {
            if (clip is { Frames: > 1 } c) frameSlider.Value = (frameSlider.Value + c.Fps * playTimer.Interval.TotalSeconds) % (c.Frames - 1);
        };

        statesBox.TextChanged += (_, _) =>
        {
            if (loading) return;
            ShowBones();
            FillPreview();
        };
        asiBox.TextChanged += (_, _) =>
        {
            if (!loading) FillPreview();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Load(force: false);
            else StopPreview(); // a hidden panel must not leave the model drawn animated
        };
        Ed.Changed += c =>
        {
            if ((c & (Change.Document | Change.Load)) != 0 && IsVisible) Load(force: false);
            if ((c & (Change.Model | Change.ActiveLod)) != 0 && clip is not null) ShowPose();
        };
        AnimSession.Changed += () =>
        {
            if (IsVisible && !loading) ShowBones();
        };
    }

    // ---- loading --------------------------------------------------------------------------------

    /// <summary>The first folder under the work drive that holds the model ("P:\MyMod"), or the model's folder.</summary>
    static string? ModRoot(string? p3d)
    {
        if (p3d is null) return null;
        var root = Path.GetFullPath(AppServices.Settings.ProjectRoot).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(p3d);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(full);
        var first = full[root.Length..].Split('\\')[0];
        return Path.Combine(root, first);
    }

    async void Load(bool force)
    {
        var p3d = Ed.Path;
        if (!force && loadedFor == p3d && library is not null) return;
        loadedFor = p3d;
        AnimSession.Refresh();
        if (p3d is null)
        {
            status.Text = "Save the model first — its config.cpp and scripts are looked up around it.";
            return;
        }
        status.Text = "Reading config.cpp files and scripts…";
        status.Foreground = (Brush)FindResource("Editor.Dim");
        var modRoot = ModRoot(p3d)!;
        var projectRoot = AppServices.Settings.ProjectRoot;
        var (lib, found, scriptLinks, warnings) = await Task.Run(() => Scan(p3d, modRoot, projectRoot, force));
        if (loadedFor != p3d) return;
        library = lib;
        links = scriptLinks;
        loading = true;
        classBox.Items.Clear();
        foreach (var (c, model, exact) in found)
            classBox.Items.Add(new ComboBoxItem { Content = $"{c.Name}   ({Path.GetFileName(Path.GetDirectoryName(c.File))}\\config.cpp)", Tag = (c, model, exact) });
        presetBox.ItemsSource = AnimFiles.Presets(projectRoot).Select(p => p.Name).Prepend("(choose a game weapon)").ToList();
        presetBox.SelectedIndex = 0;
        loading = false;
        status.Text = found.Count == 0
            ? $"No CfgWeapons class in the config.cpp files of {modRoot} has model = …\\{Path.GetFileName(p3d)}."
            : $"{found.Count} weapon class{(found.Count == 1 ? "" : "es")} use this model · {lib.Files.Count} config files · {scriptLinks.Count} hands registrations in {modRoot}";
        if (warnings.Count > 0) status.Text += "\n⚠ " + string.Join("\n⚠ ", warnings.Take(4));
        status.Foreground = (Brush)FindResource(found.Count == 0 || warnings.Count > 0 ? "Editor.Warn" : "Editor.Dim");
        if (classBox.Items.Count > 0) classBox.SelectedIndex = 0;
        else ShowClass();
    }

    static readonly Dictionary<string, (DateTime Stamp, string Text)> textCache = new(StringComparer.OrdinalIgnoreCase);

    static string ReadCached(string file)
    {
        var stamp = File.GetLastWriteTimeUtc(file);
        lock (textCache)
            if (textCache.TryGetValue(file, out var e) && e.Stamp == stamp) return e.Text;
        var text = File.ReadAllText(file);
        lock (textCache) textCache[file] = (stamp, text);
        return text;
    }

    static (ConfigLibrary, List<(ClassRef, string, bool)>, List<HandsLink>, List<string>) Scan(string p3d, string modRoot, string projectRoot, bool force)
    {
        var warnings = new List<string>();
        var lib = new ConfigLibrary();
        // the mod's configs first (they win), then the game's
        var modConfigs = SafeFiles(modRoot, "config.cpp", 400);
        foreach (var f in modConfigs) TryAdd(lib, f, warnings);
        var dz = Path.Combine(projectRoot, "DZ");
        foreach (var f in new[] { Path.Combine(dz, "data", "config.cpp") }.Concat(SafeFiles(Path.Combine(dz, "weapons"), "config.cpp", 400)))
            if (File.Exists(f)) TryAdd(lib, f, warnings);
        if (!Directory.Exists(dz)) warnings.Add($"{dz} not found — inherited values (boneRemap of the game's base classes) are unknown");
        var game = AppServices.NormalizeGamePath(p3d);
        var found = lib.ByModel("CfgWeapons", game);
        foreach (var (c, model, exact) in found)
            if (!exact)
                warnings.Add($"{c.Name}: model = \"{model}\" does not match this file ({game}) — the game looks for P:\\{ConfigLibrary.NormalizeModel(model)}");
        var scriptLinks = new List<HandsLink>();
        foreach (var f in SafeFiles(modRoot, "*.c", 2000))
        {
            string text;
            try
            {
                text = ReadCached(f);
            }
            catch
            {
                continue;
            }
            if (text.Contains("AddItemInHandsProfileIK", StringComparison.Ordinal)) scriptLinks.AddRange(WeaponLinks.FindLinks(f, text));
        }
        return (lib, found, scriptLinks, warnings);
    }

    static void TryAdd(ConfigLibrary lib, string file, List<string> warnings)
    {
        try
        {
            lib.Add(file, ReadCached(file));
        }
        catch (Exception ex)
        {
            warnings.Add($"{file}: {ex.Message}");
        }
    }

    static List<string> SafeFiles(string dir, string pattern, int max)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Take(max).ToList() : [];
        }
        catch
        {
            return [];
        }
    }

    // ---- class ----------------------------------------------------------------------------------

    (ClassRef Class, string Model, bool Exact)? Current => (classBox.SelectedItem as ComboBoxItem)?.Tag as (ClassRef, string, bool)?;

    void ShowClass()
    {
        loading = true;
        if (Current is not { } cur || library is null)
        {
            classInfo.Text = "";
            asiBox.Text = ikBox.Text = statesBox.Text = "";
            linkInfo.Text = "";
            loading = false;
            ShowBones();
            FillPreview();
            return;
        }
        var chain = library.Chain("CfgWeapons", cur.Class.Name);
        var states = library.Get("CfgWeapons", cur.Class.Name, "weaponStateAnim", out var statesFrom)?.AsString() ?? "";
        library.Get("CfgWeapons", cur.Class.Name, "boneRemap", out var remapFrom);
        classInfo.Text = $"{string.Join(" ▸ ", chain.Select(c => c.Name))}" + (chain.Count > 0 && chain[^1].Class.Base is { } missing ? $" ▸ {missing} (game core class, not on the work drive)" : "")
                         + $"\nweaponStateAnim: {(states.Length > 0 ? $"{states}  (from {statesFrom?.Name})" : "not set")}"
                         + $"\nboneRemap: {(remapFrom is null ? "not set" : $"from {remapFrom.Name}")}";
        var link = LinkFor(chain);
        asiBox.Text = link?.Asi ?? "";
        ikBox.Text = link?.Ik ?? "";
        statesBox.Text = link?.States is { Length: > 0 } s ? s : states.Replace('\\', '/');
        linkInfo.Text = link is null
            ? "No AddItemInHandsProfileIK for this class (or its bases) in the mod's scripts yet — fill the files and press Write to script."
            : $"Registered in {link.File}" + (string.Equals(link.ClassName, cur.Class.Name, StringComparison.OrdinalIgnoreCase) ? "" : $" (for the base class {link.ClassName})");
        linkInfo.Foreground = (Brush)FindResource(link is null ? "Editor.Warn" : "Editor.Dim");
        loading = false;
        ShowBones();
        FillPreview();
    }

    HandsLink? LinkFor(List<ClassRef> chain)
    {
        foreach (var c in chain)
            foreach (var l in links)
                if (string.Equals(l.ClassName, c.Name, StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    // ---- bones ----------------------------------------------------------------------------------

    void ShowBones()
    {
        bones.Children.Clear();
        var statesFile = ResolveAnm(statesBox.Text);
        if (statesFile is null)
        {
            bones.Children.Add(Note(statesBox.Text.Length == 0 ? "Choose the states .anm to check the bones." : $"Not found on the work drive: {statesBox.Text}", "Editor.Warn"));
            return;
        }
        List<string> anm;
        try
        {
            anm = Anm.Bones(File.ReadAllBytes(statesFile));
        }
        catch (Exception ex)
        {
            bones.Children.Add(Note($"Cannot read {statesFile}: {ex.Message}", "Editor.Error"));
            return;
        }
        var check = BoneLinks(anm);
        int bad = check.Count(c => !c.Ok && c.ModelBone.Length > 0);
        bones.Children.Add(Note($"{anm.Count} bones in {Path.GetFileName(statesFile)} · skeleton \"{AnimSession.Skeleton}\" ({AnimSession.AllBones.Count()} bones) · named selections of LOD {Core.LodNames.Name(CheckedLod.Resolution)}",
            "Editor.Dim"));
        foreach (var c in check)
        {
            string line = c.ModelBone.Length == 0 ? $"{c.AnmBone}  →  —" : $"{c.AnmBone}  →  {c.ModelBone}";
            var row = Note(c.Ok ? "✓ " + line : $"{(c.ModelBone.Length == 0 ? "·" : "⚠")} {line}   {c.Note}",
                c.Ok ? "Editor.Ok" : c.ModelBone.Length == 0 ? "Editor.Faint" : "Editor.Warn");
            bones.Children.Add(row);
        }
        if (bad > 0)
            bones.Children.Add(Note("Bones the game cannot move stay still in the animation. Missing skeleton bones can be added in Tools ▸ Animations (Skeleton bones).", "Editor.Dim"));
    }

    static Lod CheckedLod => Ed.Model.Lods.FirstOrDefault(l => l.Resolution < 10000) ?? Ed.Lod;

    /// <summary>Every bone of an animation followed through boneRemap to the model (see <see cref="WeaponLinks.Check"/>).</summary>
    List<BoneLink> BoneLinks(IReadOnlyList<string> anmBones)
    {
        var remap = Current is { } cur && library is not null ? WeaponLinks.BoneRemap(library.Get("CfgWeapons", cur.Class.Name, "boneRemap", out _)) : [];
        return WeaponLinks.Check(anmBones, remap, AnimSession.AllBones.Select(b => b.Name).ToList(), CheckedLod.Selections.Select(s => s.Name).ToList());
    }

    // ---- preview --------------------------------------------------------------------------------

    /// <summary>The animations to play: the states .anm, then the .asi's own (shortcut: its #parent files are not read).</summary>
    void FillPreview()
    {
        StopPreview();
        loading = true;
        previewBox.Items.Clear();
        previewBox.Items.Add(new ComboBoxItem { Content = "(off)" });
        if (ResolveAnm(statesBox.Text) is { } states)
            previewBox.Items.Add(new ComboBoxItem { Content = $"States: {Path.GetFileName(states)}", Tag = states, ToolTip = "Frame 0: closed · 1: open · 2: jammed (as the game uses them)" });
        if (ResolveAnm(asiBox.Text) is { } asi)
        {
            try
            {
                foreach (var (state, anm) in WeaponLinks.AsiAnimations(File.ReadAllText(asi)))
                    if (ResolveAnm(anm) is { } file) previewBox.Items.Add(new ComboBoxItem { Content = state, Tag = file, ToolTip = anm });
            }
            catch (IOException ex)
            {
                Ed.Log($"Weapon animations: cannot read {asi}: {ex.Message}", LogLevel.Warn);
            }
        }
        previewBox.SelectedIndex = 0;
        loading = false;
    }

    void PickClip()
    {
        StopPreview();
        if ((previewBox.SelectedItem as ComboBoxItem)?.Tag is not string file) return;
        try
        {
            clip = Anm.Read(File.ReadAllBytes(file));
        }
        catch (IOException ex)
        {
            Ed.Log($"Weapon animations: cannot read {file}: {ex.Message}", LogLevel.Warn);
            return;
        }
        if (clip is null)
        {
            Ed.Log($"Weapon animations: {Path.GetFileName(file)} cannot be played (damaged, or an old SET4 file)", LogLevel.Warn);
            return;
        }
        // like the game: only model bones of the skeleton move
        clipLinks = [.. BoneLinks(clip.Bones.Select(b => b.Name).ToList()).Where(l => l.ModelBone.Length > 0 && l.InSkeleton).Select(l => (l.ModelBone, l.AnmBone))];
        Ed.Log(clipLinks.Count == 0
            ? $"Weapon animations: {Path.GetFileName(file)} moves no bone of this model (boneRemap / skeleton)"
            : $"Weapon animations: {Path.GetFileName(file)} — {clip.Frames} frames at {clip.Fps} fps, moves {string.Join(", ", clipLinks.Select(l => l.ModelBone))}",
            clipLinks.Count == 0 ? LogLevel.Warn : LogLevel.Info);
        if (AnimSession.PreviewOn) AnimSession.SetPreview(false); // one preview at a time
        frameSlider.Maximum = Math.Max(0, clip.Frames - 1);
        frameSlider.IsEnabled = playButton.IsEnabled = clip.Frames > 1;
        frameSlider.Value = 0;
        ShowPose();
    }

    void ShowPose()
    {
        if (clip is null || ViewportPlugins.Current is not { } vp) return;
        float frame = (float)frameSlider.Value;
        frameText.Text = $"{frame:0.#} / {clip.Frames - 1}";
        if (previewLod >= 0 && previewLod != Ed.ActiveLod) vp.SetPreviewPositions(previewLod, null);
        previewLod = Ed.ActiveLod;
        var pos = Anm.Pose(clip, frame, Ed.Lod, clipLinks);
        vp.SetPreviewPositions(previewLod, pos);
        ProxySceneHost.SetPreview(previewLod, pos);
    }

    void StopPreview()
    {
        playButton.IsChecked = false;
        clip = null;
        frameSlider.IsEnabled = playButton.IsEnabled = false;
        frameText.Text = "";
        if (previewLod < 0) return;
        ViewportPlugins.Current?.SetPreviewPositions(previewLod, null);
        ProxySceneHost.SetPreview(-1, null);
        previewLod = -1;
    }

    /// <summary>A script / config path ("dz/anims/…") or an absolute file to the file on the work drive.</summary>
    static string? ResolveAnm(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var abs = Path.IsPathRooted(path) ? path : AppServices.ResolveProjectPath(path.Replace('/', '\\'));
        return abs is not null && File.Exists(abs) ? abs : null;
    }

    // ---- presets / pickers ------------------------------------------------------------------------

    void ApplyPreset()
    {
        if (loading || presetBox.SelectedIndex <= 0 || presetBox.SelectedItem is not string name) return;
        var p = AnimFiles.Presets(AppServices.Settings.ProjectRoot).FirstOrDefault(x => x.Name == name);
        if (p is null) return;
        asiBox.Text = p.Asi;
        if (p.Ik.Length > 0) ikBox.Text = p.Ik;
        if (p.States.Length > 0) statesBox.Text = p.States;
        Ed.Log($"Weapon animations like the game's {name}: {p.Asi}" + (p.Ik.Length == 0 ? " (no IK .anm found — choose one)" : "") + (p.States.Length == 0 ? " (no states .anm found — choose one)" : ""));
    }

    void Pick(TextBox box, Func<string, string?, List<string>> source, string title)
    {
        var items = source(AppServices.Settings.ProjectRoot, ModRoot(Ed.Path));
        if (FilePickerDialog.Pick(title, items, box.Text) is { } chosen) box.Text = chosen;
    }

    // ---- writing --------------------------------------------------------------------------------

    /// <summary>Fills the file fields: "asi=…;ik=…;states=…" or "like=svd" (a game weapon preset); "preview=states;frame=1" plays.</summary>
    public void SetFiles(string spec)
    {
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq].Trim().ToLowerInvariant(), value = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "asi": asiBox.Text = value; break;
                case "ik": ikBox.Text = value; break;
                case "states": statesBox.Text = value; break;
                case "like":
                    presetBox.SelectedItem = (presetBox.ItemsSource as IEnumerable<string>)?.FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase));
                    break;
                case "preview": // an item of the preview list whose text contains the value ("states", "Fire")
                    previewBox.SelectedItem = previewBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Content is string t && t.Contains(value, StringComparison.OrdinalIgnoreCase));
                    break;
                case "frame":
                    if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) frameSlider.Value = f;
                    break;
            }
        }
    }

    public void WriteScript()
    {
        if (Current is not { } cur || library is null)
        {
            Dlg.Fail("No weapon class uses this model — check model = in config.cpp");
            return;
        }
        string asi = asiBox.Text.Trim(), ik = ikBox.Text.Trim(), st = statesBox.Text.Trim();
        if (asi.Length == 0 || ik.Length == 0)
        {
            Dlg.Fail("Choose the .asi and the IK .anm first (or pick a game weapon under Like)");
            return;
        }
        var chain = library.Chain("CfgWeapons", cur.Class.Name);
        var existing = LinkFor(chain);
        string className = existing?.ClassName ?? cur.Class.Name;
        string file;
        string? newText;
        string before;
        if (existing is not null)
        {
            file = existing.File;
            var text = File.ReadAllText(file);
            var fresh = WeaponLinks.FindLinks(file, text).FirstOrDefault(l => string.Equals(l.ClassName, existing.ClassName, StringComparison.OrdinalIgnoreCase));
            if (fresh is null)
            {
                Dlg.Fail($"{file} changed — press Reload");
                return;
            }
            before = text[fresh.Start..fresh.End];
            newText = WeaponLinks.SetLink(text, fresh, className, asi, ik, st);
        }
        else
        {
            var target = SafeFiles(ModRoot(Ed.Path)!, "*.c", 2000).FirstOrDefault(f => ReadCached(f).Contains("RegisterFireArms", StringComparison.Ordinal));
            if (target is null)
            {
                Dlg.Fail("The mod has no script with a ModItemRegisterCallbacks ▸ RegisterFireArms override. Create one (4_World) and add it to CfgMods, or copy this line:\n"
                         + WeaponLinks.Call(className, asi, "pBehavior", ik, st));
                return;
            }
            file = target;
            before = "(new line)";
            newText = WeaponLinks.SetLink(File.ReadAllText(file), null, className, asi, ik, st);
        }
        if (newText is null)
        {
            Dlg.Fail($"Could not find where to add the call in {file}");
            return;
        }
        var after = WeaponLinks.Call(className, asi, existing?.Behavior ?? "pBehavior", ik, st);
        if (!Dlg.Confirm("Write to script", $"{file}\n\nBefore:\n{before}\n\nAfter:\n{after}", "Write")) return;
        if (!WriteFile(file, newText)) return;
        Ed.Log($"Hands animation of {className} written to {file}");
        Load(force: true);
    }

    public void WriteConfig()
    {
        if (Current is not { } cur)
        {
            Dlg.Fail("No weapon class uses this model");
            return;
        }
        var st = statesBox.Text.Trim();
        if (st.Length == 0)
        {
            Dlg.Fail("Choose the states .anm first");
            return;
        }
        var value = WeaponLinks.GamePath(st, script: false);
        var file = cur.Class.File;
        var text = File.ReadAllText(file);
        var section = ConfigSpans.Classes(text).FirstOrDefault(c => c.HasBody && c.Name.Equals("CfgWeapons", StringComparison.OrdinalIgnoreCase)).Name ?? "CfgWeapons";
        var newText = WeaponLinks.SetConfigString(text, section, cur.Class.Name, "weaponStateAnim", value);
        if (newText is null)
        {
            Dlg.Fail($"{cur.Class.Name} not found in {file}");
            return;
        }
        if (newText == text)
        {
            Ed.Log("weaponStateAnim is already set to that file");
            return;
        }
        if (!Dlg.Confirm("Write weaponStateAnim", $"{file}\n\nclass {cur.Class.Name}:\nweaponStateAnim = \"{value}\";", "Write")) return;
        if (!WriteFile(file, newText)) return;
        Ed.Log($"weaponStateAnim of {cur.Class.Name} set in {file}");
        Load(force: true);
    }

    static bool WriteFile(string file, string text)
    {
        try
        {
            FileService.WriteAtomic(file, new System.Text.UTF8Encoding(false).GetBytes(text), AppServices.Settings.BackupOnSave);
            return true;
        }
        catch (Exception ex)
        {
            Dlg.Fail($"Cannot write {file}: {ex.Message}");
            return false;
        }
    }

    // ---- widgets --------------------------------------------------------------------------------

    TextBox Field() => new() { Style = (Style)FindResource("NumberTextBox"), FontSize = 12 };

    void AddRow(Grid g, string label, FrameworkElement editor, Action? pick)
    {
        int r = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 2) };
        Grid.SetRow(l, r);
        Grid.SetRow(editor, r);
        Grid.SetColumn(editor, 1);
        editor.Margin = new Thickness(0, 2, 0, 2);
        g.Children.Add(l);
        g.Children.Add(editor);
        if (pick is null) return;
        var b = new Button { Content = "…", Style = (Style)FindResource("CompactButton"), Margin = new Thickness(4, 2, 0, 2), ToolTip = "Search the game's and the mod's files" };
        b.Click += (_, _) => pick();
        Grid.SetRow(b, r);
        Grid.SetColumn(b, 2);
        g.Children.Add(b);
    }

    TextBlock Section(string text) => new() { Text = text, Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(6, 8, 6, 2), FontWeight = FontWeights.SemiBold };

    TextBlock Note(string text, string brush) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource(brush), Margin = new Thickness(0, 1, 0, 1) };

    Button Btn(string text, string tip, Action action)
    {
        var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("CompactButton"), Margin = new Thickness(0, 0, 4, 2) };
        b.Click += (_, _) => action();
        return b;
    }
}

/// <summary>The game's (and the mod's) animation files, as script paths ("dz/anims/…"), and weapon presets.</summary>
public static class AnimFiles
{
    public sealed record Preset(string Name, string Asi, string Ik, string States);

    static readonly Dictionary<string, List<string>> cache = new(StringComparer.OrdinalIgnoreCase);
    static List<Preset>? presets;
    static string? presetsRoot;

    static List<string> Files(string root, string relDir, string pattern, bool recursive)
    {
        var dir = Path.Combine(root, relDir);
        var key = dir + "|" + pattern + recursive;
        lock (cache)
            if (cache.TryGetValue(key, out var hit)) return hit;
        var list = new List<string>();
        try
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                    list.Add(ToScript(root, f));
        }
        catch
        {
            // unreadable folder: nothing listed
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        lock (cache) cache[key] = list;
        return list;
    }

    static string ToScript(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        return WeaponLinks.GamePath(rel, script: true);
    }

    static List<string> Mod(string root, string? modRoot, string pattern) =>
        modRoot is null ? [] : Files(root, Path.GetRelativePath(root, modRoot), pattern, recursive: true);

    public static List<string> Asi(string root, string? modRoot) =>
        [.. Files(root, @"DZ\anims\workspaces\player\player_main\weapons", "*.asi", false), .. Mod(root, modRoot, "*.asi")];

    public static List<string> Ik(string root, string? modRoot) =>
        [.. Files(root, @"DZ\anims\anm\player\ik\weapons", "*.anm", false), .. Mod(root, modRoot, "*.anm")];

    public static List<string> States(string root, string? modRoot) =>
        [.. Files(root, @"DZ\anims\anm\player\reloads", "w_*.anm", true), .. Mod(root, modRoot, "*.anm")];

    /// <summary>Every game weapon with its player_main_&lt;name&gt;.asi, ik\weapons\&lt;name&gt;.anm and reloads\&lt;name&gt;\w_&lt;name&gt;_states.anm.</summary>
    public static List<Preset> Presets(string root)
    {
        if (presets is not null && presetsRoot == root) return presets;
        var ik = Ik(root, null);
        var states = States(root, null);
        var list = new List<Preset>();
        foreach (var asi in Asi(root, null))
        {
            var file = Path.GetFileNameWithoutExtension(asi);
            if (!file.StartsWith("player_main_", StringComparison.OrdinalIgnoreCase)) continue;
            var name = file["player_main_".Length..];
            var ikFile = ik.FirstOrDefault(i => Path.GetFileNameWithoutExtension(i).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? "";
            var st = states.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s).Equals($"w_{name}_states", StringComparison.OrdinalIgnoreCase)) ?? "";
            list.Add(new Preset(name, asi, ikFile, st));
        }
        presetsRoot = root;
        return presets = list;
    }
}
