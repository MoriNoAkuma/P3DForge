using System.Numerics;
using P3DForge.Core.Model;

namespace P3DForge.Core.Anim;

/// <summary>A source the animations of a model react to, with the value range they use.</summary>
public sealed record AnimSource(string Name, double Min, double Max, IReadOnlyList<string> Animations);

/// <summary>
/// Plays model.cfg animations on a LOD, as the engine does: every animation turns its source value into a phase
/// ((value − minValue) / (maxValue − minValue), clamped / looped / mirrored), the phase into a rotation about its axis,
/// a translation along it, a hide or a direct transform; a bone's animations combine in order and a bone moves with its
/// parent bone. A point follows the bones whose named selection holds it (by weight). Axes come from the Memory LOD
/// (memory = 1) or the animated LOD. Pure; build once per model / LOD and evaluate per frame.
/// </summary>
public sealed class AnimRig
{
    sealed class Track(AnimDef def)
    {
        public readonly AnimDef Def = def;
        public Vector3 Pivot;
        public Vector3 Dir = Vector3.UnitY;
        public float AxisLength = 1;
        public bool Valid = true;
    }

    sealed class BoneNode(string name)
    {
        public readonly string Name = name;
        public int Parent = -1;
        public readonly List<Track> Tracks = [];
        public (int Point, float Weight)[] Points = [];
        public Vector3 Centre;
    }

    readonly Lod lod;
    readonly List<BoneNode> bones = [];
    readonly Dictionary<string, int> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly int[] order; // parents before children

    public IReadOnlyList<AnimSource> Sources { get; }
    /// <summary>Problems found while building (missing selections, axes…), one line each.</summary>
    public IReadOnlyList<string> Problems { get; }
    /// <summary>Animations that can move something.</summary>
    public int ActiveAnimations { get; }

    public AnimRig(Lod lod, Lod? memory, IReadOnlyList<Bone> skeleton, IReadOnlyList<AnimDef> animations)
    {
        this.lod = lod;
        var problems = new List<string>();
        int NodeOf(string name)
        {
            if (byName.TryGetValue(name, out int i)) return i;
            bones.Add(new BoneNode(name));
            return byName[name] = bones.Count - 1;
        }
        foreach (var b in skeleton) NodeOf(b.Name);
        foreach (var b in skeleton)
            if (b.Parent.Length > 0) bones[NodeOf(b.Name)].Parent = NodeOf(b.Parent);

        int active = 0;
        foreach (var a in animations)
        {
            if (!byName.ContainsKey(a.Selection))
                problems.Add($"{a.Name}: \"{a.Selection}\" is not a bone of the skeleton — the game ignores this animation");
            var node = bones[NodeOf(a.Selection)];
            var t = new Track(a);
            ResolveAxis(t, a.Memory ? memory ?? lod : lod, a.Memory && memory is null, problems);
            node.Tracks.Add(t);
            if (t.Valid) active++;
        }
        foreach (var n in bones)
        {
            var sel = lod.Selection(n.Name);
            if (sel is null)
            {
                if (n.Tracks.Count > 0) problems.Add($"bone \"{n.Name}\": no named selection with this name in LOD {LodNames.Name(lod.Resolution)}");
                continue;
            }
            var list = new List<(int, float)>();
            var c = Vector3.Zero;
            for (int i = 0; i < Math.Min(sel.Points.Length, lod.PointCount); i++)
                if (sel.Points[i] != 0)
                {
                    list.Add((i, Weights.Decode(sel.Points[i])));
                    c += P(lod, i);
                }
            n.Points = [.. list];
            n.Centre = list.Count > 0 ? c / list.Count : Vector3.Zero;
        }
        // a rotation axis far away from the part it turns is almost always a wrong memory point name
        foreach (var n in bones)
        {
            if (n.Points.Length == 0) continue;
            float radius = 0;
            foreach (var (pt, _) in n.Points) radius = MathF.Max(radius, (P(lod, pt) - n.Centre).Length());
            foreach (var t in n.Tracks)
            {
                if (!t.Valid || t.Def.Kind != "rotation" || t.Def.Axis.Length == 0 && t.Def.Begin.Length == 0) continue;
                var rel = n.Centre - t.Pivot;
                float dist = (rel - Vector3.Dot(rel, t.Dir) * t.Dir).Length();
                if (dist > radius + 0.3f && dist > 2 * radius)
                    problems.Add($"{t.Def.Name}: the axis \"{(t.Def.Axis.Length > 0 ? t.Def.Axis : t.Def.Begin)}\" is {dist:0.##} m from \"{n.Name}\" (the part is {2 * radius:0.##} m across) — the part swings around a far point; wrong memory point?");
            }
        }

        // parents first (cycles are cut)
        var ord = new List<int>();
        var state = new int[bones.Count];
        void Visit(int i, int depth)
        {
            if (state[i] != 0 || depth > 64) return;
            state[i] = 1;
            if (bones[i].Parent >= 0) Visit(bones[i].Parent, depth + 1);
            state[i] = 2;
            ord.Add(i);
        }
        for (int i = 0; i < bones.Count; i++) Visit(i, 0);
        order = [.. ord];

        Sources = animations.Where(a => a.Source.Length > 0)
            .GroupBy(a => a.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AnimSource(g.First().Source, g.Min(a => Math.Min(a.MinValue, a.MaxValue)), g.Max(a => Math.Max(a.MinValue, a.MaxValue)),
                g.Select(a => a.Name).ToList()))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Problems = problems;
        ActiveAnimations = active;
    }

