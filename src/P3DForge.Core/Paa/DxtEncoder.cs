using P3DForge.Core.Imaging;

namespace P3DForge.Core.Paa;

/// <summary>
/// DXT1 (BC1) and DXT5 (BC3) block compression. Colour blocks: optimal single-colour tables for flat blocks, otherwise
/// a principal-axis (PCA) range fit refined by least squares, tried in 4-colour and 3-colour mode (DXT1) — the lower
/// error wins. Alpha blocks: 8- and 6-value modes with least-squares refinement. Palettes and errors use the exact
/// integer formulas of <see cref="Dxt"/> (the decoder), so what is measured is what the decoder reproduces.
/// Block rows are encoded in parallel for large images.
/// </summary>
public static class DxtEncoder
{
    // ---- tables ----------------------------------------------------------------------------------------------
    static readonly byte[] Q5 = new byte[256], Q6 = new byte[256];      // nearest 5/6-bit level of an 8-bit value
    static readonly byte[] E5 = new byte[32], E6 = new byte[64];         // decoder expansion
    // single-colour fits: [v*2] = endpoint with weight 2/3 (or 1/2), [v*2+1] = the other one
    static readonly byte[] One5 = new byte[512], One6 = new byte[512], Half5 = new byte[512], Half6 = new byte[512];

    static DxtEncoder()
    {
        for (int i = 0; i < 32; i++) E5[i] = (byte)((i << 3) | (i >> 2));
        for (int i = 0; i < 64; i++) E6[i] = (byte)((i << 2) | (i >> 4));
        for (int v = 0; v < 256; v++)
        {
            Q5[v] = Nearest(E5, v);
            Q6[v] = Nearest(E6, v);
        }
        BuildSingle(E5, One5, Half5);
        BuildSingle(E6, One6, Half6);
    }

    static byte Nearest(byte[] expand, int v)
    {
        int best = 0, bestErr = int.MaxValue;
        for (int i = 0; i < expand.Length; i++)
        {
            int e = Math.Abs(expand[i] - v);
            if (e < bestErr)
            {
                bestErr = e;
                best = i;
            }
        }
        return (byte)best;
    }

