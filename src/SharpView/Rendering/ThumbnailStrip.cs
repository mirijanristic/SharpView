using System.Numerics;
using SharpView.Core;
using SharpView.Services;

namespace SharpView.Rendering;

/// <summary>
/// Renders the horizontal thumbnail strip at the bottom of the viewer.
/// Keeps the selected thumbnail centered with smooth scrolling.
/// </summary>
sealed class ThumbnailStrip
{
    readonly DeviceResources _res;
    readonly ThumbnailCache _cache;

    // Layout constants
    const int ThumbSize = ThumbnailCache.ThumbnailSize; // squares drawn 1:1 at decode size
    /// <summary>Breathing room above AND below the thumbnails inside the band.</summary>
    const int ThumbPadding = 10;
    /// <summary>Height of the strip band itself: thumbnails + equal padding on
    /// both sides. The band sits flush with the bottom window edge (no extra
    /// margin — the band background makes it a solid bar, so lifting it above
    /// the see-through taskbar zone no longer serves a purpose).</summary>
    public const int StripHeight = ThumbSize + ThumbPadding * 2;
    /// <summary>Kept for call sites; the band is flush with the bottom edge.</summary>
    public const int BottomMargin = 0;
    /// <summary>Total vertical space reserved at the bottom —
    /// the image input zone ends above this (the image itself draws under it).</summary>
    public const int ReservedHeight = StripHeight + BottomMargin;
    const int CellWidth = 65;       // cell width including padding
    const int BorderWidth = 2;

    // Scroll state
    float _scrollOffset;
    float _targetScrollOffset;
    const float ScrollLerpSpeed = 14f;

    // Reused every frame to avoid a per-frame List allocation (GC churn at 60 fps).
    readonly List<string> _loadRequestBuffer = new();

    /// <summary>True when the scroll animation has reached its target.</summary>
    public bool IsSettled => _scrollOffset == _targetScrollOffset;

    // ─── Auto-hide (mirrors TopBar: same fade, same thresholds) ────────
    float _opacity;                   // animated 0..1
    float _targetOpacity;             // 0 or 1
    float _holdTimer = HoldSeconds;   // starts held: the strip shows itself at launch
    /// <summary>How long the strip stays up after a show trigger (navigation).</summary>
    const float HoldSeconds = 2f;
    /// <summary>True once opaque enough to interact with (same bar semantics).</summary>
    public bool IsStripVisible => _opacity > TopBar.VisibleThreshold;

    /// <summary>True only while the strip is ANIMATING (fade in progress). A
    /// steady visible strip deliberately does NOT request frames — see
    /// <see cref="TopBar.WantsFrames"/> for why (a continuous Present loop over
    /// a static scene blocked taskbar minimize/restore for the hold duration).
    /// Idle-time bookkeeping lives in <see cref="Poll"/>; the scroll animation
    /// is covered separately by <see cref="IsSettled"/>.</summary>
    public bool WantsFrames => _opacity != _targetOpacity;

    /// <summary>
    /// Lightweight state tick for the IDLE loop: advances the hold timer and
    /// re-evaluates the hover target WITHOUT animating or rendering. Returns
    /// true when a fade must start (target moved away from current opacity).
    /// </summary>
    public bool Poll(float dt, int cursorY, bool cursorAvailable, int windowHeight)
    {
        if (_holdTimer > 0f) _holdTimer -= dt;
        bool inZone = cursorAvailable
            && cursorY >= windowHeight - ReservedHeight && cursorY < windowHeight;
        _targetOpacity = inZone || _holdTimer > 0f ? 1f : 0f;
        return _opacity != _targetOpacity;
    }

    /// <summary>Show the strip now and keep it up for <see cref="HoldSeconds"/> —
    /// called on keyboard navigation so the strip narrates where you are.</summary>
    public void Show() => _holdTimer = HoldSeconds;

    static readonly Vector4 SelectionColor = new(0.0f, 0.47f, 0.83f, 1.0f); // #0078D4

    // Constant buffer slot layout (slot 0 belongs to ImageRenderer):
    //   1..32    thumbnails (up to MaxVisibleThumbs)
    //   33..36   selection border quads
    //   37..40   hover top bar (see TopBar)
    //   41..42   live-resize underlay + frozen-resize veil (ImageRenderer/App)
    //   43       strip band background
    const int CbSlotThumbStart = 1;
    const int MaxVisibleThumbs = 32;
    const int CbSlotBorderStart = CbSlotThumbStart + MaxVisibleThumbs; // 33
    const int CbSlotBackground = 43;

