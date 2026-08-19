using System.Diagnostics;
using System.Numerics;
using SharpView.Platform;
using SharpView.Rendering;
using SharpView.Services;

namespace SharpView;

/// <summary>
/// Main application class. Creates the Win32 window, manages input, and runs the
/// render loop. The loop is demand-driven: when nothing is animating, loading, or
/// being dragged, it sleeps briefly instead of redrawing a static image, so idle
/// CPU/GPU usage drops to (near) zero.
/// </summary>
sealed class ViewerApp : IDisposable
{
    readonly ViewerWindow _window;
    int _width, _height;

    readonly Core.DeviceResources _res = new();

    // Frozen-geometry resize: the OS window is parked at the gesture's maximal
    // bounds while the content renders at this offset with the apparent size in
    // _width/_height. Both offsets are 0 outside the gesture, so every viewport
    // below degenerates to the classic (0, 0, w, h).
    bool _frozenResize;
    int _viewOffsetX, _viewOffsetY;
    const int CbSlotVeil = 42; // after ImageRenderer's underlay slot (41)
    readonly ImageNavigator _nav = new();
    ImageRenderer _imageRenderer = null!;
    ThumbnailStrip _thumbStrip = null!;
    ThumbnailCache _thumbCache = null!;
    TopBar _topBar = null!;

    bool _running = true, _needsResize;
    bool _dragging;
    int _lastMouseX, _lastMouseY;

    // High-resolution frame timing (DateTime.UtcNow is low-resolution and slower).
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastFrameTime;

    // Render a few extra frames after any event so the final state reaches the screen.
    int _forcedFrames = 3;

    // The very first image gets an instant (non-animated) fit; also guards the
    // one-time "cannot open" warning if the initial decode fails.
    bool _firstImageShown;
    bool _initialFailureReported;

    readonly string _initialImagePath;

    public ViewerApp(string imagePath)
    {
        _initialImagePath = imagePath;

        // The window opens borderless-maximized, so its client area will be the
        // FULL bounds of the screen it lands on. Windows maximizes a caption-less
        // window over the whole monitor containing it — creating it centered on
        // the cursor's monitor therefore sizes the swap chain correctly before
        // the window is even shown, which turns the post-Show HandleResize into a
        // no-op instead of a swap chain rebuild behind a full GPU wait. A wrong
        // guess (cursor moved to another monitor mid-startup) simply falls back
        // to a real resize.
        var screen = NativeMethods.MonitorBoundsFromCursor();
        _width = screen.Width;
        _height = screen.Height;

        // Restored (un-maximized) bounds: 1400×900 centered on that monitor —
        // this is where drag-restore from maximized lands, exactly like before.
        const int restoredW = 1400, restoredH = 900;
        int x = screen.Left + Math.Max(0, (screen.Width - restoredW) / 2);
        int y = screen.Top + Math.Max(0, (screen.Height - restoredH) / 2);

        _window = new ViewerWindow(
            $"SharpView — {Path.GetFileName(imagePath)}", x, y, restoredW, restoredH);

        _window.Resized += () => { _needsResize = true; Wake(); };
        _window.LiveResize = OnLiveResize;
        _window.SizeMoveEnded = () => _res.EndLiveResize();
        _window.FrozenResizeBegin = OnFrozenResizeBegin;
        _window.FrozenResizeTick = OnFrozenResizeTick;
        _window.FrozenResizeEnd = OnFrozenResizeEnd;
        _window.CloseRequested += () => _running = false;
        _window.MouseWheel = OnMouseWheel;
        _window.MouseDown = OnMouseDown;
        _window.MouseUp = OnMouseUp;
        _window.MouseMove = OnMouseMove;
        _window.MouseDoubleClick = OnMouseDoubleClick;
        _window.CaptureLost = EndDragIfActive; // Alt-Tab mid-pan must not leave a stuck drag
        _window.KeyHandler = HandleKey;

        InitGraphics();
    }

