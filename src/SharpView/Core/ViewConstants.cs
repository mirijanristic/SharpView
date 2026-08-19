using System.Numerics;
using System.Runtime.InteropServices;

namespace SharpView.Core;

/// <summary>
/// Per-draw constants consumed by the shared quad shader.
/// Layout must match the <c>ViewCB</c> cbuffer in <see cref="Shaders"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ViewConstants
{
    public Matrix4x4 Transform;               // offset 0,  64 bytes
    public Vector4 TintColor;                 // offset 64; alpha > 0 = solid color mode (ignore texture)
    /// <summary>x = draw opacity multiplier for BOTH shader modes (fading the
    /// thumbnail strip needs it — TintColor.a cannot fade textures because it is
    /// also the mode flag). SENTINEL: 0 means "unset" and renders fully opaque,
    /// so the many existing writers that leave this at default keep working;
    /// faders early-out below ~0.004 instead of drawing at literal 0.</summary>
    public Vector4 Misc;                      // offset 80, 16 bytes
}