    public ThumbnailStrip(DeviceResources res, ThumbnailCache cache)
    {
        _res = res;
        _cache = cache;
    }

    /// <summary>Update scroll/fade animation and request thumbnail loading.</summary>
    public void Update(float dt, int windowWidth, int windowHeight,
                       int cursorY, bool cursorAvailable, ImageNavigator nav)
    {
        if (!nav.HasFiles) return;

        if (_holdTimer > 0f) _holdTimer -= dt;

        // The reveal zone is the WHOLE band area, hidden or visible — unlike the
        // top bar there is no thin-trigger special case: the band sits where a
        // toolbar visually belongs, so entering that area is always intent.
        bool inZone = cursorAvailable
            && cursorY >= windowHeight - ReservedHeight && cursorY < windowHeight;

        _targetOpacity = inZone || _holdTimer > 0f ? 1f : 0f;

        float fade = 1f - MathF.Exp(-TopBar.FadeSpeed * dt);
        _opacity += (_targetOpacity - _opacity) * fade;
        if (MathF.Abs(_opacity - _targetOpacity) < 0.005f)
            _opacity = _targetOpacity; // snap → lets the render loop go idle

        // Center the current thumbnail. Rounded to a whole pixel: with an integer
        // offset every derived cell/border coordinate is integral too, so settled
        // thumbnails map texel-per-pixel (decode size == draw size) and stay
        // perfectly sharp instead of landing on blur-inducing half-pixels.
        _targetScrollOffset = MathF.Round(
            windowWidth * 0.5f - nav.CurrentIndex * CellWidth - CellWidth * 0.5f);

        float t = 1f - MathF.Exp(-ScrollLerpSpeed * dt);
        _scrollOffset = Lerp(_scrollOffset, _targetScrollOffset, t);
        if (MathF.Abs(_scrollOffset - _targetScrollOffset) < 0.4f)
            _scrollOffset = _targetScrollOffset; // snap → lets the app go idle

        // Request loading for visible thumbnails plus a small buffer on each side.
        var (firstVisible, lastVisible) = GetVisibleRange(windowWidth);
        const int bufferSize = 5;
        int loadFirst = Math.Max(0, firstVisible - bufferSize);
        int loadLast = Math.Min(nav.Count - 1, lastVisible + bufferSize);

        _loadRequestBuffer.Clear();
        for (int i = loadFirst; i <= loadLast; i++)
            _loadRequestBuffer.Add(nav.Files[i]);
        _cache.RequestThumbnails(_loadRequestBuffer);
    }

    /// <summary>Render the thumbnail strip. Viewport should be set to the full window by the caller.</summary>
    public void Render(int windowWidth, int windowHeight, ImageNavigator nav)
    {
        if (!nav.HasFiles) return;
        if (_opacity <= 0.004f) return; // fully faded out — draw nothing

        // Top of the strip band [stripY, stripY + StripHeight]; the BottomMargin
        // below it stays empty so the strip clears the see-through taskbar area.
        float stripY = windowHeight - ReservedHeight;

        // Band background: the image is laid out over the full window and runs
        // UNDERNEATH the strip, darkened here with exactly the top bar's shade
        // (TopBar.BarColor) so both overlays read as one visual language. The
        // quad covers the whole reserved band down to the window edge.
        // (Never fade this out with tint a = 0 — TintColor.a is the shader's
        // solid-color mode flag, so a = 0 falls through to texture mode and
        // paints the white texture as an opaque bar.)
        WriteRectConstants(CbSlotBackground, 0, stripY, windowWidth, ReservedHeight,
            windowWidth, windowHeight, TopBar.BarColor);
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotBackground);

        // 1. Visible thumbnails
        var (firstVisible, lastVisible) = GetVisibleRange(windowWidth);
        firstVisible = Math.Max(0, firstVisible);
        lastVisible = Math.Min(nav.Count - 1, lastVisible);

