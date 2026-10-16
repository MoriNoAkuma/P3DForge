using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Viewport.Interaction;

/// <summary>
/// CPU item/depth buffer of the visible faces of a LOD as seen from one view, for rectangle / lasso selection of
/// visible points: the faces are rasterized once instead of ray-casting every candidate point (BTR LOD 0, 17k
/// faces: tens of ms instead of hundreds). A point is visible when, at one of the 2×2 pixel centres around it,
/// the front-most face is one of its own faces, nothing is drawn, or the front-most surface is not in front of it
/// by more than about one pixel of depth.
/// </summary>
sealed class DepthVisibility
{
    readonly int w, h;
    readonly bool persp;
    /// <summary>Per pixel: closeness of the nearest surface (perspective: 1/depth, ortho: -depth); -inf = empty.</summary>
    readonly float[] near;
    /// <summary>Per pixel: front-most face, -1 = empty.</summary>
    readonly int[] faceAt;
    readonly float[] proj;
    readonly float[] dist;
    readonly float uppPerDepth, uppConst;
    readonly Lod lod;

    public DepthVisibility(IViewHandle v, Lod lod, float[] raw)
    {
        this.lod = lod;
        int n = lod.PointCount;
        w = Math.Max(1, (int)Math.Ceiling(v.Rect.Width));
        h = Math.Max(1, (int)Math.Ceiling(v.Rect.Height));
        persp = !v.IsOrthographic;
        proj = new float[n * 2];
        v.ProjectRaw(raw.AsSpan(0, n * 3), proj);
        dist = new float[n];
        var eye = v.Eye;
        var fwd = v.Forward;
        for (int i = 0; i < n; i++)
        {
            // RENDER = (x, y, -z)
            float x = raw[i * 3] - eye.X, y = raw[i * 3 + 1] - eye.Y, z = -raw[i * 3 + 2] - eye.Z;
            dist[i] = x * fwd.X + y * fwd.Y + z * fwd.Z;
        }
        if (persp) uppPerDepth = v.UnitsPerPixel(eye + fwd);
        else uppConst = v.UnitsPerPixel(eye + fwd);

        near = new float[w * h];
        faceAt = new int[w * h];
        Array.Fill(near, float.NegativeInfinity);
        Array.Fill(faceAt, -1);
        var hiddenFaces = Selection.FacesTouching(lod, Selection.HiddenPoints(lod));
        var fp = lod.FacePoints;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (hiddenFaces[f] != 0) continue;
            int nv = lod.FaceVertCount[f];
            int a = (int)fp[f * 4];
            for (int k = 1; k + 1 < nv; k++) Raster(f, a, (int)fp[f * 4 + k], (int)fp[f * 4 + k + 1]);
        }
    }

    float Closeness(float d) => persp ? 1f / d : -d;

    void Raster(int face, int a, int b, int c)
    {
        float ax = proj[a * 2], ay = proj[a * 2 + 1], bx = proj[b * 2], by = proj[b * 2 + 1], cx = proj[c * 2], cy = proj[c * 2 + 1];
        if (float.IsNaN(ax) || float.IsNaN(bx) || float.IsNaN(cx)) return;
        if (persp && (dist[a] <= 1e-4f || dist[b] <= 1e-4f || dist[c] <= 1e-4f)) return;
        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (MathF.Abs(area) < 1e-9f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
        int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
        int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
        if (x0 > x1 || y0 > y1) return;
        float qa = Closeness(dist[a]), qb = Closeness(dist[b]), qc = Closeness(dist[c]);
        float inv = 1f / area;
        // barycentric weights (sign-normalized by the area, so both windings rasterize) are affine in x
        float aDx = (by - cy) * inv, bDx = (cy - ay) * inv;
        const float eps = -1e-5f;
        for (int y = y0; y <= y1; y++)
        {
            // row start from the difference form (exact for tiny triangles), then step along x
            float py = y + 0.5f, px0 = x0 + 0.5f;
            float wa = ((bx - px0) * (cy - py) - (by - py) * (cx - px0)) * inv;
            float wb = ((cx - px0) * (ay - py) - (cy - py) * (ax - px0)) * inv;
            int row = y * w;
            for (int x = x0; x <= x1; x++, wa += aDx, wb += bDx)
            {
                float wc = 1 - wa - wb;
                if (wa < eps || wb < eps || wc < eps) continue;
                float q = qc + (qa - qc) * wa + (qb - qc) * wb;
                int i = row + x;
                if (q > near[i])
                {
                    near[i] = q;
                    faceAt[i] = face;
                }
            }
        }
    }

    bool OwnFace(int face, int point)
    {
        int nv = lod.FaceVertCount[face];
        for (int k = 0; k < nv; k++)
            if (lod.FacePoints[face * 4 + k] == (uint)point) return true;
        return false;
    }

    /// <summary>True when no face hides the point (see the class summary).</summary>
    public bool IsVisible(int point)
    {
        float x = proj[point * 2], y = proj[point * 2 + 1];
        if (float.IsNaN(x)) return false;
        float d = dist[point];
        float upp = persp ? uppPerDepth * MathF.Abs(d) : uppConst;
        float tol = Tolerance * upp + 1e-4f * MathF.Abs(d) + 1e-5f;
        // the 2×2 pixel centres around the projected point
        int px0 = (int)MathF.Floor(x - 0.5f), py0 = (int)MathF.Floor(y - 0.5f);
        for (int py = py0; py <= py0 + 1; py++)
        for (int px = px0; px <= px0 + 1; px++)
        {
            if (px < 0 || py < 0 || px >= w || py >= h) return true;
            int i = py * w + px;
            int f = faceAt[i];
            if (f < 0 || OwnFace(f, point)) return true;
            float q = near[i];
            float surface = persp ? 1f / q : -q;
            if (d <= surface + tol) return true;
        }
        return false;
    }

    /// <summary>Depth tolerance in pixels of world size at the point (faces of other parts nearly touching the point).</summary>
    public static float Tolerance = 1f;

    /// <summary>Keeps the visible points of <paramref name="candidates"/>.</summary>
    public int[] Filter(int[] candidates)
    {
        int k = 0;
        var o = new int[candidates.Length];
        foreach (int i in candidates)
            if (IsVisible(i)) o[k++] = i;
        Array.Resize(ref o, k);
        return o;
    }
}
