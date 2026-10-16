namespace P3DForge.Core.Topology;

/// <summary>Why a convex hull could not be built.</summary>
public enum HullFailure
{
    None,
    TooFewPoints,
    /// <summary>All points (nearly) coincide.</summary>
    Degenerate,
    Colinear,
    Coplanar,
    /// <summary>An internal topology check failed even after retrying with larger tolerances.</summary>
    Broken,
}

/// <summary>
/// 3D QuickHull with coplanar/concave face merging, after J. E. Lloyd's QuickHull3D (ported from the debugged
/// legacy TypeScript implementation). Double arithmetic, struct-of-arrays half-edge storage with slot recycling.
/// Robustness beyond the original: iterative horizon DFS with a verified loop, merges refused when two faces touch
/// along more than one chain or the result would be degenerate, repeated removal of redundant (degree-2) vertices,
/// points that are no longer outside after a merge are dropped, and the final mesh is validated (twins, loops, Euler).
/// Output faces are vertex loops counter-clockwise seen from outside in a RIGHT-handed frame (local point indices).
/// </summary>
internal sealed class QuickHull
{
    private const byte Alive = 0, NonConvex = 1, Deleted = 2;
    private const int MergeNonConvexWrtLarger = 0, MergeNonConvex = 1;

    private readonly double[] _p;
    private readonly int _n;
    private readonly double _tol;
    private readonly int[] _vNext;
    private readonly int[] _vStamp;
    private int _horizonStamp;

    // Half-edges: a half-edge stores its head vertex; its tail is the head of its prev.
    private int[] _heHead = new int[256], _heNext = new int[256], _hePrev = new int[256], _heTwin = new int[256], _heFace = new int[256];
    private byte[] _heDead = new byte[256];
    private int _heTop;
    private readonly List<int> _heFree = [];

    // Faces.
    private int[] _fEdge = new int[64], _fOut = new int[64], _fVerts = new int[64];
    private byte[] _fMark = new byte[64];
    /// <summary>nx, ny, nz, offset per face (unit outward normal).</summary>
    private double[] _fPlane = new double[256];
    /// <summary>cx, cy, cz, area per face (area = |Newell normal| = twice the polygon area).</summary>
    private double[] _fCent = new double[256];
    private int _fTop;
    private readonly List<int> _fFree = [];

    private readonly List<int> _pending = [], _horizon = [], _newFaces = [], _deadFaces = [], _deadEdges = [], _redundant = [];
    private int[] _stackStop = new int[64], _stackCur = new int[64];
    private int _unclaimed = -1, _eyeFace = -1;
    private bool _broken;

    private QuickHull(double[] p, double tol)
    {
        _p = p;
        _n = p.Length / 3;
        _tol = tol;
        _vNext = new int[_n];
        Array.Fill(_vNext, -1);
        _vStamp = new int[_n];
    }

    /// <summary>
    /// Builds the hull of <paramref name="coords"/> (xyz per point, preferably centred near the origin) with distance
    /// tolerance <paramref name="tol"/>: points within tol of the hull surface may be left out.
    /// </summary>
    public static HullFailure Build(double[] coords, double tol, out int[] faceStart, out int[] loops)
    {
        faceStart = [];
        loops = [];
        if (coords.Length / 3 < 4) return HullFailure.TooFewPoints;
        var h = new QuickHull(coords, tol);
        var fail = h.CreateInitialSimplex();
        if (fail != HullFailure.None) return fail;
        for (int eye = h.NextEye(); eye >= 0; eye = h.NextEye())
        {
            h.AddPointToHull(eye);
            if (h._broken) return HullFailure.Broken;
        }
        if (!h.Validate()) return HullFailure.Broken;
        h.Polygons(out faceStart, out loops);
        return HullFailure.None;
    }

    // ------------------------------------------------------------------ storage

