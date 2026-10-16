using System.Collections.Concurrent;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using P3DForge.App.Editor;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>
/// GL textures for face texture paths, keyed by the normalised lower-case path. Files are read and decoded on
/// background tasks; uploads happen on the GL thread (<see cref="ProcessUploads"/> inside the Render callback).
/// Missing/undecodable textures show a grey checker (one log warning per path); pending ones a neutral grey.
/// </summary>
public sealed class TextureCache : IDisposable
{
    sealed class Entry(string key, string name, bool srgb)
    {
        public readonly string Key = key;
        public readonly string Name = name;
        public RgbaImage? Image;
        public string? Error;
        public int Handle;
        public bool Failed;
        public readonly bool Srgb = srgb;
        /// <summary>Cut out by its alpha: a _ca name, or the PAA declares an alpha key (<see cref="RgbaImage.Keyed"/>).</summary>
        public bool AlphaTest = IsAlphaMap(StripSpace(key));
        /// <summary>Alpha blended instead of alpha tested (from the decoded image, see <see cref="RgbaImage.Blend"/>).</summary>
        public bool Blend;
    }

    static string StripSpace(string key) => key.EndsWith("|lin") || key.EndsWith("|rgb") ? key[..^4] : key;

    /// <param name="AlphaTest">A _ca texture: its alpha cuts out (unless <paramref name="Blend"/>).</param>
    /// <param name="Blend">Its alpha is blended (glass): drawn after the opaque surfaces, without depth writes.</param>
    public readonly record struct Binding(int Handle, bool Srgb, bool AlphaTest, bool Real, bool Blend = false);

    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<Entry> ready = new();
    readonly Action requestRedraw;
    readonly int checker, pending;
    readonly float maxAniso;
    string root = "";
    int maxSize;
    int inFlight;
    bool decoderNoticeLogged;

    /// <param name="requestRedraw">Called from any thread when a decode finished (must marshal to the UI thread).</param>
    public TextureCache(Action requestRedraw)
    {
        this.requestRedraw = requestRedraw;
        GL.GetFloat((GetPName)0x84FF /* MAX_TEXTURE_MAX_ANISOTROPY */, out float a);
        maxAniso = GL.GetError() == ErrorCode.NoError ? a : 0;
        checker = MakeChecker();
        pending = MakeSolid(150, 152, 158);
    }

    /// <summary>Decodes still running.</summary>
    public int InFlight => Volatile.Read(ref inFlight);
    public int Count => entries.Count;
    public int Loaded => entries.Values.Count(e => e.Handle != 0);

    public static string KeyOf(string name) => name.Trim().Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    static string Stem(string key)
    {
        int slash = key.LastIndexOf('\\');
        int dot = key.LastIndexOf('.');
        return dot > slash ? key[..dot] : key;
    }

    static bool IsColorMap(string key)
    {
        if (key.StartsWith('#')) return key.Contains(",co)") || key.Contains(",ca)") || key.EndsWith("co)") || !key.Contains(',');
        var s = Stem(key);
        return s.EndsWith("_co") || s.EndsWith("_ca") || s.EndsWith("_lco") || s.EndsWith("_mco") || s.EndsWith("_mc") || s.EndsWith("_mca");
    }

    static bool IsAlphaMap(string key) => key.StartsWith('#') ? key.Contains(",ca)") : Stem(key).EndsWith("_ca");

    /// <summary>Clears everything when the project root or the size limit changed. Call once per frame (GL thread).</summary>
    public void BeginFrame()
    {
        var s = AppServices.Settings;
        if (s.ProjectRoot == root && s.TextureMaxSize == maxSize) return;
        root = s.ProjectRoot;
        maxSize = s.TextureMaxSize;
        Clear();
    }

