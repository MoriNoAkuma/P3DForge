using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>Result of <see cref="Decimate.Run"/>.</summary>
public sealed record DecimateStats(int TrianglesBefore, int TrianglesAfter, int LockedPoints, int Collapses);

/// <summary>
/// Polygon reduction for resolution LODs: quadric error edge collapses (Garland–Heckbert) on the triangulated LOD,
/// each point moving onto a neighbour (no new positions, so UVs and weights stay exact). What must not move stays:
/// borders between textures / materials / named selections, UV seams and open edges only collapse along themselves
/// (with the UVs of both sides carried over), their corners and proxy triangles are locked; collapses that would
/// flip or flatten a face or break the mesh (link condition) are refused. Selections, weights, UV sets, sharp edges,
/// mass and animation phases follow (<see cref="Structure.Compact"/>); normals are recalculated.
/// </summary>
public static class Decimate
{
    /// <summary>Reduces <paramref name="source"/> to about <paramref name="keepRatio"/> of its triangles.</summary>
    public static Lod Run(Lod source, double keepRatio, out DecimateStats stats, CancellationToken ct = default)
    {
        var (lod, _) = FaceOps.Triangulate(source, MeshMath.Ones(source.FaceCount));
        var m = new Mesh(lod);
        int target = Math.Max(4, (int)Math.Round(m.AliveTris * Math.Clamp(keepRatio, 0, 1)));
        int before = m.AliveTris, collapses = 0;
        m.Seed();
        while (m.AliveTris > target && m.Queue.TryDequeue(out var e, out _))
        {
            if ((collapses & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (m.Dead[e.U] || m.Dead[e.V] || m.Stamp[e.U] != e.Su || m.Stamp[e.V] != e.Sv) continue;
            if (m.TryCollapse(e.U, e.V))
            {
                collapses++;
                m.PushAround(e.V);
            }
        }
        var result = m.Build();
        stats = new DecimateStats(before, m.AliveTris, m.LockedCount, collapses);
        return result;
    }

    readonly record struct Edge(int U, int V, int Su, int Sv);

    sealed class Mesh
    {
        readonly Lod lod;
        readonly int np, nf;
        readonly double[] pos;
        /// <summary>Corners on the welded points: coincident points (split along hard edges / UV seams) are one point
        /// here, so both sides of a split simplify together and no gap opens between them.</summary>
        readonly uint[] fp;
        /// <summary>The original (unwelded) point of every corner: written back on output, so the splits stay.</summary>
        readonly uint[] orig;
        /// <summary>UV set 0 of the faces and every UV set tag (all changed together), stride 8.</summary>
        readonly List<float[]> uvs = [];
        readonly int[] uvTag;
        readonly bool[] alive, fixedFace, locked;
        readonly int[] faceSig;
        readonly long[] pointSig;
        readonly List<int>[] vf;
        readonly double[] q;
        /// <summary>Unit normal of every face as it was: no face may end up turned away from it.</summary>
        readonly double[] n0;
        public readonly bool[] Dead;
        public readonly int[] Stamp;
        public readonly PriorityQueue<Edge, double> Queue = new();
        public int AliveTris;
        public int LockedCount => locked.Count(l => l);

        public Mesh(Lod lod)
        {
            this.lod = lod;
            np = lod.PointCount;
            nf = lod.FaceCount;
            pos = new double[np * 3];
            for (int i = 0; i < pos.Length; i++) pos[i] = lod.Positions[i];
            orig = (uint[])lod.FacePoints.Clone();
            fp = (uint[])lod.FacePoints.Clone();
            uvs.Add((float[])lod.FaceUV.Clone());
            var tagIdx = new List<int> { -1 };
            for (int i = 0; i < lod.Tags.Length; i++)
                if (lod.Tags[i] is UVSetTag u && u.UV.Length >= nf * 8)
                {
                    uvs.Add((float[])u.UV.Clone());
                    tagIdx.Add(i);
                }
            uvTag = [.. tagIdx];
            alive = new bool[nf];
            fixedFace = new bool[nf];
            locked = new bool[np];
            Dead = new bool[np];
            Stamp = new int[np];
            vf = new List<int>[np];
            for (int i = 0; i < np; i++) vf[i] = new List<int>(6);
            faceSig = new int[nf];
            pointSig = new long[np];
            for (int k = 0; k < lod.Tags.Length; k++)
            {
                if (lod.Tags[k] is not SelectionTag s) continue;
                bool proxy = Proxies.IsProxyName(s.Name);
                for (int f = 0; f < nf && f < s.Faces.Length; f++)
                    if (s.Faces[f] != 0)
                    {
                        if (proxy) fixedFace[f] = true;
                        else faceSig[f] = faceSig[f] * 31 + k + 1;
                    }
                if (proxy) continue;
                for (int p = 0; p < np && p < s.Points.Length; p++)
                    if (s.Points[p] != 0) pointSig[p] = pointSig[p] * 1_000_003 + (k + 1) * 257 + s.Points[p];
            }
            // weld coincident points with the same selections / weights / flags (proxy triangles stay apart)
            var proxyPoint = new bool[np];
            for (int f = 0; f < nf; f++)
                if (fixedFace[f])
                    for (int k = 0; k < lod.FaceVertCount[f]; k++) proxyPoint[lod.FacePoints[f * 4 + k]] = true;
            var first = new Dictionary<(float, float, float, long, uint), int>();
            var weld = new int[np];
            for (int p = 0; p < np; p++)
            {
                weld[p] = p;
                if (proxyPoint[p]) continue;
                var key = (lod.Positions[p * 3], lod.Positions[p * 3 + 1], lod.Positions[p * 3 + 2], pointSig[p], lod.PointFlags[p]);
                if (first.TryGetValue(key, out int r)) weld[p] = r;
                else first[key] = p;
            }
            for (int i = 0; i < fp.Length; i++) fp[i] = (uint)weld[fp[i]];
            for (int f = 0; f < nf; f++)
            {
                faceSig[f] = HashCode.Combine(faceSig[f], lod.FaceTexture[f], lod.FaceMaterial[f]);
                if (lod.FaceVertCount[f] != 3) fixedFace[f] = true;
                alive[f] = true;
                AliveTris += fixedFace[f] ? 0 : 1;
                for (int k = 0; k < lod.FaceVertCount[f]; k++)
                {
                    int p = (int)fp[f * 4 + k];
                    vf[p].Add(f);
                    if (fixedFace[f]) locked[p] = true;
                }
            }
            q = new double[np * 10];
            n0 = new double[nf * 3];
            for (int f = 0; f < nf; f++)
            {
                if (lod.FaceVertCount[f] != 3) continue;
                var n = Normal(f);
                double l = Len(n);
                if (l < 1e-20) continue;
                n0[f * 3] = n.X / l;
                n0[f * 3 + 1] = n.Y / l;
                n0[f * 3 + 2] = n.Z / l;
            }
        }

        // ---- geometry helpers

        (double X, double Y, double Z) P(int i) => (pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);

        static (double, double, double) Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
            (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        static double Len((double X, double Y, double Z) a) => Math.Sqrt(Dot(a, a));

        int V(int f, int k) => (int)fp[f * 4 + k];

        int Corner(int f, int p)
        {
            for (int k = 0; k < 3; k++)
                if (fp[f * 4 + k] == p) return k;
            return -1;
        }

        (double X, double Y, double Z) Normal(int f, int replace = -1, int with = -1)
        {
            int a = V(f, 0), b = V(f, 1), c = V(f, 2);
            if (a == replace) a = with;
            if (b == replace) b = with;
            if (c == replace) c = with;
            return Cross(Sub(P(b), P(a)), Sub(P(c), P(a)));
        }

        void AddPlane(int p, (double X, double Y, double Z) n, double d, double w)
        {
            var o = p * 10;
            q[o] += w * n.X * n.X; q[o + 1] += w * n.X * n.Y; q[o + 2] += w * n.X * n.Z; q[o + 3] += w * n.X * d;
            q[o + 4] += w * n.Y * n.Y; q[o + 5] += w * n.Y * n.Z; q[o + 6] += w * n.Y * d;
            q[o + 7] += w * n.Z * n.Z; q[o + 8] += w * n.Z * d;
            q[o + 9] += w * d * d;
        }

        double Error(int a, int b, int at)
        {
            var (x, y, z) = P(at);
            int i = a * 10, j = b * 10;
            double Q(int k) => q[i + k] + q[j + k];
            return Q(0) * x * x + 2 * Q(1) * x * y + 2 * Q(2) * x * z + 2 * Q(3) * x
                 + Q(4) * y * y + 2 * Q(5) * y * z + 2 * Q(6) * y
                 + Q(7) * z * z + 2 * Q(8) * z + Q(9);
        }

        // ---- topology helpers

        IEnumerable<int> Shared(int u, int x)
        {
            foreach (int f in vf[u])
                if (alive[f] && Corner(f, x) >= 0) yield return f;
        }

        HashSet<int> Neighbours(int u)
        {
            var set = new HashSet<int>();
            foreach (int f in vf[u])
            {
                if (!alive[f]) continue;
                for (int k = 0; k < 3; k++)
                    if (V(f, k) != u) set.Add(V(f, k));
            }
            return set;
        }

        bool SameUv(int f1, int f2, int p)
        {
            int c1 = Corner(f1, p), c2 = Corner(f2, p);
            foreach (var a in uvs)
                if (Math.Abs(a[f1 * 8 + c1 * 2] - a[f2 * 8 + c2 * 2]) > 1e-5f || Math.Abs(a[f1 * 8 + c1 * 2 + 1] - a[f2 * 8 + c2 * 2 + 1]) > 1e-5f) return false;
            return true;
        }

        /// <summary>Open edge, non-manifold edge, or the faces on its sides differ (texture, material, selections, UVs).</summary>
        bool IsSeam(int u, int x)
        {
            int f1 = -1, f2 = -1, n = 0;
            foreach (int f in Shared(u, x))
            {
                if (n == 0) f1 = f;
                else f2 = f;
                n++;
            }
            if (n != 2) return true;
            if (fixedFace[f1] || fixedFace[f2] || faceSig[f1] != faceSig[f2]) return true;
            // a split in the source (hard edge / seam): the two faces used different points
            if (orig[f1 * 4 + Corner(f1, u)] != orig[f2 * 4 + Corner(f2, u)] || orig[f1 * 4 + Corner(f1, x)] != orig[f2 * 4 + Corner(f2, x)]) return true;
            return !SameUv(f1, f2, u) || !SameUv(f1, f2, x);
        }

        List<int> SeamEdges(int u)
        {
            var list = new List<int>(2);
            foreach (int x in Neighbours(u))
                if (IsSeam(u, x)) list.Add(x);
            return list;
        }

        /// <summary>u may move onto v: u is free, and a seam point only slides along its seam.</summary>
        bool CanMove(int u, int v)
        {
            if (locked[u] || pointSig[u] != pointSig[v]) return false;
            var seams = SeamEdges(u);
            if (seams.Count == 0) return true;
            return seams.Count == 2 && seams.Contains(v);
        }

        // ---- queue

        public void Seed()
        {
            for (int f = 0; f < nf; f++)
            {
                if (!alive[f] || fixedFace[f]) continue;
                var n = Normal(f);
                double len = Len(n);
                if (len < 1e-20) continue;
                var nn = (n.X / len, n.Y / len, n.Z / len);
                double area = len / 2;
                int a = V(f, 0);
                double d = -Dot(nn, P(a));
                for (int k = 0; k < 3; k++) AddPlane(V(f, k), nn, d, area);
            }
            // seams and borders keep their line: a plane through the edge, across the face, with a heavy weight
            for (int f = 0; f < nf; f++)
            {
                if (!alive[f]) continue;
                var n = Normal(f);
                double len = Len(n);
                if (len < 1e-20) continue;
                for (int k = 0; k < 3; k++)
                {
                    int a = V(f, k), b = V(f, (k + 1) % 3);
                    if (!IsSeam(a, b)) continue;
                    var e = Sub(P(b), P(a));
                    var c = Cross(e, n);
                    double cl = Len(c);
                    if (cl < 1e-20) continue;
                    var cn = (c.X / cl, c.Y / cl, c.Z / cl);
                    double d = -Dot(cn, P(a)), w = 1000 * Dot(e, e);
                    AddPlane(a, cn, d, w);
                    AddPlane(b, cn, d, w);
                }
            }
            var seen = new HashSet<long>();
            for (int f = 0; f < nf; f++)
            {
                if (!alive[f] || fixedFace[f]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int a = V(f, k), b = V(f, (k + 1) % 3);
                    if (seen.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a)) Push(a, b);
                }
            }
        }

        void Push(int a, int b)
        {
            double ab = CanMove(a, b) ? Error(a, b, b) : double.PositiveInfinity;
            double ba = CanMove(b, a) ? Error(a, b, a) : double.PositiveInfinity;
            if (double.IsPositiveInfinity(ab) && double.IsPositiveInfinity(ba)) return;
            if (ab <= ba) Queue.Enqueue(new Edge(a, b, Stamp[a], Stamp[b]), ab);
            else Queue.Enqueue(new Edge(b, a, Stamp[b], Stamp[a]), ba);
        }

        public void PushAround(int v)
        {
            foreach (int x in Neighbours(v)) Push(v, x);
        }

        // ---- collapse

        public bool TryCollapse(int u, int v)
        {
            if (!CanMove(u, v)) return false;
            var remove = Shared(u, v).ToList();
            if (remove.Count is 0 or > 2 || remove.Any(f => fixedFace[f])) return false;
            // link condition: the common neighbours are exactly the third points of the removed faces
            var nu = Neighbours(u);
            nu.IntersectWith(Neighbours(v));
            var third = new HashSet<int>();
            foreach (int f in remove)
                for (int k = 0; k < 3; k++)
                    if (V(f, k) != u && V(f, k) != v) third.Add(V(f, k));
            if (!nu.SetEquals(third)) return false;
            // no face may flip or collapse
            var moving = vf[u].Where(f => alive[f] && !remove.Contains(f)).ToList();
            foreach (int f in moving)
            {
                var before = Normal(f);
                var after = Normal(f, u, v);
                double lb = Len(before), la = Len(after);
                if (la < lb * 1e-3 || la < 1e-12) return false;
                if (Dot(before, after) < 0.2 * lb * la) return false;
                // turned against the original face (many small turns add up): thin parts would fold inside out
                if (Dot((n0[f * 3], n0[f * 3 + 1], n0[f * 3 + 2]), after) < 0.1 * la) return false;
            }
            // UVs: every face around u takes v's UV from the removed face of its side (wedge between seam edges)
            var wedge = new Dictionary<int, int>();
            foreach (int r in remove)
            {
                var stack = new Stack<int>();
                stack.Push(r);
                while (stack.Count > 0)
                {
                    int f = stack.Pop();
                    if (!wedge.TryAdd(f, r)) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int x = V(f, k);
                        if (x == u || x == v || IsSeam(u, x)) continue;
                        foreach (int g in Shared(u, x))
                            if (!wedge.ContainsKey(g)) stack.Push(g);
                    }
                }
            }
            if (moving.Any(f => !wedge.ContainsKey(f))) return false;
            foreach (int f in moving)
            {
                int r = wedge[f], cu = Corner(f, u), cv = Corner(r, v);
                orig[f * 4 + cu] = orig[r * 4 + cv]; // the point of v on this side of a split
                foreach (var a in uvs)
                {
                    a[f * 8 + cu * 2] = a[r * 8 + cv * 2];
                    a[f * 8 + cu * 2 + 1] = a[r * 8 + cv * 2 + 1];
                }
            }
            // apply
            foreach (int f in remove)
            {
                alive[f] = false;
                AliveTris--;
                for (int k = 0; k < 3; k++) vf[V(f, k)].Remove(f);
            }
            foreach (int f in moving)
            {
                fp[f * 4 + Corner(f, u)] = (uint)v;
                vf[v].Add(f);
            }
            vf[u].Clear();
            for (int k = 0; k < 10; k++) q[v * 10 + k] += q[u * 10 + k];
            Dead[u] = true;
            Stamp[v]++;
            return true;
        }

        // ---- output

        public Lod Build()
        {
            var keepFaces = new byte[nf];
            var used = new byte[np];
            var everUsed = new byte[np];
            for (int f = 0; f < nf; f++)
                for (int k = 0; k < lod.FaceVertCount[f]; k++) everUsed[lod.FacePoints[f * 4 + k]] = 1;
            for (int f = 0; f < nf; f++)
            {
                if (!alive[f]) continue;
                keepFaces[f] = 1;
                for (int k = 0; k < lod.FaceVertCount[f]; k++) used[orig[f * 4 + k]] = 1;
            }
            var keepPoints = new byte[np];
            for (int p = 0; p < np; p++) keepPoints[p] = used[p] != 0 || everUsed[p] == 0 ? (byte)1 : (byte)0;
            var tags = lod.Tags;
            for (int i = 1; i < uvs.Count; i++) tags = tags.SetItem(uvTag[i], (UVSetTag)tags[uvTag[i]] with { UV = uvs[i] });
            var edited = lod with { FacePoints = orig, FaceUV = uvs[0], Tags = tags };
            return Structure.RecalcNormals(Structure.Compact(edited, keepPoints, keepFaces));
        }
    }
}