    private int AllocEdge()
    {
        int e;
        if (_heFree.Count > 0) { e = _heFree[^1]; _heFree.RemoveAt(_heFree.Count - 1); }
        else e = _heTop++;
        if (e >= _heHead.Length)
        {
            int size = _heHead.Length * 2;
            Array.Resize(ref _heHead, size);
            Array.Resize(ref _heNext, size);
            Array.Resize(ref _hePrev, size);
            Array.Resize(ref _heTwin, size);
            Array.Resize(ref _heFace, size);
            Array.Resize(ref _heDead, size);
        }
        _heDead[e] = 0;
        _heTwin[e] = -1;
        return e;
    }

    private int AllocFace()
    {
        int f;
        if (_fFree.Count > 0) { f = _fFree[^1]; _fFree.RemoveAt(_fFree.Count - 1); }
        else f = _fTop++;
        if (f >= _fEdge.Length)
        {
            int size = _fEdge.Length * 2;
            Array.Resize(ref _fEdge, size);
            Array.Resize(ref _fOut, size);
            Array.Resize(ref _fVerts, size);
            Array.Resize(ref _fMark, size);
            Array.Resize(ref _fPlane, size * 4);
            Array.Resize(ref _fCent, size * 4);
        }
        _fMark[f] = Alive;
        _fOut[f] = -1;
        return f;
    }

    private void KillEdge(int e)
    {
        _heDead[e] = 1;
        _deadEdges.Add(e);
    }

    /// <summary>Retires a face; its storage is recycled at the end of the current iteration.</summary>
    private void KillFace(int f)
    {
        _fMark[f] = Deleted;
        _deadFaces.Add(f);
    }

    private void ReleaseDead()
    {
        _fFree.AddRange(_deadFaces);
        _heFree.AddRange(_deadEdges);
        _deadFaces.Clear();
        _deadEdges.Clear();
    }

    /// <summary>Creates triangle (a, b, c), counter-clockwise seen from outside. Twins are unset.</summary>
    private int NewTriangle(int a, int b, int c)
    {
        int f = AllocFace();
        int e0 = AllocEdge(), e1 = AllocEdge(), e2 = AllocEdge();
        _heHead[e0] = a; _heHead[e1] = b; _heHead[e2] = c;
        _heNext[e0] = e1; _heNext[e1] = e2; _heNext[e2] = e0;
        _hePrev[e0] = e2; _hePrev[e1] = e0; _hePrev[e2] = e1;
        _heFace[e0] = f; _heFace[e1] = f; _heFace[e2] = f;
        _fEdge[f] = e0;
        ComputePlane(f);
        return f;
    }

