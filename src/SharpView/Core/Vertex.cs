using System.Numerics;
using System.Runtime.InteropServices;

namespace SharpView.Core;

/// <summary>Position + texcoord vertex for the shared unit quad.</summary>
[StructLayout(LayoutKind.Sequential)]
struct Vertex
{
    public Vector2 Position;
    public Vector2 TexCoord;

    public Vertex(float x, float y, float u, float v)
    {
        Position = new Vector2(x, y);
        TexCoord = new Vector2(u, v);
    }
}
