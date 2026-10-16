using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using P3DForge.App.Services;
using P3DForge.Core.Imaging;
using P3DForge.Core.Paa;
using PaaCodec = P3DForge.Core.Paa.Paa;

namespace P3DForge.App.Dialogs.Textures;

/// <summary>A loaded texture: pixels (swizzle undone, rows top-first) + PAA header when it came from a .paa.</summary>
public sealed record LoadedTexture(string Path, RgbaImage Image, PaaHeader? Header)
{
    public string Describe()
    {
        var name = System.IO.Path.GetFileName(Path);
        if (Header is not { } h) return $"{name} — {Image.Width}×{Image.Height} {System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant()}";
        bool lzo = h.Mips.Any(m => m.Compression == "lzo"), lzss = h.Mips.Any(m => m.Compression == "lzss");
        string comp = lzo ? ", LZO" : lzss ? ", LZSS" : "";
        string alpha = h.Flags is { } f && (f & 3) != 0 ? (f & 2) != 0 ? ", 1-bit alpha" : ", alpha" : "";
        return $"{name} — {h.Format} {Image.Width}×{Image.Height}, {h.Mips.Count} mips{comp}{alpha}{(h.Swizzle is null ? "" : ", swizzled")}";
    }
}

/// <summary>
/// Image file IO for the texture tools: PNG / JPG / BMP / TIFF / GIF through WPF codecs, TGA and PAA through
/// P3DForge.Core. Load/Save are thread-safe (no UI objects escape), so they run on background threads.
/// </summary>
public static class TextureIO
{
    public static readonly string[] ImageExtensions = [".png", ".tga", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif"];

    public const string ImageFilter = "Images (*.png;*.tga;*.jpg;*.bmp;*.tif)|*.png;*.tga;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|All files (*.*)|*.*";
    public const string TextureFilter = "Textures (*.paa;*.png;*.tga;*.jpg;*.bmp;*.tif)|*.paa;*.png;*.tga;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|All files (*.*)|*.*";

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsPaa(string path) => Path.GetExtension(path).Equals(".paa", StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads any supported file. PAA: the largest mip, swizzle undone; alpha forced opaque when the
    /// file declares no alpha (what the engine renders).</summary>
    public static LoadedTexture Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (IsPaa(path))
        {
            var header = PaaCodec.ReadHeader(bytes);
            var mip = PaaCodec.DecodeMip(bytes, header, 0);
            if (header.Flags is not { } f || (f & 3) == 0)
                for (int i = 3; i < mip.Rgba.Length; i += 4) mip.Rgba[i] = 255;
            return new LoadedTexture(path, RgbaImage.From(mip), header);
        }
        if (Path.GetExtension(path).Equals(".tga", StringComparison.OrdinalIgnoreCase)) return new LoadedTexture(path, Tga.Read(bytes), null);
        return new LoadedTexture(path, DecodeWpf(bytes), null);
    }

    static RgbaImage DecodeWpf(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = frame.PixelWidth, h = frame.PixelHeight;
        var px = new byte[w * h * 4];
        frame.CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
        return new RgbaImage(w, h, px);
    }

    /// <summary>Encodes PNG (RGBA).</summary>
    public static byte[] EncodePng(RgbaImage img)
    {
        var bmp = ToBitmap(img);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Bytes of <paramref name="img"/> for <paramref name="path"/>'s extension (.paa uses the suffix rules).</summary>
    public static byte[] Encode(RgbaImage img, string path, PaaFormat? format = null) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".paa" => PaaWriter.Encode(img, path, format),
        ".tga" => Tga.Write(img),
        ".png" => EncodePng(img),
        var ext => throw new ArgumentException($"cannot save {ext} files (use .paa, .png or .tga)"),
    };

    /// <summary>Atomic write (temp + rename) with an optional .bak of the previous file.</summary>
    public static void Write(string path, byte[] bytes) => FileService.WriteAtomic(path, bytes, AppServices.Settings.BackupOnSave);

    /// <summary>A frozen Bgra32 bitmap (usable from any thread).</summary>
    public static BitmapSource ToBitmap(RgbaImage img)
    {
        var s = img.Rgba;
        var bgra = new byte[img.Width * img.Height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = s[i + 2];
            bgra[i + 1] = s[i + 1];
            bgra[i + 2] = s[i];
            bgra[i + 3] = s[i + 3];
        }
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, bgra, img.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Downscaled copy whose longest edge is at most <paramref name="maxEdge"/> (same image when it fits).</summary>
    public static RgbaImage Fit(RgbaImage img, int maxEdge)
    {
        int edge = Math.Max(img.Width, img.Height);
        if (edge <= maxEdge) return img;
        double s = (double)maxEdge / edge;
        return ImageOps.Resize(img, Math.Max(1, (int)Math.Round(img.Width * s)), Math.Max(1, (int)Math.Round(img.Height * s)));
    }

    /// <summary>Scratch folder that automation runs may write to (testdata/out/textures).</summary>
    public static string AutomationOutDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "P3DForge.sln")))
                return Path.Combine(d.FullName, "testdata", "out", "textures");
        return Path.Combine(Path.GetTempPath(), "P3DForge", "textures");
    }

    /// <summary>Under automation only paths inside <see cref="AutomationOutDir"/> may be written.</summary>
    public static bool MayWrite(string path)
    {
        if (!Dlg.Automated) return true;
        var root = Path.GetFullPath(AutomationOutDir()).TrimEnd('\\') + "\\";
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static Window? Owner => Application.Current?.MainWindow;

    /// <summary>
    /// Sets the UI flag "textures.changed" = <paramref name="path"/> (full path) so viewports / material previews reload it.
    /// Cleared first so saving the same file twice still notifies (consumers ignore null).
    /// </summary>
    public static void NotifyChanged(string path)
    {
        AppServices.Editor.SetFlag("textures.changed", null);
        AppServices.Editor.SetFlag("textures.changed", Path.GetFullPath(path));
    }
}
