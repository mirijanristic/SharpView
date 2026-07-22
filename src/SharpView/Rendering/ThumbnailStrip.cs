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
    /// <summary>Height of the strip band itself.</summary>
    public const int StripHeight = 85;
    /// <summary>Gap between the strip and the bottom window edge. In fullscreen the
    /// taskbar shows through the translucent backdrop exactly there, so the strip
    /// is lifted above it to stay readable.</summary>
    public const int BottomMargin = 5;
    /// <summary>Total vertical space reserved at the bottom (strip + margin) —
    /// the main image viewport must stay above this.</summary>
    public const int ReservedHeight = StripHeight + BottomMargin;
    const int ThumbSize = ThumbnailCache.ThumbnailSize; // squares drawn 1:1 at decode size
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

    static readonly Vector4 SelectionColor = new(0.0f, 0.47f, 0.83f, 1.0f); // #0078D4

    // Constant buffer slot layout (slot 0 belongs to ImageRenderer):
    //   1..32    thumbnails (up to MaxVisibleThumbs)
    //   33..36   selection border quads
    //   37..40   hover top bar (see TopBar)
    const int CbSlotThumbStart = 1;
    const int MaxVisibleThumbs = 32;
    const int CbSlotBorderStart = CbSlotThumbStart + MaxVisibleThumbs; // 33

    public ThumbnailStrip(DeviceResources res, ThumbnailCache cache)
    {
        _res = res;
        _cache = cache;
    }

    /// <summary>Update scroll animation and request thumbnail loading.</summary>
    public void Update(float dt, int windowWidth, int windowHeight, ImageNavigator nav)
    {
        if (!nav.HasFiles) return;

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

        // Top of the strip band [stripY, stripY + StripHeight]; the BottomMargin
        // below it stays empty so the strip clears the see-through taskbar area.
        float stripY = windowHeight - ReservedHeight;

        // No background quad on purpose: the strip area keeps the same translucent
        // backdrop as the rest of the window and the thumbnails float on it.
        // (If a background ever returns, don't fade it out with tint a = 0 —
        // TintColor.a is the shader's solid-color mode flag, so a = 0 falls
        // through to texture mode and paints the white texture as an opaque bar.)

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
