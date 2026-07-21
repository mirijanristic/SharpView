namespace SharpView.Rendering;

/// <summary>
/// Pure zoom/pan state machine with smooth exponential animation.
/// No GPU or UI dependencies, so it is fully unit-testable.
/// </summary>
/// <remarks>
/// Zoom semantics: zoom is a <b>pixel scale factor</b> — zoom = 1 means one image
/// pixel maps to exactly one viewport pixel (true 100%). "Fit to window" is therefore
/// min(viewW/imageW, viewH/imageH). Pan is expressed in viewport pixels, relative to
/// the viewport center.
/// </remarks>
internal sealed class ZoomPanController
{
    public const float ZoomStep = 1.12f;
    public const float MinZoom = 0.005f;
    public const float MaxZoom = 500f;
    const float LerpSpeed = 18f;
    const float OneToOneTolerance = 0.05f;

    float _zoom = 1f;
    float _targetZoom = 1f;
    float _panX, _panY;
    float _targetPanX, _targetPanY;

    /// <summary>Current animated zoom (what is rendered this frame).</summary>
    public float Zoom => _zoom;
    /// <summary>Current animated pan X in viewport pixels.</summary>
    public float PanX => _panX;
    /// <summary>Current animated pan Y in viewport pixels.</summary>
    public float PanY => _panY;

    public float TargetZoom => _targetZoom;
    public float TargetPanX => _targetPanX;
    public float TargetPanY => _targetPanY;

    /// <summary>True when the target zoom is (approximately) 100%.</summary>
    public bool IsOneToOne => MathF.Abs(_targetZoom - 1f) < OneToOneTolerance;

    /// <summary>True when the animation has fully reached its targets (nothing left to animate).</summary>
    public bool IsSettled => _zoom == _targetZoom && _panX == _targetPanX && _panY == _targetPanY;

    /// <summary>Advance the smooth animation toward the targets. Call once per frame.</summary>
    public void Update(float dt)
    {
        float t = 1f - MathF.Exp(-LerpSpeed * dt);
        _zoom = Lerp(_zoom, _targetZoom, t);
        _panX = Lerp(_panX, _targetPanX, t);
        _panY = Lerp(_panY, _targetPanY, t);

        // Snap once the remaining distance is visually indistinguishable, so
        // IsSettled becomes true and the render loop can stop redrawing.
        if (MathF.Abs(_zoom - _targetZoom) < MathF.Max(_targetZoom, 0.01f) * 0.0005f)
            _zoom = _targetZoom;
        if (MathF.Abs(_panX - _targetPanX) < 0.05f) _panX = _targetPanX;
        if (MathF.Abs(_panY - _targetPanY) < 0.05f) _panY = _targetPanY;
    }

    /// <summary>
    /// Zoom in/out (sign of <paramref name="wheelDelta"/>) while keeping the image
    /// point under the mouse cursor fixed on screen.
    /// </summary>
    public void ZoomAt(float wheelDelta, float mouseX, float mouseY,
                       float viewWidth, float viewHeight)
    {
        float oldZoom = _targetZoom;
        _targetZoom = Math.Clamp(
            _targetZoom * (wheelDelta > 0 ? ZoomStep : 1f / ZoomStep),
            MinZoom, MaxZoom);

        // Cursor position relative to the viewport center.
        float mx = mouseX - viewWidth * 0.5f;
        float my = mouseY - viewHeight * 0.5f;

        // Keep the image point under the cursor invariant:
        // pan' = m - (m - pan) * (zoom' / zoom)
        float ratio = 1f - _targetZoom / oldZoom;
        _targetPanX += (mx - _targetPanX) * ratio;
        _targetPanY += (my - _targetPanY) * ratio;
    }

    public void ZoomIn()
        => _targetZoom = Math.Clamp(_targetZoom * ZoomStep, MinZoom, MaxZoom);

    public void ZoomOut()
        => _targetZoom = Math.Clamp(_targetZoom / ZoomStep, MinZoom, MaxZoom);

    /// <summary>Pan immediately (used for mouse dragging — no lag on the drag itself).</summary>
    public void Pan(float dx, float dy)
    {
        _targetPanX += dx;
        _targetPanY += dy;
        _panX += dx;
        _panY += dy;
    }

    /// <summary>Set the target zoom so the whole image fits inside the viewport, centered.</summary>
    public void Fit(int imageWidth, int imageHeight, float viewWidth, float viewHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return;
        float scale = Math.Min(viewWidth / imageWidth, viewHeight / imageHeight);
        _targetZoom = Math.Clamp(scale, MinZoom, MaxZoom);
        _targetPanX = 0f;
        _targetPanY = 0f;
    }

    /// <summary>Set the target zoom to true 100% (1 image pixel = 1 viewport pixel), centered.</summary>
    public void SetOneToOne()
    {
        _targetZoom = 1f;
        _targetPanX = 0f;
        _targetPanY = 0f;
    }

    /// <summary>Jump the animated state directly to the targets (skip the animation).</summary>
    public void SnapToTargets()
    {
        _zoom = _targetZoom;
        _panX = _targetPanX;
        _panY = _targetPanY;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
