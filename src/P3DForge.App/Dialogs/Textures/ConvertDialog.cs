using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using P3DForge.App.Dialogs.Ops;
using P3DForge.App.Editor;
using P3DForge.Core.Imaging;
using P3DForge.Core.Paa;

namespace P3DForge.App.Dialogs.Textures;

/// <summary>Options of a batch conversion.</summary>
/// <param name="Format">Forced format, or null = by suffix (TexConvert rules).</param>
/// <param name="Resize">Resize non power-of-two images to the nearest power of two (otherwise they are refused).</param>
/// <param name="OutputFolder">Target folder, or null = next to each source.</param>
/// <param name="Overwrite">Replace existing targets (a .bak is kept when Settings.BackupOnSave).</param>
public sealed record ConvertOptions(PaaFormat? Format, bool Resize, string? OutputFolder, bool Overwrite);

/// <summary>
/// "Convert to PAA…" / "Convert folder to PAA…" / "Export PAA to PNG…": option dialog (Dialogs/Ops/Form), then a
/// background batch with a progress window (Cancel stops after the current file). Results go to the log; every written
/// texture is announced through the UI flag "textures.changed" (= saved path) so viewports reload it.
/// Automation: files come from the flag "textures.convertFiles" (';'-separated) / "textures.convertFolder", options
/// from "automation.answer", and output is forced into testdata/out/textures.
/// </summary>
public static class ConvertDialog
{
    static EditorState Ed => AppServices.Editor;

    static readonly string[] FormatChoices = ["Auto (by suffix)", "DXT1", "DXT5", "ARGB8888", "ARGB4444", "ARGB1555", "AI88"];

    static PaaFormat? FormatOf(int index) => index <= 0 ? null : PaaFormats.Parse(FormatChoices[index]);

    static string LastFolder
    {
        get => AppServices.Settings.Get("textures.lastFolder", AppServices.Settings.ProjectRoot);
        set => AppServices.Settings.Set("textures.lastFolder", value);
    }

    // ---- commands --------------------------------------------------------------------------------------------

    public static void ConvertFiles()
    {
        string[] files;
        if (Dlg.Automated) files = SplitList(Ed.Flag<string?>("textures.convertFiles", null));
        else
        {
            var dlg = new OpenFileDialog { Title = "Convert to PAA", Filter = TextureIO.ImageFilter, Multiselect = true, InitialDirectory = ExistingFolder(LastFolder) };
            if (dlg.ShowDialog(TextureIO.Owner) != true) return;
            files = dlg.FileNames;
            LastFolder = Path.GetDirectoryName(files[0]) ?? LastFolder;
        }
        if (files.Length == 0)
        {
            Dlg.Fail("Convert to PAA: no files");
            return;
        }
        if (AskOptions("Convert to PAA", files, folderMode: false, out var options, out _)) _ = RunToPaa("Convert to PAA", files, options);
    }

    public static void ConvertFolder()
    {
        string? folder;
        if (Dlg.Automated) folder = Ed.Flag<string?>("textures.convertFolder", null);
        else
        {
            var dlg = new OpenFolderDialog { Title = "Convert folder to PAA", InitialDirectory = ExistingFolder(LastFolder) };
            if (dlg.ShowDialog(TextureIO.Owner) != true) return;
            folder = dlg.FolderName;
            LastFolder = folder;
        }
        if (folder is null || !Directory.Exists(folder))
        {
            Dlg.Fail("Convert folder to PAA: folder not found");
            return;
        }
        var preview = Directory.EnumerateFiles(folder).Where(TextureIO.IsImage).ToArray();
        if (!AskOptions("Convert folder to PAA", preview, folderMode: true, out var options, out bool recursive, folder)) return;
        var files = Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(TextureIO.IsImage).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
        {
            Dlg.Fail($"Convert folder to PAA: no images in {folder}");
            return;
        }
        _ = RunToPaa("Convert folder to PAA", files, options);
    }

