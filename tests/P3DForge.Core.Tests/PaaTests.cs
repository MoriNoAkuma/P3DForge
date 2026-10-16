using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using P3DForge.Core.Paa;
using PaaCodec = P3DForge.Core.Paa.Paa;

namespace P3DForge.Core.Tests;

public class PaaTests(ITestOutputHelper output)
{
    /// <summary>Minimal PNG writer (RGBA8) for visual checks.</summary>
    internal static void WritePng(string path, int w, int h, byte[] rgba)
    {
        static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            s.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            s.Write(td);
            BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(td));
            s.Write(len);
        }
        using var fs = File.Create(path);
        fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0);
                z.Write(rgba, y * w * 4, w * 4);
            }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
    }

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    static IEnumerable<string> PaaFiles(int max)
    {
        if (!Directory.Exists(@"P:\")) yield break;
        int n = 0;
        foreach (var top in Directory.EnumerateDirectories(@"P:\"))
        {
            if (Path.GetFileName(top).Equals("DZ", StringComparison.OrdinalIgnoreCase)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(top, "*.paa", SearchOption.AllDirectories).ToList();
            }
            catch
            {
                continue;
            }
            foreach (var f in files)
            {
                if (n++ >= max) yield break;
                yield return f;
            }
        }
    }

    [Fact]
    public void Lzss_round_trips_hand_made_stream()
    {
        // flag 0xFF = 8 literals "ABCDEFGH", then flag 0x00 → pointer: distance 8, length 3+5 = 8 (repeat), checksum.
        var data = "ABCDEFGH"u8.ToArray();
        var expected = data.Concat(data).ToArray();
        uint sum = 0;
        foreach (var b in expected) sum += b;
        var stream = new List<byte> { 0xFF };
        stream.AddRange(data);
        stream.Add(0x00);
        stream.Add(0x08);
        stream.Add(0x05);
        stream.AddRange(BitConverter.GetBytes(sum));
        var outBuf = new byte[16];
        Lzss.Decompress(stream.ToArray(), outBuf);
        Assert.Equal(expected, outBuf);
    }

    [Fact]
    public void Dxt1_decodes_solid_block_and_one_bit_alpha()
    {
        // c0 = pure red (0xF800), c1 = 0x0000, c0 > c1 → 4-colour; all indices 0 → red.
        byte[] block = [0x00, 0xF8, 0x00, 0x00, 0, 0, 0, 0];
        var rgba = new byte[4 * 4 * 4];
        Dxt.DecodeDxt1(block, 4, 4, rgba);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[..4]);
        // c0 <= c1 → 3-colour mode, index 3 = transparent black.
        byte[] block2 = [0x00, 0x00, 0x00, 0xF8, 0xFF, 0xFF, 0xFF, 0xFF];
        Dxt.DecodeDxt1(block2, 4, 4, rgba);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, rgba[..4]);
    }

    [Fact]
    public void Procedural_color_texture()
    {
        var img = ProceduralTexture.Parse("#(argb,8,8,3)color(1,0,0,0.5,co)")!;
        Assert.Equal(8, img.Width);
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, img.Rgba[..4]);
        Assert.True(img.HasAlpha);
        Assert.Null(ProceduralTexture.Parse("dz\\data\\x_co.paa"));
        var rgb = ProceduralTexture.Parse("#(rgb,8,8,3)color(0.5,0.5,0.5,1)")!;
        Assert.False(rgb.HasAlpha);
    }

    [Fact]
    public void Corpus_textures_decode()
    {
        var files = PaaFiles(400).ToList();
        Assert.SkipWhen(files.Count == 0, "no .paa files on P:\\");
        var formats = new Dictionary<string, int>();
        var failures = new List<string>();
        var dumped = new HashSet<string>();
        var outDir = Path.Combine(TestData.OutDir(), "..", "paa");
        Directory.CreateDirectory(outDir);
        var sw = Stopwatch.StartNew();
        foreach (var f in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(f);
                var img = PaaCodec.DecodeRgba(bytes, 512);
                Assert.Equal(img.Width * img.Height * 4, img.Rgba.Length);
                formats[img.Format] = formats.GetValueOrDefault(img.Format) + 1;
                string suffix = Path.GetFileNameWithoutExtension(f).Split('_').Last().ToLowerInvariant();
                string key = img.Format + "_" + suffix;
                if (dumped.Count < 12 && dumped.Add(key))
                    WritePng(Path.Combine(outDir, $"{key}_{Path.GetFileNameWithoutExtension(f)}.png"), img.Width, img.Height, img.Rgba);
            }
            catch (Exception ex)
            {
                failures.Add($"{f}: {ex.Message}");
            }
        }
        output.WriteLine($"{files.Count} files in {sw.ElapsedMilliseconds} ms; formats: {string.Join(", ", formats.Select(kv => $"{kv.Key}={kv.Value}"))}");
        foreach (var fl in failures.Take(10)) output.WriteLine("FAIL " + fl);
        Assert.True(failures.Count <= files.Count / 50, $"{failures.Count} of {files.Count} textures failed:\n{string.Join("\n", failures.Take(10))}");
    }
}
