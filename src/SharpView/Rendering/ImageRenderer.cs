using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Vortice.Direct3D12;
using SharpView.Core;
using SharpView.Services;

namespace SharpView.Rendering;

/// <summary>
/// Renders the main image view with zoom, pan, and smooth animation.
/// Image decode happens on a background thread; the GPU upload is recorded directly
/// into the frame's command list, so navigation never blocks on the GPU.
/// A small bounded CPU-side prefetch cache keeps the neighboring images pre-decoded,
/// which makes next/previous navigation effectively instant.
/// </summary>
sealed class ImageRenderer : IDisposable
{
    readonly DeviceResources _res;
    readonly ZoomPanController _view = new();

    ID3D12Resource? _texture;
    int _srvSlot = -1;
    const int CbSlot = 0;

    int _texW, _texH;

    // Async loading: stale decodes are identified by generation and dropped.
    readonly ConcurrentQueue<DecodedImage> _pendingImages = new();
    int _loadGeneration;
    int _decodesInFlight;            // user-requested decodes only (prefetch excluded)
    byte[]? _pendingUploadPixels;    // picked up by FlushPendingUpload on the render thread

    sealed record DecodedImage(int Width, int Height, byte[] Pixels, int Generation);

    // ── Prefetch cache: decoded neighbor images kept in CPU memory ──
    const int PrefetchMaxEntries = 4;
    const long PrefetchMaxBytes = 512L * 1024 * 1024;
    readonly object _prefetchLock = new();
    readonly Dictionary<string, DecodedImage> _prefetched = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _prefetchOrder = new(); // most-recent first
    readonly HashSet<string> _prefetchInFlight = new(StringComparer.OrdinalIgnoreCase);
    long _prefetchBytes;

    // Promotion: when a navigation request targets a file that is currently being
    // prefetched, the finished prefetch is delivered directly to the pending queue
    // instead of decoding the same image a second time. Guarded by _prefetchLock.
    string? _promotePath;
    int _promoteGeneration;

    public bool HasImage => _texW > 0;
    public bool IsOneToOne => _view.IsOneToOne;

    /// <summary>True while a decode or GPU upload for the main image is outstanding.
    /// The render loop keeps running while this is true so the image appears promptly.</summary>
    public bool IsBusy =>
        Volatile.Read(ref _decodesInFlight) > 0
        || !_pendingImages.IsEmpty
        || _pendingUploadPixels is not null;

    /// <summary>True when the zoom/pan animation has reached its targets.</summary>
    public bool IsAnimationSettled => _view.IsSettled;

    public ImageRenderer(DeviceResources res) => _res = res;

