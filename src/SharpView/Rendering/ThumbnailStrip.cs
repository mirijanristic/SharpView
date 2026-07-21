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
    public const int StripHeight = 75;
    const int ThumbSize = 55;       // thumbnail image max dimension
    const int CellWidth = 65;       // cell width including padding
    const int BorderWidth = 2;

    // Scroll state
    float _scrollOffset;
    float _targetScrollOffset;
    const float ScrollLerpSpeed = 14f;

    static readonly Vector4 SelectionColor = new(0.0f, 0.47f, 0.83f, 1.0f); // #0078D4
    static readonly Vector4 StripBgColor = new(0.10f, 0.10f, 0.10f, 1.0f);

    // Constant buffer slot layout (slot 0 belongs to ImageRenderer):
    //   1        strip background
    //   2..33    thumbnails (up to MaxVisibleThumbs)
    //   34..37   selection border quads
    const int CbSlotStripBg = 1;
    const int CbSlotThumbStart = 2;
    const int MaxVisibleThumbs = 32;
    const int CbSlotBorderStart = CbSlotThumbStart + MaxVisibleThumbs; // 34

    public ThumbnailStrip(DeviceResources res, ThumbnailCache cache)
    {
        _res = res;
        _cache = cache;
    }

    /// <summary>Update scroll animation and request thumbnail loading.</summary>
    public void Update(float dt, int windowWidth, int windowHeight, ImageNavigator nav)
    {
        if (!nav.HasFiles) return;

        // Center the current thumbnail.
        _targetScrollOffset = windowWidth * 0.5f - nav.CurrentIndex * CellWidth - CellWidth * 0.5f;

        float t = 1f - MathF.Exp(-ScrollLerpSpeed * dt);
        _scrollOffset = Lerp(_scrollOffset, _targetScrollOffset, t);

        // Request loading for visible thumbnails plus a small buffer on each side.
        var (firstVisible, lastVisible) = GetVisibleRange(windowWidth);
        const int bufferSize = 5;
        int loadFirst = Math.Max(0, firstVisible - bufferSize);
        int loadLast = Math.Min(nav.Count - 1, lastVisible + bufferSize);

        var pathsToLoad = new List<string>(loadLast - loadFirst + 1);
        for (int i = loadFirst; i <= loadLast; i++)
            pathsToLoad.Add(nav.Files[i]);
        _cache.RequestThumbnails(pathsToLoad);
    }

    /// <summary>Render the thumbnail strip. Viewport should be set to the full window by the caller.</summary>
    public void Render(int windowWidth, int windowHeight, ImageNavigator nav)
    {
        if (!nav.HasFiles) return;

        float stripY = windowHeight - StripHeight;

        // 1. Strip background
        WriteRectConstants(CbSlotStripBg, 0, stripY, windowWidth, StripHeight,
            windowWidth, windowHeight, StripBgColor);
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotStripBg);

        // 2. Visible thumbnails
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

            // Fit thumbnail within the cell, preserving aspect ratio.
            float aspect = (float)cached.Width / cached.Height;
            float drawW, drawH;
            if (aspect > 1f)
            { drawW = ThumbSize; drawH = ThumbSize / aspect; }
            else
            { drawW = ThumbSize * aspect; drawH = ThumbSize; }

            float drawX = cellCenterX - drawW * 0.5f;
            float drawY = cellCenterY - drawH * 0.5f;

            int cbSlot = CbSlotThumbStart + thumbsDrawn;
            WriteRectConstants(cbSlot, drawX, drawY, drawW, drawH,
                windowWidth, windowHeight, Vector4.Zero); // zero tint = use texture

            _res.DrawQuad(cached.SrvSlot, cbSlot);
            thumbsDrawn++;
        }

        // 3. Selection border around the current thumbnail
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
            TexWidth = w,
            TexHeight = h,
            ViewWidth = viewW,
            ViewHeight = viewH,
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
        float offset = windowWidth * 0.5f - index * CellWidth - CellWidth * 0.5f;
        _scrollOffset = offset;
        _targetScrollOffset = offset;
    }

    /// <summary>Get the thumbnail index at a given screen position, or -1.</summary>
    public int HitTest(float screenX, float screenY, int windowWidth, int windowHeight, int fileCount)
    {
        float stripY = windowHeight - StripHeight;
        if (screenY < stripY || screenY > windowHeight) return -1;

        int index = (int)MathF.Floor((screenX - _scrollOffset) / CellWidth);
        if (index < 0 || index >= fileCount) return -1;
        return index;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
