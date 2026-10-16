using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Resources;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.Core.Materials;
using P3DForge.Core.Ops;

namespace P3DForge.App.Panels.Materials;

/// <summary>
/// Material editor (Window ▸ Material Editor, Ctrl+Shift+M): edits one .rvmat — shaders, colours, specular power, render
/// flags, surfaceInfo, stages (texture, uvSource, filter, texGen, uvTransform with scale / rotate / offset helpers) —
/// while every edit is installed as an unsaved override in <see cref="MaterialStore.Shared"/> so the viewport previews it
/// live. Save (.bak when Settings.BackupOnSave), Save as, Reload, Revert; unknown entries are kept.
/// Flags: reads "materials.open" (path to open) and "materials.set" (automation edits, see <see cref="ApplyScript"/>);
/// writes "materials.current" and "textures.openPath" (Texture tools button).
/// </summary>
public sealed class MaterialEditorPanel : DockPanel
{
    public static MaterialEditorPanel? Current { get; private set; }

    static EditorState Ed => AppServices.Editor;
    static MaterialStore Store => MaterialStore.Shared;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly HashSet<string> Expanded = new(StringComparer.OrdinalIgnoreCase) { "Stage1", "Stage2" };

    readonly ComboBox picker;
    readonly TextBlock status;
    readonly StackPanel body;
    readonly Style numStyle;
    TextBox? sourceBox;
    string? path;
    Rvmat? working;
    Rvmat? installed;
    string? loadError;
    bool building;
    int quiet;

    /// <summary>Model path of the material being edited (null when none).</summary>
    public string? MaterialPath => path;

    /// <summary>The working copy (do not modify; use the panel's editing methods).</summary>
    public Rvmat? Material => working;

    public bool IsEdited => path is not null && Store.HasOverride(path);