    private void SetTwins(int a, int b)
    {
        _heTwin[a] = b;
        _heTwin[b] = a;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Newell-style normal (fan from the first vertex), centroid, area and vertex count.</summary>
    private void ComputePlane(int f)
    {
        var p = _p;
        int e0 = _fEdge[f];
        int o = _heHead[e0] * 3;
        double x0 = p[o], y0 = p[o + 1], z0 = p[o + 2];
        int e = _heNext[e0];
        int v = _heHead[e] * 3;
        double d2x = p[v] - x0, d2y = p[v + 1] - y0, d2z = p[v + 2] - z0;
        double sx = x0 + p[v], sy = y0 + p[v + 1], sz = z0 + p[v + 2];
        double nx = 0, ny = 0, nz = 0;
        int count = 2;
        for (e = _heNext[e]; e != e0; e = _heNext[e])
        {
            v = _heHead[e] * 3;
            double d1x = d2x, d1y = d2y, d1z = d2z;
            d2x = p[v] - x0;
            d2y = p[v + 1] - y0;
            d2z = p[v + 2] - z0;
            nx += d1y * d2z - d1z * d2y;
            ny += d1z * d2x - d1x * d2z;
            nz += d1x * d2y - d1y * d2x;
            sx += p[v];
            sy += p[v + 1];
            sz += p[v + 2];
            count++;
            if (count > _heTop + 3) { _broken = true; break; } // corrupted loop guard
        }
        double area = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (area > 0) { nx /= area; ny /= area; nz /= area; }
        double cx = sx / count, cy = sy / count, cz = sz / count;
        int i = f * 4;
        _fPlane[i] = nx;
        _fPlane[i + 1] = ny;
        _fPlane[i + 2] = nz;
        _fPlane[i + 3] = nx * cx + ny * cy + nz * cz;
        _fCent[i] = cx;
        _fCent[i + 1] = cy;
        _fCent[i + 2] = cz;
        _fCent[i + 3] = area;
        _fVerts[f] = count;
    }

    private double DistToPoint(int f, int v)
    {
        int i = f * 4, o = v * 3;
        return _fPlane[i] * _p[o] + _fPlane[i + 1] * _p[o + 1] + _fPlane[i + 2] * _p[o + 2] - _fPlane[i + 3];
    }

    /// <summary>Signed distance of the centroid of the face across <paramref name="he"/> from the plane of he's face.</summary>
    private double OppFaceDistance(int he)
    {
        int i = _heFace[he] * 4;
        int j = _heFace[_heTwin[he]] * 4;
        return _fPlane[i] * _fCent[j] + _fPlane[i + 1] * _fCent[j + 1] + _fPlane[i + 2] * _fCent[j + 2] - _fPlane[i + 3];
    }

    // ------------------------------------------------------------------ outside sets

    private void AddPointToFace(int v, int f)
    {
        if (_fOut[f] < 0) _pending.Add(f);
        _vNext[v] = _fOut[f];
        _fOut[f] = v;
    }

    private void MoveToUnclaimed(int f)
    {
        int v = _fOut[f];
        while (v >= 0)
        {
            int next = _vNext[v];
            _vNext[v] = _unclaimed;
            _unclaimed = v;
            v = next;
        }
        _fOut[f] = -1;
    }

    /// <summary>Moves the outside points of a dying face to <paramref name="absorbing"/> when above it, else unclaims them.</summary>
    private void HandOverPoints(int dying, int absorbing)
    {
        int v = _fOut[dying];
        _fOut[dying] = -1;
        while (v >= 0)
        {
            int next = _vNext[v];
            if (DistToPoint(absorbing, v) > _tol) AddPointToFace(v, absorbing);
            else
            {
                _vNext[v] = _unclaimed;
                _unclaimed = v;
            }
            v = next;
        }
    }

    /// <summary>Assigns every unclaimed point to the new face it is farthest above, or drops it.</summary>
    private void ResolveUnclaimed()
    {
        double early = 1000 * _tol;
        int v = _unclaimed;
        _unclaimed = -1;
        while (v >= 0)
        {
            int next = _vNext[v];
            int best = -1;
            double bestDist = _tol;
            for (int i = 0; i < _newFaces.Count; i++)
            {
                int f = _newFaces[i];
                if (_fMark[f] == Deleted) continue;
                double d = DistToPoint(f, v);
                if (d > bestDist)
                {
                    bestDist = d;
                    best = f;
                    if (d > early) break;
                }
            }
            if (best >= 0) AddPointToFace(v, best);
            else _vNext[v] = -1;
            v = next;
        }
    }

    /// <summary>Pops the next eye point: the farthest outside point of some face with an outside set.</summary>
    private int NextEye()
    {
        while (_pending.Count > 0)
        {
            int f = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
            if (_fMark[f] == Deleted || _fOut[f] < 0) continue;
            int best = -1, bestPrev = -1;
            double bestDist = _tol;
            for (int v = _fOut[f], prev = -1; v >= 0; prev = v, v = _vNext[v])
            {
                double d = DistToPoint(f, v);
                if (d > bestDist)
                {
                    bestDist = d;
                    best = v;
                    bestPrev = prev;
                }
            }
            if (best < 0)
            {
                // The face plane moved (merge) and none of its points is outside any more.
                _fOut[f] = -1;
                continue;
            }
            if (bestPrev < 0) _fOut[f] = _vNext[best];
            else _vNext[bestPrev] = _vNext[best];
            _vNext[best] = -1;
            _eyeFace = f;
            return best;
        }
        return -1;
    }

    // ------------------------------------------------------------------ initial simplex

    private HullFailure CreateInitialSimplex()
    {
        var p = _p;
        int n = _n;
        double tol = _tol;
        // Extreme points along each axis; the farthest-apart pair seeds the simplex.
        Span<int> ext = stackalloc int[6];
        for (int i = 1; i < n; i++)
        {
            for (int a = 0; a < 3; a++)
            {
                if (p[i * 3 + a] < p[ext[a] * 3 + a]) ext[a] = i;
                if (p[i * 3 + a] > p[ext[a + 3] * 3 + a]) ext[a + 3] = i;
            }
        }
        int v0 = 0, v1 = 0;
        double best = -1;
        for (int i = 0; i < 6; i++)
            for (int j = i + 1; j < 6; j++)
            {
                double d = Dist2(ext[i], ext[j]);
                if (d > best) { best = d; v0 = ext[i]; v1 = ext[j]; }
            }
        if (Math.Sqrt(best) <= tol) return HullFailure.Degenerate;

        // Farthest point from the line v0-v1.
        double ux = p[v1 * 3] - p[v0 * 3], uy = p[v1 * 3 + 1] - p[v0 * 3 + 1], uz = p[v1 * 3 + 2] - p[v0 * 3 + 2];
        double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        int v2 = -1;
        double bestLine = -1, nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = p[i * 3] - p[v0 * 3], dy = p[i * 3 + 1] - p[v0 * 3 + 1], dz = p[i * 3 + 2] - p[v0 * 3 + 2];
            double cx = uy * dz - uz * dy, cy = uz * dx - ux * dz, cz = ux * dy - uy * dx;
            double d = cx * cx + cy * cy + cz * cz;
            if (d > bestLine) { bestLine = d; v2 = i; nx = cx; ny = cy; nz = cz; }
        }
        if (Math.Sqrt(bestLine) / ul <= tol) return HullFailure.Colinear;

        // Farthest point from the plane v0-v1-v2.
        double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        nx /= nl; ny /= nl; nz /= nl;
        double d0 = nx * p[v0 * 3] + ny * p[v0 * 3 + 1] + nz * p[v0 * 3 + 2];
        int v3 = -1;
        double bestPlane = -1, side = 0;
        for (int i = 0; i < n; i++)
        {
            double d = nx * p[i * 3] + ny * p[i * 3 + 1] + nz * p[i * 3 + 2] - d0;
            if (Math.Abs(d) > bestPlane) { bestPlane = Math.Abs(d); v3 = i; side = d; }
        }
        if (bestPlane <= tol) return HullFailure.Coplanar;

        // Orient the base so that its normal points away from v3.
        var (a0, b0) = side > 0 ? (v1, v0) : (v0, v1);
        int[] faces =
        [
            NewTriangle(a0, b0, v2),
            NewTriangle(a0, v3, b0),
            NewTriangle(b0, v3, v2),
            NewTriangle(a0, v2, v3),
        ];
        LinkTwins(faces);

        for (int i = 0; i < n; i++)
        {
            if (i == v0 || i == v1 || i == v2 || i == v3) continue;
            int bestFace = -1;
            double bestDist = tol;
            foreach (int f in faces)
            {
                double d = DistToPoint(f, i);
                if (d > bestDist) { bestDist = d; bestFace = f; }
            }
            if (bestFace >= 0) AddPointToFace(i, bestFace);
        }
        return HullFailure.None;
    }

