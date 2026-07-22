using System.Diagnostics;
using SharpView.Platform;
using SharpView.Rendering;
using SharpView.Services;

using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace SharpView;

/// <summary>
/// Main application class. Creates the form, manages input, and runs the render loop.
/// The loop is demand-driven: when nothing is animating, loading, or being dragged,
/// it sleeps briefly instead of redrawing a static image, so idle CPU/GPU usage
/// drops to (near) zero.
/// </summary>
sealed class ViewerApp : IDisposable
{
    readonly ViewerForm _form;
    int _width, _height;

    readonly Core.DeviceResources _res = new();
    readonly ImageNavigator _nav = new();
    ImageRenderer _imageRenderer = null!;
    ThumbnailStrip _thumbStrip = null!;
    ThumbnailCache _thumbCache = null!;
    TopBar _topBar = null!;

    bool _running = true, _needsResize;
    bool _dragging;
    Point _lastMouse;

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
        _width = 1400;
        _height = 900;

        _form = new ViewerForm
        {
            Text = $"SharpView — {Path.GetFileName(imagePath)}",
            // Borderless overlay, Picasa-style: the whole screen is the viewer and
            // the desktop stays visible through the translucent backdrop. Esc or
            // Alt+F4 closes. Prefer a normal framed window again? Delete this line
            // (it is set before ClientSize on purpose, so ClientSize is preserved).
            FormBorderStyle = FormBorderStyle.None,
            ClientSize = new Size(_width, _height), // restored (un-maximized) size
            StartPosition = FormStartPosition.CenterScreen,
            WindowState = FormWindowState.Maximized, // start full screen
            BackColor = Color.FromArgb(18, 18, 18), // never visible (no GDI surface); kept for a framed fallback
            KeyPreview = true,
        };

        // Title-bar / Alt-Tab icon: reuse the icon embedded into the EXE via
        // <ApplicationIcon>icon.ico</ApplicationIcon> in the .csproj, so icon.ico
        // does not need to ship next to the executable.
        try { _form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* missing/odd icon resource — keep the default form icon */ }

        // The old WinForms "1:1" button is gone on purpose: WS_EX_NOREDIRECTIONBITMAP
        // removes the window's GDI surface, so a classic child control would be
        // invisible yet still swallow clicks. 1:1 stays available on double-click
        // and the '1' key ('0' returns to fit).

        _form.Resize += (_, _) => { _needsResize = true; Wake(); };
        _form.FormClosing += (_, _) => _running = false;
        _form.MouseWheel += OnMouseWheel;
        _form.MouseDown += OnMouseDown;
        _form.MouseUp += OnMouseUp;
        _form.MouseMove += OnMouseMove;
        _form.MouseDoubleClick += OnMouseDoubleClick;
        _form.KeyHandler = HandleKey;

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

        _res.Init(_form.Handle, _width, _height);
        WindowStyling.ApplyDarkStyle(_form.Handle); // no-op while borderless; kept for a framed fallback

        _thumbCache = new ThumbnailCache(_res);
        _thumbStrip = new ThumbnailStrip(_res, _thumbCache);
        _topBar = new TopBar(_res);

        // The bar's zone doubles as the window caption: Windows itself runs the
        // move loop for it (drag-restore from maximized, Aero Snap, dragging to
        // the other monitor, double-click restore, right-click system menu). Only
        // the X stays client area so our own mouse handler gets the click.
        _form.HitTestHandler = (x, y) =>
            _topBar.HitTest(x, y, _width, _form.WindowState == FormWindowState.Maximized) switch
            {
                TopBar.Hit.Close => ViewerForm.HTClient,
                TopBar.Hit.Drag => ViewerForm.HTCaption,
                _ => 0,
            };
        // Caption-zone mouse moves arrive as non-client messages, not MouseMove —
        // wake the loop so the bar's hover logic (in Update) gets frames to run.
        _form.NonClientMouseMove = Wake;

