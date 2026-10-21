using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using P3DForge.Core.Model;

namespace P3DForge.Core.Anim;

/// <summary>
/// One bone of an .anm: translation and rotation keys (frame numbers ascending) in the bone's parent space, as the .txa
/// it was compiled from has them (<c>#t</c> / <c>#q</c>).
/// </summary>
public sealed record AnmBone(string Name, int[] TFrames, Vector3[] T, int[] QFrames, Quaternion[] Q)
{
    /// <summary>The translation at a (fractional) frame, linear between keys; null when the bone has no translation keys.</summary>
    public Vector3? Translation(float frame)
    {
        if (T.Length == 0) return null;
        var (a, b, w) = Between(TFrames, frame);
        return Vector3.Lerp(T[a], T[b], w);
    }

    /// <summary>The rotation at a (fractional) frame; null when the bone has no rotation keys.</summary>
    public Quaternion? Rotation(float frame)
    {
        if (Q.Length == 0) return null;
        var (a, b, w) = Between(QFrames, frame);
        return Quaternion.Slerp(Q[a], Q[b], w);
    }

    /// <summary>
    /// The bone's transform at a frame in model (RAW) coordinates. The .anm space is Z-up: model (x, y, z) = .anm
    /// (x, z, -y) — a weapon's bolt turns about the bore (X) at the bore's height.
    /// </summary>
    public Matrix4x4 ModelTransform(float frame)
    {
        var t = Translation(frame) ?? Vector3.Zero;
        var q = Rotation(frame) ?? Quaternion.Identity;
        return Matrix4x4.CreateFromQuaternion(new Quaternion(q.X, q.Z, -q.Y, q.W)) * Matrix4x4.CreateTranslation(t.X, t.Z, -t.Y);
    }

    static (int A, int B, float W) Between(int[] frames, float f)
    {
        int i = 0;
        while (i < frames.Length && frames[i] < f) i++;
        if (i == 0) return (0, 0, 0);
        if (i == frames.Length) return (i - 1, i - 1, 0);
        return (i - 1, i, (f - frames[i - 1]) / (frames[i] - frames[i - 1]));
    }
}

/// <summary>A decoded .anm: frames per second, frame count and the keyed bones.</summary>
public sealed record AnmClip(int Fps, int Frames, IReadOnlyList<AnmBone> Bones);

/// <summary>
/// DayZ (Enfusion) .anm animation files, as Workbench compiles them from .txa: an IFF "FORM" "ANIM" holding one "SETn"
/// chunk with FPS, HEAD (one record per bone: key counts and the value ranges) and DATA (per bone and channel the key
/// frame numbers, then the values, every number a little-endian u16; values are scaled into the bone's range).
/// SET5 has translation and rotation; SET6 adds scale (read past, not used). Chunk sizes are big-endian, unpadded.
/// </summary>
public static class Anm
{
    public static bool IsAnm(ReadOnlySpan<byte> data) => data.Length >= 12 && data[..4].SequenceEqual("FORM"u8) && data[8..12].SequenceEqual("ANIM"u8);

    /// <summary>Bone names in file order; empty when the file cannot be read.</summary>
    public static List<string> Bones(byte[] data) => Read(data)?.Bones.Select(b => b.Name).ToList() ?? [];

    /// <summary>The animation, or null for another file, a damaged one or a layout not known here (SET4).</summary>
    public static AnmClip? Read(byte[] data)
    {
        if (!IsAnm(data)) return null;
        try
        {
            return Parse(data);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null; // a size or count pointing past the data
        }
    }

    /// <summary>
    /// Positions (RAW, 3 per point) of <paramref name="lod"/> with the animation at <paramref name="frame"/>: each linked
    /// .anm bone moves the named selection of its model bone (by weight), about the model's origin.
    /// </summary>
    /// <remarks>shortcut: a model.cfg parent bone does not carry its children here; the vanilla weapon states animate
    /// every weapon bone on its own (the bolt's own keys include the slide).</remarks>
    public static float[] Pose(AnmClip clip, float frame, Lod lod, IEnumerable<(string ModelBone, string AnmBone)> links)
    {
        var src = lod.Positions;
        var acc = new Vector3[lod.PointCount];
        var wsum = new float[lod.PointCount];
        foreach (var (modelBone, anmBone) in links)
        {
            if (lod.Selection(modelBone) is not { } sel || clip.Bones.FirstOrDefault(b => string.Equals(b.Name, anmBone, StringComparison.OrdinalIgnoreCase)) is not { } bone) continue;
            var m = bone.ModelTransform(frame);
            for (int i = 0; i < Math.Min(sel.Points.Length, lod.PointCount); i++)
            {
                if (sel.Points[i] == 0) continue;
                float w = Weights.Decode(sel.Points[i]);
                var p = new Vector3(src[i * 3], src[i * 3 + 1], src[i * 3 + 2]);
                acc[i] += (Vector3.Transform(p, m) - p) * w;
                wsum[i] += w;
            }
        }
        var pos = (float[])src.Clone();
        for (int i = 0; i < lod.PointCount; i++)
        {
            if (wsum[i] <= 0) continue;
            var d = acc[i] / Math.Max(1f, wsum[i]);
            pos[i * 3] += d.X;
            pos[i * 3 + 1] += d.Y;
            pos[i * 3 + 2] += d.Z;
        }
        return pos;
    }