    public MaterialEditorPanel()
    {
        Current = this;
        Background = Brushes.Transparent;
        numStyle = new Style(typeof(TextBox), (Style)FindResource("NumberTextBox"));
        numStyle.Setters.Add(new Setter(MinWidthProperty, 0.0));
        numStyle.Setters.Add(new Setter(MinHeightProperty, 22.0));
        numStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 1, 3, 1)));
        numStyle.Setters.Add(new Setter(Control.FontSizeProperty, 11.5));
        numStyle.Setters.Add(new Setter(MarginProperty, new Thickness(1)));

        var top = new StackPanel { Margin = new Thickness(6, 6, 6, 2) };
        SetDock(top, Dock.Top);
        Children.Add(top);

        var pickRow = new DockPanel();
        foreach (var b in new[]
                 {
                     IconBtn("", "Reload from disk", Reload),
                     IconBtn("", "Browse for a material (.rvmat)…", Browse),
                     IconBtn("", "Edit the material of the selected faces", FromSelection),
                 })
        {
            SetDock(b, Dock.Right);
            pickRow.Children.Add(b);
        }
        picker = new ComboBox
        {
            IsEditable = true,
            MinWidth = 0,
            FontSize = 12,
            ToolTip = "Materials used by the model (● = edited, not saved). Type or paste a path and press Enter.",
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (!building && picker.SelectedItem is ComboBoxItem { Tag: string p }) Dispatcher.BeginInvoke(() => Open(p));
        };
        picker.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Open(picker.Text);
            e.Handled = true;
        };
        picker.DropDownOpened += (_, _) => RefreshList();
        pickRow.Children.Add(picker);
        top.Children.Add(pickRow);

        status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 2), Foreground = Dlg.Res("Editor.Dim") };
        top.Children.Add(status);

        var actions = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        actions.Children.Add(Btn("Assign", "Assign this material to the selected faces", () => Run(p => MaterialActions.AssignToSelectedFaces(p))));
        actions.Children.Add(Btn("Select faces", "Select the faces of the active LOD that use this material", () => Run(p => MaterialActions.SelectFacesUsing(p))));
        actions.Children.Add(Btn("Save", "Write the .rvmat (a .bak copy is kept when 'Backup on save' is on)", Save));
        actions.Children.Add(Btn("Save as…", "Write the material to another file and edit that one", SaveAs));
        actions.Children.Add(Btn("Revert", "Drop the unsaved edits of this material", Revert));
        top.Children.Add(actions);

        body = new StackPanel { Margin = new Thickness(6, 0, 4, 10) };
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Children.Add(scroll);

        Store.Changed += OnStoreChanged;
        Ed.Changed += OnEditorChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && path is null) OpenDefault();
        };
        if (Ed.Flag<string?>(MaterialActions.OpenFlag, null) is { } pending)
        {
            Ed.SetFlag(MaterialActions.OpenFlag, null);
            Open(pending);
        }
        else Build();
    }

    // ── opening / saving ────────────────────────────────────────────────────────────────────────────────────

    void Run(Action<string> a)
    {
        if (path is null) Ed.Log("No material open", LogLevel.Warn);
        else a(path);
    }

    /// <summary>Opens the material of the selected faces, else the most used material of the model.</summary>
    public void OpenDefault()
    {
        var m = MaterialActions.SelectedFacesMaterial() ?? MaterialActions.UsedMaterials().Select(u => u.Path).FirstOrDefault();
        if (m is not null) Open(m);
        else Build();
    }

    /// <summary>Shows a material (model path or file). Unsaved edits of other materials stay in the store.</summary>
    public void Open(string? materialPath)
    {
        var p = materialPath?.Trim().Trim('"');
        if (string.IsNullOrEmpty(p)) return;
        p = MaterialActions.ToModelPath(p);
        if (Store.Resolve(p) is null)
        {
            Ed.Log($"Not a material path: {p}", LogLevel.Warn);
            return;
        }
        path = p;
        Store.TryGet(p, out var m, out loadError);
        if (m is not null)
        {
            working = m.Clone();
            installed = Store.HasOverride(p) ? m : null;
        }
        else
        {
            working = RvmatTemplates.Super();
            installed = null;
        }
        Ed.SetFlag(MaterialActions.CurrentFlag, p);
        building = true;
        picker.Text = p;
        building = false;
        Build();
    }

    void FromSelection()
    {
        var m = MaterialActions.SelectedFacesMaterial();
        if (m is null) Ed.Log("The selected faces have no material", LogLevel.Warn);
        else Open(m);
    }

    void Browse()
    {
        var p = ResourcePicker.Pick(Window.GetWindow(this), ResourceKind.Material, path ?? "");
        if (!string.IsNullOrWhiteSpace(p)) Open(p);
    }

    public void Save()
    {
        if (path is null || working is null) return;
        var full = Store.Resolve(path);
        if (full is null) return;
        if (!File.Exists(full))
        {
            // Usually a wrong path in the model (e.g. the first folder is missing): saving would create a stray
            // folder tree on the work drive. Make that an explicit decision.
            var dir = Path.GetDirectoryName(full);
            string where = dir is not null && !Directory.Exists(dir)
                ? $"The folder {dir} does not exist either — the path is probably wrong (missing first folder?).\nSurfaces ▸ Fix Missing Texture Paths can find the real file.\n\n"
                : "";
            if (!Dlg.Confirm("Create a new material file?",
                    $"{full} does not exist.\n\n{where}Create it{(where.Length > 0 ? " together with the missing folders" : "")}? (Save as… lets you choose another place.)",
                    "Create file"))
                return;
        }
        var written = MaterialActions.Write(working, full);
        if (written is null) return;
        if (MaterialActions.AutomationSaveDir() is null)
        {
            Quiet(() =>
            {
                Store.Invalidate(path);
                Store.ClearOverride(path);
            });
            Open(path);
        }
        else UpdateStatus();
    }

    public void SaveAs()
    {
        if (path is null || working is null) return;
        string? target;
        var full = Store.Resolve(path) ?? "material.rvmat";
        if (Dlg.Automated)
        {
            target = Dlg.TakeAnswer();
            if (string.IsNullOrWhiteSpace(target)) return;
        }
        else
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save material as",
                Filter = "Material (*.rvmat)|*.rvmat|All files|*.*",
                FileName = Path.GetFileName(full),
                DefaultExt = ".rvmat",
            };
            var dir = Path.GetDirectoryName(full);
            if (dir is not null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            target = dlg.FileName;
        }
        var written = MaterialActions.Write(working, target);
        if (written is null) return;
        var old = path;
        Quiet(() =>
        {
            Store.ClearOverride(old);
            Store.Invalidate(written);
        });
        Open(MaterialActions.ToModelPath(written));
    }

    public void Revert()
    {
        if (path is null) return;
        Quiet(() => Store.ClearOverride(path));
        Open(path);
    }

    public void Reload()
    {
        if (path is null) return;
        if (IsEdited && !Dlg.Confirm("Reload material", "Discard the unsaved edits of this material and reload it from disk?", "Discard")) return;
        Quiet(() =>
        {
            Store.ClearOverride(path);
            Store.Invalidate(path);
        });
        Open(path);
    }

    /// <summary>New material at a chosen file (from the Super template; stage textures follow the selected faces' texture).</summary>
    public void NewMaterial()
    {
        string? target;
        var tex = SelectedFacesTexture();
        var baseName = RvmatTemplates.BaseName(tex);
        if (Dlg.Automated)
        {
            target = Dlg.TakeAnswer();
            if (string.IsNullOrWhiteSpace(target)) return;
            if (!Path.IsPathRooted(target) && MaterialActions.AutomationSaveDir() is { } d) target = Path.Combine(d, target);
        }
        else
        {
            var suggestion = baseName is not null ? Store.Resolve(baseName + ".rvmat") : null;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "New material",
                Filter = "Material (*.rvmat)|*.rvmat",
                DefaultExt = ".rvmat",
                FileName = suggestion is null ? "material.rvmat" : Path.GetFileName(suggestion),
            };
            var dir = suggestion is null ? null : Path.GetDirectoryName(suggestion);
            if (dir is not null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            target = dlg.FileName;
        }
        string? Existing(string suffix) =>
            baseName is not null && Store.Resolve(baseName + suffix) is { } f && File.Exists(f) ? baseName + suffix : null;
        var m = RvmatTemplates.Super(Existing("_nohq.paa"), Existing("_smdi.paa"), Existing("_as.paa"));
        var mp = MaterialActions.ToModelPath(target);
        Quiet(() => Store.SetOverride(mp, m.Clone()));
        Open(mp);
        Ed.Log($"New material {mp} (not saved yet — Save writes it)");
    }

    static string? SelectedFacesTexture()
    {
        if (Ed.Model.Lods.Length == 0) return null;
        var lod = Ed.Lod;
        var faces = MaterialActions.SelectedFaces();
        for (int f = 0; f < faces.Length && f < lod.FaceCount; f++)
            if (faces[f] != 0 && lod.Strings[(int)lod.FaceTexture[f]] is { Length: > 0 } t) return t;
        return null;
    }

    void Quiet(Action a)
    {
        quiet++;
        try
        {
            a();
        }
        finally
        {
            quiet--;
        }
    }

    void OnStoreChanged(string key)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnStoreChanged(key));
            return;
        }
        if (quiet > 0 || path is null || Store.Key(path) != key) return;
        Store.TryGet(path, out var m, out loadError);
        if (m is not null && ReferenceEquals(m, installed))
        {
            UpdateStatus();
            return;
        }
        // changed elsewhere (file reloaded, override cleared/installed by another panel)
        working = m?.Clone() ?? RvmatTemplates.Super();
        installed = Store.HasOverride(path) ? m : null;
        Build();
    }

    void OnEditorChanged(Change c)
    {
        if (c.HasFlag(Change.Ui))
        {
            if (Ed.Flag<string?>(MaterialActions.OpenFlag, null) is { } open)
            {
                Ed.SetFlag(MaterialActions.OpenFlag, null);
                Open(open);
            }
            if (Ed.Flag<string?>("materials.set", null) is { } script)
            {
                Ed.SetFlag("materials.set", null);
                ApplyScript(script);
            }
            if (Dlg.Automated && Ed.Flag<string?>("materials.editorShot", null) is { } shot)
            {
                Ed.SetFlag("materials.editorShot", null);
                Dispatcher.BeginInvoke(() => SaveContentShot(shot), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        if ((c & (Change.Model | Change.Document | Change.Load)) != 0 && IsVisible && path is not null && !IsEdited && !UsedInModel(path))
        {
            // The faces got another material (Set Material, Fix Paths, undo…): follow it instead of editing a
            // material nothing uses any more.
            if ((MaterialActions.SelectedFacesMaterial() ?? MostUsedMaterial()) is { } next && Attributes.PathKey(next) != Attributes.PathKey(path))
            {
                Open(next);
                return;
            }
        }
        if ((c & (Change.Model | Change.Document | Change.Load | Change.ActiveLod)) != 0 && IsVisible) UpdateStatus();
    }

    /// <summary>True when some face of some LOD uses <paramref name="materialPath"/>.</summary>
    static bool UsedInModel(string materialPath)
    {
        var key = Attributes.PathKey(materialPath);
        foreach (var lod in Ed.Model.Lods)
        {
            var hit = new bool[lod.Strings.Length];
            bool any = false;
            for (int i = 0; i < hit.Length; i++)
                if (Attributes.PathKey(lod.Strings[i]) == key) hit[i] = any = true;
            if (!any) continue;
            var fm = lod.FaceMaterial;
            for (int f = 0; f < fm.Length; f++)
                if (hit[fm[f]]) return true;
        }
        return false;
    }

    /// <summary>The material used by the most faces of the active LOD, or null.</summary>
    static string? MostUsedMaterial()
    {
        if (Ed.Model.Lods.Length == 0) return null;
        var lod = Ed.Lod;
        var counts = new int[lod.Strings.Length];
        foreach (var m in lod.FaceMaterial) counts[m]++;
        int best = -1;
        for (int i = 0; i < counts.Length; i++)
            if (counts[i] > 0 && lod.Strings[i].Trim().Length > 0 && (best < 0 || counts[i] > counts[best])) best = i;
        return best < 0 ? null : lod.Strings[best];
    }

    /// <summary>Automation: PNG of the whole editor content (not clipped by the dock height) at the panel's width.</summary>
    void SaveContentShot(string file)
    {
        try
        {
            var w = Math.Max(300, ActualWidth);
            body.Measure(new Size(w, double.PositiveInfinity));
            var h = Math.Max(50, body.DesiredSize.Height);
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Dlg.Res("Editor.PanelBackground"), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(body) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, w, h));
            }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            using var fs = File.Create(file);
            enc.Save(fs);
        }
        catch (Exception ex)
        {
            Ed.Log($"materials.editorShot: {ex.Message}", LogLevel.Warn);
        }
    }

    // ── editing core ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Applies a change to the working copy and installs it as the store override (live viewport preview).</summary>
    void Edit(Action<Rvmat> change, bool rebuild = false)
    {
        if (building || working is null || path is null) return;
        change(working);
        var disk = Store.LoadFromDisk(path);
        if (disk is not null && working.Root.DeepEquals(disk.Root))
        {
            // back to the file's content: no unsaved edit any more
            installed = null;
            var p = path;
            Quiet(() => Store.ClearOverride(p));
        }
        else
        {
            var copy = working.Clone();
            installed = copy;
            var p = path;
            Quiet(() => Store.SetOverride(p, copy));
            MaterialActions.EnsureFinalView(); // rvmat edits are only drawn in Final
        }
        if (rebuild) Build();
        else
        {
            UpdateStatus();
            UpdateSource();
        }
    }

    /// <summary>
    /// Scripted edits (automation flag "materials.set"), statements separated by ';':
    /// PixelShaderID=X · VertexShaderID=X · ambient|diffuse|forcedDiffuse|emmisive|specular=r,g,b,a · specularPower=x ·
    /// flag+=Name · flag-=Name · surfaceInfo=X · stageN.texture=X · stageN.uvSource=X · stageN.uv=su,sv,rot,ou,ov ·
    /// addStage · removeStage=N
    /// </summary>
    public void ApplyScript(string script)
    {
        if (working is null || path is null) return;
        foreach (var raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var eq = raw.IndexOf('=');
                var key = eq < 0 ? raw : raw[..eq].Trim();
                var val = eq < 0 ? "" : raw[(eq + 1)..].Trim();
                float[] F() => val.Split(',').Select(x => float.Parse(x.Trim(), Inv)).ToArray();
                Rgba C()
                {
                    var f = F();
                    return new Rgba(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1);
                }
                switch (key.ToLowerInvariant())
                {
                    case "pixelshaderid": Edit(m => m.PixelShaderID = val); break;
                    case "vertexshaderid": Edit(m => m.VertexShaderID = val); break;
                    case "ambient": Edit(m => m.Ambient = C()); break;
                    case "diffuse": Edit(m => m.Diffuse = C()); break;
                    case "forceddiffuse": Edit(m => m.ForcedDiffuse = C()); break;
                    case "emmisive" or "emissive": Edit(m => m.Emmisive = C()); break;
                    case "specular": Edit(m => m.Specular = C()); break;
                    case "specularpower": Edit(m => m.SpecularPower = F()[0]); break;
                    case "surfaceinfo": Edit(m => m.SurfaceInfo = val); break;
                    case "flag+": Edit(m => m.SetRenderFlag(val, true)); break;
                    case "flag-": Edit(m => m.SetRenderFlag(val, false)); break;
                    case "addstage": Edit(m => m.AddStage(m.NextStageIndex)); break;
                    case "removestage": Edit(m => m.RemoveStage(int.Parse(val, Inv))); break;
                    default:
                        if (key.StartsWith("stage", StringComparison.OrdinalIgnoreCase) && key.IndexOf('.') is var dot and > 5)
                        {
                            var n = int.Parse(key[5..dot], Inv);
                            var prop = key[(dot + 1)..].ToLowerInvariant();
                            Edit(m =>
                            {
                                var s = m.Stage(n) ?? m.AddStage(n);
                                switch (prop)
                                {
                                    case "texture": s.Texture = val; break;
                                    case "uvsource": s.UvSource = val; break;
                                    case "uv":
                                        var f = F();
                                        SetStageUv(m, s, UvTransform.FromComponents(f[0], f[1], f.Length > 2 ? f[2] : 0, f.Length > 3 ? f[3] : 0, f.Length > 4 ? f[4] : 0, s.EffectiveUvTransform.Dir));
                                        break;
                                }
                            });
                        }
                        else Ed.Log($"materials.set: unknown statement '{raw}'", LogLevel.Warn);
                        break;
                }
            }
            catch (Exception ex)
            {
                Ed.Log($"materials.set '{raw}': {ex.Message}", LogLevel.Warn);
            }
        }
        Build();
    }

    /// <summary>Writes a stage's UV transform: into its TexGen when it references one, else into the stage.</summary>
    static void SetStageUv(Rvmat m, RvmatStage s, UvTransform t)
    {
        if (s.TexGen is int g && m.TexGen(g) is { } tg) tg.UvTransform = t;
        else s.UvTransform = t;
    }

    // ── UI building ─────────────────────────────────────────────────────────────────────────────────────────

    void Build()
    {
        building = true;
        try
        {
            body.Children.Clear();
            sourceBox = null;
            UpdateStatus();
            if (working is null || path is null)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "No material. Select faces and press the eyedropper, pick a material used by the model, or Browse.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Dlg.Res("Editor.Dim"),
                    Margin = new Thickness(2, 8, 2, 0),
                    FontSize = 12,
                });
                return;
            }
            var m = working;

            Header("Shaders");
            var g = FormGrid();
            AddRow(g, "Pixel shader", EditableCombo(RvmatShaders.PixelShaders, m.PixelShaderID, v => Edit(x => x.PixelShaderID = v)));
            AddRow(g, "Vertex shader", EditableCombo(RvmatShaders.VertexShaders, m.VertexShaderID, v => Edit(x => x.VertexShaderID = v)));
            body.Children.Add(g);

            Header("Colours");
            var cg = FormGrid();
            ColorRow(cg, "Ambient", x => x.Ambient, (x, c) => x.Ambient = c);
            ColorRow(cg, "Diffuse", x => x.Diffuse, (x, c) => x.Diffuse = c);
            ColorRow(cg, "Forced diffuse", x => x.ForcedDiffuse, (x, c) => x.ForcedDiffuse = c);
            ColorRow(cg, "Emissive", x => x.Emmisive, (x, c) => x.Emmisive = c);
            ColorRow(cg, "Specular", x => x.Specular, (x, c) => x.Specular = c);
            var sp = Num(m.SpecularPower);
            sp.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Width = 70;
            sp.TextChanged += (_, _) =>
            {
                if (TryF(sp.Text, out var v)) Edit(x => x.SpecularPower = v);
            };
            AddRow(cg, "Specular power", sp);
            body.Children.Add(cg);

            Header("Render flags");
            var flags = new WrapPanel { Margin = new Thickness(2, 0, 0, 2) };
            var all = RvmatShaders.RenderFlags.ToList();
            foreach (var f in m.RenderFlags)
                if (!all.Contains(f, StringComparer.OrdinalIgnoreCase)) all.Add(f);
            foreach (var f in all)
            {
                var flag = f;
                var cb = new CheckBox { Content = flag, FontSize = 11.5, Margin = new Thickness(0, 0, 10, 2), MinWidth = 0, IsChecked = m.HasRenderFlag(flag) };
                cb.Checked += (_, _) => Edit(x => x.SetRenderFlag(flag, true));
                cb.Unchecked += (_, _) => Edit(x => x.SetRenderFlag(flag, false));
                flags.Children.Add(cb);
            }
            body.Children.Add(flags);
            var sg = FormGrid();
            var surf = Text(m.SurfaceInfo ?? "");
            surf.ToolTip = "surfaceInfo (.bisurf) — empty removes the entry";
            surf.TextChanged += (_, _) => Edit(x => x.SurfaceInfo = surf.Text.Trim());
            AddRow(sg, "surfaceInfo", surf);
            body.Children.Add(sg);

            var stages = m.Stages;
            Header($"Stages ({stages.Count})");
            foreach (var s in stages) body.Children.Add(StageBlock(m, s));
            var add = Btn("+ Add stage", "Adds the next StageN (texture empty, uvSource tex, identity uvTransform)", () => Edit(x =>
            {
                var n = x.NextStageIndex;
                x.AddStage(n);
                Expanded.Add($"Stage{n}");
            }, rebuild: true));
            add.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(add);

            var texGens = m.TexGens;
            if (texGens.Count > 0)
            {
                Header($"TexGens ({texGens.Count})");
                foreach (var tg in texGens)
                {
                    var users = stages.Where(s => s.TexGen == tg.Index).Select(s => s.Name).ToList();
                    var idx = tg.Index;
                    var exp = new Expander
                    {
                        Header = $"{tg.Name} · {tg.UvSource ?? "tex"} · used by {(users.Count == 0 ? "no stage" : string.Join(", ", users))}",
                        IsExpanded = Expanded.Contains(tg.Name),
                        Margin = new Thickness(0, 2, 0, 2),
                        FontSize = 12,
                    };
                    exp.Expanded += (_, _) => Expanded.Add(tg.Name);
                    exp.Collapsed += (_, _) => Expanded.Remove(tg.Name);
                    var panel = new StackPanel { Margin = new Thickness(14, 2, 0, 4) };
                    var tgg = FormGrid();
                    var uvs = EditableCombo(RvmatShaders.UvSources, tg.UvSource ?? "", v => Edit(x =>
                    {
                        if (x.TexGen(idx) is { } t) t.UvSource = v.Length == 0 ? null : v;
                    }));
                    AddRow(tgg, "uvSource", uvs);
                    panel.Children.Add(tgg);
                    panel.Children.Add(UvEditor(x => x.TexGen(idx)?.UvTransform, (x, t) =>
                    {
                        if (x.TexGen(idx) is { } tt) tt.UvTransform = t;
                    }, null));
                    exp.Content = panel;
                    body.Children.Add(exp);
                }
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PixelShaderID", "VertexShaderID", "ambient", "diffuse", "forcedDiffuse", "emmisive", "specular", "specularPower",
                "renderFlags", "surfaceInfo",
            };
            var others = m.Root.Entries.Where(e => !known.Contains(e.Name) && Rvmat.IndexOf(e.Name, "Stage") < 0 && Rvmat.IndexOf(e.Name, "TexGen") < 0)
                .Select(e => e is ConfigClass ? $"class {e.Name}" : e.Name).ToList();
            if (others.Count > 0)
            {
                Header("Other entries (kept on save)");
                body.Children.Add(new TextBlock
                {
                    Text = string.Join(", ", others),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11.5,
                    Foreground = Dlg.Res("Editor.Dim"),
                    Margin = new Thickness(2, 0, 2, 4),
                });
            }

            var src = new Expander { Header = "Source (.rvmat text)", Margin = new Thickness(0, 8, 0, 0), FontSize = 12, IsExpanded = Expanded.Contains("#source") };
            sourceBox = new TextBox
            {
                IsReadOnly = true,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinWidth = 0,
            };
            src.Expanded += (_, _) =>
            {
                Expanded.Add("#source");
                UpdateSource();
            };
            src.Collapsed += (_, _) => Expanded.Remove("#source");
            src.Content = sourceBox;
            body.Children.Add(src);
            UpdateSource();
        }
        finally
        {
            building = false;
        }
    }

    FrameworkElement StageBlock(Rvmat m, RvmatStage stage)
    {
        var idx = stage.Index;
        var name = stage.Name;
        var exp = new Expander { IsExpanded = Expanded.Contains(name), Margin = new Thickness(0, 2, 0, 2), FontSize = 12 };
        exp.Expanded += (_, _) => Expanded.Add(name);
        exp.Collapsed += (_, _) => Expanded.Remove(name);

        var thumb = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 6, 0), Stretch = Stretch.UniformToFill };
        var headerText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var header = new DockPanel { LastChildFill = true };
        header.Children.Add(thumb);
        header.Children.Add(headerText);
        exp.Header = header;

        void UpdateHeader(string tex)
        {
            var role = m.RoleOf(m.Stage(idx) ?? stage);
            var file = tex.StartsWith('#') ? tex : Path.GetFileName(tex.Replace('/', '\\'));
            headerText.Text = $"{name} · {RoleText(role)} · {(file.Length == 0 ? "(no texture)" : file)}";
            exp.ToolTip = tex;
            thumb.Source = null;
            if (tex.Length > 0)
                Thumbnails.Request(tex, 40, img =>
                {
                    if ((m.Stage(idx)?.Texture ?? "") == tex) thumb.Source = img;
                });
        }

        var panel = new StackPanel { Margin = new Thickness(14, 2, 0, 6) };
        var g = FormGrid();

        var tex = Text(stage.Texture);
        tex.FontFamily = (FontFamily)FindResource("MonoFont");
        tex.FontSize = 11;
        void CheckTex()
        {
            var t = tex.Text.Trim();
            var full = Store.Resolve(t);
            var missing = full is not null && !File.Exists(full);
            if (missing) tex.Foreground = Dlg.Res("Editor.Error");
            else tex.ClearValue(Control.ForegroundProperty);
            tex.ToolTip = t.StartsWith('#') ? "Procedural texture" : missing ? $"Not found: {full}" : full;
        }
        tex.TextChanged += (_, _) =>
        {
            var v = tex.Text.Trim();
            Edit(x =>
            {
                if (x.Stage(idx) is { } s) s.Texture = v;
            });
            CheckTex();
            UpdateHeader(v);
        };
        var texRow = new DockPanel();
        var tools = IconBtn("", "Texture tools… (opens the texture in the Textures panel)", () => OpenTextureTools(tex.Text.Trim()));
        var browse = IconBtn("", "Browse for a texture…", () =>
        {
            var p = ResourcePicker.Pick(Window.GetWindow(this), ResourceKind.Texture, tex.Text.Trim());
            if (!string.IsNullOrWhiteSpace(p)) tex.Text = p;
        });
        SetDock(tools, Dock.Right);
        SetDock(browse, Dock.Right);
        texRow.Children.Add(tools);
        texRow.Children.Add(browse);
        texRow.Children.Add(tex);
        AddRow(g, "Texture", texRow);

        AddRow(g, "uvSource", EditableCombo(RvmatShaders.UvSources, stage.UvSource ?? "", v => Edit(x =>
        {
            if (x.Stage(idx) is { } s) s.UvSource = v.Length == 0 ? null : v;
        })));
        AddRow(g, "Filter", EditableCombo(RvmatShaders.Filters, stage.Filter ?? "", v => Edit(x =>
        {
            if (x.Stage(idx) is { } s) s.Filter = v;
        })));

        var texGen = Text(stage.TexGen?.ToString(Inv) ?? "");
        texGen.Width = 60;
        texGen.HorizontalAlignment = HorizontalAlignment.Left;
        texGen.ToolTip = "texGen: number of the TexGen class whose uvSource / uvTransform this stage uses (empty = its own)";
        void CommitTexGen()
        {
            var t = texGen.Text.Trim();
            int? v = int.TryParse(t, NumberStyles.Integer, Inv, out var n) ? n : null;
            if (t.Length > 0 && v is null) return;
            if (v == m.Stage(idx)?.TexGen) return;
            Edit(x =>
            {
                if (x.Stage(idx) is { } s) s.TexGen = v;
            }, rebuild: true);
        }
        texGen.LostKeyboardFocus += (_, _) => CommitTexGen();
        texGen.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitTexGen();
        };
        AddRow(g, "texGen", texGen);
        panel.Children.Add(g);

        if (stage.TexGen is int tgi && m.TexGen(tgi) is { } tg)
        {
            var users = m.Stages.Where(s => s.TexGen == tgi && s.Index != idx).Select(s => s.Name).ToList();
            panel.Children.Add(new TextBlock
            {
                Text = $"UV from {tg.Name} ({tg.UvSource ?? "tex"}){(users.Count > 0 ? " — shared with " + string.Join(", ", users) : "")}",
                FontSize = 11,
                Foreground = Dlg.Res("Editor.Warn"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 0, 0),
            });
            panel.Children.Add(UvEditor(x => x.TexGen(tgi)?.UvTransform, (x, t) =>
            {
                if (x.TexGen(tgi) is { } tt) tt.UvTransform = t;
            }, null));
        }
        else
        {
            panel.Children.Add(UvEditor(x => x.Stage(idx)?.UvTransform, (x, t) =>
            {
                if (x.Stage(idx) is { } s) s.UvTransform = t;
            }, stage.UvTransform is null ? "No uvTransform (identity)." : null));
        }

        var remove = Btn("Remove stage", $"Deletes class {name}", () =>
        {
            if (Dlg.Confirm("Remove stage", $"Remove {name} from the material?", "Remove")) Edit(x => x.RemoveStage(idx), rebuild: true);
        });
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(remove);

        exp.Content = panel;
        UpdateHeader(stage.Texture);
        CheckTex();
        return exp;
    }

    static string RoleText(StageRole r) => r switch
    {
        StageRole.Normal => "normal",
        StageRole.Detail => "detail",
        StageRole.Macro => "macro",
        StageRole.AmbientShadow => "ambient shadow",
        StageRole.Specular => "specular",
        StageRole.Fresnel => "fresnel",
        StageRole.Environment => "environment",
        StageRole.Diffuse => "diffuse",
        _ => "stage",
    };

    void OpenTextureTools(string texture)
    {
        if (texture.Length == 0) return;
        var full = ProceduralTexture.IsProcedural(texture) ? texture : Store.Resolve(texture) ?? texture;
        Ed.SetFlag("textures.openPath", full);
        if (PanelRegistry.All.Any(p => p.Id == "textures")) PanelRegistry.Focus("textures");
        else Ed.Log("The Textures panel is not available in this build", LogLevel.Warn);
    }

    /// <summary>UV transform editor: scale (with link), rotate, offset, reset / ×2 / ÷2 and the raw aside/up/dir/pos rows.</summary>
    FrameworkElement UvEditor(Func<Rvmat, UvTransform?> get, Action<Rvmat, UvTransform?> set, string? note)
    {
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var cur = working is null ? null : get(working);
        if (cur is null)
        {
            var row = new DockPanel();
            var addBtn = Btn("Add uvTransform", "Adds class uvTransform (identity)", () => Edit(x => set(x, UvTransform.Identity), rebuild: true));
            SetDock(addBtn, Dock.Right);
            row.Children.Add(addBtn);
            row.Children.Add(new TextBlock { Text = note ?? "No uvTransform.", FontSize = 11, Foreground = Dlg.Res("Editor.Dim"), VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(row);
            return root;
        }
        var t0 = cur.Value;
        t0.Decompose(out var su0, out var sv0, out var rot0, out var ou0, out var ov0);

        TextBox su = Num(su0), sv = Num(sv0), rot = Num(rot0), ou = Num(ou0), ov = Num(ov0);
        var link = new ToggleButton
        {
            Content = "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12,
            Width = 26,
            Height = 22,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 1, 0, 1),
            ToolTip = "Link U and V scale",
            IsChecked = Math.Abs(su0 - sv0) < 1e-6,
        };
        var raw = new TextBox[12];
        bool sync = false;

        UvTransform Cur() => (working is null ? null : get(working)) ?? UvTransform.Identity;

        void FillRaw(UvTransform t, object? except)
        {
            var v = new[] { t.Aside, t.Up, t.Dir, t.Pos };
            for (int r = 0; r < 4; r++)
            for (int k = 0; k < 3; k++)
            {
                var tb = raw[r * 3 + k];
                if (ReferenceEquals(tb, except)) continue;
                tb.Text = Fmt(k == 0 ? v[r].X : k == 1 ? v[r].Y : v[r].Z);
            }
        }

        void FillComponents(UvTransform t, object? except)
        {
            t.Decompose(out var a, out var b, out var c, out var d, out var e);
            foreach (var (tb, val) in new[] { (su, a), (sv, b), (rot, c), (ou, d), (ov, e) })
                if (!ReferenceEquals(tb, except)) tb.Text = Fmt(val);
        }

        void FromComponents(object sender)
        {
            if (sync || building) return;
            if (!(TryF(su.Text, out var a) && TryF(sv.Text, out var b) && TryF(rot.Text, out var c) && TryF(ou.Text, out var d) && TryF(ov.Text, out var e))) return;
            sync = true;
            try
            {
                if (link.IsChecked == true)
                {
                    if (ReferenceEquals(sender, su))
                    {
                        b = a;
                        sv.Text = su.Text;
                    }
                    else if (ReferenceEquals(sender, sv))
                    {
                        a = b;
                        su.Text = sv.Text;
                    }
                }
                var t = UvTransform.FromComponents(a, b, c, d, e, Cur().Dir);
                Edit(x => set(x, t));
                FillRaw(t, null);
            }
            finally
            {
                sync = false;
            }
        }

        void FromRaw(object sender)
        {
            if (sync || building) return;
            var f = new float[12];
            for (int i = 0; i < 12; i++)
                if (!TryF(raw[i].Text, out f[i])) return;
            var t = new UvTransform(new(f[0], f[1], f[2]), new(f[3], f[4], f[5]), new(f[6], f[7], f[8]), new(f[9], f[10], f[11]));
            sync = true;
            try
            {
                Edit(x => set(x, t));
                FillComponents(t, sender);
            }
            finally
            {
                sync = false;
            }
        }

        void Apply(UvTransform t)
        {
            sync = true;
            try
            {
                Edit(x => set(x, t));
                FillComponents(t, null);
                FillRaw(t, null);
            }
            finally
            {
                sync = false;
            }
        }

        foreach (var tb in new[] { su, sv, rot, ou, ov }) tb.TextChanged += (s, _) => FromComponents(s!);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(UIElement e, int row, int col)
        {
            while (g.RowDefinitions.Count <= row) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }
        Cell(SmallLabel("Scale U/V"), 0, 0);
        Cell(su, 0, 1);
        Cell(sv, 0, 2);
        Cell(link, 0, 4);
        Cell(SmallLabel("Rotate °"), 1, 0);
        Cell(rot, 1, 1);
        Cell(SmallLabel("Offset U/V"), 2, 0);
        Cell(ou, 2, 1);
        Cell(ov, 2, 2);
        su.ToolTip = "Scale U (tiling)";
        sv.ToolTip = "Scale V (tiling)";
        rot.ToolTip = "Rotation in degrees";
        ou.ToolTip = "Offset U";
        ov.ToolTip = "Offset V";
        root.Children.Add(g);

        var helpers = new WrapPanel { Margin = new Thickness(52, 2, 0, 0) };
        helpers.Children.Add(SmallBtn("Reset", "Identity transform", () => Apply(UvTransform.Identity with { Dir = Cur().Dir })));
        helpers.Children.Add(SmallBtn("×2", "Double the tiling", () => Apply(Scaled(Cur(), 2))));
        helpers.Children.Add(SmallBtn("÷2", "Halve the tiling", () => Apply(Scaled(Cur(), 0.5f))));
        helpers.Children.Add(SmallBtn("+90°", "Rotate by 90°", () =>
        {
            Cur().Decompose(out var a, out var b, out var c, out var d, out var e);
            Apply(UvTransform.FromComponents(a, b, c + 90, d, e, Cur().Dir));
        }));
        root.Children.Add(helpers);

        var matrix = new Expander { Header = "Matrix (aside / up / dir / pos)", FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
        var mg = new Grid();
        mg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        for (int i = 0; i < 3; i++) mg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var names = new[] { "aside", "up", "dir", "pos" };
        for (int r = 0; r < 4; r++)
        {
            mg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = SmallLabel(names[r]);
            Grid.SetRow(l, r);
            mg.Children.Add(l);
            for (int k = 0; k < 3; k++)
            {
                var tb = Num(0);
                raw[r * 3 + k] = tb;
                Grid.SetRow(tb, r);
                Grid.SetColumn(tb, k + 1);
                mg.Children.Add(tb);
            }
        }
        FillRaw(t0, null);
        foreach (var tb in raw) tb.TextChanged += (s, _) => FromRaw(s!);
        matrix.Content = mg;
        root.Children.Add(matrix);
        return root;
    }

    static UvTransform Scaled(UvTransform t, float f) => t with { Aside = t.Aside * f, Up = t.Up * f };

    void ColorRow(Grid g, string label, Func<Rvmat, Rgba> get, Action<Rvmat, Rgba> set)
    {
        var c = working is null ? Rgba.White : get(working);
        var swatch = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = Dlg.Res("Editor.Faint"),
            Margin = new Thickness(0, 1, 4, 1),
            Cursor = Cursors.Hand,
            ToolTip = "Click for sliders",
        };
        var boxes = new[] { Num(c.R), Num(c.G), Num(c.B), Num(c.A) };
        string[] tips = ["R", "G", "B", "A"];
        for (int i = 0; i < 4; i++) boxes[i].ToolTip = $"{label} {tips[i]}";

        void Paint(Rgba v) => swatch.Background = new SolidColorBrush(Color.FromRgb(B(v.R), B(v.G), B(v.B)));
        Paint(c);

        bool fromSlider = false;
        Slider[]? sliders = null;
        foreach (var b in boxes)
            b.TextChanged += (_, _) =>
            {
                if (!(TryF(boxes[0].Text, out var r) && TryF(boxes[1].Text, out var gg) && TryF(boxes[2].Text, out var bb) && TryF(boxes[3].Text, out var a))) return;
                var v = new Rgba(r, gg, bb, a);
                Paint(v);
                Edit(x => set(x, v));
                if (sliders is not null && !fromSlider)
                {
                    fromSlider = true;
                    sliders[0].Value = r;
                    sliders[1].Value = gg;
                    sliders[2].Value = bb;
                    sliders[3].Value = a;
                    fromSlider = false;
                }
            };

        var popup = new Popup { PlacementTarget = swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            if (sliders is null)
            {
                var sp = new StackPanel { Width = 220, Margin = new Thickness(8) };
                sliders = new Slider[4];
                for (int i = 0; i < 4; i++)
                {
                    var k = i;
                    var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                    var l = new TextBlock { Text = tips[i], Width = 16, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                    row.Children.Add(l);
                    TryF(boxes[i].Text, out var init);
                    var s = new Slider { Minimum = 0, Maximum = 1, Value = Math.Clamp(init, 0, 1), SmallChange = 0.01, LargeChange = 0.1, MinWidth = 0 };
                    s.ValueChanged += (_, e) =>
                    {
                        if (fromSlider) return;
                        fromSlider = true;
                        boxes[k].Text = Fmt((float)Math.Round(e.NewValue, 3));
                        fromSlider = false;
                    };
                    sliders[i] = s;
                    row.Children.Add(s);
                    sp.Children.Add(row);
                }
                popup.Child = new Border
                {
                    Child = sp,
                    Background = Dlg.Res("Editor.PanelBackground"),
                    BorderBrush = Dlg.Res("Editor.Faint"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                };
            }
            popup.IsOpen = true;
        };

        var row2 = new Grid();
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 4; i++) row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(swatch, 0);
        row2.Children.Add(swatch);
        for (int i = 0; i < 4; i++)
        {
            Grid.SetColumn(boxes[i], i + 1);
            row2.Children.Add(boxes[i]);
        }
        row2.Children.Add(popup);
        AddRow(g, label, row2);
    }

    static byte B(float v) => (byte)Math.Clamp((int)Math.Round(Math.Clamp(v, 0, 1) * 255), 0, 255);

    // ── status / source ─────────────────────────────────────────────────────────────────────────────────────

    void UpdateStatus()
    {
        status.Inlines.Clear();
        if (path is null || working is null)
        {
            status.Inlines.Add(new Run("No material open"));
            return;
        }
        var full = Store.Resolve(path) ?? path;
        status.Inlines.Add(new Run(full));
        var exists = File.Exists(full);
        if (!exists) status.Inlines.Add(new Run(" · FILE NOT FOUND — showing an empty template (Save would create the file). Wrong first folder? Surfaces ▸ Fix Missing Texture Paths") { Foreground = Dlg.Res("Editor.Warn") });
        else if (loadError is not null && loadError != "file not found") status.Inlines.Add(new Run($" · {loadError}") { Foreground = Dlg.Res("Editor.Error") });
        if (working.WasBinarized) status.Inlines.Add(new Run(" · binarized (raP): Save writes text"));
        if (working.Warnings.Count > 0)
        {
            var w = new Run($" · {working.Warnings.Count} parser note(s)") { Foreground = Dlg.Res("Editor.Warn") };
            status.Inlines.Add(w);
            status.ToolTip = string.Join("\n", working.Warnings.Take(30));
        }
        else status.ToolTip = null;
        // The most common "why is it shiny like glass?": no _smdi texture = white = full specular and glossiness.
        if (working.FindStage(StageRole.Specular) is { } smdi && smdi.Texture.Trim().Length == 0)
        {
            status.Inlines.Add(new LineBreak());
            status.Inlines.Add(new Run($"⚠ {smdi.Name} (_smdi) has no texture → full specular + gloss, the surface looks like glass. " +
                                       "Set an _smdi.paa, or for a matte look #(argb,8,8,3)color(1,0.15,0.25,1,SMDI) (G = specular, B = glossiness).")
            { Foreground = Dlg.Res("Editor.Warn") });
        }
        if (Ed.Model.Lods.Length > 0)
        {
            var key = Attributes.PathKey(path);
            int faces = 0;
            var lod = Ed.Lod;
            var hit = new bool[lod.Strings.Length];
            for (int i = 0; i < hit.Length; i++) hit[i] = Attributes.PathKey(lod.Strings[i]) == key;
            for (int f = 0; f < lod.FaceCount; f++)
                if (hit[lod.FaceMaterial[f]]) faces++;
            status.Inlines.Add(faces > 0
                ? new Run($" · {faces:N0} face(s) in this LOD")
                : new Run(" · not used by any face of this LOD — edits will not show here (Assign puts it on the selected faces)") { Foreground = Dlg.Res("Editor.Warn") });
        }
        if (IsEdited) status.Inlines.Add(new Run(" · ● edited, not saved") { Foreground = Dlg.Res("Editor.Warn"), FontWeight = FontWeights.SemiBold });
    }

    void UpdateSource()
    {
        if (sourceBox is null || working is null || !Expanded.Contains("#source")) return;
        sourceBox.Text = working.ToText();
    }

    void RefreshList()
    {
        building = true;
        try
        {
            var text = picker.Text;
            picker.Items.Clear();
            var used = MaterialActions.UsedMaterials();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, faces) in used)
            {
                listed.Add(Store.Key(p) ?? p);
                picker.Items.Add(Item(p, $"{faces:N0} faces"));
            }
            foreach (var key in Store.OverrideKeys)
                if (listed.Add(key)) picker.Items.Add(Item(MaterialActions.ToModelPath(key), "not used by the model"));
            picker.Text = text;
        }
        finally
        {
            building = false;
        }
    }

    ComboBoxItem Item(string p, string detail)
    {
        var edited = Store.HasOverride(p);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = (edited ? "● " : "") + p, FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = "  " + detail, FontSize = 11, Foreground = Dlg.Res("Editor.Dim") });
        var it = new ComboBoxItem { Content = sp, Tag = p };
        TextSearch.SetText(it, p);
        return it;
    }

    // ── small UI helpers ────────────────────────────────────────────────────────────────────────────────────

    void Header(string text) =>
        body.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(0, 10, 0, 3) });

    static Grid FormGrid()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    static void AddRow(Grid g, string label, UIElement control)
    {
        var row = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 1), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(l, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        g.Children.Add(l);
        g.Children.Add(control);
    }

    static TextBlock SmallLabel(string text) =>
        new() { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Dlg.Res("Editor.Dim"), Margin = new Thickness(0, 0, 4, 0) };

    TextBox Num(float v) => new() { Text = Fmt(v), Style = numStyle };

    TextBox Text(string v) => new() { Text = v, Style = numStyle };

    ComboBox EditableCombo(IEnumerable<string> items, string value, Action<string> onChange)
    {
        var cb = new ComboBox { IsEditable = true, MinWidth = 0, FontSize = 12, Margin = new Thickness(1), ItemsSource = items.ToList(), Text = value };
        var last = value.Trim();
        cb.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            var t = cb.Text.Trim();
            if (building || t == last) return;
            last = t;
            onChange(t);
        }));
        return cb;
    }

    Button IconBtn(string glyph, string tip, Action a)
    {
        var b = new Button { Content = glyph, Style = (Style)FindResource("IconButton"), ToolTip = tip, Margin = new Thickness(2, 0, 0, 0) };
        b.Click += (_, _) => Dlg.Try(a);
        return b;
    }

    Button Btn(string text, string tip, Action a)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("CompactButton"), ToolTip = tip, Margin = new Thickness(0, 2, 4, 2), Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
        b.Click += (_, _) => Dlg.Try(a);
        return b;
    }

    Button SmallBtn(string text, string tip, Action a)
    {
        var b = Btn(text, tip, a);
        b.Padding = new Thickness(6, 0, 6, 0);
        b.MinHeight = 20;
        b.FontSize = 11;
        return b;
    }

    static string Fmt(float v) => ConfigValue.FormatFloat(v);

    static bool TryF(string s, out float v) =>
        float.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, Inv, out v) && float.IsFinite(v);
}