    static Vector3 P(Lod l, int i) => new(l.Positions[i * 3], l.Positions[i * 3 + 1], l.Positions[i * 3 + 2]);

    static List<Vector3> PointsOf(Lod l, string name)
    {
        var list = new List<Vector3>();
        if (name.Length == 0 || l.Selection(name) is not { } s) return list;
        for (int i = 0; i < Math.Min(s.Points.Length, l.PointCount); i++)
            if (s.Points[i] != 0) list.Add(P(l, i));
        return list;
    }

    static void ResolveAxis(Track t, Lod axisLod, bool noMemoryLod, List<string> problems)
    {
        var a = t.Def;
        string where = noMemoryLod ? "(the model has no Memory LOD)" : $"in LOD {LodNames.Name(axisLod.Resolution)}";
        switch (a.Kind)
        {
            case "unknown":
                problems.Add($"{a.Name}: unknown type \"{a.Type}\"");
                t.Valid = false;
                return;
            case "hide":
            case "direct":
                return;
        }
        string type = a.Type.ToLowerInvariant();
        bool fixedAxis = type.EndsWith('x') || type.EndsWith('y') || type.EndsWith('z');
        Vector3? p0 = null, p1 = null;
        if (a.Axis.Length > 0)
        {
            var pts = PointsOf(axisLod, a.Axis);
            if (pts.Count >= 2) (p0, p1) = (pts[0], pts[1]);
            else if (pts.Count == 1) p0 = pts[0];
            else problems.Add($"{a.Name}: axis \"{a.Axis}\" not found {where}");
        }
        else if (a.Begin.Length > 0 || a.End.Length > 0)
        {
            var b = PointsOf(axisLod, a.Begin);
            var e = PointsOf(axisLod, a.End);
            if (b.Count > 0) p0 = Avg(b);
            if (e.Count > 0) p1 = Avg(e);
            if (b.Count == 0 || e.Count == 0) problems.Add($"{a.Name}: begin / end \"{a.Begin}\" / \"{a.End}\" not found {where}");
        }
        if (fixedAxis)
        {
            t.Dir = type[^1] switch { 'x' => Vector3.UnitX, 'y' => Vector3.UnitY, _ => Vector3.UnitZ };
            t.Pivot = p0 is { } q && p1 is { } r ? (q + r) / 2 : p0 ?? Vector3.Zero;
            t.AxisLength = 1;
            return;
        }
        if (p0 is { } s0 && p1 is { } s1 && (s1 - s0).LengthSquared() > 1e-12f)
        {
            t.Pivot = s0;
            t.AxisLength = (s1 - s0).Length();
            t.Dir = (s1 - s0) / t.AxisLength;
            return;
        }
        if (p0 is not null && p1 is null && a.Axis.Length > 0) problems.Add($"{a.Name}: axis \"{a.Axis}\" needs two points");
        else if (a.Axis.Length == 0 && a.Begin.Length == 0 && a.End.Length == 0)
            problems.Add($"{a.Name}: no axis — choose Axis (two Memory LOD points) or Begin / End, or press \"New axis…\"");
        t.Valid = false;
    }

    static Vector3 Avg(List<Vector3> v)
    {
        var s = Vector3.Zero;
        foreach (var x in v) s += x;
        return s / v.Count;
    }

    /// <summary>The source value of the rest pose: 0 when the range includes it (steering straight, damper centred), else the start.</summary>
    public static double Rest(AnimDef a) => Math.Clamp(0, Math.Min(a.MinValue, a.MaxValue), Math.Max(a.MinValue, a.MaxValue));

    /// <summary>The rest value of a source (see <see cref="Rest(AnimDef)"/>).</summary>
    public static double Rest(AnimSource s) => Math.Clamp(0, s.Min, s.Max);

