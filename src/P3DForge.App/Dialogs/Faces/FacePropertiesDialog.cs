using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using P3DForge.App.Dialogs.Resources;
using P3DForge.App.Editor;
using P3DForge.Core;
using P3DForge.Core.Ops;

namespace P3DForge.App.Dialogs.Faces;

/// <summary>
/// Object Builder "Face Properties" (E): texture / material with browse + preview, lighting &amp; shadows, Z bias,
/// texture merging and user value for every selected face (or, with no face selected, the faces whose points are all
/// selected). Mixed values show as blank / indeterminate and stay untouched unless edited. OK and Apply each make one
/// undo step.
/// <para>Automation: "automation.answer" = "texture=…;material=…;lighting=both|position|flat|reversed|normal;
/// shadow=on|off;zbias=none|low|middle|high;merge=on|off;user=0…127" (unset → cancelled).</para>
/// </summary>
public sealed class FacePropertiesDialog
{
    static EditorState Ed => AppServices.Editor;

    readonly int lodIndex;
    readonly byte[] faces;
    readonly FaceSummary summary;
    readonly string initialTexture, initialMaterial;
    readonly TextBox texture, material;
    readonly Image preview = new() { Width = 96, Height = 96, Stretch = Stretch.Uniform };
    readonly TextBlock previewInfo = new() { FontSize = 11, Width = 96, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
    readonly List<IFlagField> fields = [];
    readonly TextBlock error = Dlg.ErrorText();
    readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Window window;
    Button? ok;

    /// <summary>Opens the dialog for the current selection of the active LOD.</summary>
    public static void Show()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lod = Ed.Lod;
        var sel = Ed.GetSelection();
        var faces = Attributes.EffectiveFaces(lod, sel.Points, sel.Faces);
        var summary = Attributes.SummarizeFaces(lod, faces);
        if (Dlg.Automated) Ed.Log($"[perf] face summary of {summary.Count:N0} faces: {sw.ElapsedMilliseconds} ms");
        if (summary.Count == 0)
        {
            Dlg.Fail("Face Properties: select faces (or all points of some faces) first.");
            return;
        }
        bool derived = !sel.Faces.AsSpan().ContainsAnyExcept((byte)0);
        new FacePropertiesDialog(Ed.ActiveLod, faces, summary, derived).Run();
    }

