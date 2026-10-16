using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using P3DForge.Core.Materials;

namespace P3DForge.App.Panels.Materials;

/// <summary>
/// Preview spheres for the material library: rendered on worker threads (newest request first, only while some tile
/// still wants the key), kept in an LRU of frozen bitmaps (UI thread). Decoded textures are cached separately (small
/// mips, shared by materials using the same maps). Keys carry the file stamp and the store version, so edits re-render.
/// </summary>
static class Previews
{
    public const int RenderSize = 128;
    const int BitmapCapacity = 700, TextureCapacity = 256, MaxWorkers = 2;

    static MaterialStore Store => MaterialStore.Shared;

    static readonly object sync = new();
    static readonly List<(string Key, string Path, Dispatcher Ui)> queue = []; // LIFO
    static readonly Dictionary<string, int> wanted = [];
    static readonly HashSet<string> inFlight = [];
    static int workers, generation;

    // UI thread only
    static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource? Bmp)>> lru = [];
    static readonly LinkedList<(string Key, BitmapSource? Bmp)> order = new();

    static readonly object texSync = new();
    static readonly Dictionary<string, PreviewImage?> textures = [];
    static readonly Queue<string> texOrder = new();

    /// <summary>A preview finished (UI thread); null bitmap = the material could not be rendered.</summary>
    public static event Action<string, BitmapSource?>? Ready;

    public static bool TryGet(string key, out BitmapSource? bmp)
    {
        if (lru.TryGetValue(key, out var node))
        {
            order.Remove(node);
            order.AddFirst(node);
            bmp = node.Value.Bmp;
            return true;
        }
        bmp = null;
        return false;
    }

    public static void Want(string key, string path, Dispatcher ui)
    {
        lock (sync)
        {
            wanted[key] = wanted.GetValueOrDefault(key) + 1;
            if (inFlight.Contains(key)) return;
            var at = queue.FindLastIndex(q => q.Key == key);
            if (at >= 0) queue.RemoveAt(at);
            queue.Add((key, path, ui));
            if (workers >= MaxWorkers) return;
            workers++;
        }
        Task.Run(Work);
    }

    public static void Unwant(string key)
    {
        lock (sync)
        {
            if (!wanted.TryGetValue(key, out var n)) return;
            if (n > 1)
            {
                wanted[key] = n - 1;
                return;
            }
            wanted.Remove(key);
            var at = queue.FindLastIndex(q => q.Key == key);
            if (at >= 0) queue.RemoveAt(at);
        }
    }

    /// <summary>Drops every cached preview and decoded texture (Rescan); running renders are discarded.</summary>
    public static void Clear()
    {
        lru.Clear();
        order.Clear();
        lock (sync) generation++;
        lock (texSync)
        {
            textures.Clear();
            texOrder.Clear();
        }
    }

    static void Work()
    {
        while (true)
        {
            (string Key, string Path, Dispatcher Ui)? next = null;
            int gen;
            lock (sync)
            {
                while (queue.Count > 0)
                {
                    var j = queue[^1];
                    queue.RemoveAt(queue.Count - 1);
                    if (wanted.ContainsKey(j.Key) && inFlight.Add(j.Key))
                    {
                        next = j;
                        break;
                    }
                }
                if (next is null)
                {
                    workers--;
                    return;
                }
                gen = generation;
            }
            var job = next.Value;
            BitmapSource? bmp = null;
            try
            {
                bmp = Render(job.Path);
            }
            catch (Exception)
            {
                // unreadable material → "!" mark on the tile
            }
            var done = job;
            done.Ui.BeginInvoke(() =>
            {
                bool current;
                lock (sync)
                {
                    inFlight.Remove(done.Key);
                    current = gen == generation;
                }
                if (!current) return;
                Put(done.Key, bmp);
                Ready?.Invoke(done.Key, bmp);
            }, DispatcherPriority.Background);
        }
    }

    static void Put(string key, BitmapSource? bmp)
    {
        if (lru.Remove(key, out var old)) order.Remove(old);
        lru[key] = order.AddFirst((key, bmp));
        while (order.Count > BitmapCapacity)
        {
            var last = order.Last!;
            order.RemoveLast();
            lru.Remove(last.Value.Key);
        }
    }

    static BitmapSource Render(string path)
    {
        var m = Store.HasOverride(path) ? Store.Get(path) ?? Rvmat.Load(path) : Rvmat.Load(path);
        var px = MaterialPreview.Render(m, RenderSize, Texture, Exists);
        var bmp = BitmapSource.Create(RenderSize, RenderSize, 96, 96, PixelFormats.Bgra32, null, px, RenderSize * 4);
        bmp.Freeze();
        return bmp;
    }

    static bool Exists(string texture) => Store.Resolve(texture) is { } f && File.Exists(f);

    static PreviewImage? Texture(string texture)
    {
        var key = (Store.Resolve(texture) ?? texture).ToLowerInvariant();
        lock (texSync)
            if (textures.TryGetValue(key, out var hit))
                return hit;
        var img = MaterialPreview.LoadTexture(texture, Store.Resolve, RenderSize);
        lock (texSync)
        {
            if (textures.TryAdd(key, img)) texOrder.Enqueue(key);
            while (texOrder.Count > TextureCapacity) textures.Remove(texOrder.Dequeue());
        }
        return img;
    }
}
