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
/// A small bounded GPU-RESIDENT prefetch cache keeps the neighboring images as
/// ready textures (decoded AND uploaded), so next/previous navigation is a pure
/// SRV-slot swap — no decode, no upload. Pixels exist on the CPU only while in
/// transit (decode → upload queue → texture), which removes the half-gigabyte of
/// LOH byte[] the old CPU-side cache pinned. On UMA (integrated) GPUs the textures
/// still occupy system RAM, but outside the managed heap — no GC pressure.
/// </summary>
sealed class ImageRenderer : IDisposable
{
    readonly DeviceResources _res;
    readonly ZoomPanController _view = new();

    // The displayed image as one GPU object (texture + SRV slot + its own
    // dimensions and source path). The path is what lets the texture be
    // recycled INTO the prefetch cache when navigating away, instead of being
    // destroyed — going back to a recent image is then a pure slot swap.
    GpuImage? _current;
    const int CbSlot = 0;
    // Live-resize camouflage pass (see RenderUnderlay). 41 = first slot past
    // TopBar's block (37..40); ThumbnailStrip owns 1..36.
    const int CbSlotUnderlay = 41;

    // The image's destination rectangle in viewport pixels, as computed by the
    // last Update — the underlay redraws the image at this exact position.
    float _lastLeft, _lastTop, _lastDrawW, _lastDrawH;

    int _texW, _texH;

    // Async loading: stale results are identified by generation. A pending item
    // carries EITHER freshly decoded pixels (needs an upload) OR a ready GPU
    // texture taken out of the prefetch cache (needs only a slot swap).
    readonly ConcurrentQueue<PendingImage> _pendingImages = new();
    int _loadGeneration;
    int _decodesInFlight;         // user-requested decodes only (prefetch excluded)
    PendingImage? _pendingApply;  // polled result, consumed by FlushPendingUpload

    sealed record PendingImage(
        string Path, int Width, int Height,
        byte[]? Pixels, GpuImage? Gpu, int Generation);

    /// <summary>An uploaded, draw-ready image: texture + SRV slot + dimensions.</summary>
    sealed record GpuImage(string Path, ID3D12Resource Texture, int SrvSlot,
                           int Width, int Height)
    {
        /// <summary>Approximate GPU memory footprint (BGRA, no mips).</summary>
        public long Bytes => (long)Width * Height * 4;
    }

    // ── Prefetch cache: neighbor images kept as READY GPU textures ──
    // Budget counts texture bytes (W×H×4), not managed memory. Deliberately
    // smaller than the old CPU cache's 512 MB: VRAM is the scarcer resource on
    // discrete cards, and 256 MB still holds two+ 24-megapixel photographs.
    const int PrefetchMaxEntries = 4;
    const long PrefetchMaxBytes = 256L * 1024 * 1024;
    readonly object _prefetchLock = new();
    readonly Dictionary<string, GpuImage> _prefetched = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _prefetchOrder = new(); // most-recent first
    readonly HashSet<string> _prefetchInFlight = new(StringComparer.OrdinalIgnoreCase);
    long _prefetchBytes;

    // Decoded prefetch pixels waiting for their upload — uploads may only be
    // recorded on the render thread, so workers park results here and
    // FlushPendingUpload drains at most one per frame. Bounded: during held-key
    // browsing more prefetches can finish than frames drain; overflow results
    // are dropped (a later Prefetch simply re-decodes if still relevant).
    readonly ConcurrentQueue<(string Path, int W, int H, byte[] Pixels)> _prefetchUploads = new();
    int _prefetchUploadCount; // ConcurrentQueue.Count is O(n) — tracked manually
    const int PrefetchUploadQueueMax = 3;

    // Promotion: when a navigation request targets a file whose prefetch is in
    // flight (still decoding OR already decoded and waiting in the upload
    // queue), the result is delivered straight to the pending queue instead of
    // decoding the same image a second time. Guarded by _prefetchLock; both
    // completion points (decode finish, upload-queue drain) honor it.
    string? _promotePath;
    int _promoteGeneration;

    public bool HasImage => _texW > 0;
    public bool IsOneToOne => _view.IsOneToOne;