    static void BuildSingle(byte[] expand, byte[] third, byte[] half)
    {
        int n = expand.Length;
        for (int v = 0; v < 256; v++)
        {
            int best3 = int.MaxValue, best2 = int.MaxValue;
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++)
                {
                    int ea = expand[a], eb = expand[b], spread = Math.Abs(ea - eb);
                    // small penalty on the endpoint spread: robust against GPU interpolation differences
                    int e3 = Math.Abs((2 * ea + eb + 1) / 3 - v) * 100 + spread;
                    int e2 = Math.Abs(((ea + eb + 1) >> 1) - v) * 100 + spread;
                    if (e3 < best3)
                    {
                        best3 = e3;
                        third[v * 2] = (byte)a;
                        third[v * 2 + 1] = (byte)b;
                    }
                    if (e2 < best2)
                    {
                        best2 = e2;
                        half[v * 2] = (byte)a;
                        half[v * 2 + 1] = (byte)b;
                    }
                }
        }
    }

    // ---- image API -------------------------------------------------------------------------------------------

    /// <summary>DXT1. With <paramref name="punchThroughAlpha"/> pixels with alpha &lt; 128 become transparent (3-colour blocks).</summary>
    public static byte[] EncodeDxt1(RgbaImage image, bool punchThroughAlpha = false) => Encode(image, 8, punchThroughAlpha);

    /// <summary>DXT5 (interpolated alpha).</summary>
    public static byte[] EncodeDxt5(RgbaImage image) => Encode(image, 16, false);

    static byte[] Encode(RgbaImage image, int blockBytes, bool punch)
    {
        int w = image.Width, h = image.Height;
        int bw = (w + 3) >> 2, bh = (h + 3) >> 2;
        var dst = new byte[bw * bh * blockBytes];
        var src = image.Rgba;
        void Row(int by)
        {
            Span<byte> block = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                {
                    int sy = Math.Min((by << 2) + y, h - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min((bx << 2) + x, w - 1);
                        int s = (sy * w + sx) << 2, d = (y * 4 + x) << 2;
                        block[d] = src[s];
                        block[d + 1] = src[s + 1];
                        block[d + 2] = src[s + 2];
                        block[d + 3] = src[s + 3];
                    }
                }
                var o = dst.AsSpan((by * bw + bx) * blockBytes, blockBytes);
                if (blockBytes == 8) EncodeColorBlock(block, punch, true, o);
                else
                {
                    EncodeAlphaBlock(block, o[..8]);
                    EncodeColorBlock(block, false, false, o[8..]);
                }
            }
        }
        if (bw * bh >= 1024) Parallel.For(0, bh, Row);
        else
            for (int by = 0; by < bh; by++) Row(by);
        return dst;
    }

    // ---- colour block ----------------------------------------------------------------------------------------

    /// <summary>
    /// Encodes 16 RGBA pixels (64 bytes, row-major) into an 8-byte colour block.
    /// <paramref name="punch"/>: alpha &lt; 128 → transparent index 3 (DXT1 only). <paramref name="allowThree"/>: 3-colour mode
    /// may be used (DXT1); DXT5 colour blocks are always 4-colour with c0 &gt; c1.
    /// </summary>
    internal static void EncodeColorBlock(ReadOnlySpan<byte> block, bool punch, bool allowThree, Span<byte> dst)
    {
        Span<int> px = stackalloc int[48];
        uint transparent = 0;
        int count = 0;
        for (int i = 0; i < 16; i++)
        {
            px[i * 3] = block[i * 4];
            px[i * 3 + 1] = block[i * 4 + 1];
            px[i * 3 + 2] = block[i * 4 + 2];
            if (punch && block[i * 4 + 3] < 128) transparent |= 1u << i;
            else count++;
        }
        if (count == 0)
        {
            Write(dst, 0, 0, 0xFFFFFFFFu); // c0 == c1 → 3-colour mode, index 3 = transparent black
            return;
        }

        // flat block (over the opaque pixels) → optimal single-colour endpoints
        int first = -1;
        bool flat = true;
        for (int i = 0; i < 16 && flat; i++)
        {
            if ((transparent >> i & 1) != 0) continue;
            if (first < 0) first = i;
            else if (px[i * 3] != px[first * 3] || px[i * 3 + 1] != px[first * 3 + 1] || px[i * 3 + 2] != px[first * 3 + 2]) flat = false;
        }
        if (flat)
        {
            int r = px[first * 3], g = px[first * 3 + 1], b = px[first * 3 + 2];
            if (transparent == 0)
            {
                int a = (One5[r * 2] << 11) | (One6[g * 2] << 5) | One5[b * 2];
                int c = (One5[r * 2 + 1] << 11) | (One6[g * 2 + 1] << 5) | One5[b * 2 + 1];
                PackFour(dst, a, c, 0xAAAAAAAAu); // every pixel = index 2 (2/3 a + 1/3 c)
            }
            else
            {
                int a = (Half5[r * 2] << 11) | (Half6[g * 2] << 5) | Half5[b * 2];
                int c = (Half5[r * 2 + 1] << 11) | (Half6[g * 2 + 1] << 5) | Half5[b * 2 + 1];
                uint idx = 0;
                for (int i = 0; i < 16; i++) idx |= ((transparent >> i & 1) != 0 ? 3u : 2u) << (i * 2);
                PackThree(dst, a, c, idx);
            }
            return;
        }

        // principal axis of the opaque colours
        float mr = 0, mg = 0, mb = 0;
        for (int i = 0; i < 16; i++)
        {
            if ((transparent >> i & 1) != 0) continue;
            mr += px[i * 3];
            mg += px[i * 3 + 1];
            mb += px[i * 3 + 2];
        }
        float inv = 1f / count;
        mr *= inv;
        mg *= inv;
        mb *= inv;
        float crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (int i = 0; i < 16; i++)
        {
            if ((transparent >> i & 1) != 0) continue;
            float r = px[i * 3] - mr, g = px[i * 3 + 1] - mg, b = px[i * 3 + 2] - mb;
            crr += r * r;
            crg += r * g;
            crb += r * b;
            cgg += g * g;
            cgb += g * b;
            cbb += b * b;
        }
        float vr, vg, vb;
        if (crr >= cgg && crr >= cbb) (vr, vg, vb) = (crr, crg, crb);
        else if (cgg >= cbb) (vr, vg, vb) = (crg, cgg, cgb);
        else (vr, vg, vb) = (crb, cgb, cbb);
        for (int it = 0; it < 6; it++)
        {
            float nr = crr * vr + crg * vg + crb * vb;
            float ng = crg * vr + cgg * vg + cgb * vb;
            float nb = crb * vr + cgb * vg + cbb * vb;
            float m = Math.Max(Math.Abs(nr), Math.Max(Math.Abs(ng), Math.Abs(nb)));
            if (m < 1e-12f) break;
            vr = nr / m;
            vg = ng / m;
            vb = nb / m;
        }
        float len = MathF.Sqrt(vr * vr + vg * vg + vb * vb);
        if (len < 1e-6f) (vr, vg, vb, len) = (1f, 1f, 1f, MathF.Sqrt(3f));
        vr /= len;
        vg /= len;
        vb /= len;
        float tmin = float.MaxValue, tmax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            if ((transparent >> i & 1) != 0) continue;
            float t = (px[i * 3] - mr) * vr + (px[i * 3 + 1] - mg) * vg + (px[i * 3 + 2] - mb) * vb;
            if (t < tmin) tmin = t;
            if (t > tmax) tmax = t;
        }
        int e0 = Quantize(mr + vr * tmax, mg + vg * tmax, mb + vb * tmax);
        int e1 = Quantize(mr + vr * tmin, mg + vg * tmin, mb + vb * tmin);

        int bestErr = int.MaxValue, bestA = 0, bestB = 0;
        uint bestIdx = 0;
        bool bestFour = true;
        if (transparent == 0)
        {
            bestErr = Fit(px, 0, true, e0, e1, out bestA, out bestB, out bestIdx);
            bestFour = true;
        }
        if (allowThree && bestErr > 0)
        {
            int err = Fit(px, transparent, false, e0, e1, out int a, out int b, out uint idx);
            if (err < bestErr)
            {
                bestErr = err;
                bestA = a;
                bestB = b;
                bestIdx = idx;
                bestFour = false;
            }
        }
        if (bestFour) PackFour(dst, bestA, bestB, bestIdx);
        else PackThree(dst, bestA, bestB, bestIdx);
    }

    static int Quantize(float r, float g, float b)
    {
        int ri = Math.Clamp((int)(r + 0.5f), 0, 255), gi = Math.Clamp((int)(g + 0.5f), 0, 255), bi = Math.Clamp((int)(b + 0.5f), 0, 255);
        return (Q5[ri] << 11) | (Q6[gi] << 5) | Q5[bi];
    }

    /// <summary>Assign + least-squares refinement from the start endpoints; returns the squared error.</summary>
    static int Fit(ReadOnlySpan<int> px, uint transparent, bool four, int a, int b, out int bestA, out int bestB, out uint bestIdx)
    {
        int bestErr = Assign(px, transparent, four, a, b, out bestIdx);
        bestA = a;
        bestB = b;
        for (int iter = 0; iter < 4 && bestErr > 0; iter++)
        {
            if (!LeastSquares(px, transparent, four, bestIdx, out int na, out int nb)) break;
            if (na == bestA && nb == bestB) break;
            int err = Assign(px, transparent, four, na, nb, out uint idx);
            if (err >= bestErr) break;
            bestErr = err;
            bestA = na;
            bestB = nb;
            bestIdx = idx;
        }
        return bestErr;
    }

    /// <summary>Nearest palette entry per pixel (decoder palette); transparent pixels get index 3.</summary>
    static int Assign(ReadOnlySpan<int> px, uint transparent, bool four, int a, int b, out uint indices)
    {
        Span<int> pal = stackalloc int[12];
        int r0 = E5[a >> 11], g0 = E6[(a >> 5) & 63], b0 = E5[a & 31];
        int r1 = E5[b >> 11], g1 = E6[(b >> 5) & 63], b1 = E5[b & 31];
        pal[0] = r0;
        pal[1] = g0;
        pal[2] = b0;
        pal[3] = r1;
        pal[4] = g1;
        pal[5] = b1;
        int n;
        if (four)
        {
            pal[6] = (2 * r0 + r1 + 1) / 3;
            pal[7] = (2 * g0 + g1 + 1) / 3;
            pal[8] = (2 * b0 + b1 + 1) / 3;
            pal[9] = (r0 + 2 * r1 + 1) / 3;
            pal[10] = (g0 + 2 * g1 + 1) / 3;
            pal[11] = (b0 + 2 * b1 + 1) / 3;
            n = 4;
        }
        else
        {
            pal[6] = (r0 + r1 + 1) >> 1;
            pal[7] = (g0 + g1 + 1) >> 1;
            pal[8] = (b0 + b1 + 1) >> 1;
            n = 3;
        }
        int total = 0;
        uint idx = 0;
        for (int i = 0; i < 16; i++)
        {
            if ((transparent >> i & 1) != 0)
            {
                idx |= 3u << (i * 2);
                continue;
            }
            int r = px[i * 3], g = px[i * 3 + 1], bl = px[i * 3 + 2];
            int best = 0, bestErr = int.MaxValue;
            for (int k = 0; k < n; k++)
            {
                int dr = r - pal[k * 3], dg = g - pal[k * 3 + 1], db = bl - pal[k * 3 + 2];
                int e = dr * dr + dg * dg + db * db;
                if (e < bestErr)
                {
                    bestErr = e;
                    best = k;
                }
            }
            total += bestErr;
            idx |= (uint)best << (i * 2);
        }
        indices = idx;
        return total;
    }

    // weight of endpoint A for each index (×3 for 4-colour, ×2 for 3-colour)
    static ReadOnlySpan<int> WeightFour => [3, 0, 2, 1];
    static ReadOnlySpan<int> WeightThree => [2, 0, 1, 0];

    static bool LeastSquares(ReadOnlySpan<int> px, uint transparent, bool four, uint indices, out int a, out int b)
    {
        var wt = four ? WeightFour : WeightThree;
        int scale = four ? 3 : 2;
        long aa = 0, ab = 0, bb = 0;
        long axr = 0, axg = 0, axb = 0, bxr = 0, bxg = 0, bxb = 0;
        for (int i = 0; i < 16; i++)
        {
            if ((transparent >> i & 1) != 0) continue;
            int k = (int)(indices >> (i * 2)) & 3;
            int wa = wt[k], wb = scale - wa;
            aa += wa * wa;
            ab += wa * wb;
            bb += wb * wb;
            int r = px[i * 3], g = px[i * 3 + 1], bl = px[i * 3 + 2];
            axr += wa * r;
            axg += wa * g;
            axb += wa * bl;
            bxr += wb * r;
            bxg += wb * g;
            bxb += wb * bl;
        }
        long det = aa * bb - ab * ab;
        if (det == 0)
        {
            a = b = 0;
            return false;
        }
        float f = (float)scale / det;
        a = Quantize((bb * axr - ab * bxr) * f, (bb * axg - ab * bxg) * f, (bb * axb - ab * bxb) * f);
        b = Quantize((aa * bxr - ab * axr) * f, (aa * bxg - ab * axg) * f, (aa * bxb - ab * axb) * f);
        return true;
    }

    /// <summary>4-colour block: c0 &gt; c1 (swaps endpoints and flips index bit 0 when needed); c0 == c1 → all index 0.</summary>
    static void PackFour(Span<byte> dst, int a, int b, uint idx)
    {
        if (a == b) idx = 0;
        else if (a < b)
        {
            (a, b) = (b, a);
            idx ^= 0x55555555u;
        }
        Write(dst, a, b, idx);
    }

    /// <summary>3-colour block: c0 &lt;= c1 (swap exchanges indices 0 and 1; 2 = midpoint and 3 = transparent stay).</summary>
    static void PackThree(Span<byte> dst, int a, int b, uint idx)
    {
        if (a > b)
        {
            (a, b) = (b, a);
            uint flipped = 0;
            for (int i = 0; i < 16; i++)
            {
                uint k = (idx >> (i * 2)) & 3;
                if (k < 2) k ^= 1;
                flipped |= k << (i * 2);
            }
            idx = flipped;
        }
        Write(dst, a, b, idx);
    }

    static void Write(Span<byte> dst, int c0, int c1, uint idx)
    {
        dst[0] = (byte)c0;
        dst[1] = (byte)(c0 >> 8);
        dst[2] = (byte)c1;
        dst[3] = (byte)(c1 >> 8);
        // decoder: row y = byte 4+y, pixel x = bits 2x → matches idx bit layout (pixel i = y*4+x at bits 2i)
        dst[4] = (byte)idx;
        dst[5] = (byte)(idx >> 8);
        dst[6] = (byte)(idx >> 16);
        dst[7] = (byte)(idx >> 24);
    }

    // ---- alpha block -----------------------------------------------------------------------------------------

    /// <summary>Encodes the alpha of 16 RGBA pixels into an 8-byte DXT5 alpha block.</summary>
    internal static void EncodeAlphaBlock(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        Span<int> al = stackalloc int[16];
        int min = 255, max = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = block[i * 4 + 3];
            al[i] = a;
            if (a < min) min = a;
            if (a > max) max = a;
        }
        if (min == max)
        {
            dst[0] = dst[1] = (byte)min;
            dst[2..8].Clear();
            return;
        }

        // 8-value mode: a0 > a1
        int bestErr = AssignAlpha(al, max, min, out ulong bestBits);
        int best0 = max, best1 = min;
        for (int iter = 0; iter < 3 && bestErr > 0; iter++)
        {
            if (!AlphaLeastSquares(al, bestBits, out int n0, out int n1) || n0 <= n1) break;
            if (n0 == best0 && n1 == best1) break;
            int e = AssignAlpha(al, n0, n1, out ulong bits);
            if (e >= bestErr) break;
            (bestErr, best0, best1, bestBits) = (e, n0, n1, bits);
        }

        // 6-value mode (a0 <= a1, explicit 0 and 255): useful when the block has both extremes and mid values
        if (bestErr > 0 && (min == 0 || max == 255))
        {
            int lo = 255, hi = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = al[i];
                if (a == 0 || a == 255) continue;
                if (a < lo) lo = a;
                if (a > hi) hi = a;
            }
            if (lo > hi) (lo, hi) = (0, 255);
            int e = AssignAlpha(al, lo, hi, out ulong bits);
            if (e < bestErr) (bestErr, best0, best1, bestBits) = (e, lo, hi, bits);
        }

        dst[0] = (byte)best0;
        dst[1] = (byte)best1;
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bestBits >> (8 * i));
    }

    static int AssignAlpha(ReadOnlySpan<int> al, int a0, int a1, out ulong bits)
    {
        Span<int> pal = stackalloc int[8];
        pal[0] = a0;
        pal[1] = a1;
        if (a0 > a1)
            for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
        else
        {
            for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
            pal[6] = 0;
            pal[7] = 255;
        }
        int total = 0;
        ulong b = 0;
        for (int i = 0; i < 16; i++)
        {
            int v = al[i], best = 0, bestErr = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int d = v - pal[k];
                d *= d;
                if (d < bestErr)
                {
                    bestErr = d;
                    best = k;
                }
            }
            total += bestErr;
            b |= (ulong)best << (3 * i);
        }
        bits = b;
        return total;
    }

    static bool AlphaLeastSquares(ReadOnlySpan<int> al, ulong bits, out int a0, out int a1)
    {
        long aa = 0, ab = 0, bb = 0, ax = 0, bx = 0;
        for (int i = 0; i < 16; i++)
        {
            int k = (int)(bits >> (3 * i)) & 7;
            int wa = k == 0 ? 7 : k == 1 ? 0 : 8 - k, wb = 7 - wa;
            aa += wa * wa;
            ab += wa * wb;
            bb += wb * wb;
            ax += wa * al[i];
            bx += wb * al[i];
        }
        long det = aa * bb - ab * ab;
        if (det == 0)
        {
            a0 = a1 = 0;
            return false;
        }
        a0 = Math.Clamp((int)MathF.Round(7f * (bb * ax - ab * bx) / det), 0, 255);
        a1 = Math.Clamp((int)MathF.Round(7f * (aa * bx - ab * ax) / det), 0, 255);
        return true;
    }
}
