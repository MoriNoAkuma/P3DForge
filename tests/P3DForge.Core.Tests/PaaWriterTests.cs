using System.Buffers.Binary;
using System.Diagnostics;
using P3DForge.Core.Imaging;
using P3DForge.Core.Paa;
using PaaCodec = P3DForge.Core.Paa.Paa;

namespace P3DForge.Core.Tests;

public class PaaWriterTests(ITestOutputHelper output)
{
    static readonly string OutDir = TestData.Path_("testdata", "out", "textures");

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>Deterministic "natural-ish" test image: gradients, soft noise, hard edges, a few flat areas.</summary>
    internal static RgbaImage Synthetic(int w, int h, bool alpha = false, int seed = 1)
    {
        var rnd = new Random(seed);
        var img = RgbaImage.Create(w, h);
        var p = img.Rgba;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                double fx = x / (double)w, fy = y / (double)h;
                double r = 120 + 100 * Math.Sin(fx * 7) * Math.Cos(fy * 3);
                double g = 90 + 80 * fy + 40 * Math.Sin((fx + fy) * 11);
                double b = 60 + 150 * fx * fy;
                if ((x / 37 + y / 23) % 5 == 0) (r, g, b) = (200, 40, 30);         // hard-edged patches
                if (Math.Abs(x - w / 2) < w / 8 && Math.Abs(y - h / 3) < h / 10) (r, g, b) = (128, 128, 255); // flat area
                double n = rnd.NextDouble() * 16 - 8;
                p[o] = (byte)Math.Clamp(r + n, 0, 255);
                p[o + 1] = (byte)Math.Clamp(g + n, 0, 255);
                p[o + 2] = (byte)Math.Clamp(b + n, 0, 255);
                p[o + 3] = alpha ? (byte)Math.Clamp(255 * (0.5 + 0.5 * Math.Sin(fx * 9 + fy * 4)) + n, 0, 255) : (byte)255;
            }
        return img;
    }

    static double Psnr(byte[] a, byte[] b, int channelMask = 0b0111)
    {
        double se = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < 4; c++)
            {
                if ((channelMask >> c & 1) == 0) continue;
                double d = a[i + c] - b[i + c];
                se += d * d;
                n++;
            }
        if (se == 0) return 99;
        return 10 * Math.Log10(255.0 * 255.0 / (se / n));
    }

    static byte[] DecodeDxt(byte[] data, int w, int h, bool dxt5)
    {
        var o = new byte[w * h * 4];
        if (dxt5) Dxt.DecodeDxt5(data, w, h, o);
        else Dxt.DecodeDxt1(data, w, h, o);
        return o;
    }

    static List<string> Taggs(byte[] file)
    {
        var names = new List<string>();
        int pos = 2;
        while (file.AsSpan(pos, 4).SequenceEqual("GGAT"u8))
        {
            names.Add(new string(System.Text.Encoding.ASCII.GetString(file, pos + 4, 4).Reverse().ToArray()));
            pos += 12 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pos + 8));
        }
        return names;
    }

    // ---- compressors -----------------------------------------------------------------------------------------

    public static IEnumerable<TheoryDataRow<string, int>> CompressorInputs()
    {
        foreach (var kind in new[] { "random", "zeros", "pattern", "dxt" })
            foreach (var n in new[] { 1, 3, 5, 20, 21, 64, 300, 5000, 70000, 300000 })
                yield return new(kind, n);
    }

    static byte[] Input(string kind, int n)
    {
        var rnd = new Random(n);
        var d = new byte[n];
        switch (kind)
        {
            case "random": rnd.NextBytes(d); break;
            case "zeros": break;
            case "pattern":
                for (int i = 0; i < n; i++) d[i] = (byte)(i % 7 * 31 + i / 5000);
                break;
            default:
                var img = Synthetic(64, Math.Max(4, (n / 64 + 3) & ~3));
                var dxt = DxtEncoder.EncodeDxt1(img);
                for (int i = 0; i < n; i++) d[i] = dxt[i % dxt.Length];
                break;
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(CompressorInputs))]
    public void Lzo_round_trips(string kind, int n)
    {
        var src = Input(kind, n);
        var packed = LzoEncoder.Compress(src);
        var back = new byte[n];
        int used = Lzo.Decompress(packed, back);
        Assert.Equal(packed.Length, used);
        Assert.Equal(src, back);
        if (kind == "zeros" && n >= 5000) Assert.True(packed.Length < n / 50, $"zeros compress poorly: {packed.Length}");
    }

    [Theory]
    [MemberData(nameof(CompressorInputs))]
    public void Lzss_round_trips_with_signed_checksum(string kind, int n)
    {
        var src = Input(kind, n);
        var packed = LzssEncoder.Compress(src);
        var back = new byte[n];
        int used = Lzss.Decompress(packed, back);
        Assert.Equal(packed.Length, used);
        Assert.Equal(src, back);
        int signed = 0;
        foreach (byte b in src) signed += (sbyte)b;
        Assert.Equal(signed, BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(packed.Length - 4)));
        if (kind == "zeros" && n >= 5000) Assert.True(packed.Length < n / 4, $"zeros compress poorly: {packed.Length}");
    }

    // ---- DXT encoder -----------------------------------------------------------------------------------------

    [Fact]
    public void Dxt1_quality_on_synthetic_image()
    {
        var img = Synthetic(512, 512);
        var sw = Stopwatch.StartNew();
        var data = DxtEncoder.EncodeDxt1(img);
        sw.Stop();
        var back = DecodeDxt(data, 512, 512, false);
        double psnr = Psnr(img.Rgba, back);
        output.WriteLine($"DXT1 512² synthetic: PSNR {psnr:F2} dB, {sw.ElapsedMilliseconds} ms");
        Assert.True(psnr >= 34, $"DXT1 PSNR {psnr:F2}");
        Assert.True(back.Where((_, i) => i % 4 == 3).All(a => a == 255), "opaque DXT1 must not use the transparent index");
    }

    [Fact]
    public void Dxt5_quality_on_synthetic_image_with_alpha()
    {
        var img = Synthetic(256, 256, alpha: true);
        var back = DecodeDxt(DxtEncoder.EncodeDxt5(img), 256, 256, true);
        double rgb = Psnr(img.Rgba, back), alpha = Psnr(img.Rgba, back, 0b1000);
        output.WriteLine($"DXT5 256² synthetic: RGB {rgb:F2} dB, alpha {alpha:F2} dB");
        Assert.True(rgb >= 34, $"RGB PSNR {rgb:F2}");
        Assert.True(alpha >= 40, $"alpha PSNR {alpha:F2}");
    }

    [Fact]
    public void Flat_blocks_are_reproduced_almost_exactly()
    {
        var rnd = new Random(5);
        Span<byte> block = stackalloc byte[64];
        Span<byte> enc = stackalloc byte[8];
        var dec = new byte[64];
        int worst = 0;
        for (int t = 0; t < 2000; t++)
        {
            byte r = (byte)rnd.Next(256), g = (byte)rnd.Next(256), b = (byte)rnd.Next(256);
            if (t < 256) r = g = b = (byte)t;
            for (int i = 0; i < 16; i++)
            {
                block[i * 4] = r;
                block[i * 4 + 1] = g;
                block[i * 4 + 2] = b;
                block[i * 4 + 3] = 255;
            }
            DxtEncoder.EncodeColorBlock(block, false, true, enc);
            Dxt.DecodeDxt1(enc.ToArray(), 4, 4, dec);
            for (int i = 0; i < 16; i++)
                for (int c = 0; c < 3; c++)
                    worst = Math.Max(worst, Math.Abs(dec[i * 4 + c] - block[i * 4 + c]));
            Assert.Equal(255, dec[3]);
        }
        output.WriteLine($"flat blocks: worst channel error {worst}");
        Assert.True(worst <= 2, $"flat block error {worst}");
    }

    [Fact]
    public void Dxt1_punch_through_alpha_keeps_cutouts()
    {
        var img = Synthetic(64, 64);
        for (int i = 0; i < 64 * 64; i++) img.Rgba[i * 4 + 3] = (byte)((i % 64) < 20 ? 0 : 255);
        var back = DecodeDxt(DxtEncoder.EncodeDxt1(img, punchThroughAlpha: true), 64, 64, false);
        for (int i = 0; i < 64 * 64; i++) Assert.Equal(img.Rgba[i * 4 + 3], back[i * 4 + 3]);
    }

    // ---- writer: layout --------------------------------------------------------------------------------------

    [Fact]
    public void Colour_map_layout_matches_ImageToPAA()
    {
        var img = Synthetic(512, 256);
        var file = PaaWriter.Encode(img, "test_co.paa");
        Assert.Equal(0xFF01, BinaryPrimitives.ReadUInt16LittleEndian(file));
        Assert.Equal(["AVGC", "MAXC", "OFFS"], Taggs(file));
        var h = PaaCodec.ReadHeader(file);
        Assert.Equal("DXT1", h.Format);
        Assert.Equal(0xFFFFFFFFu, h.MaxColor);
        Assert.Null(h.Flags);
        Assert.Equal(112 + 7, h.Mips[0].Offset);
        Assert.Equal([(512, 256), (256, 128), (128, 64), (64, 32), (32, 16), (16, 8), (8, 4)], h.Mips.Select(m => (m.Width, m.Height)));
        Assert.Equal(["lzo", "lzo", "none", "none", "none", "none", "none"], h.Mips.Select(m => m.Compression));
        // OFFS = absolute offsets of the mip headers
        int offs = 2 + 16 + 16 + 12;
        for (int i = 0; i < 16; i++)
        {
            uint o = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offs + i * 4));
            Assert.Equal(i < h.Mips.Count ? (uint)(h.Mips[i].Offset - 7) : 0u, o);
        }
        Assert.True(file.AsSpan(file.Length - 6).SequenceEqual(new byte[6]), "tail must be 6 zero bytes");
        var last = h.Mips[^1];
        Assert.Equal(file.Length - 6, last.Offset + last.StoredSize);
        // AVGC = source average (B,G,R,A bytes)
        Assert.Equal(PaaWriter.Average(img), h.AvgColor);
    }

    [Fact]
    public void Every_mip_decodes_close_to_the_source_chain()
    {
        var img = Synthetic(256, 256, alpha: true);
        var file = PaaWriter.Encode(img, "test_ca.paa");
        var h = PaaCodec.ReadHeader(file);
        Assert.Equal("DXT5", h.Format);
        Assert.Equal(1u, h.Flags);
        Assert.Equal(["AVGC", "MAXC", "FLAG", "OFFS"], Taggs(file));
        var chain = MipChain.Build(img, true, MipFilter.Box, true);
        Assert.Equal(chain.Count, h.Mips.Count);
        for (int i = 0; i < chain.Count; i++)
        {
            var d = PaaCodec.DecodeMip(file, h, i);
            double psnr = Psnr(chain[i].Rgba, d.Rgba, 0b1111);
            output.WriteLine($"mip {i} {d.Width}x{d.Height}: {psnr:F2} dB");
            Assert.True(psnr >= (d.Width >= 64 ? 33 : 20), $"mip {i}: {psnr:F2} dB"); // small mips of the noisy image are pure noise
        }
    }

    [Fact]
    public void Normal_map_nohq_is_swizzled_like_TexConvert()
    {
        var img = RgbaImage.Create(128, 128);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                double nx = Math.Sin(x / 9.0) * 0.4, ny = Math.Cos(y / 7.0) * 0.4, nz = Math.Sqrt(1 - nx * nx - ny * ny);
                int o = (y * 128 + x) * 4;
                img.Rgba[o] = (byte)Math.Round((nx + 1) * 127.5);
                img.Rgba[o + 1] = (byte)Math.Round((ny + 1) * 127.5);
                img.Rgba[o + 2] = (byte)Math.Round((nz + 1) * 127.5);
                img.Rgba[o + 3] = 255;
            }
        var file = PaaWriter.Encode(img, @"x\y\test_nohq.paa");
        var h = PaaCodec.ReadHeader(file);
        Assert.Equal("DXT5", h.Format);
        Assert.Equal(["AVGC", "MAXC", "FLAG", "SWIZ", "OFFS"], Taggs(file));
        Assert.Equal(1u, h.Flags);
        Assert.Equal(0x03020405u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(2 + 16 + 16 + 16 + 12)));
        // raw stored data: A = 1-R, R = 1-A (= 0 for an opaque source)
        var stored = new byte[128 * 128 * 4];
        Dxt.DecodeDxt5(file.AsSpan(h.Mips[0].Offset, h.Mips[0].StoredSize), 128, 128, stored);
        Assert.True(stored.Where((_, i) => i % 4 == 0).All(v => v <= 4), "stored R must be 1-A");
        // the decoder undoes the swizzle
        var back = PaaCodec.DecodeMip(file, h, 0);
        double psnr = Psnr(img.Rgba, back.Rgba, 0b0011);
        output.WriteLine($"_nohq X/Y PSNR {psnr:F2} dB");
        Assert.True(psnr >= 38, $"normal X/Y PSNR {psnr:F2}");
    }

    [Fact]
    public void Smdi_keeps_specular_and_gloss_channels()
    {
        var img = Synthetic(64, 64);
        var file = PaaWriter.Encode(img, "test_smdi.paa");
        var h = PaaCodec.ReadHeader(file);
        Assert.Equal("DXT1", h.Format);
        Assert.Equal(["AVGC", "MAXC", "SWIZ", "OFFS"], Taggs(file));
        Assert.Equal(new byte[] { 8, 2, 3, 8 }, h.Swizzle); // decoder order r,g,b,a
        var back = PaaCodec.DecodeMip(file, h, 0);
        Assert.True(Psnr(img.Rgba, back.Rgba, 0b0110) >= 33);
    }

    [Theory]
    [InlineData("t_8888.paa", "RGBA8888", 99.0)]
    [InlineData("t_4444.paa", "RGBA4444", 30.0)]
    [InlineData("t_1555.paa", "RGBA5551", 34.0)]
    public void Uncompressed_formats_round_trip_with_lzss(string name, string format, double minPsnr)
    {
        var img = Synthetic(64, 32, alpha: name.Contains("1555") ? false : true);
        var file = PaaWriter.Encode(img, name);
        var h = PaaCodec.ReadHeader(file);
        Assert.Equal(format, h.Format);
        Assert.Equal((1, 1), (h.Mips[^1].Width, h.Mips[^1].Height));
        Assert.Equal(7, h.Mips.Count);
        Assert.All(h.Mips, m => Assert.Equal("lzss", m.Compression));
        var back = PaaCodec.DecodeMip(file, h, 0);
        double psnr = Psnr(img.Rgba, back.Rgba, name.Contains("1555") ? 0b0111 : 0b1111);
        output.WriteLine($"{format}: {psnr:F2} dB");
        Assert.True(psnr >= minPsnr, $"{format} PSNR {psnr:F2}");
        for (int i = 0; i < h.Mips.Count; i++) PaaCodec.DecodeMip(file, h, i);
    }

    [Fact]
    public void Ai88_stores_grey_and_alpha()
    {
        var img = Synthetic(32, 32, alpha: true);
        var file = PaaWriter.Encode(img, "t_gs.paa");
        var h = PaaCodec.ReadHeader(file);
        Assert.Equal("AI88", h.Format);
        var back = PaaCodec.DecodeMip(file, h, 0);
        for (int i = 0; i < 32 * 32; i++)
        {
            int luma = (img.Rgba[i * 4] * 299 + img.Rgba[i * 4 + 1] * 587 + img.Rgba[i * 4 + 2] * 114 + 500) / 1000;
            Assert.Equal(luma, back.Rgba[i * 4]);
            Assert.Equal(img.Rgba[i * 4 + 3], back.Rgba[i * 4 + 3]);
        }
    }

    [Fact]
    public void Sizes_must_be_powers_of_two()
    {
        Assert.NotNull(PaaWriter.CheckSize(300, 256, PaaFormat.Dxt1));
        Assert.NotNull(PaaWriter.CheckSize(2, 2, PaaFormat.Dxt5));
        Assert.Null(PaaWriter.CheckSize(2, 2, PaaFormat.Argb8888));
        Assert.Null(PaaWriter.CheckSize(2048, 512, PaaFormat.Dxt5));
        Assert.Throws<ArgumentException>(() => PaaWriter.Encode(RgbaImage.Create(100, 64), "x_co.paa"));
    }

    [Fact]
    public void Mips_are_gamma_aware_for_colour_maps_and_linear_for_data()
    {
        var img = RgbaImage.Create(2, 2);
        for (int i = 0; i < 4; i++)
        {
            byte v = (byte)(i % 2 == 0 ? 0 : 255);
            img.Rgba[i * 4] = img.Rgba[i * 4 + 1] = img.Rgba[i * 4 + 2] = v;
            img.Rgba[i * 4 + 3] = 255;
        }
        Assert.Equal(188, MipChain.Downsample(img, true, false).Rgba[0]);   // linear 0.5 in sRGB
        Assert.Equal(128, MipChain.Downsample(img, false, false).Rgba[0]);
        Assert.True(TextureHints.ForPath("a_co.tga").Srgb);
        Assert.False(TextureHints.ForPath("a_nohq.tga").Srgb);
        Assert.False(TextureHints.ForPath("a_smdi.png").Srgb);
        Assert.Equal("_ca", TextureHints.ForPath(@"P:\x\obj_ti_ca.png").Suffix);
    }

    // ---- real files ------------------------------------------------------------------------------------------

    static IEnumerable<string> RealPaas()
    {
        // the game's own textures of several formats (DXT1 / DXT5 / ARGB4444) when P:\DZ is unpacked
        string[] candidates =
        [
            @"P:\DZ\data\data\detailmap_4444.paa",
            @"P:\DZ\characters\pants\data\bdu_pants_nohq.paa",
            @"P:\DZ\characters\pants\data\bdu_pants_woodland_co.paa",
        ];
        return candidates.Where(File.Exists);
    }

    [Fact]
    public void Real_paa_decode_reencode_decode()
    {
        var files = RealPaas().ToList();
        Assert.SkipWhen(files.Count == 0, "no sample .paa files on P:\\");
        Directory.CreateDirectory(OutDir);
        foreach (var path in files)
        {
            var src = File.ReadAllBytes(path);
            var header = PaaCodec.ReadHeader(src);
            int mip = PaaCodec.PickMip(header.Mips, 512);
            var decoded = PaaCodec.DecodeMip(src, header, mip);
            var image = RgbaImage.From(decoded);
            var name = Path.GetFileName(path);
            var sw = Stopwatch.StartNew();
            var file = PaaWriter.Encode(image, name);
            sw.Stop();
            File.WriteAllBytes(Path.Combine(OutDir, "reencoded_" + name), file);
            var h2 = PaaCodec.ReadHeader(file);
            var back = PaaCodec.DecodeMip(file, h2, 0);
            Assert.Equal(header.Format, h2.Format);
            double psnr = Psnr(decoded.Rgba, back.Rgba, header.Format == "DXT1" ? 0b0111 : 0b1111);
            output.WriteLine($"{name} {header.Format} {decoded.Width}x{decoded.Height}: re-encode PSNR {psnr:F2} dB, {sw.ElapsedMilliseconds} ms, {file.Length} bytes (source {src.Length})");
            PaaTests.WritePng(Path.Combine(OutDir, "reencoded_" + Path.ChangeExtension(name, ".png")), back.Width, back.Height, back.Rgba);
            Assert.True(psnr >= 38, $"{name}: {psnr:F2} dB");
        }
    }

    [Fact]
    public void Encoding_2048_is_fast()
    {
        var img = Synthetic(2048, 2048, alpha: true);
        var sw = Stopwatch.StartNew();
        var file = PaaWriter.Encode(img, "big_ca.paa");
        sw.Stop();
        output.WriteLine($"2048² DXT5 + mips + LZO: {sw.ElapsedMilliseconds} ms, {file.Length} bytes");
        var h = PaaCodec.ReadHeader(file);
        Assert.Equal(10, h.Mips.Count);
        Assert.True(sw.ElapsedMilliseconds < 20000);
    }
}
