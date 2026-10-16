using System.Buffers.Binary;
using System.Text;

namespace P3DForge.Core.Materials;

/// <summary>
/// Binarized config ("raP", e.g. rvmats/config.bin packed by Binarize/AddonBuilder).
/// Layout: "\0raP", uint32 0, uint32 8, uint32 enumOffset, then the root class body at offset 16.
/// Class body: asciiz base, compressed-int entry count, entries. Entry: byte type
/// 0 class (asciiz name, uint32 body offset) · 1 value (byte subtype, asciiz name, value) · 2 array (asciiz name, array)
/// 3 extern class (asciiz) · 4 delete (asciiz) · 5 array with flag (uint32 flag 1 = "+=" / 2 = "-=", asciiz name, array).
/// Value subtypes: 0 string, 1 float32, 2 int32, 4 variable (word), 6 int64; array elements use the same ids + 3 = nested array.
/// Compressed int: 7 bits per byte, low group first, high bit = more.
/// </summary>
public static class Rap
{
    public static bool IsRap(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0 && data[1] == (byte)'r' && data[2] == (byte)'a' && data[3] == (byte)'P';

    /// <summary>Reads a raP file. Throws <see cref="InvalidDataException"/> when it is corrupt.</summary>
    public static ConfigClass Read(byte[] data)
    {
        if (!IsRap(data) || data.Length < 16) throw new InvalidDataException("not a raP (binarized config) file");
        var r = new Reader(data) { Pos = 16 };
        var root = new ConfigClass("");
        r.ReadBody(root, 0);
        return root;
    }

    sealed class Reader(byte[] d)
    {
        public int Pos;

        void Need(int n)
        {
            if (Pos < 0 || Pos + n > d.Length) throw new InvalidDataException($"raP: unexpected end of data at {Pos}");
        }

        byte U8()
        {
            Need(1);
            return d[Pos++];
        }

        uint U32()
        {
            Need(4);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(Pos));
            Pos += 4;
            return v;
        }

        int I32() => unchecked((int)U32());

        long I64()
        {
            Need(8);
            var v = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(Pos));
            Pos += 8;
            return v;
        }