    /// <summary>The phase (0..1) of an animation at a source value.</summary>
    public static double Phase(AnimDef a, double value)
    {
        double span = a.MaxValue - a.MinValue;
        double p = Math.Abs(span) < 1e-12 ? (value >= a.MaxValue ? 1 : 0) : (value - a.MinValue) / span;
        var mode = a.SourceAddress.ToLowerInvariant(); // "loop" / "AnimLoop", "mirror" / "AnimMirror", else clamp
        if (mode.Contains("loop")) p -= Math.Floor(p);
        else if (mode.Contains("mirror"))
        {
            p = Math.Abs(p) % 2;
            if (p > 1) p = 2 - p;
        }
        else p = Math.Clamp(p, 0, 1);
        return p;
    }

    /// <summary>Positions (RAW, 3 per point) with every source at its value; sources not given sit at their rest value.</summary>
    public float[] Evaluate(IReadOnlyDictionary<string, double> values)
    {
        var src = lod.Positions;
        var world = Pose(values, out var hidden);
        var outPos = (float[])src.Clone();
        var acc = new Vector3[lod.PointCount];
        var wsum = new float[lod.PointCount];
        for (int i = 0; i < bones.Count; i++)
        {
            var n = bones[i];
            if (n.Points.Length == 0 || (world[i].IsIdentity && !hidden[i])) continue;
            foreach (var (pt, w) in n.Points)
            {
                var p = P(lod, pt);
                var moved = hidden[i] ? n.Centre : Vector3.Transform(p, world[i]); // hidden: collapsed (not drawn in the game)
                acc[pt] += (moved - p) * w;
                wsum[pt] += w;
            }
        }
        for (int pt = 0; pt < lod.PointCount; pt++)
        {
            if (wsum[pt] <= 0) continue;
            var d = acc[pt] / Math.Max(1f, wsum[pt]);
            outPos[pt * 3] += d.X;
            outPos[pt * 3 + 1] += d.Y;
            outPos[pt * 3 + 2] += d.Z;
        }
        return outPos;
    }

    /// <summary>The transform (RAW) of a bone with every source at its value, its parents included; null for an unknown bone.</summary>
    public Matrix4x4? BoneTransform(string bone, IReadOnlyDictionary<string, double> values) =>
        byName.TryGetValue(bone, out int i) ? Pose(values, out _)[i] : null;

    Matrix4x4[] Pose(IReadOnlyDictionary<string, double> values, out bool[] hidden)
    {
        var world = new Matrix4x4[bones.Count];
        hidden = new bool[bones.Count];
        foreach (int i in order)
        {
            var n = bones[i];
            var m = Matrix4x4.Identity;
            bool hide = false;
            foreach (var t in n.Tracks)
            {
                if (!t.Valid) continue;
                var a = t.Def;
                double v = values.TryGetValue(a.Source, out var x) ? x : Rest(a);
                double p = Phase(a, v);
                switch (a.Kind)
                {
                    case "rotation":
                    {
                        float angle = (float)(a.Angle0 + p * (a.Angle1 - a.Angle0));
                        m *= Rotation(t.Pivot, t.Dir, angle);
                        break;
                    }
                    case "translation":
                    {
                        float off = (float)(a.Offset0 + p * (a.Offset1 - a.Offset0)) * t.AxisLength;
                        m *= Matrix4x4.CreateTranslation(t.Dir * off);
                        break;
                    }
                    case "hide":
                        if (p >= a.HideValue && p < a.UnHideValue) hide = true;
                        break;
                    case "direct":
                    {
                        var pos = new Vector3((float)a.AxisPos[0], (float)a.AxisPos[1], (float)a.AxisPos[2]);
                        var dir = new Vector3((float)a.AxisDir[0], (float)a.AxisDir[1], (float)a.AxisDir[2]);
                        if (dir.LengthSquared() < 1e-12f) break;
                        dir = Vector3.Normalize(dir);
                        m *= Rotation(pos, dir, (float)(a.Angle * p)) * Matrix4x4.CreateTranslation(dir * (float)(a.AxisOffset * p));
                        break;
                    }
                }
            }
            if (n.Parent >= 0)
            {
                m *= world[n.Parent];
                hide |= hidden[n.Parent];
            }
            world[i] = m;
            hidden[i] = hide;
        }
        return world;
    }

    /// <summary>
    /// Rotation by <paramref name="angle"/> radians about the axis through <paramref name="pivot"/> along
    /// <paramref name="dir"/> in model (RAW) coordinates. RAW is left-handed, so a positive angle turns clockwise when
    /// looking along the axis direction, which is how the engine's model.cfg rotations behave.
    /// </summary>
    static Matrix4x4 Rotation(Vector3 pivot, Vector3 dir, float angle) =>
        Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromAxisAngle(dir, angle) * Matrix4x4.CreateTranslation(pivot);
}