    private double Dist2(int a, int b)
    {
        double dx = _p[a * 3] - _p[b * 3], dy = _p[a * 3 + 1] - _p[b * 3 + 1], dz = _p[a * 3 + 2] - _p[b * 3 + 2];
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Pairs up the half-edges of a small closed set of faces by matching endpoints.</summary>
    private void LinkTwins(int[] faces)
    {
        var edges = new List<int>();
        foreach (int f in faces)
        {
            int e = _fEdge[f];
            do { edges.Add(e); e = _heNext[e]; } while (e != _fEdge[f]);
        }
        foreach (int e in edges)
        {
            int tail = _heHead[_hePrev[e]], head = _heHead[e];
            foreach (int t in edges)
                if (_heHead[t] == tail && _heHead[_hePrev[t]] == head) SetTwins(e, t);
        }
    }

    // ------------------------------------------------------------------ main step

    private void AddPointToHull(int eye)
    {
        _horizon.Clear();
        _newFaces.Clear();
        if (!ComputeHorizon(eye, _eyeFace))
        {
            _broken = true;
            return;
        }
        AddNewFaces(eye);

        foreach (int f in _newFaces)
            if (_fMark[f] == Alive)
                while (_fMark[f] != Deleted && !_broken && DoAdjacentMerge(f, MergeNonConvexWrtLarger)) { }
        foreach (int f in _newFaces)
        {
            if (_fMark[f] == NonConvex)
            {
                _fMark[f] = Alive;
                while (_fMark[f] != Deleted && !_broken && DoAdjacentMerge(f, MergeNonConvex)) { }
            }
        }
        if (_broken) return;
        ResolveUnclaimed();
        ReleaseDead();
    }

    private void Push(int top, int stop, int cur)
    {
        if (top >= _stackStop.Length)
        {
            Array.Resize(ref _stackStop, _stackStop.Length * 2);
            Array.Resize(ref _stackCur, _stackCur.Length * 2);
        }
        _stackStop[top] = stop;
        _stackCur[top] = cur;
    }

    /// <summary>
    /// Deletes every face visible from the eye (depth-first from the eye face) and collects the horizon edges in loop
    /// order. Returns false if the horizon is not a single closed loop.
    /// </summary>
    private bool ComputeHorizon(int eye, int eyeFace)
    {
        RemoveVisibleFace(eyeFace);
        int top = 0;
        Push(0, _fEdge[eyeFace], _fEdge[eyeFace]);
        bool rootStarted = false;
        while (top >= 0)
        {
            int cur = _stackCur[top];
            if (cur == _stackStop[top] && (top > 0 || rootStarted))
            {
                top--;
                continue;
            }
            rootStarted = true;
            _stackCur[top] = _heNext[cur];
            int twin = _heTwin[cur];
            if (twin < 0) return false;
            int opp = _heFace[twin];
            if (_fMark[opp] == Deleted) continue;
            if (DistToPoint(opp, eye) > _tol)
            {
                RemoveVisibleFace(opp);
                top++;
                Push(top, twin, _heNext[twin]);
            }
            else
            {
                _horizon.Add(cur);
            }
        }

        // The horizon must be one simple loop: chained edges, no vertex visited twice.
        int h = _horizon.Count;
        if (h < 3) return false;
        int stamp = ++_horizonStamp;
        for (int i = 0; i < h; i++)
        {
            int head = _heHead[_horizon[i]];
            if (_vStamp[head] == stamp) return false;
            _vStamp[head] = stamp;
            if (head != _heHead[_hePrev[_horizon[i + 1 == h ? 0 : i + 1]]]) return false;
        }
        return true;
    }

    private void RemoveVisibleFace(int f)
    {
        KillFace(f);
        MoveToUnclaimed(f);
        int e0 = _fEdge[f], e = e0;
        do
        {
            KillEdge(e);
            e = _heNext[e];
        } while (e != e0);
    }

    /// <summary>Cones the horizon to the eye: one triangle (eye, tail, head) per horizon edge.</summary>
    private void AddNewFaces(int eye)
    {
        int firstSide = -1, prevSide = -1;
        foreach (int h in _horizon)
        {
            int tail = _heHead[_hePrev[h]];
            int head = _heHead[h];
            int f = NewTriangle(eye, tail, head);
            int e0 = _fEdge[f];      // head -> eye
            int e1 = _heNext[e0];    // eye -> tail
            int e2 = _heNext[e1];    // tail -> head
            SetTwins(e2, _heTwin[h]);
            if (prevSide >= 0) SetTwins(e1, prevSide);
            else firstSide = e1;
            prevSide = e0;
            _newFaces.Add(f);
        }
        SetTwins(firstSide, prevSide);
    }

    // ------------------------------------------------------------------ merging

    /// <summary>Merges <paramref name="f"/> with one neighbour if their junction is not convex enough.</summary>
    private bool DoAdjacentMerge(int f, int mergeType)
    {
        double tol = _tol;
        int start = _fEdge[f], he = start;
        bool convex = true;
        int guard = 0;
        do
        {
            int opp = _heFace[_heTwin[he]];
            bool merge = false;
            if (mergeType == MergeNonConvex)
            {
                merge = OppFaceDistance(he) > -tol || OppFaceDistance(_heTwin[he]) > -tol;
            }
            else if (_fCent[f * 4 + 3] > _fCent[opp * 4 + 3])
            {
                if (OppFaceDistance(he) > -tol) merge = true;
                else if (OppFaceDistance(_heTwin[he]) > -tol) convex = false;
            }
            else if (OppFaceDistance(_heTwin[he]) > -tol)
            {
                merge = true;
            }
            else if (OppFaceDistance(he) > -tol)
            {
                convex = false;
            }
            if (merge && MergeAdjacentFace(f, he)) return true;
            he = _heNext[he];
            if (++guard > _heTop) { _broken = true; return false; }
        } while (he != start);
        if (!convex) _fMark[f] = NonConvex;
        return false;
    }

    /// <summary>
    /// Absorbs the face across <paramref name="hedgeAdj"/> into <paramref name="f"/>. Refuses (returns false) when the
    /// two faces touch along more than one chain or the merged face would have fewer than 3 edges.
    /// </summary>
    private bool MergeAdjacentFace(int f, int hedgeAdj)
    {
        int hedgeOpp = _heTwin[hedgeAdj];
        int opp = _heFace[hedgeOpp];
        int adjPrev = _hePrev[hedgeAdj], adjNext = _heNext[hedgeAdj];
        int oppPrev = _hePrev[hedgeOpp], oppNext = _heNext[hedgeOpp];
        int chain = 1;
        while (_heFace[_heTwin[adjPrev]] == opp)
        {
            adjPrev = _hePrev[adjPrev];
            oppNext = _heNext[oppNext];
            if (++chain >= _fVerts[f]) return false;
        }
        while (_heFace[_heTwin[adjNext]] == opp)
        {
            oppPrev = _hePrev[oppPrev];
            adjNext = _heNext[adjNext];
            if (++chain >= _fVerts[f]) return false;
        }
        if (chain >= _fVerts[opp] || _fVerts[f] + _fVerts[opp] - 2 * chain < 3) return false;
        if (adjNext != adjPrev)
        {
            for (int e = _heNext[adjNext]; e != adjPrev; e = _heNext[e])
                if (_heFace[_heTwin[e]] == opp) return false;
        }

        for (int e = _heNext[adjPrev]; e != adjNext; e = _heNext[e]) KillEdge(e);
        for (int e = _heNext[oppPrev]; e != oppNext; e = _heNext[e]) KillEdge(e);
        for (int e = oppNext; ; e = _heNext[e])
        {
            _heFace[e] = f;
            if (e == oppPrev) break;
        }
        _heNext[adjPrev] = oppNext;
        _hePrev[oppNext] = adjPrev;
        _heNext[oppPrev] = adjNext;
        _hePrev[adjNext] = oppPrev;
        _fEdge[f] = adjNext;
        KillFace(opp);
        ComputePlane(f);
        HandOverPoints(opp, f);

        _redundant.Add(adjPrev);
        _redundant.Add(oppPrev);
        RemoveRedundantVertices();
        return true;
    }

    /// <summary>
    /// Removes degree-2 vertices: for each queued edge e, the vertex head(e) is redundant when e and next(e) border the
    /// same face. Collapsing a triangle can make further vertices redundant; those are queued too.
    /// </summary>
    private void RemoveRedundantVertices()
    {
        while (_redundant.Count > 0)
        {
            int e1 = _redundant[^1];
            _redundant.RemoveAt(_redundant.Count - 1);
            if (_heDead[e1] == 1) continue;
            int e2 = _heNext[e1];
            int face = _heFace[e1];
            int other = _heFace[_heTwin[e1]];
            if (_heFace[_heTwin[e2]] != other) continue;
            if (_fVerts[face] > 3) RemoveVertex(face, e1, e2);
            else if (_fVerts[other] > 3) RemoveVertex(other, _heTwin[e2], _heTwin[e1]); // `face` collapses
            else
            {
                _broken = true;
                _redundant.Clear();
                return;
            }
        }
    }

    /// <summary>
    /// Removes vertex V = head(e1) = tail(e2) of <paramref name="face"/> (≥ 4 edges), where e1 and e2 both border the
    /// same face `other`. If `other` is a triangle it collapses into an edge.
    /// </summary>
    private void RemoveVertex(int face, int e1, int e2)
    {
        int other = _heFace[_heTwin[e1]];
        int t1 = _heTwin[e1]; // V -> A in other
        int t2 = _heTwin[e2]; // B -> V in other
        int a = _hePrev[e1];
        _heNext[a] = e2;
        _hePrev[e2] = a;
        if (_fEdge[face] == e1) _fEdge[face] = e2;
        KillEdge(e1);

        if (_fVerts[other] == 3)
        {
            int t3 = _heNext[t1]; // A -> B
            SetTwins(e2, _heTwin[t3]);
            KillEdge(t1);
            KillEdge(t2);
            KillEdge(t3);
            KillFace(other);
            ComputePlane(face);
            HandOverPoints(other, face);
            // A and B each lost an edge and may have become redundant.
            _redundant.Add(a);
            _redundant.Add(e2);
        }
        else
        {
            int c = _hePrev[t2];
            _heNext[c] = t1;
            _hePrev[t1] = c;
            if (_fEdge[other] == t2) _fEdge[other] = t1;
            KillEdge(t2);
            SetTwins(e2, t1);
            ComputePlane(other);
            ComputePlane(face);
        }
    }

    // ------------------------------------------------------------------ output

    /// <summary>Checks the half-edge structure of the final hull (closed, consistent, sphere-like).</summary>
    private bool Validate()
    {
        int faces = 0, halfEdges = 0, vertices = 0;
        var seen = new bool[_n];
        for (int f = 0; f < _fTop; f++)
        {
            if (_fMark[f] == Deleted) continue;
            faces++;
            int count = 0;
            int e = _fEdge[f];
            do
            {
                int t = _heTwin[e];
                if (_heDead[e] == 1 || _heFace[e] != f || _heNext[_hePrev[e]] != e) return false;
                if (t < 0 || _heDead[t] == 1 || _heTwin[t] != e || _heFace[t] == f) return false;
                if (_heHead[t] != _heHead[_hePrev[e]]) return false;
                if (!seen[_heHead[e]])
                {
                    seen[_heHead[e]] = true;
                    vertices++;
                }
                halfEdges++;
                if (++count > _heTop) return false;
                e = _heNext[e];
            } while (e != _fEdge[f]);
            if (count < 3) return false;
        }
        return vertices - halfEdges / 2 + faces == 2;
    }

    private void Polygons(out int[] faceStart, out int[] loops)
    {
        int faceCount = 0, total = 0;
        for (int f = 0; f < _fTop; f++)
        {
            if (_fMark[f] == Deleted) continue;
            faceCount++;
            int e = _fEdge[f];
            do { total++; e = _heNext[e]; } while (e != _fEdge[f]);
        }
        faceStart = new int[faceCount + 1];
        loops = new int[total];
        int k = 0, j = 0;
        for (int f = 0; f < _fTop; f++)
        {
            if (_fMark[f] == Deleted) continue;
            faceStart[k++] = j;
            int e = _fEdge[f];
            do
            {
                loops[j++] = _heHead[e];
                e = _heNext[e];
            } while (e != _fEdge[f]);
        }
        faceStart[k] = j;
    }
}