        _nav.ScanFolder(_initialImagePath);
        PrefetchNeighbors(); // next/prev are pre-decoded before the user asks
        UpdateTitle();
    }

    /// <summary>Height of the main image area: from the very top of the window
    /// (the hover top bar OVERLAYS the image rather than reserving space for
    /// itself) down to the thumbnail strip's reserved bottom band.</summary>
    int MainViewHeight => _height - ThumbnailStrip.ReservedHeight;

    /// <summary>True when a window-space Y lies inside the main image area.</summary>
    bool InMainView(int y) => y >= 0 && y < MainViewHeight;

    public void Run()
    {
        _form.Show();

        // The window opens maximized, so the real client size is only known now:
        // sync the swap chain to it and center the strip. The startup view for the
        // image itself is applied in Update() the moment its (async) decode lands.
        _needsResize = false;
        HandleResize();
        _thumbStrip.SnapToIndex(_nav.CurrentIndex, _width);

        _lastFrameTime = _clock.Elapsed.TotalSeconds;

        while (_running && _form.Visible)
        {
            Application.DoEvents();
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
                // every few milliseconds by DoEvents above.
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
                _imageRenderer.FitOrOneToOne(_width, MainViewHeight);
            }
            else
            {
                _imageRenderer.FitOrOneToOneInstant(_width, MainViewHeight);
                _firstImageShown = true;
            }
        }
        else if (!_firstImageShown && !_initialFailureReported
                 && !_imageRenderer.IsBusy && !_imageRenderer.HasImage)
        {
            // The initial decode failed (corrupt/unsupported file). Report once and
            // keep running — the rest of the folder stays browsable via the strip.
            _initialFailureReported = true;
            MessageBox.Show(_form,
                $"Cannot open image:\n{_initialImagePath}",
                "SharpView", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _imageRenderer.Update(dt, _width, MainViewHeight);
        _thumbStrip.Update(dt, _width, _height, _nav);

        // Hover top bar: polled rather than event-driven, because its caption zone
        // produces no client MouseMove and the mouse can leave the window sideways
        // (toward the other monitor) without any message at all. Hidden while
        // dragging so the bar stays out of the way of a pan near the top edge.
        var cursor = _form.PointToClient(Cursor.Position);
        bool cursorAvailable = !_dragging && _form.ContainsFocus
                               && _form.ClientRectangle.Contains(cursor);
        _topBar.Update(dt, _width, cursor.X, cursor.Y, cursorAvailable,
            _form.WindowState == FormWindowState.Maximized);
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

        // Main image (from the top of the window down to the strip band; the
        // hover top bar is drawn later as an overlay on top of it)
        _res.SetViewportAndScissor(0, 0, _width, MainViewHeight);
        _imageRenderer.Render();

        // Thumbnail strip (full-window viewport for pixel-coordinate rendering)
        _res.SetViewportAndScissor(0, 0, _width, _height);
        _thumbStrip.Render(_width, _height, _nav);

        // Hover top bar — drawn last, overlays the image (full-window viewport).
        _topBar.Render(_width, _height);

        _res.EndFrame();
    }

    void HandleResize()
    {
        int w = _form.ClientSize.Width, h = _form.ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        _res.Resize(w, h);
        _width = w;
        _height = h;
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
        => _form.Text = $"SharpView — {Path.GetFileName(_nav.CurrentFile)}  [{_nav.CurrentIndex + 1}/{_nav.Count}]";

    // ─── Input Handlers ───────────────────────────────────────────────

    void OnMouseWheel(object? s, MouseEventArgs e)
    {
        // Only zoom while the cursor is over the main image area (which now spans
        // from the very top, so window Y and viewport Y are the same thing).
        if (InMainView(e.Y))
        {
            _imageRenderer.ZoomAt(e.Delta, e.X, e.Y, _width, MainViewHeight);
            Wake();
        }
    }

    void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // The top bar's X? (Checked first — the bar overlays everything. The rest
        // of the bar never gets here: it hit-tests as caption, so Windows turns
        // clicks there into a window drag.)
        if (_topBar.HitTestClose(e.X, e.Y, _width))
        {
            _running = false;
            return;
        }

        // Click on the thumbnail strip?
        int thumbIndex = _thumbStrip.HitTest(e.X, e.Y, _width, _height, _nav.Count);
        if (thumbIndex >= 0)
        {
            if (_nav.MoveTo(thumbIndex))
                NavigateToImage();
            return;
        }

        // Otherwise start dragging the main image.
        if (InMainView(e.Y))
        {
            _dragging = true;
            _lastMouse = e.Location;
            _form.Cursor = Cursors.SizeAll;
            Wake();
        }
    }

    void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
            _form.Cursor = Cursors.Default;
            Wake();
        }
    }

    void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (!_dragging)
        {
            // Near the top edge? Give the bar's hover logic a frame to run (its
            // trigger zone is mostly caption, but the X area is client — and this
            // also catches re-entry from just below the bar).
            if (e.Y < TopBar.BarHeight) Wake();
            return;
        }
        float dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
        _imageRenderer.Pan(dx, dy);
        _lastMouse = e.Location;
    }

    void OnMouseDoubleClick(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !InMainView(e.Y)) return;

        if (!_imageRenderer.IsOneToOne)
            _imageRenderer.SetOneToOne();
        else
            _imageRenderer.FitToWindow(_width, MainViewHeight);
        Wake();
    }

    bool HandleKey(Keys keyData)
    {
        bool handled = HandleKeyCore(keyData);
        if (handled) Wake();
        return handled;
    }

    bool HandleKeyCore(Keys keyData)
    {
        switch (keyData & Keys.KeyCode) // strip Shift/Ctrl/Alt modifiers
        {
            case Keys.Left:
                if (_nav.MovePrevious()) NavigateToImage();
                return true;
            case Keys.Right:
                if (_nav.MoveNext()) NavigateToImage();
                return true;
            case Keys.Home:
                if (_nav.MoveFirst()) NavigateToImage();
                return true;
            case Keys.End:
                if (_nav.MoveLast()) NavigateToImage();
                return true;

            case Keys.D0 or Keys.NumPad0:
                _imageRenderer.FitToWindow(_width, MainViewHeight);
                return true;
            case Keys.D1 or Keys.NumPad1:
                _imageRenderer.SetOneToOne();
                return true;
            case Keys.Add or Keys.Oemplus:
                _imageRenderer.ZoomIn();
                return true;
            case Keys.Subtract or Keys.OemMinus:
                _imageRenderer.ZoomOut();
                return true;
            case Keys.Escape:
                _running = false;
                return true;

            default:
                return false; // let the system handle it (Alt+F4, Tab, ...)
        }
    }

    public void Dispose()
    {
        // Make sure the GPU is idle before tearing down resources it may still use.
        _res.WaitForGpu();

        _thumbCache?.Dispose();
        _imageRenderer?.Dispose();
        _res.Dispose();
        _form.Dispose();
    }
}