    FacePropertiesDialog(int lodIndex, byte[] faces, FaceSummary summary, bool derived)
    {
        this.lodIndex = lodIndex;
        this.faces = faces;
        this.summary = summary;
        var flags = summary.Flags;
        initialTexture = summary.Texture ?? "";
        initialMaterial = summary.Material ?? "";

        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
        var head = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        head.Inlines.Add(new System.Windows.Documents.Run($"{summary.Count:N0}") { FontWeight = FontWeights.SemiBold });
        head.Inlines.Add(new System.Windows.Documents.Run(summary.Count == 1 ? $" face selected (face #{summary.First})" : " faces selected"));
        if (derived) head.Inlines.Add(new System.Windows.Documents.Run("  — faces whose points are all selected") { Foreground = Dlg.Res("Editor.Dim") });
        root.Children.Add(head);

        // ---- texture & material -------------------------------------------------------------------------
        texture = Ui.Mono(initialTexture, 360);
        material = Ui.Mono(initialMaterial, 360);
        var tm = new Grid();
        tm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 5; i++) tm.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(UIElement e, int row, int col, int rowSpan = 1)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            Grid.SetRowSpan(e, rowSpan);
            tm.Children.Add(e);
        }
        Put(Ui.Caption("_Texture", 58, texture), 0, 0);
        Put(texture, 0, 1);
        var browseTex = Ui.SmallButton("Browse…", "Pick a texture from the model or the project folder");
        browseTex.Click += (_, _) => Browse(texture, ResourceKind.Texture);
        Put(browseTex, 0, 2);
        if (summary.TextureMixed) Put(Ui.Hint("Mixed textures — leave blank to keep each face's texture", 360), 1, 1);
        Put(Ui.Caption("Mate_rial", 58, material), 2, 0);
        Put(material, 2, 1);
        var browseMat = Ui.SmallButton("Browse…", "Pick a material (.rvmat) from the model or the project folder");
        browseMat.Click += (_, _) => Browse(material, ResourceKind.Material);
        Put(browseMat, 2, 2);
        if (summary.MaterialMixed) Put(Ui.Hint("Mixed materials — leave blank to keep each face's material", 360), 3, 1);
        var merge = new FlagCheck("merge", "Enable texture merging", FaceFlags.DisableTexMerge, flags, inverted: true,
            "Allows the engine to merge this face's texture into a texture atlas (flag 0x01000000 clear)");
        fields.Add(merge);
        Put(merge.Box, 4, 1);
        var thumb = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        thumb.Children.Add(new Border
        {
            Width = 100,
            Height = 100,
            BorderBrush = Dlg.Res("Editor.Faint"),
            BorderThickness = new Thickness(1),
            Background = Checker(),
            Child = preview,
        });
        thumb.Children.Add(previewInfo);
        Put(thumb, 0, 3, 5);
        root.Children.Add(Ui.Group("Texture & material", tm));
        if (summary.Count == 1) root.Children.Add(MappingGroup(Ed.Model.Lods[lodIndex], summary.First));

        // ---- lighting, z bias, user ---------------------------------------------------------------------
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var lighting = new FlagRadioGroup("lighting", "Lighting & shadows", FaceFlags.BothSidesLight | FaceFlags.SkyLight | FaceFlags.FlatLight | FaceFlags.ReverseLight, flags,
            ("Normal", 0), ("Both sides", FaceFlags.BothSidesLight), ("Position", FaceFlags.SkyLight), ("Flat", FaceFlags.FlatLight), ("Reversed (transparent)", FaceFlags.ReverseLight));
        var shadow = new FlagCheck("shadow", "Enable s_hadow", FaceFlags.NoShadow, flags, inverted: true, "Casts shadows (flag 0x10 clear)");
        ((StackPanel)lighting.Box.Content).Children.Add(shadow.Box);
        fields.Add(lighting);
        fields.Add(shadow);
        row.Children.Add(lighting.Box);
        var zbias = new FlagRadioGroup("zbias", "Z bias", FaceFlags.ZBiasMask, flags,
            ("None", 0), ("Low", FaceFlags.ZBiasStep), ("Middle", 2 * FaceFlags.ZBiasStep), ("High", 3 * FaceFlags.ZBiasStep));
        fields.Add(zbias);
        row.Children.Add(zbias.Box);
        var user = new FlagNumber("user", FaceFlags.UserMask, FaceFlags.UserShift, flags);
        fields.Add(user);
        var userPanel = new StackPanel();
        userPanel.Children.Add(Ui.Row(Ui.Caption("_User value", 0, user.Box), user.Box));
        var raw = Ui.Hint(flags.Count == 1 || !flags.IsMixed(uint.MaxValue) ? $"flags 0x{flags.And:X8}" : $"flags vary (AND 0x{flags.And:X8}, OR 0x{flags.Or:X8})");
        raw.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        userPanel.Children.Add(raw);
        row.Children.Add(Ui.Group("Other", userPanel));
        root.Children.Add(row);
        root.Children.Add(error);

        // ---- buttons ------------------------------------------------------------------------------------
        window = Dlg.MakeWindow("Face Properties", root, 520);
        var (buttons, okButton) = Dlg.OkCancel(window, Apply);
        ok = okButton;
        var apply = Dlg.Button("_Apply");
        apply.Click += (_, _) => Apply();
        buttons.Children.Add(apply);
        root.Children.Add(buttons);

        foreach (var f in fields) f.Changed += () => Validate();
        texture.TextChanged += (_, _) =>
        {
            previewTimer.Stop();
            previewTimer.Start();
        };
        previewTimer.Tick += (_, _) =>
        {
            previewTimer.Stop();
            UpdatePreview();
        };
        window.Closed += (_, _) => previewTimer.Stop();
        window.Loaded += (_, _) =>
        {
            texture.Focus();
            texture.SelectAll();
        };
        UpdatePreview();
    }

    void Run()
    {
        if (Dlg.Automated)
        {
            RunAutomated();
            return;
        }
        window.ShowDialog();
    }

    void RunAutomated()
    {
        var answer = Dlg.TakeAnswer();
        if (answer is not null) ApplyAnswer(answer);
        // let the thumbnail decode finish so the capture shows it
        var frame = new DispatcherFrame();
        var until = DateTime.UtcNow.AddMilliseconds(1500);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        t.Tick += (_, _) =>
        {
            if (!loading || DateTime.UtcNow > until)
            {
                t.Stop();
                frame.Continue = false;
            }
        };
        t.Start();
        Dispatcher.PushFrame(frame);
        Dlg.Capture(window, "face-properties");
        if (answer is null)
        {
            Ed.Log("[automation] Face Properties → cancel (no automation.answer)");
            return;
        }
        bool applied = Apply();
        Ed.Log($"[automation] Face Properties \"{answer}\" → {(applied ? "applied" : "rejected: " + error.Text)}", applied ? LogLevel.Info : LogLevel.Warn);
    }

    void ApplyAnswer(string answer)
    {
        foreach (var (k, v) in Ui.ParseAnswer(answer))
        {
            if (k.Equals("texture", StringComparison.OrdinalIgnoreCase)) texture.Text = v;
            else if (k.Equals("material", StringComparison.OrdinalIgnoreCase)) material.Text = v;
            else if (k.Equals("uv", StringComparison.OrdinalIgnoreCase) && uvBoxes is not null)
            {
                var parts = v.Split(',');
                for (int i = 0; i < uvBoxes.Length && i < parts.Length; i++) uvBoxes[i].Text = parts[i].Trim();
            }
            else if (k.Equals("browse", StringComparison.OrdinalIgnoreCase)) Browse(v.StartsWith("mat", StringComparison.OrdinalIgnoreCase) ? material : texture, v.StartsWith("mat", StringComparison.OrdinalIgnoreCase) ? ResourceKind.Material : ResourceKind.Texture);
            else if (fields.FirstOrDefault(f => f.Key.Equals(k, StringComparison.OrdinalIgnoreCase)) is { } f)
            {
                if (!f.SetFromText(v)) Ed.Log($"[automation] Face Properties: bad value {k}={v}", LogLevel.Warn);
            }
            else Ed.Log($"[automation] Face Properties: unknown key '{k}'", LogLevel.Warn);
        }
        UpdatePreview();
    }

    string? Validate()
    {
        string? e = null;
        foreach (var f in fields)
            e ??= f.Error;
        if (uvBoxes is not null)
            foreach (var b in uvBoxes)
                if (!Dlg.TryParse(b.Text, out _)) e ??= "UV coordinates must be numbers.";
        Dlg.SetError(error, e);
        if (ok is not null) ok.IsEnabled = e is null;
        return e;
    }

    /// <summary>Applies the edited fields to the faces as one undo step; false (dialog stays open) on invalid input.</summary>
    bool Apply()
    {
        if (Validate() is not null) return false;
        var tex = AppServices.NormalizeGamePath(texture.Text); // never store "P:\..." in the model
        var mat = AppServices.NormalizeGamePath(material.Text);
        string? newTex = tex != initialTexture.Trim() && !(summary.TextureMixed && tex.Length == 0) ? tex : null;
        string? newMat = mat != initialMaterial.Trim() && !(summary.MaterialMixed && mat.Length == 0) ? mat : null;
        var edited = fields.Where(f => f.Edited).ToList();
        float[]? newUV = null;
        if (uvBoxes is not null && uvInitial is not null)
        {
            bool uvEdited = false;
            for (int i = 0; i < uvBoxes.Length; i++) uvEdited |= uvBoxes[i].Text.Trim() != uvInitial[i];
            if (uvEdited)
            {
                newUV = new float[uvBoxes.Length];
                for (int i = 0; i < uvBoxes.Length; i++)
                {
                    Dlg.TryParse(uvBoxes[i].Text, out var x);
                    newUV[i] = (float)x;
                }
            }
        }
        if (newTex is null && newMat is null && edited.Count == 0 && newUV is null) return true;
        if (lodIndex >= Ed.Model.Lods.Length || Ed.Model.Lods[lodIndex].FaceCount != faces.Length)
        {
            Dlg.SetError(error, "The LOD changed since the dialog opened; reopen Face Properties.");
            return false;
        }
        var what = new List<string>();
        if (newTex is not null) what.Add("texture");
        if (newMat is not null) what.Add("material");
        foreach (var f in edited) what.Add(f.Key);
        if (newUV is not null) what.Add("mapping");
        bool changed = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Dlg.Try(() => changed = Ed.EditLod($"Face properties ({string.Join(", ", what)}; {summary.Count:N0} faces)", lod =>
        {
            var l = Attributes.SetFaceTextureMaterial(lod, faces, newTex, newMat);
            foreach (var f in edited) l = Attributes.SetFaceFlags(l, faces, f.Mask, f.Value);
            if (newUV is not null) l = Attributes.SetFaceUV(l, summary.First, newUV);
            return l;
        }, new EditOptions { LodIndex = lodIndex, KeepNormals = true }));
        if (changed) Ed.Log($"Face properties: {string.Join(", ", what)} set on {summary.Count:N0} faces" + (Dlg.Automated ? $" [perf {sw.ElapsedMilliseconds} ms]" : ""));
        return true;
    }

    // ---- mapping (single face) --------------------------------------------------------------------------

    TextBox[]? uvBoxes;
    string[]? uvInitial;

    /// <summary>Object Builder's Mapping group: U/V of each vertex of the one selected face (stage-0 UV set kept in sync).</summary>
    UIElement MappingGroup(P3DForge.Core.Model.Lod lod, int face)
    {
        int n = lod.FaceVertCount[face];
        uvBoxes = new TextBox[n * 2];
        uvInitial = new string[n * 2];
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        for (int k = 0; k < 4; k++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < 3; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }
        Put(Ui.Caption("U"), 1, 0);
        Put(Ui.Caption("V"), 2, 0);
        for (int k = 0; k < n; k++)
        {
            var head = Ui.Caption($"v{k + 1}  (#{lod.FacePoints[face * 4 + k]})");
            head.Margin = new Thickness(2, 0, 6, 2);
            Put(head, 0, k + 1);
            for (int c = 0; c < 2; c++)
            {
                int i = k * 2 + c;
                uvInitial[i] = lod.FaceUV[face * 8 + i].ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                var box = Ui.Mono(uvInitial[i], 92);
                box.TextChanged += (_, _) => Validate();
                uvBoxes[i] = box;
                Put(box, c + 1, k + 1);
            }
        }
        return Ui.Group($"Mapping (face #{face}, {n} vertices)", g);
    }

    // ---- browse / preview ---------------------------------------------------------------------------------

    void Browse(TextBox box, ResourceKind kind)
    {
        var picked = ResourcePicker.Pick(window, kind, box.Text.Trim());
        if (picked is null) return;
        box.Text = picked;
        if (box == texture) UpdatePreview();
    }

    bool loading;

    void UpdatePreview()
    {
        var path = texture.Text.Trim();
        previewInfo.Foreground = Dlg.Res("Editor.Dim");
        if (path.Length == 0)
        {
            preview.Source = null;
            previewInfo.Text = summary.TextureMixed ? "mixed" : "no texture";
            return;
        }
        if (path.StartsWith('#'))
        {
            preview.Source = Thumbnails.Procedural(path);
            previewInfo.Text = "procedural";
            return;
        }
        var abs = AppServices.ResolveProjectPath(path);
        if (abs is null || !File.Exists(abs))
        {
            preview.Source = null;
            previewInfo.Text = "not found under " + AppServices.Settings.ProjectRoot;
            previewInfo.Foreground = Dlg.Res("Editor.Warn");
            return;
        }
        loading = true;
        previewInfo.Text = "loading…";
        Thumbnails.RequestFile(abs, 128, img =>
        {
            if (!string.Equals(texture.Text.Trim(), path, StringComparison.Ordinal)) return; // changed meanwhile
            loading = false;
            preview.Source = img;
            previewInfo.Text = img is System.Windows.Media.Imaging.BitmapSource b ? $"{b.PixelWidth}×{b.PixelHeight} preview" : img is null ? "cannot decode" : "";
            if (img is null) previewInfo.Foreground = Dlg.Res("Editor.Warn");
        });
    }

    /// <summary>Checkerboard background so alpha shows in the preview.</summary>
    static Brush Checker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var dark = new GeometryGroup();
        dark.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        dark.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)), null, dark));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }
}
