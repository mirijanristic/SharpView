using System.Numerics;
using SharpView.Core;

namespace SharpView.Rendering;

/// <summary>
/// Hover-revealed top bar, drawn entirely through Direct3D because the window has
/// no GDI surface (WS_EX_NOREDIRECTIONBITMAP). Invisible until the mouse enters a
/// thin trigger zone at the top edge; then it fades in as a translucent strip with
/// a close (X) button on the right. Everything left of the X acts as a drag
/// handle: <see cref="HitTest"/> reports it as caption, so Windows itself runs the
/// native move loop — drag-restore from maximized, Aero Snap, dragging across
/// monitors and the right-click system menu all behave like a real title bar.
/// </summary>
sealed class TopBar
{
    /// <summary>Height of the visible strip (also the hover keep-alive zone).</summary>
    public const int BarHeight = 34;
    /// <summary>Height of the invisible reveal zone while the bar is hidden and the
    /// window is maximized — kept thin so working near the top edge does not pop the
    /// bar up constantly. In a restored (windowed) state the reveal zone is the full
    /// <see cref="BarHeight"/> instead: exactly where a title bar would be.</summary>
    public const int TriggerHeight = 8;
    const int CloseWidth = 46;           // close button hit rect (right-aligned)
    const float FadeSpeed = 12f;         // exponential fade rate, 1/s
    const float VisibleThreshold = 0.5f; // the X becomes clickable above this
    const float StartupReveal = 1.6f;    // seconds the bar shows itself after launch

    // Constant buffer slots: 0 = ImageRenderer, 1..36 = ThumbnailStrip, then ours.
    const int CbSlotBg = 37;
    const int CbSlotCloseBg = 38;
    const int CbSlotStrokeA = 39;
    const int CbSlotStrokeB = 40;

    static readonly Vector4 BarColor = new(0.05f, 0.05f, 0.05f, 0.85f);
    static readonly Vector4 CloseHoverColor = new(0.91f, 0.07f, 0.14f, 1.0f); // #E81123
    static readonly Vector4 StrokeColor = new(0.92f, 0.92f, 0.92f, 1.0f);

    readonly DeviceResources _res;

    float _opacity;       // animated 0..1
    float _targetOpacity; // 0 or 1
    float _startupHold = StartupReveal;
    bool _closeHovered;

    public TopBar(DeviceResources res) => _res = res;

    public enum Hit { None, Drag, Close }

    /// <summary>True once the bar is opaque enough for the X to be interactive.</summary>
    public bool IsVisible => _opacity > VisibleThreshold;

    /// <summary>
    /// True while the bar needs frames: fading in/out, or fully visible. While
    /// visible, <see cref="Update"/> polls the cursor every frame to decide when to
    /// fade back out — that also covers the mouse leaving the window sideways
    /// (toward the other monitor), which produces no window message at all.
    /// </summary>
    public bool WantsFrames => _opacity > 0f || _targetOpacity > 0f;

    /// <summary>
    /// Advance the fade animation. <paramref name="cursorX"/>/<paramref name="cursorY"/>
    /// are client pixels; <paramref name="cursorAvailable"/> should be false when the
    /// cursor is outside the client area, the app is not focused, or an image drag is
    /// in progress (the bar stays out of the way while panning).
    /// <paramref name="windowMaximized"/> is passed fresh on every call because it can
    /// flip mid-drag (drag-restore) while the render loop is blocked.
    /// </summary>
    public void Update(float dt, int windowWidth, int cursorX, int cursorY,
                       bool cursorAvailable, bool windowMaximized)
    {
        if (_startupHold > 0f) _startupHold -= dt;

        // Hysteresis: the hidden-state zone reveals the bar, the full bar height
        // keeps it alive — no flicker right at the trigger boundary.
        int zone = _opacity > 0.01f ? BarHeight : HiddenZone(windowMaximized);
        bool inZone = cursorAvailable && cursorY >= 0 && cursorY < zone;

        _targetOpacity = inZone || _startupHold > 0f ? 1f : 0f;

        float t = 1f - MathF.Exp(-FadeSpeed * dt);
        _opacity += (_targetOpacity - _opacity) * t;
        if (MathF.Abs(_opacity - _targetOpacity) < 0.005f)
            _opacity = _targetOpacity; // snap → lets the render loop go idle

        _closeHovered = IsVisible && HitTestClose(cursorX, cursorY, windowWidth);
    }

