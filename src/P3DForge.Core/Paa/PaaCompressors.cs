using System.Buffers.Binary;

namespace P3DForge.Core.Paa;

/// <summary>
/// LZO1X-1 compatible compressor (greedy, 16K-entry hash, the instruction set of minilzo's lzo1x_1_compress), used by
/// ImageToPAA for DXT mips wider than 128 pixels. Output decodes with <see cref="Lzo.Decompress"/> and any LZO1X decoder.
/// </summary>
public static class LzoEncoder
{
    const int M2MaxLen = 8, M2MaxOffset = 0x0800, M3MaxLen = 33, M3MaxOffset = 0x4000, M4MaxLen = 9, M4MaxOffset = 0xBFFF;
    const int M3Marker = 32, M4Marker = 16, DictBits = 14;

    public static byte[] Compress(ReadOnlySpan<byte> src)
    {
        int n = src.Length;
        var dst = new byte[n + n / 16 + 64 + 3];
        int op = 0, ii = 0;
        if (n > 20)
        {
            var dict = new int[1 << DictBits]; // position + 1 (0 = empty)
            int ipEnd = n - 20, ip = 1;
            while (ip < ipEnd)
            {
                uint dv = BinaryPrimitives.ReadUInt32LittleEndian(src[ip..]);
                int h = (int)((dv * 0x1824429Du) >> (32 - DictBits));
                int cand = dict[h] - 1;
                dict[h] = ip + 1;
                if (cand < 0 || ip - cand > M4MaxOffset || BinaryPrimitives.ReadUInt32LittleEndian(src[cand..]) != dv)
                {
                    ip += 1 + ((ip - ii) >> 5);
                    continue;
                }
                op = Literals(src, ii, ip - ii, dst, op);
                int len = 4;
                while (ip + len < ipEnd && src[ip + len] == src[cand + len]) len++;
                op = Match(dst, op, len, ip - cand);
                ip += len;
                ii = ip;
            }
        }
        op = Literals(src, ii, n - ii, dst, op);
        dst[op++] = M4Marker | 1;
        dst[op++] = 0;
        dst[op++] = 0;
        return dst.AsSpan(0, op).ToArray();
    }

    static int Literals(ReadOnlySpan<byte> src, int from, int t, byte[] dst, int op)
    {
        if (t <= 0) return op;
        if (op == 0 && t <= 238) dst[op++] = (byte)(17 + t);
        else if (t <= 3) dst[op - 2] |= (byte)t; // previous instruction is always a match: its low offset bits are free
        else if (t <= 18) dst[op++] = (byte)(t - 3);
        else
        {
            int tt = t - 18;
            dst[op++] = 0;
            while (tt > 255)
            {
                tt -= 255;
                dst[op++] = 0;
            }
            dst[op++] = (byte)tt;
        }
        src.Slice(from, t).CopyTo(dst.AsSpan(op));
        return op + t;
    }

    static int Match(byte[] dst, int op, int len, int off)
    {
        if (len <= M2MaxLen && off <= M2MaxOffset)
        {
            off -= 1;
            dst[op++] = (byte)(((len - 1) << 5) | ((off & 7) << 2));
            dst[op++] = (byte)(off >> 3);
            return op;
        }
        if (off <= M3MaxOffset)
        {
            off -= 1;
            if (len <= M3MaxLen) dst[op++] = (byte)(M3Marker | (len - 2));
            else op = Extended(dst, op, M3Marker, len - M3MaxLen);
        }
        else
        {
            off -= 0x4000;
            int marker = M4Marker | ((off >> 11) & 8);
            if (len <= M4MaxLen) dst[op++] = (byte)(marker | (len - 2));
            else op = Extended(dst, op, marker, len - M4MaxLen);
        }
        dst[op++] = (byte)(off << 2);
        dst[op++] = (byte)(off >> 6);
        return op;
    }

    static int Extended(byte[] dst, int op, int marker, int rest)
    {
        dst[op++] = (byte)marker;
        while (rest > 255)
        {
            rest -= 255;
            dst[op++] = 0;
        }
        dst[op++] = (byte)rest;
        return op;
    }
}

/// <summary>
/// Bohemia LZSS compressor (4 KiB window, matches of 3..18 bytes, flag byte per 8 tokens, trailing checksum) as used by
/// ImageToPAA for every mip of the non-DXT formats. The checksum is the SIGNED byte sum (verified on vanilla PAAs).
/// </summary>
public static class LzssEncoder
{
    const int Window = 4096, MinMatch = 3, MaxMatch = 18, HashBits = 13, MaxChain = 48;

    public static byte[] Compress(ReadOnlySpan<byte> src)
    {
        int n = src.Length;
        var dst = new byte[n + n / 8 + 16];
        var head = new int[1 << HashBits];
        Array.Fill(head, -1);
        var prev = new int[Window];
        int op = 0, ip = 0, flagPos = 0, bit = 8;
        while (ip < n)
        {
            if (bit == 8)
            {
                flagPos = op++;
                dst[flagPos] = 0;
                bit = 0;
            }
            int bestLen = 0, bestDist = 0;
            if (ip + MinMatch <= n)
            {
                int h = Hash(src, ip);
                int limit = Math.Min(MaxMatch, n - ip);
                for (int cand = head[h], chain = 0; cand >= 0 && ip - cand < Window && chain < MaxChain; cand = prev[cand & (Window - 1)], chain++)
                {
                    int l = 0;
                    while (l < limit && src[cand + l] == src[ip + l]) l++;
                    if (l > bestLen)
                    {
                        bestLen = l;
                        bestDist = ip - cand;
                        if (l == limit) break;
                    }
                    int next = prev[cand & (Window - 1)];
                    if (next >= cand) break; // stale slot (overwritten by a newer position)
                }
            }
            if (bestLen >= MinMatch)
            {
                dst[op++] = (byte)bestDist;
                dst[op++] = (byte)(((bestDist >> 4) & 0xF0) | (bestLen - MinMatch));
                for (int k = 0; k < bestLen; k++) Insert(src, ip + k, head, prev, n);
                ip += bestLen;
            }
            else
            {
                dst[flagPos] |= (byte)(1 << bit);
                dst[op++] = src[ip];
                Insert(src, ip, head, prev, n);
                ip++;
            }
            bit++;
        }
        int sum = 0;
        foreach (byte b in src) sum += (sbyte)b;
        BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(op), sum);
        op += 4;
        return dst.AsSpan(0, op).ToArray();
    }

    static int Hash(ReadOnlySpan<byte> s, int i) => ((s[i] << 10) ^ (s[i + 1] << 5) ^ s[i + 2]) & ((1 << HashBits) - 1);

    static void Insert(ReadOnlySpan<byte> s, int i, int[] head, int[] prev, int n)
    {
        if (i + MinMatch > n) return;
        int h = Hash(s, i);
        prev[i & (Window - 1)] = head[h];
        head[h] = i;
    }
}