    void InitGraphics()
    {
        // Start decoding the very first image immediately, on the thread pool —
        // it overlaps the D3D12 initialization below, and the window shows the
        // moment it is ready instead of freezing for seconds on a huge file.
        // The picture pops in (with an instant fit) as soon as the decode lands.
        _imageRenderer = new ImageRenderer(_res);
        _imageRenderer.LoadImageAsync(_initialImagePath);

        _res.Init(_window.Handle, _width, _height);
        WindowStyling.ApplyDarkStyle(_window.Handle); // no-op while borderless; kept for a framed fallback

        _thumbCache = new ThumbnailCache(_res);
        _thumbStrip = new ThumbnailStrip(_res, _thumbCache);
        _topBar = new TopBar(_res);

        // The bar's zone doubles as the window caption: Windows itself runs the
        // move loop for it (drag-restore from maximized, Aero Snap, dragging to
        // the other monitor, double-click restore, right-click system menu). Only
        // the X stays client area so our own mouse handler gets the click.
        _window.HitTestHandler = (x, y) =>
            _topBar.HitTest(x, y, _width, _window.IsMaximized) switch
            {
                TopBar.Hit.Close => NativeMethods.HTClient,
                TopBar.Hit.Drag => NativeMethods.HTCaption,
                _ => 0,
            };
        // Caption-zone mouse moves arrive as non-client messages, not WM_MOUSEMOVE —
        // wake the loop so the bar's hover logic (in Update) gets frames to run.
        _window.NonClientMouseMove = Wake;

        _nav.ScanFolder(_initialImagePath);
        PrefetchNeighbors(); // next/prev are pre-decoded before the user asks
        UpdateTitle();
    }

    /// <summary>Height of the main image area: from the very top of the window
    /// (the hover top bar OVERLAYS the image rather than reserving space for
    /// itself) down to the thumbnail strip's reserved bottom band.</summary>
    // Input split only: clicks/wheel above this line belong to the image,
    // below it to the thumbnail strip. The image itself is laid out and DRAWN
    // over the FULL window height — both bars (hover top bar, thumbnail strip)
    // are translucent overlays on top of it, sharing the same shade.
    int MainViewHeight => _height - ThumbnailStrip.ReservedHeight;

    /// <summary>True when a window-space Y lies inside the main image area.</summary>
    bool InMainView(int y) => y >= 0 && y < MainViewHeight;

    public void Run()
    {
        _window.ShowMaximized();

        // The swap chain was already created at the predicted maximized size, so
        // in the normal case this is a pure verification — DeviceResources skips
        // same-size requests, no rebuild, no GPU wait. Real work happens only if
        // the window landed somewhere unexpected. The startup view for the image
        // itself is applied in Update() the moment its (async) decode lands.
        _needsResize = false;
        HandleResize();
        _thumbStrip.SnapToIndex(_nav.CurrentIndex, _width);

        _lastFrameTime = _clock.Elapsed.TotalSeconds;

        while (_running)
        {
            // Drain pending window messages (the DoEvents replacement); false
            // means WM_QUIT arrived and the window is gone.
            if (!_window.PumpMessages()) break;
            if (!_running) break;

            if (_needsResize)
            {
                HandleResize();
                _needsResize = false;
                Wake();
            }

            if (!NeedsFrame())
            {
                // Fully idle: static image on screen, nothing decoding or animating.
                // Sleep briefly instead of spinning at vsync — input is still polled
                // every few milliseconds by the message pump above.
                Thread.Sleep(4);
                _lastFrameTime = _clock.Elapsed.TotalSeconds;
                continue;
            }

            if (_forcedFrames > 0) _forcedFrames--;

            Update();
            RenderFrame();
        }
    }

    /// <summary>True when something on screen can still change and a frame must be drawn.</summary>
    bool NeedsFrame() =>
        _forcedFrames > 0
        || _dragging
        || _needsResize
        || !_imageRenderer.IsAnimationSettled
        || !_thumbStrip.IsSettled
        || _topBar.WantsFrames // visible/fading bar polls the cursor each frame
        || _imageRenderer.IsBusy
        || _thumbCache.IsBusy
        || (!_firstImageShown && !_initialFailureReported); // initial load still resolving

    /// <summary>Ensure the render loop runs for at least a couple more frames.</summary>
    void Wake() => _forcedFrames = Math.Max(_forcedFrames, 2);

