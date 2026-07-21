using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Vortice.Direct3D12;
using SharpView.Core;
using SharpView.Services;

namespace SharpView.Rendering;

/// <summary>
/// Renders the main image view with zoom, pan, and smooth animation.
/// Image decode happens on a background thread to avoid stalling the render loop.
/// </summary>
sealed class ImageRenderer : IDisposable
{
    readonly DeviceResources _res;
    readonly ZoomPanController _view = new();
    readonly ID3D12CommandAllocator _uploadAllocator;

    ID3D12Resource? _texture;
    int _srvSlot = -1;
    const int CbSlot = 0;

    int _texW, _texH;

    // Async loading: stale decodes are identified by generation and dropped.
    readonly ConcurrentQueue<DecodedImage> _pendingImages = new();
    int _loadGeneration;

    sealed record DecodedImage(int Width, int Height, byte[] Pixels, int Generation);

    public int TextureWidth => _texW;
    public int TextureHeight => _texH;
    public bool HasImage => _texture is not null;
    public bool IsOneToOne => _view.IsOneToOne;

    public ImageRenderer(DeviceResources res)
    {
        _res = res;
        _uploadAllocator = res.Device.CreateCommandAllocator(CommandListType.Direct);
    }

    /// <summary>
    /// Kick off an async image decode. Does NOT block. The image appears on a
    /// subsequent frame once <see cref="TryCompletePendingLoad"/> picks it up.
    /// If called again before the previous decode finishes, the old one is discarded.
    /// </summary>
    public void LoadImageAsync(string path)
    {
        int generation = Interlocked.Increment(ref _loadGeneration);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                byte[] pixels = ImageDecoder.DecodeToRgba(path, out int w, out int h);
                // Only enqueue if this is still the latest request.
                if (Volatile.Read(ref _loadGeneration) == generation)
                    _pendingImages.Enqueue(new DecodedImage(w, h, pixels, generation));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageRenderer] Failed to decode '{path}': {ex.Message}");
            }
        });
    }

    /// <summary>Synchronous load — used only for the initial image at startup.</summary>
    public void LoadImageSync(string path)
    {
        Interlocked.Increment(ref _loadGeneration);
        byte[] pixels = ImageDecoder.DecodeToRgba(path, out int w, out int h);
        UploadToGpu(w, h, pixels);
    }

    /// <summary>
    /// Call each frame before rendering. Uploads the latest decoded image to the GPU
    /// if one is ready. Returns true if a new image was loaded (caller may want to fit).
    /// </summary>
    public bool TryCompletePendingLoad()
    {
        DecodedImage? latest = null;
        int currentGeneration = Volatile.Read(ref _loadGeneration);

        // Drain the queue, keep only the latest decode matching the current generation.
        while (_pendingImages.TryDequeue(out var img))
        {
            if (img.Generation == currentGeneration)
                latest = img;
        }

        if (latest is null) return false;

        UploadToGpu(latest.Width, latest.Height, latest.Pixels);
        return true;
    }

    void UploadToGpu(int width, int height, byte[] pixels)
    {
        // Release the previous texture only after the GPU is done with it.
        if (_texture is not null)
        {
            _res.WaitForGpu();
            _texture.Dispose();
            _texture = null;
            if (_srvSlot >= 0)
            {
                _res.FreeSrvSlot(_srvSlot);
                _srvSlot = -1;
            }
        }

        _texW = width;
        _texH = height;
        _srvSlot = _res.AllocateSrvSlot();

        _uploadAllocator.Reset();
        _res.CommandList.Reset(_uploadAllocator, null);
        _texture = TextureUploader.Upload(_res, width, height, pixels, _srvSlot, _res.CommandList);
        _res.CommandList.Close();
        _res.CommandQueue.ExecuteCommandList(_res.CommandList);
        _res.WaitForGpu(); // also releases the deferred staging buffer
    }

    /// <summary>Update the smooth animation and write this frame's constants. Call each frame.</summary>
    public void Update(float dt, int viewW, int viewH)
    {
        if (!HasImage) return;

        _view.Update(dt);

        // Pixel-accurate transform: at zoom z the image is drawn at (texW*z × texH*z)
        // viewport pixels, so Fit and 1:1 behave exactly as their names promise.
        float sx = _texW * _view.Zoom / viewW;
        float sy = _texH * _view.Zoom / viewH;

        float ox = _view.PanX * 2f / viewW;
        float oy = -_view.PanY * 2f / viewH;

        var xform = Matrix4x4.CreateScale(sx, sy, 1f)
                  * Matrix4x4.CreateTranslation(ox, oy, 0f);

        var cb = new ViewConstants
        {
            Transform = Matrix4x4.Transpose(xform),
            TexWidth = _texW,
            TexHeight = _texH,
            ViewWidth = viewW,
            ViewHeight = viewH,
            TintColor = Vector4.Zero,
        };
        _res.WriteConstants(CbSlot, cb);
    }

    /// <summary>Issue the draw call. The viewport must be set by the caller.</summary>
    public void Render()
    {
        if (!HasImage) return;
        _res.DrawQuad(_srvSlot, CbSlot);
    }

    // ─── Zoom/Pan Controls ────────────────────────────────────────────

    public void ZoomAt(float wheelDelta, float mouseX, float mouseY, int viewW, int viewH)
        => _view.ZoomAt(wheelDelta, mouseX, mouseY, viewW, viewH);

    public void Pan(float dx, float dy) => _view.Pan(dx, dy);

    public void FitToWindow(int viewW, int viewH)
    {
        if (!HasImage) return;
        _view.Fit(_texW, _texH, viewW, viewH);
    }

    /// <summary>Fit to window without animation (used at startup).</summary>
    public void FitToWindowInstant(int viewW, int viewH)
    {
        FitToWindow(viewW, viewH);
        _view.SnapToTargets();
    }

    public void SetOneToOne() => _view.SetOneToOne();
    public void ZoomIn() => _view.ZoomIn();
    public void ZoomOut() => _view.ZoomOut();

    public void Dispose()
    {
        // ViewerApp performs a full GPU wait before disposing renderers.
        _texture?.Dispose();
        _uploadAllocator.Dispose();
        if (_srvSlot >= 0)
        {
            _res.FreeSrvSlot(_srvSlot);
            _srvSlot = -1;
        }
    }
}
