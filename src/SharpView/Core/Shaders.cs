namespace SharpView.Core;

/// <summary>
/// The single shader pair used for everything: textured quads (images, thumbnails)
/// and solid-color quads (UI rectangles) selected via <c>TintColor.a</c>.
/// All output is premultiplied alpha: with DirectComposition presentation, DWM
/// reads the swap chain's alpha channel and blends every pixel with whatever is
/// behind the window, so alpha here directly controls window transparency.
/// </summary>
static class Shaders
{
    public const string HlslSource = @"
        cbuffer ViewCB : register(b0)
        {
            float4x4 Transform;
            float4 TintColor;
        };

        Texture2D    gTex     : register(t0);
        SamplerState gSampler : register(s0);

        struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; };
        struct PSIn  { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        PSIn VSMain(VSIn v)
        {
            PSIn o;
            o.pos = mul(float4(v.pos, 0.0, 1.0), Transform);
            o.uv  = v.uv;
            return o;
        }

        float4 PSMain(PSIn p) : SV_TARGET
        {
            // Solid color mode (UI rectangles). TintColor.a doubles as the mode
            // flag AND the actual opacity, so translucent UI like the glassy
            // thumbnail strip background works too.
            if (TintColor.a > 0.001)
                return float4(TintColor.rgb * TintColor.a, TintColor.a);

            // Image mode: texels are straight alpha, premultiply here. Transparent
            // PNG/GIF regions now genuinely show the live desktop through the
            // window — the old in-shader checkerboard is gone on purpose.
            float4 t = gTex.Sample(gSampler, p.uv);
            return float4(t.rgb * t.a, t.a);
        }
    ";
}