    void Update()
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = Math.Clamp((float)(now - _lastFrameTime), 0.0001f, 0.1f);
        _lastFrameTime = now;

        // A newly decoded main image? Publish its dimensions and set the view:
        // 1:1 when it fits, fit-to-window when it is bigger. The very first image
        // appears instantly (no zoom animation); navigation stays animated.
        // Prefer the old always-fit behavior? Swap FitOrOneToOne for FitToWindow.
        if (_imageRenderer.PollDecodedImage())
        {
            if (_firstImageShown)
            {
                _imageRenderer.FitOrOneToOne(_width, _height);
            }
            else
            {
                _imageRenderer.FitOrOneToOneInstant(_width, _height);
                _firstImageShown = true;
            }
        }
        else if (!_firstImageShown && !_initialFailureReported
                 && !_imageRenderer.IsBusy && !_imageRenderer.HasImage)
        {
            // The initial decode failed (corrupt/unsupported file). Report once and
            // keep running — the rest of the folder stays browsable via the strip.
            _initialFailureReported = true;
            NativeMethods.MessageBox(_window.Handle,
                $"Cannot open image:\n{_initialImagePath}",
                "SharpView", NativeMethods.MB_OK | NativeMethods.MB_ICONWARNING);
        }

        _imageRenderer.Update(dt, _width, _height);
        _thumbStrip.Update(dt, _width, _height, _nav);