    readonly record struct Channel(int Keys, float Min, float Range);

    readonly record struct Head(string Name, int Frames, Channel T, Channel S, Channel Q);

    static AnmClip? Parse(byte[] d)
    {
        if (d.Length < 20 || !d.AsSpan(12, 3).SequenceEqual("SET"u8)) return null;
        int version = d[15] - '0';
        if (version is not (5 or 6)) return null;
        int end = 20 + Size(d, 16);
        ReadOnlySpan<byte> fps = default, head = default, data = default;
        for (int o = 20; o + 8 <= end;)
        {
            int size = Size(d, o + 4);
            var chunk = d.AsSpan(o + 8, size);
            switch (Encoding.ASCII.GetString(d, o, 4))
            {
                case "FPS\0": fps = chunk; break;
                case "HEAD": head = chunk; break;
                case "DATA": data = chunk; break;
            }
            o += 8 + size;
        }
        if (fps.Length < 4) return null;

        var heads = new List<Head>();
        for (int o = 0; o < head.Length;)
        {
            if (version == 5)
            {
                // name[32], tMin, tRange, qMin, qRange, frames, translation keys, rotation keys, flags
                var r = head.Slice(o, 56);
                int n = r[..32].IndexOf((byte)0);
                heads.Add(new Head(Encoding.ASCII.GetString(r[..(n < 0 ? 32 : n)]), U16(r, 48),
                    new Channel(U16(r, 50), F32(r, 32), F32(r, 36)), default, new Channel(U16(r, 52), F32(r, 40), F32(r, 44))));
                o += 56;
            }
            else
            {
                // tMin, tRange, qMin, qRange, sMin, sRange, frames, translation / rotation / scale keys, 0, name length, name
                var r = head[o..];
                int n = r[33];
                heads.Add(new Head(Encoding.ASCII.GetString(r.Slice(34, n)), U16(r, 24),
                    new Channel(U16(r, 26), F32(r, 0), F32(r, 4)), new Channel(U16(r, 30), F32(r, 16), F32(r, 20)), new Channel(U16(r, 28), F32(r, 8), F32(r, 12))));
                o += 34 + n;
            }
        }

        var bones = new List<AnmBone>(heads.Count);
        int p = 0;
        foreach (var h in heads)
        {
            var (tf, tv) = Keys(data, ref p, h.T, 3);
            Keys(data, ref p, h.S, 3);
            var (qf, qv) = Keys(data, ref p, h.Q, 4);
            var t = new Vector3[tf.Length];
            for (int i = 0; i < t.Length; i++) t[i] = new Vector3(tv[i * 3], tv[i * 3 + 1], tv[i * 3 + 2]);
            var q = new Quaternion[qf.Length];
            for (int i = 0; i < q.Length; i++)
            {
                var v = new Quaternion(qv[i * 4], qv[i * 4 + 1], qv[i * 4 + 2], qv[i * 4 + 3]);
                // root / collision bones of some vanilla files keep only part of their rotation in the range
                q[i] = v.LengthSquared() > 1e-12f ? Quaternion.Normalize(v) : Quaternion.Identity;
            }
            bones.Add(new AnmBone(h.Name, tf, t, qf, q));
        }
        if (p != data.Length) return null; // the layout was not understood
        return new AnmClip(BinaryPrimitives.ReadInt32LittleEndian(fps), heads.Count == 0 ? 0 : heads.Max(h => h.Frames), bones);
    }

    static (int[] Frames, float[] Values) Keys(ReadOnlySpan<byte> data, ref int p, Channel c, int components)
    {
        var frames = new int[c.Keys];
        for (int i = 0; i < frames.Length; i++) frames[i] = U16(data, p + i * 2);
        p += c.Keys * 2;
        var values = new float[c.Keys * components];
        for (int i = 0; i < values.Length; i++) values[i] = c.Min + U16(data, p + i * 2) / 65535f * c.Range;
        p += values.Length * 2;
        return (frames, values);
    }

    static int Size(byte[] d, int at) => (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at)), int.MaxValue);
    static int U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(at, 2));
    static float F32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s.Slice(at, 4));
}
