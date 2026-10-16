using System.Buffers.Binary;
using System.Text;

namespace P3DForge.Core.Anim;

/// <summary>
/// DayZ (Enfusion) .anm animation files: an IFF "FORM" with an ANIMSETn chunk list; the HEAD chunk holds one
/// 56-byte record per bone (32-byte zero-terminated name + transform data). Only the bone names are read — enough to
/// check that a weapon's model bones (through boneRemap) are the ones the animation moves.
/// </summary>
public static class Anm
{
    const int Record = 56, NameBytes = 32;

    public static bool IsAnm(ReadOnlySpan<byte> data) => data.Length >= 12 && data[..4].SequenceEqual("FORM"u8) && data[8..12].SequenceEqual("ANIM"u8);

    /// <summary>Bone names in file order; empty when the file has no readable HEAD.</summary>
    public static List<string> Bones(byte[] data)
    {
        var names = new List<string>();
        if (!IsAnm(data)) return names;
        // "FORM" size "ANIM" then "SETn" size with FPS / HEAD / DATA sub-chunks (sizes big-endian)
        int head = data.AsSpan(0, Math.Min(data.Length, 1 << 16)).IndexOf("HEAD"u8);
        if (head < 0 || head + 8 > data.Length) return names;
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(head + 4));
        int body = head + 8;
        if (size <= 0 || body + size > data.Length) return names;
        if (size % Record == 0)
            for (int r = body; r + Record <= body + size; r += Record)
            {
                var name = Name(data, r, NameBytes);
                if (name.Length > 0) names.Add(name);
            }
        else names.AddRange(Strings(data, body, size));
        return names;
    }

    static string Name(byte[] d, int at, int max)
    {
        int n = 0;
        while (n < max && d[at + n] != 0) n++;
        for (int i = 0; i < n; i++)
            if (d[at + i] is < 32 or > 126) return "";
        return Encoding.ASCII.GetString(d, at, n);
    }

    /// <summary>Printable runs of 3+ characters (fallback for an unknown HEAD layout).</summary>
    static IEnumerable<string> Strings(byte[] d, int at, int size)
    {
        int start = -1;
        for (int i = at; i <= at + size; i++)
        {
            bool ok = i < at + size && d[i] is >= (byte)'0' and <= (byte)'z';
            if (ok && start < 0) start = i;
            else if (!ok && start >= 0)
            {
                if (i - start >= 3) yield return Encoding.ASCII.GetString(d, start, i - start);
                start = -1;
            }
        }
    }
}