    /// <summary>
    /// Kick off an async image decode. Does NOT block. The image appears on a
    /// subsequent frame once <see cref="PollDecodedImage"/> picks it up. If the path
    /// was prefetched, the decode is skipped entirely and the image is ready for the
    /// very next frame. If called again before the previous decode finishes, the old
    /// one is discarded.
    /// </summary>
    public void LoadImageAsync(string path)
    {
        int generation = Interlocked.Increment(ref _loadGeneration);

        DecodedImage? cached = null;
        lock (_prefetchLock)
        {
            if (_prefetched.Remove(path, out cached))
            {
                // Prefetched and ready — no decode needed at all.
                _prefetchOrder.Remove(path); // O(n), n ≤ PrefetchMaxEntries
                _prefetchBytes -= cached.Pixels.LongLength;
                _promotePath = null;
            }
            else if (_prefetchInFlight.Contains(path))
            {
                // This exact file is being decoded by a prefetch worker right now.
                // Starting a second decode would double the work during fast browsing,
                // so register a promotion instead: when the prefetch finishes,
                // StorePrefetched delivers it straight into the pending queue under
                // this generation.
                _promotePath = path;
                _promoteGeneration = generation;
                return;
            }
            else
            {
                _promotePath = null; // navigating elsewhere cancels a stale promotion
            }
        }

        if (cached is not null)
        {
            _pendingImages.Enqueue(cached with { Generation = generation });
            return;
        }

        Interlocked.Increment(ref _decodesInFlight);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                byte[] pixels = ImageDecoder.DecodeToBgra(path, out int w, out int h);
                // Only enqueue if this is still the latest request.
                if (Volatile.Read(ref _loadGeneration) == generation)
                    _pendingImages.Enqueue(new DecodedImage(w, h, pixels, generation));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageRenderer] Failed to decode '{path}': {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _decodesInFlight);
            }
        });
    }

    /// <summary>Synchronous decode — used only for the initial image at startup.
    /// Dimensions are published immediately; the upload happens on the first frame.</summary>
    public void LoadImageSync(string path)
    {
        int generation = Interlocked.Increment(ref _loadGeneration);
        byte[] pixels = ImageDecoder.DecodeToBgra(path, out int w, out int h);
        _pendingImages.Enqueue(new DecodedImage(w, h, pixels, generation));
        PollDecodedImage();
    }

    /// <summary>
    /// Decode <paramref name="path"/> in the background and keep the pixels in a small
    /// bounded CPU cache so a later <see cref="LoadImageAsync"/> for it is instant.
    /// Safe to call repeatedly; already-cached and in-flight paths are ignored.
    /// </summary>
    public void Prefetch(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        lock (_prefetchLock)
        {
            if (_prefetched.ContainsKey(path) || !_prefetchInFlight.Add(path))
                return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                byte[] pixels = ImageDecoder.DecodeToBgra(path, out int w, out int h);
                StorePrefetched(path, new DecodedImage(w, h, pixels, 0));
            }
            catch
            {
                // Corrupt/unsupported file — a real load attempt would fail the same
                // way, so also drop any pending promotion registered for it.
                lock (_prefetchLock)
                {
                    if (_promotePath == path) _promotePath = null;
                }
            }
            finally
            {
                lock (_prefetchLock) _prefetchInFlight.Remove(path);
            }
        });
    }

    void StorePrefetched(string path, DecodedImage img)
    {
        long size = img.Pixels.LongLength;

        lock (_prefetchLock)
        {
            // A navigation request for this exact file arrived while it was still
            // decoding — deliver it straight to the pending queue under the recorded
            // generation instead of caching it (the "promotion" path). This is what
            // prevents decoding the same image twice during fast arrow-key browsing.
            if (_promotePath == path)
            {
                _pendingImages.Enqueue(img with { Generation = _promoteGeneration });
                _promotePath = null;
                return;
            }

            if (size > PrefetchMaxBytes) return; // larger than the entire budget — skip
            if (_prefetched.ContainsKey(path)) return;

            _prefetched[path] = img;
            _prefetchOrder.AddFirst(path);
            _prefetchBytes += size;

            // Evict oldest entries beyond the entry/byte budget.
            while ((_prefetched.Count > PrefetchMaxEntries || _prefetchBytes > PrefetchMaxBytes)
                   && _prefetchOrder.Last is not null)
            {
                string oldest = _prefetchOrder.Last.Value;
                _prefetchOrder.RemoveLast();
                if (_prefetched.Remove(oldest, out var evicted))
                    _prefetchBytes -= evicted.Pixels.LongLength;
            }
        }
    }

    /// <summary>
    /// Pick up the newest finished decode (CPU side only). Returns true if a new image
    /// arrived: dimensions are updated immediately so the caller can re-fit the view;
    /// the actual GPU upload is recorded by <see cref="FlushPendingUpload"/> during
    /// the next frame.
    /// </summary>
    public bool PollDecodedImage()
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

        _texW = latest.Width;
        _texH = latest.Height;
        _pendingUploadPixels = latest.Pixels;
        return true;
    }

    /// <summary>
    /// Record the pending texture upload into the frame's command list (render thread,
    /// between BeginFrame and the draws). The copy + barrier execute before any draw
    /// recorded afterwards, so no GPU wait is needed. The previous texture and its SRV
    /// slot are released via fence-tagged deferral once no in-flight frame can
    /// reference them anymore.
    /// </summary>
    public void FlushPendingUpload(ID3D12GraphicsCommandList cmdList)
    {
        if (_pendingUploadPixels is null) return;

        if (_texture is not null)
        {
            _res.DeferRelease(_texture, _srvSlot);
            _texture = null;
            _srvSlot = -1;
        }

        _srvSlot = _res.AllocateSrvSlot();
        _texture = TextureUploader.Upload(_res, _texW, _texH, _pendingUploadPixels, _srvSlot, cmdList);
        _pendingUploadPixels = null;
    }

    /// <summary>Update the smooth animation and write this frame's constants. Call each frame.</summary>
    public void Update(float dt, int viewW, int viewH)
    {
        if (!HasImage) return;

        _view.Update(dt);

        // Pixel-accurate transform: at zoom z the image is drawn at (texW*z × texH*z)
        // viewport pixels, so Fit and 1:1 behave exactly as their names promise.
        float drawW = _texW * _view.Zoom;
        float drawH = _texH * _view.Zoom;

        // Top-left corner of the destination rectangle in viewport pixels.
        float left = (viewW - drawW) * 0.5f + _view.PanX;
        float top = (viewH - drawH) * 0.5f + _view.PanY;

        // Pixel snapping: once the animation has settled, round the corner onto the
        // pixel grid. Without this, an odd (viewport − image) difference leaves the
        // image centered on a HALF-pixel, so at 1:1 every screen pixel samples the
        // average of two texels and the whole picture looks slightly blurred. With
        // the snap, 1:1 maps each texel to exactly one pixel — bit-perfect display.
        // (Not applied mid-animation, so zoom/pan transitions stay sub-pixel smooth.)
        if (_view.IsSettled)
        {
            left = MathF.Round(left);
            top = MathF.Round(top);
        }

        float sx = drawW / viewW;
        float sy = drawH / viewH;
        float tx = (left + drawW * 0.5f) / viewW * 2f - 1f;
        float ty = 1f - (top + drawH * 0.5f) / viewH * 2f;

        var xform = Matrix4x4.CreateScale(sx, sy, 1f)
                  * Matrix4x4.CreateTranslation(tx, ty, 0f);

        var cb = new ViewConstants
        {
            Transform = Matrix4x4.Transpose(xform),
            TintColor = Vector4.Zero,
        };
        _res.WriteConstants(CbSlot, cb);
    }

    /// <summary>Issue the draw call. The viewport must be set by the caller.</summary>
    public void Render()
    {
        if (_texture is null) return;
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

    /// <summary>
    /// Show the image at true 1:1 when it fully fits inside the viewport, otherwise
    /// fit it to the window. Small images are never upscaled just to fill the view.
    /// Animated (used when navigating between images).
    /// </summary>
    public void FitOrOneToOne(int viewW, int viewH)
    {
        if (!HasImage) return;
        if (_texW <= viewW && _texH <= viewH)
            _view.SetOneToOne();
        else
            _view.Fit(_texW, _texH, viewW, viewH);
    }

    /// <summary>Same policy without animation (used at startup).</summary>
    public void FitOrOneToOneInstant(int viewW, int viewH)
    {
        FitOrOneToOne(viewW, viewH);
        _view.SnapToTargets();
    }

    public void SetOneToOne() => _view.SetOneToOne();
    public void ZoomIn() => _view.ZoomIn();
    public void ZoomOut() => _view.ZoomOut();

    public void Dispose()
    {
        // ViewerApp performs a full GPU wait before disposing renderers.
        _texture?.Dispose();
        if (_srvSlot >= 0)
        {
            _res.FreeSrvSlot(_srvSlot);
            _srvSlot = -1;
        }
    }
}
