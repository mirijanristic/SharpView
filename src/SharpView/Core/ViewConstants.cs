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
    public Matrix4x4 Transform;
    public float TexWidth;
    public float TexHeight;
    public float ViewWidth;
    public float ViewHeight;
    public Vector4 TintColor; // alpha > 0 = solid color mode (ignore texture)
}