        float F32()
        {
            Need(4);
            var v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(Pos));
            Pos += 4;
            return v;
        }

        string Asciiz()
        {
            var end = Array.IndexOf(d, (byte)0, Pos);
            if (end < 0) throw new InvalidDataException($"raP: unterminated string at {Pos}");
            var s = Encoding.UTF8.GetString(d, Pos, end - Pos);
            Pos = end + 1;
            return s;
        }

        int Compressed()
        {
            int v = 0, shift = 0;
            while (true)
            {
                var b = U8();
                v |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 28) throw new InvalidDataException($"raP: bad compressed integer at {Pos}");
            }
            if (v < 0 || v > d.Length) throw new InvalidDataException($"raP: implausible count {v} at {Pos}");
            return v;
        }

        public void ReadBody(ConfigClass cls, int depth)
        {
            if (depth > 64) throw new InvalidDataException("raP: classes nested too deep (cyclic offsets?)");
            var baseName = Asciiz();
            if (baseName.Length > 0) cls.Base = baseName;
            var count = Compressed();
            for (int i = 0; i < count; i++)
            {
                var type = U8();
                switch (type)
                {
                    case 0:
                    {
                        var name = Asciiz();
                        var offset = U32();
                        if (offset < 16 || offset >= d.Length) throw new InvalidDataException($"raP: class {name} offset {offset} out of range");
                        var c = new ConfigClass(name);
                        cls.Entries.Add(c);
                        var save = Pos;
                        Pos = (int)offset;
                        ReadBody(c, depth + 1);
                        Pos = save;
                        break;
                    }
                    case 1:
                    {
                        var sub = U8();
                        var name = Asciiz();
                        cls.Entries.Add(new ConfigProperty(name, Scalar(sub)));
                        break;
                    }
                    case 2:
                    {
                        var name = Asciiz();
                        cls.Entries.Add(new ConfigProperty(name, ReadArray(0)));
                        break;
                    }
                    case 3:
                        cls.Entries.Add(new ConfigClass(Asciiz()) { IsExtern = true });
                        break;
                    case 4:
                        cls.Entries.Add(new ConfigDelete { Name = Asciiz() });
                        break;
                    case 5:
                    {
                        var flag = U32();
                        var name = Asciiz();
                        cls.Entries.Add(new ConfigProperty(name, ReadArray(0), flag == 2 ? 2 : 1));
                        break;
                    }
                    default:
                        throw new InvalidDataException($"raP: unknown entry type {type} at {Pos - 1}");
                }
            }
        }

        ConfigValue Scalar(byte sub) => sub switch
        {
            0 => ConfigValue.Str(Asciiz()),
            1 => Float(F32()),
            2 => ConfigValue.Int(I32()),
            4 => ConfigValue.Word(Asciiz()),
            6 => ConfigValue.Int(I64()),
            _ => throw new InvalidDataException($"raP: unknown value type {sub} at {Pos - 1}"),
        };

        /// <summary>Integral floats keep a ".0" so raP → text → raP keeps the float type.</summary>
        static ConfigValue Float(float f)
        {
            var t = ConfigValue.FormatFloat(f);
            if (float.IsFinite(f) && t.IndexOfAny(['.', 'E', 'e']) < 0) t += ".0";
            return ConfigValue.Float(f, t);
        }

        ConfigValue ReadArray(int depth)
        {
            if (depth > 32) throw new InvalidDataException("raP: arrays nested too deep");
            var n = Compressed();
            var items = new ConfigValue[n];
            for (int i = 0; i < n; i++)
            {
                var t = U8();
                items[i] = t == 3 ? ReadArray(depth + 1) : Scalar(t);
            }
            return ConfigValue.Array(items);
        }
    }

    /// <summary>Binarizes a config tree (classes are laid out after their parent's entries, like Binarize).</summary>
    public static byte[] Write(ConfigClass root)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)0);
            w.Write((byte)'r');
            w.Write((byte)'a');
            w.Write((byte)'P');
            w.Write(0u);
            w.Write(8u);
            w.Write(0u); // enum offset (patched)
            WriteBody(w, root);
            var enumOffset = (uint)ms.Position;
            w.Write(0u); // no enums
            ms.Position = 12;
            w.Write(enumOffset);
        }
        return ms.ToArray();
    }

    static void WriteBody(BinaryWriter w, ConfigClass c)
    {
        Asciiz(w, c.Base ?? "");
        WriteCompressed(w, c.Entries.Count);
        var pending = new List<(ConfigClass Class, long Patch)>();
        foreach (var e in c.Entries)
        {
            switch (e)
            {
                case ConfigClass cc when cc.IsExtern:
                    w.Write((byte)3);
                    Asciiz(w, cc.Name);
                    break;
                case ConfigClass cc:
                    w.Write((byte)0);
                    Asciiz(w, cc.Name);
                    pending.Add((cc, w.BaseStream.Position));
                    w.Write(0u);
                    break;
                case ConfigDelete del:
                    w.Write((byte)4);
                    Asciiz(w, del.Name);
                    break;
                case ConfigProperty p when p.Value.IsArray && p.ArrayOp != 0:
                    w.Write((byte)5);
                    w.Write((uint)p.ArrayOp);
                    Asciiz(w, p.Name);
                    WriteArray(w, p.Value);
                    break;
                case ConfigProperty p when p.Value.IsArray:
                    w.Write((byte)2);
                    Asciiz(w, p.Name);
                    WriteArray(w, p.Value);
                    break;
                case ConfigProperty p:
                    w.Write((byte)1);
                    w.Write(ScalarType(p.Value));
                    Asciiz(w, p.Name);
                    WriteScalar(w, p.Value);
                    break;
            }
        }
        foreach (var (cc, patch) in pending)
        {
            var here = w.BaseStream.Position;
            w.BaseStream.Position = patch;
            w.Write((uint)here);
            w.BaseStream.Position = here;
            WriteBody(w, cc);
        }
    }

    static byte ScalarType(ConfigValue v) => v.Kind switch
    {
        ConfigValueKind.Float => 1,
        ConfigValueKind.Int => v.Number is >= int.MinValue and <= int.MaxValue ? (byte)2 : (byte)6,
        _ => 0,
    };

    static void WriteScalar(BinaryWriter w, ConfigValue v)
    {
        switch (ScalarType(v))
        {
            case 1:
                w.Write((float)v.Number);
                break;
            case 2:
                w.Write((int)v.Number);
                break;
            case 6:
                w.Write((long)v.Number);
                break;
            default:
                Asciiz(w, v.Text);
                break;
        }
    }

    static void WriteArray(BinaryWriter w, ConfigValue v)
    {
        WriteCompressed(w, v.Items.Count);
        foreach (var it in v.Items)
        {
            if (it.IsArray)
            {
                w.Write((byte)3);
                WriteArray(w, it);
            }
            else
            {
                w.Write(ScalarType(it));
                WriteScalar(w, it);
            }
        }
    }

    static void Asciiz(BinaryWriter w, string s)
    {
        w.Write(Encoding.UTF8.GetBytes(s));
        w.Write((byte)0);
    }

    internal static void WriteCompressed(BinaryWriter w, int v)
    {
        var u = (uint)v;
        while (u >= 0x80)
        {
            w.Write((byte)(u & 0x7F | 0x80));
            u >>= 7;
        }
        w.Write((byte)u);
    }
}