    /// <summary>True while a decode or GPU upload is outstanding — for the main
    /// image OR a queued prefetch upload. The render loop keeps running while
    /// this is true: the main image appears promptly, and prefetch results need
    /// frames too (their upload is recorded into a frame's command list).</summary>
    public bool IsBusy =>
        Volatile.Read(ref _decodesInFlight) > 0
        || !_pendingImages.IsEmpty
        || _pendingApply is not null
        || Volatile.Read(ref _prefetchUploadCount) > 0;

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

        GpuImage? cached = null;
        lock (_prefetchLock)
        {
            if (_prefetched.Remove(path, out cached))
            {
                // Prefetched and READY — the texture is already on the GPU, so
                // the ownership simply transfers out of the cache: no decode,
                // no upload, just a slot swap when the pending item is applied.
                _prefetchOrder.Remove(path); // O(n), n ≤ PrefetchMaxEntries
                _prefetchBytes -= cached.Bytes;
                _promotePath = null;
            }
            else if (_prefetchInFlight.Contains(path))
            {
                // This exact file is in the prefetch pipeline right now (still
                // decoding, or decoded and waiting for its upload turn).
                // Starting a second decode would double the work during fast
                // browsing, so register a promotion instead: whichever pipeline
                // stage finishes first delivers the pixels straight into the
                // pending queue under this generation.
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
            _pendingImages.Enqueue(new PendingImage(
                path, cached.Width, cached.Height, null, cached, generation));
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
                    _pendingImages.Enqueue(new PendingImage(path, w, h, pixels, null, generation));
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

    /// <summary>
    /// Decode <paramref name="path"/> in the background and hand the pixels to the
    /// render thread, which uploads them into the bounded GPU cache — a later
    /// <see cref="LoadImageAsync"/> for the file is then a pure texture swap.
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
            // Every branch settles _prefetchInFlight ATOMICALLY with its
            // decision, inside one lock: "in flight" must cover the WHOLE
            // pipeline (decode + upload), and a drop must be indistinguishable
            // from "never prefetched" at the instant it happens — otherwise
            // LoadImageAsync could register a promotion against a result that
            // was just dropped, and that navigation would never complete.
            try
            {
                byte[] pixels = ImageDecoder.DecodeToBgra(path, out int w, out int h);
                lock (_prefetchLock)
                {
                    if (_promotePath == path)
                    {
                        // A navigation request arrived mid-decode — deliver
                        // straight to the pending queue under the recorded
                        // generation (the "promotion" path).
                        _pendingImages.Enqueue(new PendingImage(
                            path, w, h, pixels, null, _promoteGeneration));
                        _promotePath = null;
                        _prefetchInFlight.Remove(path); // pipeline ends here
                    }
                    else if (Volatile.Read(ref _prefetchUploadCount) < PrefetchUploadQueueMax)
                    {
                        _prefetchUploads.Enqueue((path, w, h, pixels));
                        Interlocked.Increment(ref _prefetchUploadCount);
                        // stays in flight until ProcessOnePrefetchUpload drains it
                    }
                    else
                    {
                        // Upload backlog full — drop the pixels; a later
                        // Prefetch re-decodes the file if it is still relevant.
                        _prefetchInFlight.Remove(path);
                    }
                }
            }
            catch
            {
                // Corrupt/unsupported file — a real load attempt would fail the same
                // way, so also drop any pending promotion registered for it.
                lock (_prefetchLock)
                {
                    if (_promotePath == path) _promotePath = null;
                    _prefetchInFlight.Remove(path);
                }
            }
        });
    }

    /// <summary>
    /// Drain ONE queued prefetch result: upload it into the GPU cache (render
    /// thread, recording into the frame's command list). One per frame bounds
    /// the staging memcpy cost a single frame can pay.
    /// </summary>
    void ProcessOnePrefetchUpload(ID3D12GraphicsCommandList cmdList)
    {
        if (!_prefetchUploads.TryDequeue(out var item)) return;
        Interlocked.Decrement(ref _prefetchUploadCount);

        bool promote = false;
        int promoteGeneration = 0;
        lock (_prefetchLock)
        {
            if (_promotePath == item.Path)
            {
                // A navigation request targeted this file while it sat in the
                // upload queue — the second promotion completion point.
                promote = true;
                promoteGeneration = _promoteGeneration;
                _promotePath = null;
            }
            _prefetchInFlight.Remove(item.Path); // pipeline ends here either way

            if (!promote
                && (_prefetched.ContainsKey(item.Path)
                    || (long)item.W * item.H * 4 > PrefetchMaxBytes))
            {
                return; // duplicate, or larger than the whole budget — drop
            }
        }

        if (promote)
        {
            _pendingImages.Enqueue(new PendingImage(
                item.Path, item.W, item.H, item.Pixels, null, promoteGeneration));
            return;
        }

        int srvSlot = _res.AllocateSrvSlot();
        var texture = TextureUploader.Upload(_res, item.W, item.H, item.Pixels, srvSlot, cmdList);
        if (!TryInsertCache(new GpuImage(item.Path, texture, srvSlot, item.W, item.H)))
            _res.DeferRelease(texture, srvSlot); // raced out — fence-tagged release
    }

    /// <summary>Insert a ready texture into the cache, evicting LRU entries past
    /// the entry/byte budget (fence-tagged, no stall). False when the image does
    /// not fit or the path is already cached — the caller keeps ownership.</summary>
    bool TryInsertCache(GpuImage img)
    {
        lock (_prefetchLock)
        {
            if (img.Bytes > PrefetchMaxBytes) return false; // larger than the entire budget
            if (_prefetched.ContainsKey(img.Path)) return false;

            _prefetched[img.Path] = img;
            _prefetchOrder.AddFirst(img.Path);
            _prefetchBytes += img.Bytes;

            // Evict oldest entries beyond the entry/byte budget. The textures
            // may still be referenced by an in-flight frame, so release is
            // fence-tagged (same pattern as ThumbnailCache eviction).
            while ((_prefetched.Count > PrefetchMaxEntries || _prefetchBytes > PrefetchMaxBytes)
                   && _prefetchOrder.Last is not null)
            {
                string oldest = _prefetchOrder.Last.Value;
                _prefetchOrder.RemoveLast();
                if (_prefetched.Remove(oldest, out var evicted))
                {
                    _prefetchBytes -= evicted.Bytes;
                    _res.DeferRelease(evicted.Texture, evicted.SrvSlot);
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Pick up the newest finished load result. Returns true if a new image
    /// arrived: dimensions are updated immediately so the caller can re-fit the
    /// view; the actual apply (texture swap or GPU upload) happens in
    /// <see cref="FlushPendingUpload"/> during the next frame. Stale results
    /// carrying a ready texture are recycled into the prefetch cache — during
    /// fast browsing, the images skipped past stay warm for back-navigation.
    /// </summary>
    public bool PollDecodedImage()
    {
        PendingImage? latest = null;
        int currentGeneration = Volatile.Read(ref _loadGeneration);

        // Drain the queue, keep only the latest result matching the current generation.
        while (_pendingImages.TryDequeue(out var img))
        {
            if (img.Generation == currentGeneration)
            {
                RecycleOrRelease(latest);
                latest = img;
            }
            else
            {
                RecycleOrRelease(img); // stale — pixel payloads just fall to the GC
            }
        }

        if (latest is null) return false;

        RecycleOrRelease(_pendingApply); // superseded before it was ever applied
        _pendingApply = latest;
        _texW = latest.Width;
        _texH = latest.Height;
        return true;
    }

    /// <summary>A pending item that will never be applied: a ready texture goes
    /// back into the cache (or is fence-released when it does not fit); a pixel
    /// payload needs no action — the array is garbage the moment it is dropped.</summary>
    void RecycleOrRelease(PendingImage? img)
    {
        if (img?.Gpu is not { } gpu) return;
        if (!TryInsertCache(gpu))
            _res.DeferRelease(gpu.Texture, gpu.SrvSlot);
    }

    /// <summary>
    /// Apply the pending load result on the render thread (between BeginFrame and
    /// the draws): a cache hit is a pure texture swap; a fresh decode records its
    /// upload into the frame's command list — the copy + barrier execute before
    /// any draw recorded afterwards, so no GPU wait is needed. The OUTGOING
    /// texture is recycled into the prefetch cache under its path (back-navigation
    /// then costs nothing); only when it does not fit is it fence-released.
    /// Afterwards, at most one queued prefetch result is uploaded into the cache —
    /// skipped in frames that already paid for a main upload, so a single frame
    /// never carries two full-image staging copies.
    /// </summary>
    public void FlushPendingUpload(ID3D12GraphicsCommandList cmdList)
    {
        bool mainUploaded = false;

        if (_pendingApply is { } pending)
        {
            _pendingApply = null;
            RetireCurrent(pending.Path);

            if (pending.Gpu is { } ready)
            {
                _current = ready; // prefetch hit: already on the GPU — pure swap
            }
            else
            {
                int srvSlot = _res.AllocateSrvSlot();
                var texture = TextureUploader.Upload(
                    _res, pending.Width, pending.Height, pending.Pixels!, srvSlot, cmdList);
                _current = new GpuImage(pending.Path, texture, srvSlot,
                                        pending.Width, pending.Height);
                mainUploaded = true;
            }
        }

        if (!mainUploaded)
            ProcessOnePrefetchUpload(cmdList);
    }

    /// <summary>Detach the displayed texture and recycle it into the prefetch
    /// cache under its own path — unless the incoming image IS the same file
    /// (caching the old copy would duplicate it) or it does not fit the budget;
    /// then it is released fence-tagged like before.</summary>
    void RetireCurrent(string incomingPath)
    {
        if (_current is not { } old) return;
        _current = null;

        if (string.Equals(old.Path, incomingPath, StringComparison.OrdinalIgnoreCase)
            || !TryInsertCache(old))
        {
            _res.DeferRelease(old.Texture, old.SrvSlot);
        }
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

        _lastLeft = left;
        _lastTop = top;
        _lastDrawW = drawW;
        _lastDrawH = drawH;

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
        if (_current is null) return;
        _res.DrawQuad(_current.SrvSlot, CbSlot);
    }

    /// <summary>
    /// Live-resize camouflage: redraws the image at EXACTLY the same pixel
    /// position as the normal in-window draw, but into a buffer-sized viewport
    /// (the caller scissors it to the bands outside the current window). This
    /// makes the band content "the frame as if the window were bigger, without
    /// re-centering": where the image reaches the window edge, the band shows
    /// its true continuation; where it does not (letterboxed images), nothing
    /// is drawn and the band keeps the cleared backdrop veil — which is what
    /// the margin next to it shows anyway. Either way, the one composition
    /// pass where the window geometry outruns the content reveals pixels
    /// indistinguishable from their surroundings. (A stretched-copy filler was
    /// tried first: correct for edge-to-edge photos, but a letterboxed image
    /// leaked a visible duplicate onto the veil.)
    /// </summary>
    public void RenderUnderlay(int bufferW, int bufferH)
    {
        if (_current is null) return;
        if (bufferW <= 0 || bufferH <= 0) return;

        // Same destination rectangle as the last Update, re-expressed in the
        // buffer-sized viewport's NDC. Viewport origins coincide (both 0,0),
        // so viewport pixels ARE buffer pixels — the image lands pixel-exact.
        float sx = _lastDrawW / bufferW;
        float sy = _lastDrawH / bufferH;
        float tx = (_lastLeft + _lastDrawW * 0.5f) / bufferW * 2f - 1f;
        float ty = 1f - (_lastTop + _lastDrawH * 0.5f) / bufferH * 2f;

        var xform = Matrix4x4.CreateScale(sx, sy, 1f)
                  * Matrix4x4.CreateTranslation(tx, ty, 0f);

        var cb = new ViewConstants
        {
            Transform = Matrix4x4.Transpose(xform),
            TintColor = Vector4.Zero, // textured mode
        };
        _res.WriteConstants(CbSlotUnderlay, cb);
        _res.DrawQuad(_current.SrvSlot, CbSlotUnderlay);
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
        // ViewerApp performs a full GPU wait before disposing renderers, so
        // everything below can be released directly (no fence tagging needed).
        void Destroy(GpuImage img)
        {
            img.Texture.Dispose();
            _res.FreeSrvSlot(img.SrvSlot);
        }

        if (_current is { } current) { Destroy(current); _current = null; }
        if (_pendingApply?.Gpu is { } pendingGpu) Destroy(pendingGpu);
        _pendingApply = null;

        while (_pendingImages.TryDequeue(out var p))
            if (p.Gpu is { } g) Destroy(g);

        lock (_prefetchLock)
        {
            foreach (var kv in _prefetched)
                Destroy(kv.Value);
            _prefetched.Clear();
            _prefetchOrder.Clear();
            _prefetchBytes = 0;
        }
    }
}