    /// <summary>True when the point is on the (interactive) close button.</summary>
    public bool HitTestClose(float x, float y, int windowWidth)
        => IsVisible
        && y >= 0 && y < BarHeight
        && x >= windowWidth - CloseWidth && x < windowWidth;

    /// <summary>Semantic hit test for WM_NCHITTEST (window drag vs. close click).</summary>
    public Hit HitTest(int x, int y, int windowWidth, bool windowMaximized)
    {
        int zone = _opacity > 0.01f ? BarHeight : HiddenZone(windowMaximized);
        if (y < 0 || y >= zone || x < 0 || x >= windowWidth) return Hit.None;
        return HitTestClose(x, y, windowWidth) ? Hit.Close : Hit.Drag;
    }

    /// <summary>Reveal/drag zone while the bar is hidden: thin when maximized (the
    /// top screen edge is a huge target, accidental pops are the concern), the full
    /// bar height when windowed — there it sits exactly where a title bar would be,
    /// so revealing and grabbing the window stays easy.</summary>
    static int HiddenZone(bool windowMaximized)
        => windowMaximized ? TriggerHeight : BarHeight;

    /// <summary>Draw the bar. The viewport must cover the full window.</summary>
    public void Render(int windowWidth, int windowHeight)
    {
        // Early-out well above the shader's TintColor.a > 0.001 solid-mode flag:
        // all alphas below are multiplied by _opacity, and a tint alpha that tiny
        // would fall through to texture mode and paint the white texture opaquely.
        if (_opacity < 0.01f) return;

        // 1. Bar background
        WriteQuad(CbSlotBg, windowWidth * 0.5f, BarHeight * 0.5f,
            windowWidth, BarHeight, 0f, windowWidth, windowHeight, Fade(BarColor));
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotBg);

        float closeCx = windowWidth - CloseWidth * 0.5f;
        float closeCy = BarHeight * 0.5f;

        // 2. Close button hover highlight (Windows-style red)
        if (_closeHovered)
        {
            WriteQuad(CbSlotCloseBg, closeCx, closeCy, CloseWidth, BarHeight, 0f,
                windowWidth, windowHeight, Fade(CloseHoverColor));
            _res.DrawQuad(_res.WhiteSrvSlot, CbSlotCloseBg);
        }

        // 3. The X glyph: two thin strokes rotated ±45° around the button center.
        const float len = 14f, thick = 1.6f;
        const float quarterTurn = MathF.PI / 4f;
        WriteQuad(CbSlotStrokeA, closeCx, closeCy, len, thick, quarterTurn,
            windowWidth, windowHeight, Fade(StrokeColor));
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotStrokeA);
        WriteQuad(CbSlotStrokeB, closeCx, closeCy, len, thick, -quarterTurn,
            windowWidth, windowHeight, Fade(StrokeColor));
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotStrokeB);
    }

    Vector4 Fade(Vector4 c) => new(c.X, c.Y, c.Z, c.W * _opacity);

    /// <summary>
    /// Position the shared unit quad as a rectangle given by center + size in pixel
    /// coordinates, optionally rotated (radians, clockwise on screen). Unlike the
    /// strip's axis-aligned helper this rotates in pixel space, so the rotation
    /// stays uniform regardless of the window's aspect ratio.
    /// </summary>
    void WriteQuad(int cbSlot, float cx, float cy, float w, float h, float rotation,
                   float viewW, float viewH, Vector4 tint)
    {
        // local unit quad (−1..1, y up) → pixel size (y down) → rotate → place at
        // (cx, cy) in pixels → map pixels to NDC.
        var m = Matrix4x4.CreateScale(w * 0.5f, -h * 0.5f, 1f)
              * Matrix4x4.CreateRotationZ(rotation)
              * Matrix4x4.CreateTranslation(cx, cy, 0f)
              * Matrix4x4.CreateScale(2f / viewW, -2f / viewH, 1f)
              * Matrix4x4.CreateTranslation(-1f, 1f, 0f);

        _res.WriteConstants(cbSlot, new ViewConstants
        {
            Transform = Matrix4x4.Transpose(m),
            TintColor = tint,
        });
    }
}
