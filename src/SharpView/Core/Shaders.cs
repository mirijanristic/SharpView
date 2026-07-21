namespace SharpView.Core;

/// <summary>
/// The single shader pair used for everything: textured quads (images, thumbnails)
/// and solid-color quads (UI rectangles) selected via <c>TintColor.a</c>.
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

        float3 Checker(float2 sp)
        {
            float2 c = floor(sp / 12.0);
            float t = fmod(c.x + c.y, 2.0);
            return lerp(0.18, 0.25, t);
        }

        float4 PSMain(PSIn p) : SV_TARGET
        {
            // Solid color mode (for UI elements like borders, backgrounds)
            if (TintColor.a > 0.001)
                return TintColor;

            float4 t = gTex.Sample(gSampler, p.uv);
            float3 bg = Checker(p.pos.xy);
            return float4(lerp(bg, t.rgb, t.a), 1.0);
        }
    ";
}
