using System.Collections.Immutable;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>Result of <see cref="Extrude.Faces"/>.</summary>
/// <param name="Lod">The new LOD (the extruded faces and their points are selected).</param>
/// <param name="FaceCount">Faces extruded.</param>
/// <param name="SideFaces">Side faces created.</param>
/// <param name="NewPoints">Points created.</param>
/// <param name="Direction">Direction used (null when it could not be determined; then nothing was moved).</param>
public sealed record ExtrudeResult(Lod Lod, int FaceCount, int SideFaces, int NewPoints, Vec3? Direction);

/// <summary>
/// Extrude (Create › Extrude, G): the selected faces move along their average outward normal and are connected to the
/// points they leave behind by side quads. Points on boundary edges of the selected region and points shared with
/// unselected faces are duplicated; interior points just move. Side quads follow the winding of the face owning the
/// boundary edge and copy its texture, material, flags, UVs and named-selection membership. Pure.
/// </summary>
public static class Extrude
{
    public const int MaxSegments = 64;

    /// <summary>Area-weighted average outward normal of the faces in <paramref name="faceMask"/>, or null (also for pure numerical noise, e.g. closed regions).</summary>
    public static Vec3? AverageOutwardNormal(Lod lod, byte[] faceMask)
    {
        double x = 0, y = 0, z = 0, area = 0;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faceMask[f] == 0) continue;
            var n = MeshMath.FaceNewell(lod, f);
            x -= n.X;
            y -= n.Y;
            z -= n.Z;
            area += n.Length;
        }
        var v = new Vec3(x, y, z);
        return v.Normalized() is { } u && v.Length > 1e-6 * area ? u : null;
    }

    /// <param name="lod">Source LOD.</param>
    /// <param name="faceMask">Faces to extrude.</param>
    /// <param name="distance">Offset along the direction (metres); 0 creates zero-length sides (to be moved afterwards).</param>
    /// <param name="segments">Number of side segments (1..64).</param>
    /// <param name="direction">Direction (RAW); default: area-weighted average outward normal.</param>
    public static ExtrudeResult Faces(Lod lod, byte[] faceMask, double distance, int segments = 1, Vec3? direction = null)
    {
        int fc = lod.FaceCount, n = lod.PointCount;
        segments = Math.Clamp(segments, 1, MaxSegments);
        if (!double.IsFinite(distance)) throw new ArgumentException("Invalid extrusion distance", nameof(distance));
        var inF = new bool[fc];
        int nF = 0;
        for (int f = 0; f < fc; f++)
            if (faceMask[f] != 0)
            {
                inF[f] = true;
                nF++;
            }
        if (nF == 0) return new ExtrudeResult(lod, 0, 0, 0, null);

        var fp = lod.FacePoints;
        var adj = PointFaces.Build(lod);
        // boundary edges: (owner face, slot k) whose edge k → k+1 is used by no other selected face
        var bFace = new List<int>();
        var bSlot = new List<int>();
        var onBoundary = new bool[n];
        var inRegion = new bool[n];
        var shared = new bool[n];
        for (int f = 0; f < fc; f++)
        {
            if (!inF[f]) continue;
            int nv = lod.FaceVertCount[f];
            for (int k = 0; k < nv; k++)
            {
                uint a = fp[f * 4 + k], b = fp[f * 4 + (k + 1) % nv];
                inRegion[a] = true;
                if (a == b) continue;
                bool other = false;
                for (int s = adj.Start[a]; s < adj.Start[a + 1] && !other; s++)
                {
                    int g = adj.Faces[s];
                    if (g == f || !inF[g]) continue;
                    for (int q = 0; q < lod.FaceVertCount[g]; q++)
                        if (fp[g * 4 + q] == b)
                        {
                            other = true;
                            break;
                        }
                }
                if (!other)
                {
                    bFace.Add(f);
                    bSlot.Add(k);
                    onBoundary[a] = true;
                    onBoundary[b] = true;
                }
            }
        }
        for (int p = 0; p < n; p++)
        {
            if (!inRegion[p]) continue;
            for (int s = adj.Start[p]; s < adj.Start[p + 1]; s++)
                if (!inF[adj.Faces[s]])
                {
                    shared[p] = true;
                    break;
                }
        }

        Vec3? dir = direction is { } dd ? dd.Normalized() : AverageOutwardNormal(lod, faceMask);
        double dist = dir is null ? 0 : distance;
        var off = dir is { } du ? du * dist : Vec3.Zero;

        // ring copies: ring r (1..segments) of boundary points; the last ring also holds shared points.
        int S1 = segments + 1;
        var regionSlot = new int[n];
        Array.Fill(regionSlot, -1);
        int nRegion = 0;
        for (int p = 0; p < n; p++)
            if (inRegion[p]) regionSlot[p] = nRegion++;
        var ringOf = new int[nRegion * S1];
        Array.Fill(ringOf, -1);
        var src = new List<int>();
        var pos = new List<float>();
        int next = n;
        var P = lod.Positions;
        for (int p = 0; p < n; p++)
        {
            if (!inRegion[p]) continue;
            int bas = regionSlot[p] * S1;
            ringOf[bas] = p;
            if (!onBoundary[p] && !shared[p])
            {
                ringOf[bas + segments] = p; // interior point: moves
                continue;
            }
            for (int r = 1; r <= segments; r++)
            {
                if (r < segments && !onBoundary[p]) continue;
                double t = (double)r / segments;
                ringOf[bas + r] = next++;
                src.Add(p);
                pos.Add((float)(P[p * 3] + off.X * t));
                pos.Add((float)(P[p * 3 + 1] + off.Y * t));
                pos.Add((float)(P[p * 3 + 2] + off.Z * t));
            }
        }
        int newPoints = next - n;
        var outLod = MeshMath.AddPoints(lod, src.ToArray(), pos.ToArray());

        // move interior points (and their animation frames) by the full offset
        if (dist != 0)
        {
            var positions = (float[])outLod.Positions.Clone();
            void Shift(float[] arr)
            {
                for (int p = 0; p < n; p++)
                {
                    if (!inRegion[p] || onBoundary[p] || shared[p]) continue;
                    arr[p * 3] = (float)(arr[p * 3] + off.X);
                    arr[p * 3 + 1] = (float)(arr[p * 3 + 1] + off.Y);
                    arr[p * 3 + 2] = (float)(arr[p * 3 + 2] + off.Z);
                }
            }
            Shift(positions);
            var tags = outLod.Tags.Select(t =>
            {
                if (t is not AnimationTag a) return t;
                var ap = (float[])a.Positions.Clone();
                Shift(ap);
                return a with { Positions = ap };
            }).ToImmutableArray();
            outLod = outLod with { Positions = positions, Tags = tags };
        }

        // faces: selected faces re-pointed to the last ring; side quads appended
        int sideCount = bFace.Count * segments;
        var fb = FaceBuild.Identity(outLod, sideCount);
        for (int f = 0; f < fc; f++)
        {
            if (!inF[f]) continue;
            for (int k = 0; k < lod.FaceVertCount[f]; k++) fb.Points[f * 4 + k] = (uint)ringOf[regionSlot[fp[f * 4 + k]] * S1 + segments];
        }
        int d = fc;
        Span<int> pts = stackalloc int[4];
        Span<int> slots = stackalloc int[4];
        for (int e = 0; e < bFace.Count; e++)
        {
            int f = bFace[e], k = bSlot[e];
            int k2 = (k + 1) % lod.FaceVertCount[f];
            uint a = fp[f * 4 + k], bb = fp[f * 4 + k2];
            for (int r = 0; r < segments; r++)
            {
                int ra = regionSlot[a] * S1, rb = regionSlot[bb] * S1;
                pts[0] = ringOf[ra + r];
                pts[1] = ringOf[rb + r];
                pts[2] = ringOf[rb + r + 1];
                pts[3] = ringOf[ra + r + 1];
                slots[0] = k;
                slots[1] = k2;
                slots[2] = k2;
                slots[3] = k;
                fb.VertCount[d] = 4;
                fb.Src[d] = f;
                for (int c = 0; c < 4; c++)
                {
                    fb.Points[d * 4 + c] = (uint)pts[c];
                    fb.SlotFace[d * 4 + c] = f;
                    fb.SlotIdx[d * 4 + c] = (sbyte)slots[c];
                }
                d++;
            }
        }
        outLod = MeshMath.RebuildFaces(outLod, fb);

        var sel = SelectionMask.Empty(outLod);
        for (int f = 0; f < fc; f++)
        {
            if (!inF[f]) continue;
            sel.Faces[f] = 1;
            for (int k = 0; k < outLod.FaceVertCount[f]; k++) sel.Points[outLod.FacePoints[f * 4 + k]] = 1;
        }
        return new ExtrudeResult(Selection.WithSelected(outLod, sel), nF, sideCount, newPoints, dir);
    }
}