    /// <summary>
    /// Drops every cached texture that refers to <paramref name="path"/> (a project-relative texture name or a full
    /// file path; the extension is ignored, like Object Builder resolving .tga/.png to the .paa). Returns the count.
    /// </summary>
    public int Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        string Stem(string p)
        {
            try
            {
                var full = Path.IsPathRooted(p) ? p : AppServices.ResolveProjectPath(p) ?? p;
                return Path.ChangeExtension(Path.GetFullPath(full), null).ToLowerInvariant();
            }
            catch
            {
                return KeyOf(p);
            }
        }
        var target = Stem(path);
        var targetKey = Stem(KeyOf(path));
        List<string>? drop = null;
        foreach (var (key, e) in entries)
        {
            if (key.StartsWith('#')) continue;
            var s = Stem(e.Name);
            if (s == target || s == targetKey) (drop ??= []).Add(key);
        }
        if (drop is null) return 0;
        foreach (var k in drop)
        {
            if (entries.Remove(k, out var e) && e.Handle != 0) GL.DeleteTexture(e.Handle);
        }
        return drop.Count;
    }

    public void Clear()
    {
        foreach (var e in entries.Values)
            if (e.Handle != 0) GL.DeleteTexture(e.Handle);
        entries.Clear();
        while (ready.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Texture to bind for a face texture name (GL thread). Starts loading on first use. <paramref name="linear"/>
    /// forces the colour space (null = by name: _co/_ca/_mc… are sRGB, data maps linear).
    /// </summary>
    public Binding Get(string name, bool? linear = null)
    {
        var key = KeyOf(name);
        if (key.Length == 0) return default;
        bool srgb = IsColorMap(key);
        if (linear is { } lin && lin == srgb)
        {
            srgb = !lin;
            key += lin ? "|lin" : "|rgb";
        }
        if (!entries.TryGetValue(key, out var e))
        {
            e = new Entry(key, name, srgb);
            entries[key] = e;
            Start(e);
        }
        if (e.Handle != 0) return new Binding(e.Handle, e.Srgb, e.AlphaTest || e.Blend, true, e.Blend);
        return new Binding(e.Failed ? checker : pending, false, false, false);
    }

    void Start(Entry e)
    {
        if (e.Key.StartsWith('#'))
        {
            var img = PaaBridge.Procedural(e.Name) ?? new RgbaImage(1, 1, [128, 128, 128, 255], false);
            e.Blend = img.Blend && e.AlphaTest;
            e.Handle = Upload(img, e.Srgb);
            return;
        }
        var path = AppServices.ResolveProjectPath(e.Name);
        if (path is null)
        {
            e.Failed = true;
            return;
        }
        int limit = maxSize > 0 ? maxSize : 1024;
        Interlocked.Increment(ref inFlight);
        Task.Run(() =>
        {
            try
            {
                e.Image = Load(path, limit, out e.Error);
            }
            catch (Exception ex)
            {
                e.Error = $"cannot decode {path}: {ex.Message}";
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
                ready.Enqueue(e);
                requestRedraw();
            }
        });
    }

    static RgbaImage? Load(string path, int limit, out string? error)
    {
        error = null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        // Object Builder / binarize resolve .tga/.png references to the .paa next to them.
        var paa = ext == ".paa" || ext == ".pac" ? path : Path.ChangeExtension(path, ".paa");
        if (File.Exists(paa))
        {
            if (!PaaBridge.DecoderAvailable)
            {
                error = "decoder";
                return null;
            }
            return PaaBridge.DecodePaa(File.ReadAllBytes(paa), limit) ?? throw new InvalidDataException("decoder returned nothing");
        }
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" && File.Exists(path)) return PaaBridge.DecodeImage(File.ReadAllBytes(path), limit);
        error = $"texture not found: {path}";
        return null;
    }

    /// <summary>Uploads decoded textures (GL thread). Returns true when something changed.</summary>
    public bool ProcessUploads(int maxPerFrame = 12)
    {
        bool any = false;
        for (int i = 0; i < maxPerFrame && ready.TryDequeue(out var e); i++)
        {
            if (!entries.TryGetValue(e.Key, out var cur) || !ReferenceEquals(cur, e)) continue; // cleared meanwhile
            any = true;
            if (e.Image is { } img)
            {
                e.Blend = img.Blend;
                e.AlphaTest |= img.Keyed;
                e.Handle = Upload(img, e.Srgb);
                e.Image = null;
                continue;
            }
            e.Failed = true;
            if (e.Error == "decoder")
            {
                if (decoderNoticeLogged) continue;
                decoderNoticeLogged = true;
                Log("PAA decoder not available yet — textures are shown as checker placeholders.", LogLevel.Info);
            }
            else Log(e.Error ?? $"texture not found: {e.Name}", LogLevel.Warn);
        }
        if (!ready.IsEmpty) requestRedraw();
        return any;
    }

    static void Log(string message, LogLevel level) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => AppServices.Editor.Log(message, level));

    /// <summary>Number of textures uploaded so far (cache key for things that depend on loaded textures).</summary>
    public int Uploads { get; private set; }

    int Upload(RgbaImage img, bool srgb)
    {
        Uploads++;
        int tex = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        // Rows top-first = DirectX convention: (u, v) is sampled directly, no flip.
        GL.TexImage2D(TextureTarget.Texture2D, 0, srgb ? PixelInternalFormat.Srgb8Alpha8 : PixelInternalFormat.Rgba8,
            img.Width, img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, img.Rgba);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        if (maxAniso > 1) GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE /* TEXTURE_MAX_ANISOTROPY */, Math.Min(8f, maxAniso));
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    int MakeChecker()
    {
        const int n = 64, cell = 8;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                byte v = ((x / cell) + (y / cell)) % 2 == 0 ? (byte)112 : (byte)168;
                int o = (y * n + x) * 4;
                px[o] = px[o + 1] = px[o + 2] = v;
                px[o + 3] = 255;
            }
        return Upload(new RgbaImage(n, n, px, false), false);
    }

    int MakeSolid(byte r, byte g, byte b) => Upload(new RgbaImage(1, 1, [r, g, b, 255], false), false);

    public void Dispose()
    {
        Clear();
        GL.DeleteTexture(checker);
        GL.DeleteTexture(pending);
    }
}