        int thumbsDrawn = 0;
        for (int i = firstVisible; i <= lastVisible && thumbsDrawn < MaxVisibleThumbs; i++)
        {
            var cached = _cache.Get(nav.Files[i]);
            if (cached == null) continue; // not loaded yet

            float cellX = _scrollOffset + i * CellWidth;
            float cellCenterX = cellX + CellWidth * 0.5f;
            float cellCenterY = stripY + StripHeight * 0.5f;

            // Uniform 1:1 grid: the cache decodes every thumbnail as an exactly
            // ThumbSize × ThumbSize square, drawn here at native size — with the
            // rounded scroll offset the mapping is texel-per-pixel, no resampling.
            float drawX = cellCenterX - ThumbSize * 0.5f;
            float drawY = cellCenterY - ThumbSize * 0.5f;

            int cbSlot = CbSlotThumbStart + thumbsDrawn;
            WriteRectConstants(cbSlot, drawX, drawY, ThumbSize, ThumbSize,
                windowWidth, windowHeight, Vector4.Zero); // zero tint = use texture

            _res.DrawQuad(cached.SrvSlot, cbSlot);
            thumbsDrawn++;
        }

        // 2. Selection border around the current thumbnail
        DrawSelectionBorder(windowWidth, windowHeight, nav.CurrentIndex, stripY);
    }

    void DrawSelectionBorder(int windowWidth, int windowHeight, int selectedIndex, float stripY)
    {
        float cellX = _scrollOffset + selectedIndex * CellWidth;
        float cellCenterX = cellX + CellWidth * 0.5f;
        float cellCenterY = stripY + StripHeight * 0.5f;

        float bx = cellCenterX - ThumbSize * 0.5f - BorderWidth;
        float by = cellCenterY - ThumbSize * 0.5f - BorderWidth;
        float bw = ThumbSize + BorderWidth * 2;
        float bh = ThumbSize + BorderWidth * 2;

        // Top
        WriteRectConstants(CbSlotBorderStart, bx, by, bw, BorderWidth,
            windowWidth, windowHeight, SelectionColor);
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotBorderStart);

        // Bottom
        WriteRectConstants(CbSlotBorderStart + 1, bx, by + bh - BorderWidth, bw, BorderWidth,
            windowWidth, windowHeight, SelectionColor);
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotBorderStart + 1);

        // Left
        WriteRectConstants(CbSlotBorderStart + 2, bx, by, BorderWidth, bh,
            windowWidth, windowHeight, SelectionColor);
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotBorderStart + 2);

        // Right
        WriteRectConstants(CbSlotBorderStart + 3, bx + bw - BorderWidth, by, BorderWidth, bh,
            windowWidth, windowHeight, SelectionColor);
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotBorderStart + 3);
    }

    /// <summary>Write constants that position a rectangle given in pixel coordinates.</summary>
    void WriteRectConstants(int cbSlot, float x, float y, float w, float h,
                            float viewW, float viewH, Vector4 tintColor)
    {
        float sx = w / viewW;
        float sy = h / viewH;
        float tx = (x + w * 0.5f) / viewW * 2f - 1f;
        float ty = 1f - (y + h * 0.5f) / viewH * 2f;

        var xform = Matrix4x4.CreateScale(sx, sy, 1f)
                  * Matrix4x4.CreateTranslation(tx, ty, 0f);

        _res.WriteConstants(cbSlot, new ViewConstants
        {
            Transform = Matrix4x4.Transpose(xform),
            TintColor = tintColor,
            Misc = new Vector4(_opacity, 0f, 0f, 0f), // the whole strip fades as one
        });
    }

    (int first, int last) GetVisibleRange(int windowWidth)
    {
        int first = (int)MathF.Floor(-_scrollOffset / CellWidth) - 1;
        int last = (int)MathF.Ceiling((windowWidth - _scrollOffset) / CellWidth) + 1;
        return (first, last);
    }

    /// <summary>Set the scroll offset to immediately center the given index (no animation).</summary>
    public void SnapToIndex(int index, int windowWidth)
    {
        float offset = MathF.Round(
            windowWidth * 0.5f - index * CellWidth - CellWidth * 0.5f);
        _scrollOffset = offset;
        _targetScrollOffset = offset;
    }

    /// <summary>Get the thumbnail index at a given screen position, or -1.</summary>
    public int HitTest(float screenX, float screenY, int windowWidth, int windowHeight, int fileCount)
    {
        // Faded out = nothing visible, nothing clickable (hovering the zone
        // fades the strip in first, so real clicks always land on a visible one).
        if (!IsStripVisible) return -1;
        // The whole reserved bottom band counts, including the empty margin below
        // the thumbnails — a slightly-too-low click still selects (forgiving target).
        float stripY = windowHeight - ReservedHeight;
        if (screenY < stripY || screenY > windowHeight) return -1;

        int index = (int)MathF.Floor((screenX - _scrollOffset) / CellWidth);
        if (index < 0 || index >= fileCount) return -1;
        return index;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
