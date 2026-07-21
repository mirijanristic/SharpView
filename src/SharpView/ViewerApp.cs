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
    readonly Button _btnOneToOne;
    int _width, _height;

    readonly Core.DeviceResources _res = new();
    readonly ImageNavigator _nav = new();
    ImageRenderer _imageRenderer = null!;
    ThumbnailStrip _thumbStrip = null!;
    ThumbnailCache _thumbCache = null!;

    bool _running = true, _needsResize;
    bool _dragging;
    Point _lastMouse;

    // High-resolution frame timing (DateTime.UtcNow is low-resolution and slower).
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastFrameTime;

    // Render a few extra frames after any event so the final state reaches the screen.
    int _forcedFrames = 3;

    readonly string _initialImagePath;

    public ViewerApp(string imagePath)
    {
        _initialImagePath = imagePath;
        _width = 1400;
        _height = 900;

        _form = new ViewerForm
        {
            Text = $"SharpView — {Path.GetFileName(imagePath)}",
            ClientSize = new Size(_width, _height), // restored (un-maximized) size
            StartPosition = FormStartPosition.CenterScreen,
            WindowState = FormWindowState.Maximized, // start full screen
            BackColor = Color.FromArgb(18, 18, 18),
            KeyPreview = true,
        };

        // Title-bar / Alt-Tab icon: reuse the icon embedded into the EXE via
        // <ApplicationIcon>icon.ico</ApplicationIcon> in the .csproj, so icon.ico
        // does not need to ship next to the executable.
        try { _form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* missing/odd icon resource — keep the default form icon */ }

        _btnOneToOne = new Button
        {
            Text = "1:1",
            Size = new Size(50, 32),
            Location = new Point(10, 10),
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _btnOneToOne.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 74);
        _btnOneToOne.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 62, 66);
        _btnOneToOne.FlatAppearance.MouseDownBackColor = Color.FromArgb(80, 80, 84);
        _btnOneToOne.Click += (_, _) => { _imageRenderer?.SetOneToOne(); Wake(); };

        _form.Controls.Add(_btnOneToOne);

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
        _res.Init(_form.Handle, _width, _height);
        WindowStyling.ApplyDarkStyle(_form.Handle); // dark title bar + Mica caption

        _imageRenderer = new ImageRenderer(_res);
        _thumbCache = new ThumbnailCache(_res);
        _thumbStrip = new ThumbnailStrip(_res, _thumbCache);

        // Decode the initial image synchronously; the GPU upload itself is recorded
        // into the first frame's command list. The startup zoom (true 1:1 when the
        // image fits, fit-to-window when it is bigger) is applied in Run(), once
        // the maximized client size is actually known.
        _imageRenderer.LoadImageSync(_initialImagePath);

        _nav.ScanFolder(_initialImagePath);
        PrefetchNeighbors(); // next/prev are pre-decoded before the user asks
        UpdateTitle();
    }

    int MainViewHeight => _height - ThumbnailStrip.StripHeight;

    public void Run()
    {
        _form.Show();

        // The window opens maximized, so the real client size is only known now:
        // sync the swap chain to it and apply the startup view (1:1 when the image
        // fits, fit-to-window when it is bigger) before the first frame.
        _needsResize = false;
        HandleResize();
        _imageRenderer.FitOrOneToOneInstant(_width, MainViewHeight);
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
        || _imageRenderer.IsBusy
        || _thumbCache.IsBusy;

    /// <summary>Ensure the render loop runs for at least a couple more frames.</summary>
    void Wake() => _forcedFrames = Math.Max(_forcedFrames, 2);

    void Update()
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = Math.Clamp((float)(now - _lastFrameTime), 0.0001f, 0.1f);
        _lastFrameTime = now;

        // A newly decoded main image? Publish its dimensions and set the view:
        // 1:1 when it fits, fit-to-window when it is bigger (same as startup).
        // Prefer the old always-fit behavior? Revert this line to FitToWindow.
        if (_imageRenderer.PollDecodedImage())
            _imageRenderer.FitOrOneToOne(_width, MainViewHeight);

        _imageRenderer.Update(dt, _width, MainViewHeight);
        _thumbStrip.Update(dt, _width, _height, _nav);
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

        // Main image (top area)
        _res.SetViewportAndScissor(0, 0, _width, MainViewHeight);
        _imageRenderer.Render();

        // Thumbnail strip (full-window viewport for pixel-coordinate rendering)
        _res.SetViewportAndScissor(0, 0, _width, _height);
        _thumbStrip.Render(_width, _height, _nav);

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
        // Only zoom while the cursor is over the main image area.
        if (e.Y < MainViewHeight)
        {
            _imageRenderer.ZoomAt(e.Delta, e.X, e.Y, _width, MainViewHeight);
            Wake();
        }
    }

    void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // Click on the thumbnail strip?
        int thumbIndex = _thumbStrip.HitTest(e.X, e.Y, _width, _height, _nav.Count);
        if (thumbIndex >= 0)
        {
            if (_nav.MoveTo(thumbIndex))
                NavigateToImage();
            return;
        }

        // Otherwise start dragging the main image.
        if (e.Y < MainViewHeight)
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
        if (!_dragging) return;
        float dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
        _imageRenderer.Pan(dx, dy);
        _lastMouse = e.Location;
    }

    void OnMouseDoubleClick(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Y >= MainViewHeight) return;

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
