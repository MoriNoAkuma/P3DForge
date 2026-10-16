using System.Numerics;

namespace P3DForge.App.Viewport.Interaction;

/// <summary>
/// Last known 3D cursor position (RAW coordinates): the mouse projected onto the plane through the pin
/// (or origin) facing the active view. Maintained by the interaction plugin; used e.g. by Create > Point
/// (Insert). Null when the mouse has not been over a view yet.
/// </summary>
public static class CursorState
{
    public static Vector3? Raw { get; set; }
}
