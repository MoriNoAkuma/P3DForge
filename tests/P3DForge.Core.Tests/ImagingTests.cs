using P3DForge.Core.Imaging;
using P3DForge.Core.Paa;

namespace P3DForge.Core.Tests;

public class ImagingTests
{
    static byte[] TgaHeader(int type, int w, int h, int bits, int desc)
    {
        var d = new byte[18];
        d[2] = (byte)type;
        d[12] = (byte)w;
        d[13] = (byte)(w >> 8);
        d[14] = (byte)h;
        d[15] = (byte)(h >> 8);
        d[16] = (byte)bits;
        d[17] = (byte)desc;
        return d;
    }

    [Fact]
    public void Tga_uncompressed_24bit_bottom_left()
    {
        // 2×2: bottom row stored first (BGR)
        byte[] pixels = [0, 0, 255, 0, 255, 0, /* top row: */ 255, 0, 0, 255, 255, 255];
        var img = Tga.Read([.. TgaHeader(2, 2, 2, 24, 0), .. pixels]);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 255, 255, 255, 255, 0, 0, 255, 0, 255, 0, 255 }, img.Rgba);
    }

    [Fact]
    public void Tga_rle_32bit_top_left_and_grey()
    {
        // RLE packet: 3 × (B=1,G=2,R=3,A=4), raw packet: 1 pixel (10,20,30,40)
        byte[] data = [.. TgaHeader(10, 4, 1, 32, 0x28), 0x82, 1, 2, 3, 4, 0x00, 10, 20, 30, 40];
        var img = Tga.Read(data);
        Assert.Equal(new byte[] { 3, 2, 1, 4, 3, 2, 1, 4, 3, 2, 1, 4, 30, 20, 10, 40 }, img.Rgba);

        var grey = Tga.Read([.. TgaHeader(3, 2, 1, 8, 0x20), 7, 200]);
        Assert.Equal(new byte[] { 7, 7, 7, 255, 200, 200, 200, 255 }, grey.Rgba);
    }

    [Fact]
    public void Tga_write_read_round_trip_and_zero_alpha_is_opaque()
    {
        var img = PaaWriterTests.Synthetic(16, 8, alpha: true);
        Assert.Equal(img.Rgba, Tga.Read(Tga.Write(img)).Rgba);

        // 32-bit without declared alpha bits and all-zero alpha → opaque
        byte[] data = [.. TgaHeader(2, 1, 1, 32, 0x20), 1, 2, 3, 0];
        Assert.Equal(new byte[] { 3, 2, 1, 255 }, Tga.Read(data).Rgba);
        Assert.Throws<InvalidDataException>(() => Tga.Read([.. TgaHeader(2, 4, 4, 24, 0), 1, 2]));
    }

    [Fact]
    public void Resize_keeps_flat_colour_and_averages_when_shrinking()
    {
        var img = RgbaImage.Create(6, 3);
        for (int i = 0; i < img.Rgba.Length; i += 4) (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2], img.Rgba[i + 3]) = (10, 100, 200, 255);
        var r = ImageOps.Resize(img, 8, 4);
        Assert.All(Enumerable.Range(0, 32), p => Assert.Equal(new byte[] { 10, 100, 200, 255 }, r.Rgba[(p * 4)..(p * 4 + 4)]));

        var checker = RgbaImage.Create(4, 4);
        for (int i = 0; i < 16; i++) checker.Rgba[i * 4] = (byte)(((i % 4) + (i / 4)) % 2 == 0 ? 0 : 255);
        var small = ImageOps.Resize(checker, 2, 2);
        for (int i = 0; i < 4; i++) Assert.InRange(small.Rgba[i * 4], 120, 135);
    }

    [Fact]
    public void Channel_adjustments()
    {
        Assert.Equal(255, new ChannelAdjust { Invert = true }.BuildLut()[0]);
        var levels = new ChannelAdjust { InBlack = 50, InWhite = 150 }.BuildLut();
        Assert.Equal(0, levels[50]);
        Assert.Equal(128, levels[100]);
        Assert.Equal(255, levels[150]);
        Assert.True(new ChannelAdjust { Gamma = 2 }.BuildLut()[64] > 64);
        Assert.Equal(Enumerable.Range(0, 256).Select(v => (byte)v), ChannelAdjust.None.BuildLut());

        var img = PaaWriterTests.Synthetic(8, 8);
        var adjusted = ImageOps.Adjust(img, [null, new ChannelAdjust { Invert = true }, null, null]);
        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(img.Rgba[i * 4], adjusted.Rgba[i * 4]);
            Assert.Equal(255 - img.Rgba[i * 4 + 1], adjusted.Rgba[i * 4 + 1]);
        }
        Assert.NotSame(img.Rgba, adjusted.Rgba);
        var g = ImageOps.Isolate(img, 1);
        Assert.Equal(img.Rgba[1], g.Rgba[0]);
        Assert.Equal(255, g.Rgba[3]);
    }

    [Fact]
    public void Nearest_power_of_two()
    {
        Assert.Equal(512, RgbaImage.NearestPowerOfTwo(600));
        Assert.Equal(1024, RgbaImage.NearestPowerOfTwo(800));
        Assert.Equal(1, RgbaImage.NearestPowerOfTwo(1));
        Assert.Equal(8192, RgbaImage.NearestPowerOfTwo(20000));
    }

    static double PsnrRgb(byte[] a, byte[] b, int mask = 0b0111)
    {
        double se = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < 4; c++)
            {
                if ((mask >> c & 1) == 0) continue;
                double d = a[i + c] - b[i + c];
                se += d * d;
                n++;
            }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / n));
    }

    /// <summary>The texture-tools save path: TGA source (non power of two) → resize → adjust → PAA by suffix → decode back.</summary>
    [Theory]
    [InlineData("tex_co.paa", false)]
    [InlineData("tex_ca.paa", true)]
    [InlineData("tex_smdi.paa", false)]
    [InlineData("tex_nohq.paa", false)]
    public void Tga_resize_adjust_encode_decode_round_trip(string target, bool alpha)
    {
        var src = RgbaImage.Create(300, 200);
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 300; x++)
            {
                int o = (y * 300 + x) * 4;
                src.Rgba[o] = (byte)(x * 255 / 299);
                src.Rgba[o + 1] = (byte)(y * 255 / 199);
                src.Rgba[o + 2] = (byte)(128 + 100 * Math.Sin(x / 20.0) * Math.Cos(y / 25.0));
                src.Rgba[o + 3] = alpha ? (byte)((x + y) % 256) : (byte)255;
            }
        var img = Tga.Read(Tga.Write(src));
        Assert.Equal(src.Rgba, img.Rgba);
        Assert.False(img.HasPowerOfTwoSize);
        img = ImageOps.Resize(img, RgbaImage.NearestPowerOfTwo(img.Width), RgbaImage.NearestPowerOfTwo(img.Height));
        Assert.Equal((256, 256), (img.Width, img.Height));
        img = ImageOps.Adjust(img, [null, ChannelAdjust.None with { Invert = true }, ChannelAdjust.None with { Gamma = 1.5 }, null]);
        var bytes = PaaWriter.Encode(img, target);
        var header = P3DForge.Core.Paa.Paa.ReadHeader(bytes);
        Assert.Equal(256, header.Mips[0].Width);
        var back = P3DForge.Core.Paa.Paa.DecodeRgba(bytes, 4096);
        Assert.Equal((256, 256), (back.Width, back.Height));
        double rgb = PsnrRgb(img.Rgba, back.Rgba, target.Contains("_smdi") ? 0b0110 : 0b0111); // _smdi: R is unused (stored as 1)
        Assert.True(rgb > 30, $"{target}: RGB PSNR {rgb:0.0} dB");
        if (alpha)
        {
            double a = PsnrRgb(img.Rgba, back.Rgba, 0b1000);
            Assert.True(a > 30, $"{target}: alpha PSNR {a:0.0} dB");
        }
    }

    [Fact]
    public void Isolate_shows_one_channel_as_grey()
    {
        var img = new RgbaImage(1, 1, [10, 20, 30, 40]);
        Assert.Equal(new byte[] { 20, 20, 20, 255 }, ImageOps.Isolate(img, 1).Rgba);
        Assert.Equal(new byte[] { 40, 40, 40, 255 }, ImageOps.Isolate(img, 3).Rgba);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, ImageOps.Isolate(img, -2).Rgba);
    }
}