        // Hover top bar: polled rather than event-driven, because its caption zone
        // produces no client WM_MOUSEMOVE and the mouse can leave the window
        // sideways (toward the other monitor) without any message at all. Hidden
        // while dragging so the bar stays out of the way of a pan near the top edge.
        _window.GetCursorClientPosition(out int cx, out int cy, out bool insideClient);
        bool cursorAvailable = !_dragging && _window.IsForeground && insideClient;
        _topBar.Update(dt, _width, cx, cy, cursorAvailable, _window.IsMaximized);
    }

    void RenderFrame()
    {
        _res.BeginFrame();

        // Record pending texture uploads into this frame's command list. They execute
        // (with their barriers) before the draws below — same-queue ordering makes a
        // GPU wait unnecessary, and staging buffers are reclaimed once this frame's
        // fence completes. This removed the full pipeline stalls the old separate
        // upload path required on every image change and thumbnail batch.
        _imageRenderer.FlushPendingUpload(_res.CommandList);
        if (_thumbCache.HasPendingUploads)
            _thumbCache.ProcessUploads(_res.CommandList);

        // Live-resize camouflage: extend the frame into the bands OUTSIDE the
        // current window layout (right/bottom of the oversized buffer) with
        // "what a bigger window would show, without re-centering": the clear
        // already put the backdrop veil there, and the image is redrawn at its
        // exact same pixel position — its true continuation where it reaches
        // the window edge, untouched veil where it is letterboxed. The one
        // geometry-outruns-content composition pass then reveals pixels that
        // blend seamlessly with their surroundings. Inside the window nothing
        // is drawn (scissor), so normal rendering stays pixel-identical.
        if (_window.InSizeMove
            && (_res.BufferWidth > _width || _res.BufferHeight > _height))
        {
            // Viewport spans the full buffer so pixel coordinates line up with
            // the in-window draw; the scissor clips to one band per draw.
            _res.SetViewportAndScissor(0, 0, _res.BufferWidth, _res.BufferHeight);
            if (_res.BufferWidth > _width)
            {
                _res.SetScissor(_width, 0, _res.BufferWidth - _width, _res.BufferHeight);
                _imageRenderer.RenderUnderlay(_res.BufferWidth, _res.BufferHeight);
            }
            if (_res.BufferHeight > _height)
            {
                _res.SetScissor(0, _height, _width, _res.BufferHeight - _height);
                _imageRenderer.RenderUnderlay(_res.BufferWidth, _res.BufferHeight);
            }
        }

        // Frozen resize: the clear left the whole buffer transparent; put the
        // veil back over exactly the apparent window rect.
        if (_frozenResize)
            DrawFrozenVeil();

        // Main image over the FULL window — the strip band and the hover top
        // bar are translucent overlays drawn on top of it below. The view
        // offsets are non-zero only during the frozen-resize gesture.
        _res.SetViewportAndScissor(_viewOffsetX, _viewOffsetY, _width, _height);
        _imageRenderer.Render();

        // Thumbnail strip (window-sized viewport for pixel-coordinate rendering)
        _res.SetViewportAndScissor(_viewOffsetX, _viewOffsetY, _width, _height);
        _thumbStrip.Render(_width, _height, _nav);

        // Hover top bar — drawn last, overlays the image (window-sized viewport).
        _topBar.Render(_width, _height);

        _res.EndFrame();
    }

    void HandleResize()
    {
        // During the frozen gesture the OS client is the PARKED rect; letting it
        // through would overwrite the apparent size the gesture maintains.
        if (_frozenResize) return;

        _window.GetClientSize(out int w, out int h);
        if (w <= 0 || h <= 0) return;

        _res.Resize(w, h);
        _width = w;
        _height = h;
    }

    /// <summary>
    /// Per-tick redraw during an interactive edge/corner resize. Invoked
    /// synchronously from the WndProc — with the NEW client size, from
    /// WM_NCCALCSIZE, before the window geometry is applied — while Run()'s
    /// loop is blocked inside the system's modal resize loop, so this IS the
    /// render loop for that duration.
    /// </summary>
    void OnLiveResize(int width, int height)
    {
        if (_imageRenderer is null) return; // pre-init safety; cannot happen post-Show
        if (width <= 0 || height <= 0) return;

        // First tick of the gesture sizes the buffers for the whole gesture
        // (idempotent afterwards); every tick then renders the window-sized
        // layout into the oversized buffer and the window clips the rest —
        // no reallocation, no per-tick GPU-wait-for-rebuild, no scaling.
        // Sized to the VIRTUAL screen, not the current monitor: dragging the
        // left edge onto the neighboring monitor makes the window wider than
        // any single monitor, and a too-small buffer would fall back to a full
        // reallocation on every tick — the expensive-tick regime whose flicker
        // this whole mechanism exists to kill (plus the camouflage band would
        // vanish). Memory is transient: released with the first resize after
        // the gesture ends.
        int reachW = Math.Max(NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN), width);
        int reachH = Math.Max(NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN), height);
        _res.BeginLiveResize(reachW, reachH);

        _res.Resize(width, height);
        _width = width;
        _height = height;

        Update();
        RenderFrame();

        // The GPU wait turns "queued" into "complete" BEFORE the geometry
        // lands, so every applied window rect is preceded by its finished
        // frame. Deliberately no DwmFlush here: with ticks this cheap it only
        // inserted a forced full-composition gap between "frame ready" and
        // "geometry applied" — a window in which DWM could compose the stale
        // pair, which read as the centered image oscillating. Tightly paired
        // ticks keep content and geometry in lockstep; the scissored underlay
        // camouflages the residual race DWM cannot eliminate.
        _res.WaitForGpu();

        Wake(); // a few follow-up frames once the loop resumes
    }

    /// <summary>
    /// Frozen-resize gesture start. Called synchronously from the WndProc with
    /// the parked buffer size and the content's initial offset; the parked
    /// window geometry is applied right after this returns, so the frame must
    /// be presented AND complete here — content first, geometry second.
    /// </summary>
    void OnFrozenResizeBegin(int bufferW, int bufferH,
                             int offsetX, int offsetY, int width, int height)
    {
        if (_imageRenderer is null) return;

        // 1) Warm-up frame in the CURRENT state — identical pixels, so it can
        //    never be seen "wrong". After ~20 s idling behind another window
        //    the first render costs extra (GPU clock ramp, caches, DWM
        //    re-engagement); pay that here, while nothing is changing yet.
        Update();
        RenderFrame();
        _res.WaitForGpu();

        // 2) Phase-align the transition: DwmFlush returns right AFTER a
        //    composition, so everything below — the offset frame, its GPU
        //    wait, and the parked SetWindowPos the window applies the moment
        //    we return — lands inside ONE composition interval. The next
        //    composition then sees the new frame and the new geometry
        //    TOGETHER; no instant is left for DWM to sample the transition
        //    halfway (the rare whole-window flash at edge grab, likeliest
        //    right after refocus, when activation itself schedules an extra
        //    composition).
        NativeMethods.DwmFlush();

        _frozenResize = true;
        _res.ClearTransparent = true; // outside the apparent rect = invisible
        _viewOffsetX = offsetX;
        _viewOffsetY = offsetY;

        // Sticky buffers: reallocates at most ONCE per session per reached
        // size — and even that first reallocation is now phase-aligned, so its
        // empty-visual gap is re-filled before the next composition.
        _res.Resize(bufferW, bufferH);
        _width = width;
        _height = height;

        Update();
        RenderFrame();
        _res.WaitForGpu();
        Wake();
    }

    /// <summary>
    /// Frozen-resize mouse tick: pure state change — no geometry, no swap chain
    /// work, no synchronous render. The normal render loop (running freely,
    /// since there is no modal system loop) draws it at full pace; this is why
    /// the left edge now costs exactly as much as the right one.
    /// </summary>
    void OnFrozenResizeTick(int offsetX, int offsetY, int width, int height)
    {
        _viewOffsetX = offsetX;
        _viewOffsetY = offsetY;
        _width = width;
        _height = height;
        Wake();
    }

    /// <summary>Frozen-resize end: exact-size buffer, final frame at offset 0,
    /// GPU wait — the final window rect is applied right after this returns.</summary>
    void OnFrozenResizeEnd(int width, int height)
    {
        // Same phase-alignment as begin (no warm-up needed — the GPU is hot
        // from the gesture): final frame + final window rect inside one
        // composition interval.
        NativeMethods.DwmFlush();

        _frozenResize = false;
        _res.ClearTransparent = false;
        _viewOffsetX = 0;
        _viewOffsetY = 0;

        _res.Resize(width, height); // no-op with sticky buffers (never shrinks)
        _width = width;
        _height = height;

        Update();
        RenderFrame();
        _res.WaitForGpu();
        Wake();
    }

    /// <summary>
    /// The backdrop veil as a quad over exactly the apparent window rect.
    /// Outside the gesture the veil is simply the clear color; during it, the
    /// clear is fully transparent (the parked region must be invisible) and
    /// this draw puts the veil back where the apparent window is.
    /// </summary>
    void DrawFrozenVeil()
    {
        float bufferW = _res.BufferWidth, bufferH = _res.BufferHeight;
        _res.SetViewportAndScissor(0, 0, bufferW, bufferH);

        float sx = _width / bufferW;
        float sy = _height / bufferH;
        float tx = (_viewOffsetX + _width * 0.5f) / bufferW * 2f - 1f;
        float ty = 1f - (_viewOffsetY + _height * 0.5f) / bufferH * 2f;
        var transform = Matrix4x4.CreateScale(sx, sy, 1f)
                      * Matrix4x4.CreateTranslation(tx, ty, 0f);

        _res.WriteConstants(CbSlotVeil, new Core.ViewConstants
        {
            Transform = Matrix4x4.Transpose(transform),
            // Straight alpha — the shader premultiplies (TintColor.a is also
            // the solid-color mode flag).
            TintColor = new Vector4(0f, 0f, 0f, Core.DeviceResources.BackdropAlpha),
        });
        _res.DrawQuad(_res.WhiteSrvSlot, CbSlotVeil);
    }

    void NavigateToImage()
    {
        // Non-blocking: decode happens on the thread pool (or is skipped entirely
        // when the image was prefetched), the upload on a later frame.
        _imageRenderer.LoadImageAsync(_nav.CurrentFile);
        PrefetchNeighbors();
        UpdateTitle();
        Wake();
    }

    /// <summary>Pre-decode the previous/next images so arrow-key navigation is instant.</summary>
    void PrefetchNeighbors()
    {
        int i = _nav.CurrentIndex;
        if (i + 1 < _nav.Count) _imageRenderer.Prefetch(_nav.Files[i + 1]);
        if (i - 1 >= 0) _imageRenderer.Prefetch(_nav.Files[i - 1]);
    }

    void UpdateTitle()
        => _window.SetTitle(
            $"SharpView - {Path.GetFileName(_nav.CurrentFile)}  [{_nav.CurrentIndex + 1}/{_nav.Count}]"
            + (_res.IsWarp ? "  [software rendering]" : ""));

    // ─── Input Handlers (client pixels; left button only by construction) ──

    void OnMouseWheel(int x, int y, int delta)
    {
        // Only zoom while the cursor is over the main image area (which spans
        // from the very top, so window Y and viewport Y are the same thing).
        if (InMainView(y))
        {
            _imageRenderer.ZoomAt(delta, x, y, _width, _height);
            Wake();
        }
    }

    void OnMouseDown(int x, int y)
    {
        // The top bar's X? (Checked first — the bar overlays everything. The rest
        // of the bar never gets here: it hit-tests as caption, so Windows turns
        // clicks there into a window drag.)
        if (_topBar.HitTestClose(x, y, _width))
        {
            _running = false;
            return;
        }

        // Click on the thumbnail strip?
        int thumbIndex = _thumbStrip.HitTest(x, y, _width, _height, _nav.Count);
        if (thumbIndex >= 0)
        {
            if (_nav.MoveTo(thumbIndex))
                NavigateToImage();
            return;
        }

        // Otherwise start dragging the main image.
        if (InMainView(y))
        {
            _dragging = true;
            _lastMouseX = x;
            _lastMouseY = y;
            _window.SetDragCursor(true);
            Wake();
        }
    }

    void OnMouseUp(int x, int y) => EndDragIfActive();

    /// <summary>Ends a pan drag; also invoked when mouse capture is lost (Alt-Tab).</summary>
    void EndDragIfActive()
    {
        if (!_dragging) return;
        _dragging = false;
        _window.SetDragCursor(false);
        Wake();
    }

    void OnMouseMove(int x, int y)
    {
        if (!_dragging)
        {
            // Near the top edge? Give the bar's hover logic a frame to run (its
            // trigger zone is mostly caption, but the X area is client — and this
            // also catches re-entry from just below the bar).
            if (y < TopBar.BarHeight) Wake();
            return;
        }
        float dx = x - _lastMouseX, dy = y - _lastMouseY;
        _imageRenderer.Pan(dx, dy);
        _lastMouseX = x;
        _lastMouseY = y;
    }

    void OnMouseDoubleClick(int x, int y)
    {
        if (!InMainView(y)) return;

        if (!_imageRenderer.IsOneToOne)
            _imageRenderer.SetOneToOne();
        else
            _imageRenderer.FitToWindow(_width, _height);
        Wake();
    }

    bool HandleKey(int vk)
    {
        bool handled = HandleKeyCore(vk);
        if (handled) Wake();
        return handled;
    }

    bool HandleKeyCore(int vk)
    {
        switch (vk)
        {
            case NativeMethods.VK_LEFT:
                if (_nav.MovePrevious()) NavigateToImage();
                return true;
            case NativeMethods.VK_RIGHT:
                if (_nav.MoveNext()) NavigateToImage();
                return true;
            case NativeMethods.VK_HOME:
                if (_nav.MoveFirst()) NavigateToImage();
                return true;
            case NativeMethods.VK_END:
                if (_nav.MoveLast()) NavigateToImage();
                return true;

            case NativeMethods.VK_0 or NativeMethods.VK_NUMPAD0:
                _imageRenderer.FitToWindow(_width, _height);
                return true;
            case NativeMethods.VK_1 or NativeMethods.VK_NUMPAD1:
                _imageRenderer.SetOneToOne();
                return true;
            case NativeMethods.VK_ADD or NativeMethods.VK_OEM_PLUS:
                _imageRenderer.ZoomIn();
                return true;
            case NativeMethods.VK_SUBTRACT or NativeMethods.VK_OEM_MINUS:
                _imageRenderer.ZoomOut();
                return true;
            case NativeMethods.VK_ESCAPE:
                _running = false;
                return true;

            default:
                return false; // let the system handle it (Alt+F4, ...)
        }
    }

    public void Dispose()
    {
        // Make sure the GPU is idle before tearing down resources it may still use.
        _res.WaitForGpu();

        _thumbCache?.Dispose();
        _imageRenderer?.Dispose();
        _res.Dispose();
        _window.Dispose();
    }
}
