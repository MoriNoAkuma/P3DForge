using System.Numerics;
using P3DForge.App.Editor;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Viewport.Interaction;

/// <summary>Draws the pin (EditorState.Pin) as a 3-axis cross in every view; dimmed while Use Pin is off.</summary>
sealed class PinOverlay
{
    const string Key = "pin";
    static EditorState Ed => AppServices.Editor;

    readonly IViewportContext ctx;
    Lod? sizedFor;
    float size = 0.1f;
    (Vector3? Pin, bool On, float Size) drawn = (null, false, 0);

    public PinOverlay(IViewportContext ctx)
    {
        this.ctx = ctx;
        Ed.Changed += OnChanged;
        Update();
    }

    public void Dispose()
    {
        Ed.Changed -= OnChanged;
        ctx.SetOverlayLines(Key, null);
    }

    void OnChanged(Change c)
    {
        if ((c & (Change.Pin | Change.Ui | Change.Model | Change.ActiveLod | Change.Load)) != 0) Update();
    }

    /// <summary>Cross half-size: 4 % of the active LOD's bounding-box diagonal (recomputed when the LOD changes).</summary>
    float Size()
    {
        var lod = Ed.Lod;
        if (ReferenceEquals(lod.Positions, sizedFor?.Positions)) return size;
        sizedFor = lod;
        var b = MeshMath.Bounds(lod.Positions, null);
        double diag = b is { } bb ? (bb.Max - bb.Min).Length : 0;
        size = (float)Math.Clamp(diag * 0.04, 0.02, 2.0);
        return size;
    }

    void Update()
    {
        var pin = Ed.Pin;
        bool on = Ed.Flag(Interact.UsePin, false);
        float s = pin is null ? 0 : Size();
        if (drawn == (pin, on, s)) return;
        drawn = (pin, on, s);
        if (pin is not { } p)
        {
            ctx.SetOverlayLines(Key, null);
            return;
        }
        uint a = on ? 0xFFu : 0x80u;
        var c = Coords.RawToRender(p);
        var x = new Vector3(s, 0, 0);
        var y = new Vector3(0, s, 0);
        var z = new Vector3(0, 0, s);
        float d = s * 0.25f;
        ctx.SetOverlayLines(Key,
        [
            new OverlayLine(c - x, c + x, 0xFF505000u | a),
            new OverlayLine(c - y, c + y, 0x50E05000u | a),
            new OverlayLine(c - z, c + z, 0x5090FF00u | a),
            // small diamond in the horizontal plane marks the centre
            new OverlayLine(c + new Vector3(d, 0, 0), c + new Vector3(0, 0, d), 0xF0F0F000u | a),
            new OverlayLine(c + new Vector3(0, 0, d), c + new Vector3(-d, 0, 0), 0xF0F0F000u | a),
            new OverlayLine(c + new Vector3(-d, 0, 0), c + new Vector3(0, 0, -d), 0xF0F0F000u | a),
            new OverlayLine(c + new Vector3(0, 0, -d), c + new Vector3(d, 0, 0), 0xF0F0F000u | a),
        ]);
    }
}
