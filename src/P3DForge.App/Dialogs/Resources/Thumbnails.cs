using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaaCodec = P3DForge.Core.Paa.Paa;

namespace P3DForge.App.Dialogs.Resources;

/// <summary>
/// Texture thumbnails for the resource library / Face Properties: PAA (and common image formats) decoded on
/// background tasks at a small mip, frozen and kept in an LRU cache. Procedural textures
/// (<c>#(argb,8,8,3)color(r,g,b,a)</c>) become a solid swatch. All public members are UI-thread only.
/// </summary>
public static class Thumbnails
{
    const int Capacity = 400;
    static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource? Image)>> map = new(StringComparer.OrdinalIgnoreCase);
    static readonly LinkedList<(string Key, ImageSource? Image)> lru = new();
    static readonly Dictionary<string, List<Action<ImageSource?>>> pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim gate = new(2);

    static string KeyOf(string abs, int size) => size.ToString(CultureInfo.InvariantCulture) + "|" + abs;

    /// <summary>
    /// Calls <paramref name="onReady"/> (on the UI thread) with the thumbnail of a model path ("dz\…\x_co.paa",
    /// procedural "#(argb…)" or absolute). Null when the file is missing or cannot be decoded. Cached results are
    /// delivered synchronously.
    /// </summary>
    public static void Request(string modelPath, int size, Action<ImageSource?> onReady)
    {
        var swatch = Procedural(modelPath);
        if (swatch is not null || modelPath.TrimStart().StartsWith('#'))
        {
            onReady(swatch);
            return;
        }
        var abs = AppServices.ResolveProjectPath(modelPath);
        if (abs is null)
        {
            onReady(null);
            return;
        }
        RequestFile(abs, size, onReady);
    }

    /// <summary>Like <see cref="Request"/> for an absolute file path.</summary>
    public static void RequestFile(string abs, int size, Action<ImageSource?> onReady)
    {
        var key = KeyOf(abs, size);
        if (map.TryGetValue(key, out var node))
        {
            lru.Remove(node);
            lru.AddFirst(node);
            onReady(node.Value.Image);
            return;
        }
        if (pending.TryGetValue(key, out var waiters))
        {
            waiters.Add(onReady);
            return;
        }
        pending[key] = [onReady];
        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            ImageSource? img = null;
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                img = Decode(abs, size);
            }
            catch
            {
                img = null; // corrupt / unsupported / locked file: no thumbnail
            }
            finally
            {
                gate.Release();
            }
            await dispatcher.InvokeAsync(() => Complete(key, img));
        });
    }

    static void Complete(string key, ImageSource? img)
    {
        var node = lru.AddFirst((key, img));
        map[key] = node;
        while (lru.Count > Capacity)
        {
            var last = lru.Last!;
            lru.RemoveLast();
            map.Remove(last.Value.Key);
        }
        if (!pending.Remove(key, out var waiters)) return;
        foreach (var w in waiters)
        {
            try
            {
                w(img);
            }
            catch
            {
                // a closed dialog's callback must not break the others
            }
        }
    }

    /// <summary>Background thread: decodes a small mip of a PAA (or a WIC image) into a frozen BGRA bitmap.</summary>
    static ImageSource? Decode(string abs, int size)
    {
        if (!File.Exists(abs)) return null;
        var ext = Path.GetExtension(abs).ToLowerInvariant();
        if (ext is ".paa" or ".pac")
        {
            var bytes = File.ReadAllBytes(abs);
            var img = PaaCodec.DecodeRgba(bytes, size);
            var rgba = img.Rgba;
            var bgra = new byte[rgba.Length];
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                bgra[i] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i];
                bgra[i + 3] = rgba[i + 3];
            }
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, bgra, img.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".tif" or ".tiff")
        {
            if (ext == ".tga") return null; // WIC has no TGA codec
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = size;
            bi.UriSource = new Uri(abs);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        return null;
    }

    /// <summary>A solid swatch for "#(argb,8,8,3)color(r,g,b,a)" procedural textures (null for other paths).</summary>
    public static ImageSource? Procedural(string path)
    {
        var s = path.Trim();
        if (!s.StartsWith("#(", StringComparison.Ordinal)) return null;
        int c = s.IndexOf("color(", StringComparison.OrdinalIgnoreCase);
        if (c < 0) return null;
        int end = s.IndexOf(')', c);
        if (end < 0) return null;
        var parts = s[(c + 6)..end].Split(',');
        if (parts.Length < 3) return null;
        double[] v = [0, 0, 0, 1];
        for (int i = 0; i < Math.Min(4, parts.Length); i++)
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
        static byte B(double x) => (byte)Math.Clamp(Math.Round(x * 255), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(B(v[3]), B(v[0]), B(v[1]), B(v[2])));
        brush.Freeze();
        var d = new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(0, 0, 16, 16)));
        d.Freeze();
        var img = new DrawingImage(d);
        img.Freeze();
        return img;
    }

    /// <summary>Drops every cached thumbnail (e.g. after the project root changed).</summary>
    public static void Clear()
    {
        map.Clear();
        lru.Clear();
    }
}
