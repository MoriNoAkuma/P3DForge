using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>Where <see cref="FaceOps.Move"/> puts the selected faces in the face list.</summary>
public enum FaceOrder
{
    Top,
    Bottom,
}

/// <summary>
/// Face operations: reverse, uncross, triangulate, squarize, face order (move top/bottom), remove faces (keep points),
/// sharp/smooth edges. Pure; every per-face tag byte and every per-slot value (normal index, UV set 0, all #UVSet#
/// tags) is remapped consistently via <see cref="MeshMath.RebuildFaces"/>.
/// </summary>
public static class FaceOps
{
    static readonly int[] Rev3 = [0, 2, 1], Rev4 = [0, 3, 2, 1];
    static readonly int[] Id3 = [0, 1, 2], Id4 = [0, 1, 2, 3];
    static readonly int[][] QuadOrders = [[0, 1, 2, 3], [0, 1, 3, 2], [0, 2, 1, 3]];

    /// <summary>Reverses the vertex order (flips) of the faces in <paramref name="faceMask"/>, keeping the first vertex.</summary>
    public static Lod Reverse(Lod lod, byte[] faceMask)
    {
        var b = FaceBuild.Identity(lod);
        bool any = false;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faceMask[f] == 0) continue;
            b.CopyFace(f, lod, f, lod.FaceVertCount[f] == 4 ? Rev4 : Rev3);
            any = true;
        }
        return any ? MeshMath.RebuildFaces(lod, b) : lod;
    }

    /// <summary>
    /// Fixes self-intersecting ("crossed", bow-tie) quads among <paramref name="faceMask"/>: the vertex order with the
    /// largest polygon area is used, oriented like the face's first triangle. Returns the number of faces changed.
    /// </summary>
    public static (Lod Lod, int Changed) Uncross(Lod lod, byte[] faceMask)
    {
        var b = FaceBuild.Identity(lod);
        var P = lod.Positions;
        var fp = lod.FacePoints;
        int changed = 0;
        Span<uint> q = stackalloc uint[4];
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faceMask[f] == 0 || lod.FaceVertCount[f] != 4) continue;
            double bestArea = -1;
            int best = 0;
            var bestN = Vec3.Zero;
            for (int o = 0; o < 3; o++)
            {
                for (int k = 0; k < 4; k++) q[k] = fp[f * 4 + QuadOrders[o][k]];
                var nn = MeshMath.Newell(P, q);
                double area = nn.Length;
                if (area > bestArea * (1 + 1e-6) + 1e-12)
                {
                    bestArea = area;
                    best = o;
                    bestN = nn;
                }
            }
            if (best == 0) continue;
            int[] order = [.. QuadOrders[best]];
            // keep the orientation of the first triangle (0, 1, 2) of the original order
            var t = MeshMath.Newell(P, fp.AsSpan(f * 4, 3));
            if (Vec3.Dot(t, bestN) < 0) order = [order[0], order[3], order[2], order[1]];
            b.CopyFace(f, lod, f, order);
            changed++;
        }
        return (changed > 0 ? MeshMath.RebuildFaces(lod, b) : lod, changed);
    }

    /// <summary>
    /// Splits the quads in <paramref name="faceMask"/> into two triangles: diagonal 0-2 (OB "Triangulate \") gives
    /// (0,1,2) + (0,2,3), diagonal 1-3 ("Triangulate /") gives (0,1,3) + (1,2,3). Both halves keep texture, material,
    /// flags and selection bytes; the second half directly follows the first.
    /// </summary>
    public static (Lod Lod, int Split) Triangulate(Lod lod, byte[] faceMask, bool diagonal13 = false)
    {
        int fc = lod.FaceCount, split = 0;
        for (int f = 0; f < fc; f++)
            if (faceMask[f] != 0 && lod.FaceVertCount[f] == 4) split++;
        if (split == 0) return (lod, 0);
        var b = new FaceBuild(fc + split);
        int[] first = diagonal13 ? [0, 1, 3] : [0, 1, 2];
        int[] second = diagonal13 ? [1, 2, 3] : [0, 2, 3];
        int d = 0;
        for (int f = 0; f < fc; f++)
        {
            int nv = lod.FaceVertCount[f];
            if (faceMask[f] != 0 && nv == 4)
            {
                b.CopyFace(d++, lod, f, first);
                b.CopyFace(d++, lod, f, second);
            }
            else b.CopyFace(d++, lod, f, nv == 4 ? Id4 : Id3);
        }
        return (MeshMath.RebuildFaces(lod, b), split);
    }

    static bool UvEq(float[] a, int i, int j) =>
        Math.Abs(a[i * 2] - a[j * 2]) <= 1e-6 && Math.Abs(a[i * 2 + 1] - a[j * 2 + 1]) <= 1e-6;

    /// <summary>
    /// Merges pairs of coplanar triangles sharing an edge (consistently oriented, same texture, material and flags,
    /// continuous UVs in every UV set) into convex planar quads. Only triangles in <paramref name="faceMask"/> are
    /// considered (all when null). Pairs are chosen greedily by squareness; the quad takes the place of the lower-index
    /// triangle. Returns the number of quads created.
    /// </summary>
    public static (Lod Lod, int Merged) Squarize(Lod lod, byte[]? faceMask, double maxAngleDeg = 1)
    {
        int fc = lod.FaceCount;
        var P = lod.Positions;
        var fp = lod.FacePoints;
        double cosMax = Math.Cos(maxAngleDeg * Math.PI / 180);
        var tri = new byte[fc];
        int nTri = 0;
        for (int f = 0; f < fc; f++)
            if (lod.FaceVertCount[f] == 3 && (faceMask is null || faceMask[f] != 0))
            {
                tri[f] = 1;
                nTri++;
            }
        if (nTri < 2) return (lod, 0);
        var adj = PointFaces.Build(lod, tri);
        var uvTags = lod.UVSets.Select(t => t.UV).ToArray();
        var normals = new double[fc * 3];
        for (int f = 0; f < fc; f++)
        {
            if (tri[f] == 0) continue;
            if (MeshMath.FaceNewell(lod, f).Normalized() is { } nn)
            {
                normals[f * 3] = nn.X;
                normals[f * 3 + 1] = nn.Y;
                normals[f * 3 + 2] = nn.Z;
            }
        }

        // Candidate pairs (t < g): t's edge k (a→b) is traversed b→a by g at slot j (g[j] = b, g[j+1] = a).
        var pairT = new List<int>();
        var pairK = new List<int>();
        var pairG = new List<int>();
        var pairJ = new List<int>();
        var score = new List<double>();
        Span<uint> quad = stackalloc uint[4];
        for (int t = 0; t < fc; t++)
        {
            if (tri[t] == 0) continue;
            double nt0 = normals[t * 3], nt1 = normals[t * 3 + 1], nt2 = normals[t * 3 + 2];
            if (nt0 == 0 && nt1 == 0 && nt2 == 0) continue;
            for (int k = 0; k < 3; k++)
            {
                uint a = fp[t * 4 + k], bb = fp[t * 4 + (k + 1) % 3];
                int g = -1, j = -1, count = 0;
                for (int i = adj.Start[bb]; i < adj.Start[bb + 1]; i++)
                {
                    int h = adj.Faces[i];
                    if (h == t) continue;
                    for (int s = 0; s < 3; s++)
                    {
                        uint p0 = fp[h * 4 + s], p1 = fp[h * 4 + (s + 1) % 3];
                        if ((p0 == bb && p1 == a) || (p0 == a && p1 == bb))
                        {
                            count++;
                            if (p0 == bb)
                            {
                                g = h;
                                j = s;
                            }
                        }
                    }
                }
                if (count != 1 || g < 0 || g < t) continue; // boundary, non-manifold, inconsistent, or seen from g
                if (lod.FaceTexture[t] != lod.FaceTexture[g] || lod.FaceMaterial[t] != lod.FaceMaterial[g] || lod.FaceFlags[t] != lod.FaceFlags[g]) continue;
                if (nt0 * normals[g * 3] + nt1 * normals[g * 3 + 1] + nt2 * normals[g * 3 + 2] < cosMax) continue;
                uint w1 = fp[t * 4 + (k + 2) % 3], w2 = fp[g * 4 + (j + 2) % 3];
                if (w1 == w2) continue;
                // UV continuity on the shared edge: t(a) = g(a), t(b) = g(b) in every UV set
                int ta = t * 4 + k, tb = t * 4 + (k + 1) % 3, ga = g * 4 + (j + 1) % 3, gb = g * 4 + j;
                bool uvOk = UvEq(lod.FaceUV, ta, ga) && UvEq(lod.FaceUV, tb, gb);
                for (int u = 0; uvOk && u < uvTags.Length; u++) uvOk = UvEq(uvTags[u], ta, ga) && UvEq(uvTags[u], tb, gb);
                if (!uvOk) continue;
                // Quad (a, w2, b, w1): convex, no straight corners; score = deviation from right angles
                quad[0] = a;
                quad[1] = w2;
                quad[2] = bb;
                quad[3] = w1;
                int sign = 0;
                bool ok = true;
                double dev = 0;
                for (int c = 0; c < 4 && ok; c++)
                {
                    long p = quad[c] * 3L, pn = quad[(c + 1) % 4] * 3L, pp = quad[(c + 3) % 4] * 3L;
                    double e1x = P[pn] - P[p], e1y = P[pn + 1] - P[p + 1], e1z = P[pn + 2] - P[p + 2];
                    double e2x = P[pp] - P[p], e2y = P[pp + 1] - P[p + 1], e2z = P[pp + 2] - P[p + 2];
                    double cx = e1y * e2z - e1z * e2y, cy = e1z * e2x - e1x * e2z, cz = e1x * e2y - e1y * e2x;
                    double l1 = Math.Sqrt(e1x * e1x + e1y * e1y + e1z * e1z), l2 = Math.Sqrt(e2x * e2x + e2y * e2y + e2z * e2z);
                    double sv = cx * nt0 + cy * nt1 + cz * nt2;
                    if (!(l1 > 0 && l2 > 0) || Math.Abs(sv) < 1e-3 * l1 * l2)
                    {
                        ok = false;
                        break;
                    }
                    int sg = Math.Sign(sv);
                    if (sign == 0) sign = sg;
                    else if (sg != sign) ok = false;
                    double cosA = (e1x * e2x + e1y * e2y + e1z * e2z) / (l1 * l2);
                    dev += Math.Abs(Math.Acos(Math.Clamp(cosA, -1, 1)) - Math.PI / 2);
                }
                if (!ok) continue;
                pairT.Add(t);
                pairK.Add(k);
                pairG.Add(g);
                pairJ.Add(j);
                score.Add(dev);
            }
        }
        if (pairT.Count == 0) return (lod, 0);
        var orderIdx = Enumerable.Range(0, pairT.Count).ToArray();
        Array.Sort(orderIdx, (x, y) =>
        {
            int c = score[x].CompareTo(score[y]);
            if (c != 0) return c;
            c = pairT[x].CompareTo(pairT[y]);
            return c != 0 ? c : x.CompareTo(y);
        });
        var partner = new int[fc];
        Array.Fill(partner, -1);
        var pairOf = new int[fc];
        Array.Fill(pairOf, -1);
        int merged = 0;
        foreach (int i in orderIdx)
        {
            int t = pairT[i], g = pairG[i];
            if (partner[t] >= 0 || partner[g] >= 0) continue;
            partner[t] = g;
            partner[g] = t;
            pairOf[t] = i;
            merged++;
        }
        var b = new FaceBuild(fc - merged) { Src2 = new int[fc - merged] };
        Array.Fill(b.Src2, -1);
        int d = 0;
        for (int f = 0; f < fc; f++)
        {
            int p = partner[f];
            if (p >= 0 && p < f) continue; // second triangle of a pair: dropped
            if (p < 0)
            {
                b.CopyFace(d++, lod, f);
                continue;
            }
            int i = pairOf[f];
            int t = pairT[i], k = pairK[i], g = pairG[i], j = pairJ[i];
            // slots: a = t[k], w2 = g[j+2], b = t[k+1], w1 = t[k+2]
            ReadOnlySpan<int> sf = [t, g, t, t];
            ReadOnlySpan<int> si = [k, (j + 2) % 3, (k + 1) % 3, (k + 2) % 3];
            b.VertCount[d] = 4;
            b.Src[d] = t;
            b.Src2[d] = g;
            for (int c = 0; c < 4; c++)
            {
                b.Points[d * 4 + c] = fp[sf[c] * 4 + si[c]];
                b.SlotFace[d * 4 + c] = sf[c];
                b.SlotIdx[d * 4 + c] = (sbyte)si[c];
            }
            d++;
        }
        return (MeshMath.RebuildFaces(lod, b), merged);
    }

    /// <summary>Moves the faces in <paramref name="faceMask"/> to the start (Top) or the end (Bottom) of the face list.</summary>
    public static Lod Move(Lod lod, byte[] faceMask, FaceOrder where)
    {
        int fc = lod.FaceCount;
        var b = new FaceBuild(fc);
        int d = 0;
        void Emit(bool sel)
        {
            for (int f = 0; f < fc; f++)
                if ((faceMask[f] != 0) == sel) b.CopyFace(d++, lod, f);
        }
        Emit(where == FaceOrder.Top);
        Emit(where != FaceOrder.Top);
        return MeshMath.RebuildFaces(lod, b);
    }

    /// <summary>Removes the faces in <paramref name="faceMask"/>; all points are kept (Object Builder "Remove faces", D).</summary>
    public static Lod Remove(Lod lod, byte[] faceMask)
    {
        var keepFaces = new byte[lod.FaceCount];
        for (int f = 0; f < keepFaces.Length; f++) keepFaces[f] = faceMask[f] != 0 ? (byte)0 : (byte)1;
        return Structure.Compact(lod, MeshMath.Ones(lod.PointCount), keepFaces);
    }

    /// <summary>
    /// Marks (<paramref name="sharp"/> = true) or clears, in the #SharpEdges# tag, the face edges whose both end points
    /// are in <paramref name="pointMask"/>. The tag is created when needed (before the first named selection) and
    /// removed when it becomes empty. Returns the number of edges added or removed.
    /// </summary>
    public static (Lod Lod, int Changed) SetSharpEdges(Lod lod, byte[] pointMask, bool sharp)
    {
        long n = lod.PointCount;
        var fp = lod.FacePoints;
        long Key(uint a, uint b) => a < b ? a * n + b : b * n + a;
        var wanted = new HashSet<long>();
        var wantedList = new List<uint>();
        for (int f = 0; f < lod.FaceCount; f++)
        {
            int nv = lod.FaceVertCount[f];
            for (int k = 0; k < nv; k++)
            {
                uint a = fp[f * 4 + k], b = fp[f * 4 + (k + 1) % nv];
                if (a == b || pointMask[a] == 0 || pointMask[b] == 0) continue;
                if (wanted.Add(Key(a, b)))
                {
                    wantedList.Add(a);
                    wantedList.Add(b);
                }
            }
        }
        if (wanted.Count == 0) return (lod, 0);
        int idx = -1;
        for (int i = 0; i < lod.Tags.Length; i++)
            if (lod.Tags[i] is SharpEdgesTag)
            {
                idx = i;
                break;
            }
        var old = idx >= 0 ? ((SharpEdgesTag)lod.Tags[idx]).Edges : [];
        var edges = new List<uint>();
        int changed = 0;
        if (sharp)
        {
            var have = new HashSet<long>();
            for (int i = 0; i + 1 < old.Length; i += 2)
                if (old[i] < n && old[i + 1] < n) have.Add(Key(old[i], old[i + 1]));
            edges.AddRange(old);
            for (int i = 0; i < wantedList.Count; i += 2)
            {
                if (!have.Add(Key(wantedList[i], wantedList[i + 1]))) continue;
                edges.Add(wantedList[i]);
                edges.Add(wantedList[i + 1]);
                changed++;
            }
        }
        else
        {
            for (int i = 0; i + 1 < old.Length; i += 2)
            {
                uint a = old[i], b = old[i + 1];
                if (a < n && b < n && wanted.Contains(Key(a, b)))
                {
                    changed++;
                    continue;
                }
                edges.Add(a);
                edges.Add(b);
            }
        }
        if (changed == 0) return (lod, 0);
        var tags = lod.Tags;
        if (edges.Count == 0)
        {
            if (idx >= 0) tags = tags.RemoveAt(idx);
        }
        else
        {
            var tag = new SharpEdgesTag(idx >= 0 ? lod.Tags[idx].Active : (byte)1, edges.ToArray());
            if (idx >= 0) tags = tags.SetItem(idx, tag);
            else
            {
                int firstSel = -1;
                for (int i = 0; i < tags.Length; i++)
                    if (tags[i] is SelectionTag)
                    {
                        firstSel = i;
                        break;
                    }
                tags = tags.Insert(firstSel < 0 ? tags.Length : firstSel, tag);
            }
        }
        return (lod with { Tags = tags }, changed);
    }

    /// <summary>Quads and triangles among the faces in <paramref name="faceMask"/>.</summary>
    public static (int Quads, int Triangles) CountKinds(Lod lod, byte[] faceMask)
    {
        int quads = 0, tris = 0;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faceMask[f] == 0) continue;
            if (lod.FaceVertCount[f] == 4) quads++;
            else tris++;
        }
        return (quads, tris);
    }
}
