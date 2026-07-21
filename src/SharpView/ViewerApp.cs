using Vortice.Direct3D12;
using SharpView.Rendering;
using SharpView.Services;

using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace SharpView;

/// <summary>
/// Main application class. Creates the form, manages input, and runs the render loop.
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
    DateTime _lastFrameTime = DateTime.UtcNow;

    // Upload command allocator for thumbnail uploads (separate from frame allocators)
    ID3D12CommandAllocator _uploadAllocator = null!;

    readonly string _initialImagePath;

    public ViewerApp(string imagePath)
    {
        _initialImagePath = imagePath;
        _width = 1400;
        _height = 900;

        _form = new ViewerForm
        {
            Text = $"SharpView — {Path.GetFileName(imagePath)}",
            ClientSize = new Size(_width, _height),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(18, 18, 18),
            KeyPreview = true,
        };

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
        _btnOneToOne.Click += (_, _) => _imageRenderer?.SetOneToOne();

        _form.Controls.Add(_btnOneToOne);

        _form.Resize += (_, _) => _needsResize = true;
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
        _uploadAllocator = _res.Device.CreateCommandAllocator(CommandListType.Direct);

        _imageRenderer = new ImageRenderer(_res);
        _thumbCache = new ThumbnailCache(_res);
        _thumbStrip = new ThumbnailStrip(_res, _thumbCache);

        // Load the initial image synchronously; no animation on first show.
        _imageRenderer.LoadImageSync(_initialImagePath);
        _imageRenderer.FitToWindowInstant(_width, MainViewHeight);

        _nav.ScanFolder(_initialImagePath);
        _thumbStrip.SnapToIndex(_nav.CurrentIndex, _width);
        UpdateTitle();
    }

    int MainViewHeight => _height - ThumbnailStrip.StripHeight;

    public void Run()
    {
        _form.Show();
        _lastFrameTime = DateTime.UtcNow;

        while (_running && _form.Visible)
        {
            Application.DoEvents();
            if (!_running) break;

            if (_needsResize)
            {
                HandleResize();
                _needsResize = false;
            }

            Update();
            RenderFrame();
        }
    }

    void Update()
    {
        var now = DateTime.UtcNow;
        float dt = Math.Clamp((float)(now - _lastFrameTime).TotalSeconds, 0.0001f, 0.1f);
        _lastFrameTime = now;

        _imageRenderer.Update(dt, _width, MainViewHeight);
        _thumbStrip.Update(dt, _width, _height, _nav);
    }

    void RenderFrame()
    {
        // A newly decoded main image? Upload it and fit the view.
        if (_imageRenderer.TryCompletePendingLoad())
            _imageRenderer.FitToWindow(_width, MainViewHeight);

        // Upload any freshly decoded thumbnails before beginning the frame.
        if (_thumbCache.HasPendingUploads)
        {
            _uploadAllocator.Reset();
            _res.CommandList.Reset(_uploadAllocator, null);
            bool uploaded = _thumbCache.ProcessUploads(_res.CommandList);
            _res.CommandList.Close();

            if (uploaded)
            {
                _res.CommandQueue.ExecuteCommandList(_res.CommandList);
                _res.WaitForGpu(); // also releases staging upload buffers
            }
        }

        _res.BeginFrame();

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
        // Non-blocking: decode happens on the thread pool, upload on a later frame.
        _imageRenderer.LoadImageAsync(_nav.CurrentFile);
        UpdateTitle();
    }

    void UpdateTitle()
        => _form.Text = $"SharpView — {Path.GetFileName(_nav.CurrentFile)}  [{_nav.CurrentIndex + 1}/{_nav.Count}]";

    // ─── Input Handlers ───────────────────────────────────────────────

    void OnMouseWheel(object? s, MouseEventArgs e)
    {
        // Only zoom while the cursor is over the main image area.
        if (e.Y < MainViewHeight)
            _imageRenderer.ZoomAt(e.Delta, e.X, e.Y, _width, MainViewHeight);
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
        }
    }

    void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
            _form.Cursor = Cursors.Default;
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
    }

    bool HandleKey(Keys keyData)
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
        _uploadAllocator?.Dispose();
        _res.Dispose();
        _form.Dispose();
    }
}