    public static void ExportPaa()
    {
        string[] files;
        if (Dlg.Automated) files = SplitList(Ed.Flag<string?>("textures.convertFiles", null));
        else
        {
            var dlg = new OpenFileDialog { Title = "Export PAA to PNG / TGA", Filter = "PAA textures (*.paa)|*.paa", Multiselect = true, InitialDirectory = ExistingFolder(LastFolder) };
            if (dlg.ShowDialog(TextureIO.Owner) != true) return;
            files = dlg.FileNames;
            LastFolder = Path.GetDirectoryName(files[0]) ?? LastFolder;
        }
        files = files.Where(TextureIO.IsPaa).ToArray();
        if (files.Length == 0)
        {
            Dlg.Fail("Export PAA: no .paa files");
            return;
        }
        var s = AppServices.Settings;
        var form = new Form("Export PAA", $"{files.Length} texture(s): {string.Join(", ", files.Take(6).Select(Path.GetFileName))}{(files.Length > 6 ? ", …" : "")}\nThe largest mip is written with the swizzle undone (as the texture tools show it).")
            .Choice("format", "Image format", ["PNG", "TGA"], s.Get("textures.export.format", 0))
            .Text("out", "Output folder (empty = next to each .paa)", s.Get("textures.export.out", ""));
        form.Action("Browse…", f => BrowseInto(f, "out"));
        if (!form.Show("Export")) return;
        s.Set("textures.export.format", form.Index("format"));
        s.Set("textures.export.out", form.Str("out"));
        string ext = form.Index("format") == 1 ? ".tga" : ".png";
        string? outDir = OutputFolder(form.Str("out"));
        _ = RunBatch("Export PAA", files, file =>
        {
            var target = Path.Combine(outDir ?? Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ext);
            if (!TextureIO.MayWrite(target)) throw new InvalidOperationException("automation may only write into testdata/out/textures");
            var tex = TextureIO.Load(file);
            TextureIO.Write(target, TextureIO.Encode(tex.Image, target));
            return (target, $"{tex.Image.Width}×{tex.Image.Height}");
        });
    }

    // ---- options ---------------------------------------------------------------------------------------------

    static bool AskOptions(string title, IReadOnlyList<string> files, bool folderMode, out ConvertOptions options, out bool recursive, string? folder = null)
    {
        var s = AppServices.Settings;
        var lines = files.Take(8).Select(f =>
        {
            var hint = TextureHints.ForPath(f);
            var fixedFormat = hint.Format is { } fmt ? PaaFormats.Name(fmt) : "DXT1 / DXT5 by alpha";
            return $"• {Path.GetFileName(f)} → {hint.Kind} ({fixedFormat}{(hint.Swizzle is null ? "" : ", swizzled")})";
        }).ToList();
        if (files.Count > 8) lines.Add($"… and {files.Count - 8} more");
        string intro = folderMode
            ? $"Folder: {folder}\n{files.Count} image(s) at the top level{(files.Count > 0 ? ":" : ".")}\n{string.Join("\n", lines)}"
            : string.Join("\n", lines);
        intro += "\nThe file-name suffix picks the rules (TexConvert.cfg): _co/_ca colour, _nohq normal (swizzled DXT5), _smdi, _as, _mc, …";
        var form = new Form(title, intro)
            .Choice("format", "Format", FormatChoices, s.Get("textures.convert.format", 0))
            .Check("resize", "Resize non power-of-two images to the nearest power of two", s.Get("textures.convert.resize", false))
            .Text("out", "Output folder (empty = next to each source)", s.Get("textures.convert.out", ""))
            .Check("overwrite", "Overwrite existing .paa files", s.Get("textures.convert.overwrite", true));
        if (folderMode) form.Check("recursive", "Include subfolders", s.Get("textures.convert.recursive", false));
        form.Action("Browse…", f => BrowseInto(f, "out"));
        options = new ConvertOptions(null, false, null, true);
        recursive = false;
        if (!form.Show("Convert")) return false;
        s.Set("textures.convert.format", form.Index("format"));
        s.Set("textures.convert.resize", form.Bool("resize"));
        s.Set("textures.convert.out", form.Str("out"));
        s.Set("textures.convert.overwrite", form.Bool("overwrite"));
        if (folderMode)
        {
            recursive = form.Bool("recursive");
            s.Set("textures.convert.recursive", recursive);
        }
        options = new ConvertOptions(FormatOf(form.Index("format")), form.Bool("resize"), OutputFolder(form.Str("out")), form.Bool("overwrite"));
        return true;
    }

