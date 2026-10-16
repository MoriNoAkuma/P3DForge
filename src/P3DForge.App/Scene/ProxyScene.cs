using System.Numerics;
using P3DForge.Core.Model;

namespace P3DForge.App.Scene;

/*
 * Contract between the OBJECTS engineer (resolves + loads proxy models, builds the instances of the active LOD,
 * per-instance visibility, selection, the Objects panel, viewport picking) and the RENDER engineer (draws the
 * visible instances textured and lit like the main model, with shadows and the selection tint).
 *
 * The public members below are frozen: add members, never rename or remove them.
 * Spaces: Transform maps the proxy model's RAW coordinates into the edited model's RAW coordinates
 * (System.Numerics row vectors: rawInModel = Vector3.Transform(rawInProxy, Transform)). The render core
 * converts to RENDER space itself (RENDER = (x, y, -z) of RAW, see Viewport/ViewportContract.cs).
 */

/// <summary>A proxy model file loaded for display; shared by every instance of the same file.</summary>
public sealed class ProxyModel
{
    /// <summary>Resolved file path (e.g. P:\dz\structures\...\box_c.p3d) or the unresolved proxy path when missing.</summary>
    public required string Path { get; init; }
    /// <summary>The loaded model; null when it could not be loaded (see <see cref="Error"/>).</summary>
    public P3DModel? Model { get; init; }
    /// <summary>Index of the LOD drawn for the proxy (normally the first resolution LOD).</summary>
    public int LodIndex { get; init; }
    /// <summary>Why the model could not be loaded (missing file, unreadable file); null when loaded.</summary>
    public string? Error { get; init; }
    /// <summary>Read from a binarized (ODOL) game file — drawn, but it cannot be opened for editing.</summary>
    public bool Binarized { get; init; }
    /// <summary>Increments when the file was reloaded from disk; renderers cache GPU data by (Path, Version).</summary>
    public int Version { get; init; }

    /// <summary>The LOD drawn for this proxy, or null.</summary>
    public Lod? Lod => Model is { } m && LodIndex >= 0 && LodIndex < m.Lods.Length ? m.Lods[LodIndex] : null;
}

/// <summary>One placed proxy of the active LOD. Nested proxies (inside proxy models) are flattened with composed transforms.</summary>
/// <param name="Name">Named selection of the proxy in the edited model ("proxy:\dz\...\box_c.001"); nested instances use "parent > child".</param>
/// <param name="ProxyPath">Proxy path as stored in the selection name, without the index.</param>
/// <param name="Face">The proxy triangle (face index in the active LOD); -1 for nested instances.</param>
/// <param name="Depth">0 = placed in the edited model; 1+ = nested inside another proxy model.</param>
/// <param name="Transform">Proxy model RAW → edited model RAW.</param>
/// <param name="Model">The loaded (or failed) proxy model.</param>
/// <param name="Visible">False when hidden in the Objects panel.</param>
/// <param name="Selected">Selected in the Objects panel or by clicking it in the viewport.</param>
public sealed record ProxyInstance(
    string Name,
    string ProxyPath,
    int Face,
    int Depth,
    Matrix4x4 Transform,
    ProxyModel Model,
    bool Visible,
    bool Selected);

/// <summary>The proxy models shown in the viewport for the active LOD.</summary>
public static class ProxyScene
{
    static bool enabled = true, selectionBox = true, selectionTint = true;

    /// <summary>Master switch ("Proxies (N)"): when false the render core draws no proxy models.</summary>
    public static bool Enabled
    {
        get => enabled;
        set { if (enabled != value) { enabled = value; RaiseChanged(); } }
    }

    /// <summary>Draw a bounding box around selected instances.</summary>
    public static bool SelectionBox
    {
        get => selectionBox;
        set { if (selectionBox != value) { selectionBox = value; RaiseChanged(); } }
    }

    /// <summary>Tint selected instances.</summary>
    public static bool SelectionTint
    {
        get => selectionTint;
        set { if (selectionTint != value) { selectionTint = value; RaiseChanged(); } }
    }

    /// <summary>Instances of the active LOD of the current document (empty while nothing is loaded).</summary>
    public static IReadOnlyList<ProxyInstance> Instances { get; private set; } = [];

    /// <summary>Raised on the UI thread whenever instances, flags or a loaded proxy model change (render core: redraw).</summary>
    public static event Action? Changed;

    /// <summary>Replaces the instance list (UI thread) and raises <see cref="Changed"/>.</summary>
    public static void Publish(IReadOnlyList<ProxyInstance> instances)
    {
        Instances = instances;
        RaiseChanged();
    }

    public static void RaiseChanged() => Changed?.Invoke();
}