    static string? OutputFolder(string text)
    {
        if (Dlg.Automated) return TextureIO.AutomationOutDir();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    static void BrowseInto(Form f, string key)
    {
        var dlg = new OpenFolderDialog { Title = "Output folder", InitialDirectory = ExistingFolder(LastFolder) };
        if (dlg.ShowDialog(TextureIO.Owner) == true) f.Set(key, dlg.FolderName);
    }

    static string ExistingFolder(string? folder) => folder is not null && Directory.Exists(folder) ? folder : AppServices.Settings.ProjectRoot;

    static string[] SplitList(string? s) => (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- conversion ------------------------------------------------------------------------------------------

    /// <summary>Converts one image file to PAA; returns the written path and a short description.</summary>
    public static (string Path, string Info) ConvertOne(string source, ConvertOptions o)
    {
        var target = Path.Combine(o.OutputFolder ?? Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + ".paa");
        if (Path.GetFullPath(target).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("source is already a .paa");
        if (!TextureIO.MayWrite(target)) throw new InvalidOperationException("automation may only write into testdata/out/textures");
        if (!o.Overwrite && File.Exists(target)) throw new InvalidOperationException($"{Path.GetFileName(target)} exists (overwrite is off)");
        var img = TextureIO.Load(source).Image;
        var hint = TextureHints.ForPath(target);
        var format = hint.ResolveFormat(!img.IsOpaque(), o.Format);
        string resized = "";
        if (PaaWriter.CheckSize(img.Width, img.Height, format) is { } error)
        {
            if (!o.Resize || img.HasPowerOfTwoSize) throw new InvalidOperationException(error + (o.Resize ? "" : " — enable \"Resize\" or fix the source"));
            int w = Math.Max(4, RgbaImage.NearestPowerOfTwo(img.Width)), h = Math.Max(4, RgbaImage.NearestPowerOfTwo(img.Height));
            resized = $" (resized from {img.Width}×{img.Height})";
            img = ImageOps.Resize(img, w, h);
        }
        var bytes = PaaWriter.Encode(img, new PaaEncodeOptions { Hint = hint, Format = o.Format });
        TextureIO.Write(target, bytes);
        return (target, $"{PaaFormats.Name(format)} {img.Width}×{img.Height}{resized}, {hint.Kind}");
    }

    static Task RunToPaa(string title, IReadOnlyList<string> files, ConvertOptions o) => RunBatch(title, files, f => ConvertOne(f, o));

    /// <summary>Runs <paramref name="work"/> for every file on a background thread with a progress window.</summary>
    public static async Task RunBatch(string title, IReadOnlyList<string> files, Func<string, (string Path, string Info)> work)
    {
        bool cancel = false;
        Window? window = null;
        TextBlock? text = null;
        ProgressBar? bar = null;
        if (!Dlg.Automated)
        {
            var panel = new StackPanel { Margin = new Thickness(16), Width = 420 };
            text = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
            bar = new ProgressBar { Height = 6, Maximum = files.Count };
            var cancelButton = Dlg.Button("Cancel", isCancel: true);
            cancelButton.HorizontalAlignment = HorizontalAlignment.Right;
            cancelButton.Margin = new Thickness(0, 12, 0, 0);
            cancelButton.Click += (_, _) =>
            {
                cancel = true;
                cancelButton.IsEnabled = false;
                text.Text = "Cancelling after the current file…";
            };
            panel.Children.Add(text);
            panel.Children.Add(bar);
            panel.Children.Add(cancelButton);
            window = Dlg.MakeWindow(title, panel);
            window.Closing += (_, e) =>
            {
                if (!cancel && bar.Value < files.Count) // closing the window = cancel
                {
                    cancel = true;
                    e.Cancel = true;
                    cancelButton.IsEnabled = false;
                }
            };
            window.Show();
        }
        int ok = 0, failed = 0;
        var total = Stopwatch.StartNew();
        for (int i = 0; i < files.Count && !cancel; i++)
        {
            var file = files[i];
            if (text is not null && !cancel) text.Text = $"{i + 1} / {files.Count}: {Path.GetFileName(file)}";
            try
            {
                var sw = Stopwatch.StartNew();
                var (path, info) = await Task.Run(() => work(file));
                Ed.Log($"{title}: {Path.GetFileName(file)} → {path} ({info}) in {sw.ElapsedMilliseconds} ms");
                TextureIO.NotifyChanged(path);
                ok++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
            {
                Ed.Log($"{title}: {Path.GetFileName(file)}: {ex.Message}", LogLevel.Warn);
                failed++;
            }
            if (bar is not null) bar.Value = i + 1;
        }
        if (window is not null)
        {
            cancel = true;
            window.Close();
        }
        int skipped = files.Count - ok - failed;
        Ed.Log($"{title}: {ok} written{(failed > 0 ? $", {failed} failed" : "")}{(skipped > 0 ? $", {skipped} cancelled" : "")} ({total.Elapsed.TotalSeconds:F1} s)",
            failed > 0 ? LogLevel.Warn : LogLevel.Info);
    }
}
