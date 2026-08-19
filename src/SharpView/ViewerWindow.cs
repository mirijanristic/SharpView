using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpView.Platform;

namespace SharpView;

/// <summary>
/// Pure Win32 window that replaces the old WinForms <c>ViewerForm</c>: window
/// class + <c>CreateWindowEx</c>, a static <c>UnmanagedCallersOnly</c> WndProc
/// (function pointer, no delegates — Native-AOT-clean), and a message pump the
/// render loop drains explicitly. The behavioral contract is identical:
/// key input routed through <see cref="KeyHandler"/>, window hit testing through
/// <see cref="HitTestHandler"/> (the D3D-drawn top bar reports itself as
/// <see cref="NativeMethods.HTCaption"/>, so Windows runs its native move loop —
/// drag-restore from maximized, Aero Snap, cross-monitor drags, double-click
/// restore and the right-click system menu all work like a real title bar), and
/// <see cref="NativeMethods.WS_EX_NOREDIRECTIONBITMAP"/> removes the GDI
/// redirection surface so every pixel — including alpha — comes exclusively from
/// the DirectComposition-hosted swap chain (the per-pixel transparent window).
/// </summary>
/// <remarks>
/// Differences from WinForms worth knowing: mouse capture during drags is taken
/// explicitly (WinForms did it implicitly), the drag cursor is applied in
/// WM_SETCURSOR via <see cref="SetDragCursor"/>, a lost capture (Alt-Tab
/// mid-drag) surfaces as <see cref="CaptureLost"/> so the app can end the pan,
/// and drag-restore from maximized (grab the bar → window un-maximizes and
/// follows the mouse) is implemented manually in the WM_NCLBUTTONDOWN /
/// mouse-move handlers — the system move loop refuses to move a maximized
/// window and, for a borderless one, never performs the restore by itself.
/// The window carries WS_THICKFRAME (visible frame removed via WM_NCCALCSIZE),
/// which buys edge/corner resizing with native cursors AND makes the shell
/// treat it as snappable: Snap Layouts on drag-to-top, side snapping and
/// Win+Arrows. On Windows 11 the style also brings native rounded corners and
/// the DWM shadow; opt out with DWMWA_WINDOW_CORNER_PREFERENCE if ever unwanted.
/// Mouse-driven edge/corner resizing does NOT use the system resize loop: it is
/// a frozen-geometry gesture (see BeginFrozenResize) — the OS window is parked
/// once at the gesture's maximal bounds and only the CONTENT moves per tick,
/// which reduces DWM's two async channels (geometry, content) to one and makes
/// resize flicker impossible by construction while the mouse is down. Keyboard
/// sizing (Alt+Space → Size) still takes the system loop and the WM_NCCALCSIZE
/// live-render path.
/// Single-window design on purpose: the WndProc reaches the instance through a
/// static field, no GWLP_USERDATA bookkeeping needed.
/// </remarks>
sealed unsafe class ViewerWindow : IDisposable
{
    const string ClassName = "SharpViewWindow";

    static ViewerWindow? s_instance; // single-window app; set before CreateWindowEx

    IntPtr _hwnd;
    IntPtr _icon;
    bool _dragCursor;

    // Armed by a caption press while maximized; fires (restore + hand the window
    // to the native move loop) once the cursor passes the system drag threshold.
    bool _pendingDragRestore;
    NativeMethods.POINT _dragRestoreStart;

    // True between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE — the window is inside
    // the system's modal move/size loop and the app loop is blocked.
    bool _inSizeMove;

    // Minimum window size — enforced both by WM_GETMINMAXINFO (system paths)
    // and by the frozen-resize clamps (our own gesture).
    const int MinTrackWidth = 640;
    const int MinTrackHeight = 400;

    // ─── Frozen-geometry resize gesture ────────────────────────────────
    // While active the OS window is parked at the gesture's maximal bounds and
    // only the content moves (offset + apparent size per mouse tick).
    bool _frozenResize;
    int _frozenEdge;                        // HT code of the grabbed edge/corner
    NativeMethods.RECT _frozenBounds;       // parked window rect
    NativeMethods.RECT _frozenStartRect;    // apparent rect at gesture start
    NativeMethods.POINT _frozenStartCursor;
    NativeMethods.RECT _frozenApparent;     // current apparent rect (screen coords)

    public IntPtr Handle => _hwnd;

    /// <summary>True while inside the system's modal move/size loop.</summary>
    public bool InSizeMove => _inSizeMove;

    /// <summary>Return true if the key (virtual-key code) was handled; false for
    /// default processing (Alt+F4, ...).</summary>
    public Func<int, bool>? KeyHandler;

    /// <summary>Hit test in client pixels. Return an HT* code, or 0 for default handling.</summary>
    public Func<int, int, int>? HitTestHandler;

    /// <summary>Raised on non-client mouse movement. The caption zone produces no
    /// regular mouse-move events, so this is how the render loop learns it should
    /// wake up and let the top bar fade in.</summary>
    public Action? NonClientMouseMove;

    /// <summary>Left-button events in client pixels; wheel delta only for <see cref="MouseWheel"/>.</summary>
    public Action<int, int>? MouseDown, MouseUp, MouseMove, MouseDoubleClick;
    public Action<int, int, int>? MouseWheel; // x, y (client), delta

    /// <summary>Client size changed (resize, maximize/restore, DPI move).</summary>
    public Action? Resized;

    /// <summary>
    /// Called SYNCHRONOUSLY for every size tick during an interactive edge/corner
    /// resize, while the app's own loop is blocked inside the system's modal
    /// resize loop — with the NEW client size, from WM_NCCALCSIZE, i.e. BEFORE
    /// the window geometry is applied. That ordering is the whole point: DWM
    /// commits window-rect changes and swap chain frames independently, so a
    /// frame rendered after the move (from WM_SIZE) lags one composition behind
    /// and the edge OPPOSITE the dragged one visibly wobbles on left/top
    /// resizes. Rendering here — and finishing the frame (GPU wait) before
    /// returning — lets DWM compose the new geometry and the new content
    /// together. The handler must resize the swap chain to the given size,
    /// render one frame, and not return until the frame is complete.
    /// </summary>
    public Action<int, int>? LiveResize;

    /// <summary>The user asked to close (Alt+F4 / system menu). The app decides;
    /// the window is destroyed in <see cref="Dispose"/>.</summary>
    public Action? CloseRequested;

    /// <summary>The modal move/size loop ended — the app leaves live-resize mode
    /// here, BEFORE the follow-up <see cref="Resized"/> does the final exact-size
    /// pass.</summary>
    public Action? SizeMoveEnded;

    /// <summary>Frozen resize began: (bufferW, bufferH, offsetX, offsetY,
    /// apparentW, apparentH). The handler must resize the swap chain to the
    /// buffer size, render one frame at the offset and not return until the
    /// frame completes — the parked geometry is applied right after, so the
    /// first composition already has matching pixels.</summary>
    public Action<int, int, int, int, int, int>? FrozenResizeBegin;

    /// <summary>Per mouse tick during frozen resize: (offsetX, offsetY,
    /// apparentW, apparentH). Content-only change — rendering happens on the
    /// app's normal loop at full pace; no geometry is touched.</summary>
    public Action<int, int, int, int>? FrozenResizeTick;

    /// <summary>Frozen resize ended: (finalW, finalH). The handler must resize
    /// the swap chain to the exact final size, render at offset 0 and wait for
    /// completion; the final window rect is applied immediately after.</summary>
    public Action<int, int>? FrozenResizeEnd;

    /// <summary>Mouse capture was taken away mid-drag (Alt-Tab, ...) — end the pan.</summary>
    public Action? CaptureLost;

    public ViewerWindow(string title, int x, int y, int width, int height)
    {
        s_instance = this;

        IntPtr instance = NativeMethods.GetModuleHandle(null);

        fixed (char* className = ClassName)
        {
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                style = NativeMethods.CS_DBLCLKS, // deliver WM_LBUTTONDBLCLK (1:1 ↔ fit toggle)
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, nint, nint>)&StaticWndProc,
                hInstance = instance,
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, (IntPtr)NativeMethods.IDC_ARROW),
                hbrBackground = IntPtr.Zero, // no GDI erase — there is no redirection surface anyway
                lpszClassName = (IntPtr)className,
            };
            if (NativeMethods.RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException(
                    $"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
        }

        // Created hidden at the RESTORED bounds; the first ShowMaximized() then
        // maximizes it — same startup shape the WinForms shell had.
        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_NOREDIRECTIONBITMAP | NativeMethods.WS_EX_APPWINDOW,
            ClassName, title,
            NativeMethods.WS_POPUP | NativeMethods.WS_SYSMENU
                | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX
                | NativeMethods.WS_THICKFRAME // resizable + snappable; frame removed in WM_NCCALCSIZE
                | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");

        TrySetIconFromExe();
    }

    /// <summary>Reuse the exe's embedded icon (&lt;ApplicationIcon&gt; in the
    /// .csproj) for the Alt-Tab / taskbar icon — no System.Drawing involved.</summary>
    void TrySetIconFromExe()
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null) return;

        IntPtr icon = NativeMethods.ExtractIcon(NativeMethods.GetModuleHandle(null), exePath, 0);
        if (icon == IntPtr.Zero || icon == (IntPtr)1) return; // no icon resource — keep default

        _icon = icon;
        NativeMethods.SendMessage(_hwnd, NativeMethods.WM_SETICON, NativeMethods.ICON_BIG, icon);
        NativeMethods.SendMessage(_hwnd, NativeMethods.WM_SETICON, NativeMethods.ICON_SMALL, icon);
    }

    // ─── Shell operations used by ViewerApp ────────────────────────────

    public void ShowMaximized() => NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWMAXIMIZED);

    public void SetTitle(string title) => NativeMethods.SetWindowText(_hwnd, title);

    public bool IsMaximized => NativeMethods.IsZoomed(_hwnd);

    public bool IsForeground => NativeMethods.GetForegroundWindow() == _hwnd;

    public void GetClientSize(out int width, out int height)
    {
        NativeMethods.GetClientRect(_hwnd, out var rect);
        width = rect.Width;
        height = rect.Height;
    }

    /// <summary>Full pixel size of the monitor the window currently occupies —
    /// the upper bound a single resize gesture can reach without changing
    /// monitors (and buffer growth covers even that rare case).</summary>
    public void GetMonitorSize(out int width, out int height)
    {
        IntPtr monitor = NativeMethods.MonitorFromWindow(_hwnd, 2 /* NEAREST */);
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)sizeof(NativeMethods.MONITORINFO) };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            width = info.rcMonitor.Width;
            height = info.rcMonitor.Height;
            return;
        }
        GetClientSize(out width, out height);
    }

    /// <summary>Cursor position in client pixels + whether it is inside the client
    /// area right now (polled by the top bar's hover logic every frame).</summary>
    public void GetCursorClientPosition(out int x, out int y, out bool insideClient)
    {
        NativeMethods.GetCursorPos(out var pt);

        // Frozen resize: the OS client covers the parked rect, but every
        // consumer thinks in apparent-window coordinates — translate.
        if (_frozenResize)
        {
            x = pt.X - _frozenApparent.Left;
            y = pt.Y - _frozenApparent.Top;
            insideClient = x >= 0 && y >= 0
                && x < _frozenApparent.Width && y < _frozenApparent.Height;
            return;
        }

        NativeMethods.ScreenToClient(_hwnd, ref pt);
        x = pt.X;
        y = pt.Y;
        NativeMethods.GetClientRect(_hwnd, out var rect);
        insideClient = x >= 0 && y >= 0 && x < rect.Width && y < rect.Height;
    }

    /// <summary>Switch between the pan (size-all) and default cursor; applied in
    /// WM_SETCURSOR, which is the Win32-correct place (WinForms did this via
    /// Control.Cursor).</summary>
    public void SetDragCursor(bool dragging)
    {
        _dragCursor = dragging;
        // Apply immediately too — WM_SETCURSOR only fires on the next mouse move.
        NativeMethods.SetCursor(NativeMethods.LoadCursor(IntPtr.Zero,
            (IntPtr)(dragging ? NativeMethods.IDC_SIZEALL : NativeMethods.IDC_ARROW)));
    }

    /// <summary>
    /// Drain all pending messages (the render loop's replacement for
    /// Application.DoEvents). Returns false once WM_QUIT is seen.
    /// </summary>
    public bool PumpMessages()
    {
        while (NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
        {
            if (msg.Message == NativeMethods.WM_QUIT) return false;
            NativeMethods.TranslateMessage(in msg);
            NativeMethods.DispatchMessage(in msg);
        }
        return true;
    }

    // ─── WndProc ───────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint StaticWndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        var self = s_instance;
        if (self is null)
            return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);

        // Messages arrive (WM_NCCREATE, ...) before CreateWindowEx returns —
        // adopt the handle on first contact so instance state works throughout.
        if (self._hwnd == IntPtr.Zero) self._hwnd = hwnd;

        try
        {
            return self.WndProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // An exception must never cross the unmanaged boundary (instant
            // process corruption under NativeAOT). Log and fall back to default.
            Debug.WriteLine($"[ViewerWindow] WndProc exception: {ex}");
            return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    static int LoWordX(nint lParam) => unchecked((short)((ulong)lParam & 0xFFFF));
    static int HiWordY(nint lParam) => unchecked((short)(((ulong)lParam >> 16) & 0xFFFF));

    nint WndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCCALCSIZE when wParam != 0:
            {
                // wParam TRUE: lParam points at NCCALCSIZE_PARAMS whose first
                // field is rgrc[0] = the proposed WINDOW rect; on return it must
                // hold the CLIENT rect. Returning 0 with the rect untouched
                // claims the whole window as client — that removes the visible
                // frame WS_THICKFRAME would otherwise paint. When maximized, the
                // system may inflate the window past the monitor by the frame
                // width; clamping to the monitor keeps the client (top bar,
                // thumbnail strip) fully on-screen. Intersection only ever
                // shrinks, so it is correct for both maximize geometries a
                // caption-less window can get.
                var rect = (NativeMethods.RECT*)lParam;
                if (NativeMethods.IsZoomed(hwnd))
                {
                    IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, 2 /* NEAREST */);
                    var info = new NativeMethods.MONITORINFO { cbSize = (uint)sizeof(NativeMethods.MONITORINFO) };
                    if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
                    {
                        rect->Left = Math.Max(rect->Left, info.rcMonitor.Left);
                        rect->Top = Math.Max(rect->Top, info.rcMonitor.Top);
                        rect->Right = Math.Min(rect->Right, info.rcMonitor.Right);
                        rect->Bottom = Math.Min(rect->Bottom, info.rcMonitor.Bottom);
                    }
                }

                // Interactive resize: render at the NEW size right now, before
                // the geometry lands (see LiveResize). *rect is exactly the
                // client rect being returned. (Snap-maximize mid-drag comes
                // through here too, already clamped above — also handled.)
                if (_inSizeMove && LiveResize is not null
                    && rect->Width > 0 && rect->Height > 0)
                {
                    LiveResize(rect->Width, rect->Height);
                }
                return 0;
            }

            case NativeMethods.WM_GETMINMAXINFO:
            {
                // Only the minimum size is pinned; maximize geometry stays at the
                // system default (full monitor for a caption-less window) with
                // WM_NCCALCSIZE clamping any frame overhang.
                var mmi = (NativeMethods.MINMAXINFO*)lParam;
                mmi->MinTrackSize.X = MinTrackWidth;
                mmi->MinTrackSize.Y = MinTrackHeight;
                return 0;
            }

            case NativeMethods.WM_NCHITTEST:
            {
                // lParam packs SCREEN coords as signed shorts (monitors left of /
                // above the primary yield negative values).
                var pt = new NativeMethods.POINT { X = LoWordX(lParam), Y = HiWordY(lParam) };
                NativeMethods.ScreenToClient(hwnd, ref pt);

                // Resize bands first: the outer ~8 px ring (and its corners)
                // belongs to sizing, so the top band wins over the caption there —
                // exactly how framed windows behave. Never while maximized.
                if (!NativeMethods.IsZoomed(hwnd))
                {
                    int edge = ResizeBorderHitTest(pt.X, pt.Y);
                    if (edge != 0) return edge;
                }

                int hit = HitTestHandler?.Invoke(pt.X, pt.Y) ?? 0;
                if (hit != 0) return hit;
                break; // 0 → default handling
            }

            case NativeMethods.WM_NCMOUSEMOVE:
                if (_pendingDragRestore && TryStartDragRestore()) return 0;
                NonClientMouseMove?.Invoke();
                break;

            case NativeMethods.WM_NCLBUTTONDOWN:
                // Edge/corner press → frozen-geometry gesture; the system
                // resize loop (and its geometry-vs-content races) never starts.
                if ((int)wParam >= NativeMethods.HTLeft
                    && (int)wParam <= NativeMethods.HTBottomRight
                    && !NativeMethods.IsZoomed(hwnd) && !_frozenResize)
                {
                    BeginFrozenResize((int)wParam);
                    return 0;
                }
                // Maximized + caption: arm the WinForms-style drag-restore and
                // swallow the message. Passing it to DefWindowProc would start a
                // move loop that refuses to move a maximized window — the exact
                // regression this fixes. A plain click therefore does nothing
                // (as before), and the restore fires only after real movement,
                // so double-click keeps toggling maximize/restore untouched.
                if ((int)wParam == NativeMethods.HTCaption && NativeMethods.IsZoomed(hwnd))
                {
                    NativeMethods.GetCursorPos(out _dragRestoreStart);
                    _pendingDragRestore = true;
                    return 0;
                }
                break; // restored window: DefWindowProc runs the native move loop

            case NativeMethods.WM_NCLBUTTONUP:
            case NativeMethods.WM_NCLBUTTONDBLCLK:
                _pendingDragRestore = false;
                break; // dblclk → DefWindowProc toggles maximize/restore (unchanged)

            case NativeMethods.WM_KEYDOWN when _frozenResize:
                // ESC cancels back to the gesture's start rect, like the system
                // loop; other keys are swallowed while resizing.
                if ((int)wParam == NativeMethods.VK_ESCAPE)
                    FinishFrozenResize(_frozenStartRect);
                return 0;

            case NativeMethods.WM_KEYDOWN:
                if (KeyHandler?.Invoke((int)wParam) == true) return 0;
                break; // unhandled → DefWindowProc (lets Alt+F4 & friends work)

            case NativeMethods.WM_LBUTTONDOWN:
                // Explicit capture: keep receiving WM_MOUSEMOVE while a pan drags
                // the cursor outside the window (WinForms captured implicitly).
                NativeMethods.SetCapture(hwnd);
                MouseDown?.Invoke(LoWordX(lParam), HiWordY(lParam));
                return 0;

            case NativeMethods.WM_LBUTTONUP when _frozenResize:
                FinishFrozenResize(_frozenApparent);
                return 0;

            case NativeMethods.WM_LBUTTONUP:
                NativeMethods.ReleaseCapture();
                MouseUp?.Invoke(LoWordX(lParam), HiWordY(lParam));
                return 0;

            case NativeMethods.WM_LBUTTONDBLCLK:
                MouseDoubleClick?.Invoke(LoWordX(lParam), HiWordY(lParam));
                return 0;

            case NativeMethods.WM_MOUSEMOVE:
                if (_frozenResize) { FrozenResizeMouseMove(); return 0; }
                // A fast caption drag can cross into the client area before the
                // threshold trips — the pending restore must still fire.
                if (_pendingDragRestore && TryStartDragRestore()) return 0;
                MouseMove?.Invoke(LoWordX(lParam), HiWordY(lParam));
                return 0;

            case NativeMethods.WM_MOUSEWHEEL when _frozenResize:
                return 0; // no zoom mid-gesture (coords would be parked-window-relative)

            case NativeMethods.WM_MOUSEWHEEL:
            {
                // Wheel coords are SCREEN pixels (unlike the other mouse messages).
                var pt = new NativeMethods.POINT { X = LoWordX(lParam), Y = HiWordY(lParam) };
                NativeMethods.ScreenToClient(hwnd, ref pt);
                int delta = unchecked((short)(((ulong)wParam >> 16) & 0xFFFF));
                MouseWheel?.Invoke(pt.X, pt.Y, delta);
                return 0;
            }

            case NativeMethods.WM_CAPTURECHANGED when _frozenResize:
                // Capture stolen (Alt-Tab, ...) → commit at the current rect.
                FinishFrozenResize(_frozenApparent, releaseCapture: false);
                break;

            case NativeMethods.WM_CAPTURECHANGED:
                CaptureLost?.Invoke();
                break;

            case NativeMethods.WM_SETCURSOR:
                if ((lParam & 0xFFFF) == NativeMethods.HTClient)
                {
                    NativeMethods.SetCursor(NativeMethods.LoadCursor(IntPtr.Zero,
                        (IntPtr)(_dragCursor ? NativeMethods.IDC_SIZEALL : NativeMethods.IDC_ARROW)));
                    return 1;
                }
                break;

            case NativeMethods.WM_ENTERSIZEMOVE:
                _inSizeMove = true;
                break;

            case NativeMethods.WM_SIZE:
                // During an interactive resize the frame for this tick was
                // already rendered in WM_NCCALCSIZE (before the move landed) —
                // rendering again here would just add a stale-lag frame back.
                if (_inSizeMove && wParam != 1 && LiveResize is not null)
                    return 0;
                Resized?.Invoke(); // normal path: maximize/restore/DPI, app loop running
                break;

            case NativeMethods.WM_EXITSIZEMOVE:
                _inSizeMove = false;
                SizeMoveEnded?.Invoke();
                // Safety net after a native move/size loop: one regular resize
                // pass through the app loop (same-size requests dedupe in
                // DeviceResources anyway).
                Resized?.Invoke();
                break;

            case NativeMethods.WM_DPICHANGED:
            {
                // Per-Monitor V2: apply the suggested rect for the restored state;
                // the follow-up WM_SIZE drives the swap chain resize.
                var suggested = *(NativeMethods.RECT*)lParam;
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    suggested.Left, suggested.Top, suggested.Width, suggested.Height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                return 0;
            }

            case NativeMethods.WM_ERASEBKGND:
                return 1; // nothing to erase — no redirection surface

            case NativeMethods.WM_CLOSE:
                // The app owns shutdown: its loop exits and Dispose destroys the
                // window (mirrors the old FormClosing → _running = false path).
                CloseRequested?.Invoke();
                return 0;

            case NativeMethods.WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return 0;
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ─── Frozen-geometry resize ────────────────────────────────────────

    static bool MovesLeft(int edge) => edge is NativeMethods.HTLeft
        or NativeMethods.HTTopLeft or NativeMethods.HTBottomLeft;
    static bool MovesRight(int edge) => edge is NativeMethods.HTRight
        or NativeMethods.HTTopRight or NativeMethods.HTBottomRight;
    static bool MovesTop(int edge) => edge is NativeMethods.HTTop
        or NativeMethods.HTTopLeft or NativeMethods.HTTopRight;
    static bool MovesBottom(int edge) => edge is NativeMethods.HTBottom
        or NativeMethods.HTBottomLeft or NativeMethods.HTBottomRight;

    /// <summary>
    /// Start the frozen-geometry gesture: the window is parked ONCE at the
    /// maximal rect this gesture can reach (grabbed edges extended to the
    /// virtual screen, fixed edges untouched) and does not change again until
    /// the button is released — per tick only the content's offset and
    /// apparent size move. One async channel instead of two: DWM cannot
    /// compose a mismatched geometry/content pair, so the resize is
    /// flicker-free by construction, and the left edge costs exactly as much
    /// as the right one.
    /// </summary>
    void BeginFrozenResize(int edge)
    {
        _frozenEdge = edge;
        NativeMethods.GetWindowRect(_hwnd, out _frozenStartRect);
        NativeMethods.GetCursorPos(out _frozenStartCursor);
        _frozenApparent = _frozenStartRect;

        int vsLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vsTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vsRight = vsLeft + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vsBottom = vsTop + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        _frozenBounds = new NativeMethods.RECT
        {
            Left = MovesLeft(edge) ? vsLeft : _frozenStartRect.Left,
            Top = MovesTop(edge) ? vsTop : _frozenStartRect.Top,
            Right = MovesRight(edge) ? vsRight : _frozenStartRect.Right,
            Bottom = MovesBottom(edge) ? vsBottom : _frozenStartRect.Bottom,
        };

        // Deliberately NO DWM chrome toggling here: the parked edges sit
        // exactly on the virtual-screen bounds, so their shadow/border fall
        // offscreen by themselves, the fixed edges keep their chrome in the
        // correct place, and parked-corner rounding only clips alpha-0 pixels.
        // (An earlier version disabled NC rendering per gesture — the shadow
        // popping off and on was itself a visible blink at grab/release.)

        _frozenResize = true;
        NativeMethods.SetCapture(_hwnd);
        SetResizeCursor(edge);

        // Content first, geometry second: the handler presents the offset frame
        // into the parked-size buffer and waits for completion; the parked rect
        // is applied immediately after, so the first composition that sees the
        // new geometry already has the matching pixels.
        FrozenResizeBegin?.Invoke(
            _frozenBounds.Width, _frozenBounds.Height,
            _frozenStartRect.Left - _frozenBounds.Left,
            _frozenStartRect.Top - _frozenBounds.Top,
            _frozenStartRect.Width, _frozenStartRect.Height);

        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
            _frozenBounds.Left, _frozenBounds.Top,
            _frozenBounds.Width, _frozenBounds.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    void FrozenResizeMouseMove()
    {
        NativeMethods.GetCursorPos(out var pt);
        int dx = pt.X - _frozenStartCursor.X;
        int dy = pt.Y - _frozenStartCursor.Y;

        var rect = _frozenStartRect;
        if (MovesLeft(_frozenEdge))
            rect.Left = Math.Clamp(rect.Left + dx,
                _frozenBounds.Left, rect.Right - MinTrackWidth);
        if (MovesRight(_frozenEdge))
            rect.Right = Math.Clamp(rect.Right + dx,
                rect.Left + MinTrackWidth, _frozenBounds.Right);
        if (MovesTop(_frozenEdge))
            rect.Top = Math.Clamp(rect.Top + dy,
                _frozenBounds.Top, rect.Bottom - MinTrackHeight);
        if (MovesBottom(_frozenEdge))
            rect.Bottom = Math.Clamp(rect.Bottom + dy,
                rect.Top + MinTrackHeight, _frozenBounds.Bottom);
        _frozenApparent = rect;

        SetResizeCursor(_frozenEdge); // mouse capture bypasses WM_SETCURSOR
        FrozenResizeTick?.Invoke(
            rect.Left - _frozenBounds.Left, rect.Top - _frozenBounds.Top,
            rect.Width, rect.Height);
    }

    void FinishFrozenResize(NativeMethods.RECT finalRect, bool releaseCapture = true)
    {
        _frozenResize = false;
        if (releaseCapture) NativeMethods.ReleaseCapture();

        // Same ordering as begin: final content presented and complete first,
        // final geometry immediately after. The microseconds between the two
        // are the only remaining race — once per gesture, with an idle mouse.
        FrozenResizeEnd?.Invoke(finalRect.Width, finalRect.Height);

        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
            finalRect.Left, finalRect.Top, finalRect.Width, finalRect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        SetResizeCursor(0); // back to the arrow
        Resized?.Invoke();  // safety net (same-size requests dedupe)
    }

    void SetResizeCursor(int edge)
    {
        int cursor = edge switch
        {
            NativeMethods.HTLeft or NativeMethods.HTRight => NativeMethods.IDC_SIZEWE,
            NativeMethods.HTTop or NativeMethods.HTBottom => NativeMethods.IDC_SIZENS,
            NativeMethods.HTTopLeft or NativeMethods.HTBottomRight => NativeMethods.IDC_SIZENWSE,
            NativeMethods.HTTopRight or NativeMethods.HTBottomLeft => NativeMethods.IDC_SIZENESW,
            _ => NativeMethods.IDC_ARROW,
        };
        NativeMethods.SetCursor(NativeMethods.LoadCursor(IntPtr.Zero, (IntPtr)cursor));
    }

    // ─── Resize borders ────────────────────────────────────────────────

    /// <summary>
    /// Maps a client-space point to a resize hit-test code when it lies within
    /// the border band along the window edges; 0 otherwise. The client covers
    /// the whole window (WM_NCCALCSIZE), so client edges ARE window edges.
    /// </summary>
    int ResizeBorderHitTest(int x, int y)
    {
        NativeMethods.GetClientRect(_hwnd, out var client);
        int bandX = Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSIZEFRAME)
                              + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXPADDEDBORDER));
        int bandY = Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSIZEFRAME)
                              + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXPADDEDBORDER));

        bool left = x < bandX;
        bool right = x >= client.Width - bandX;
        bool top = y < bandY;
        bool bottom = y >= client.Height - bandY;

        if (top && left) return NativeMethods.HTTopLeft;
        if (top && right) return NativeMethods.HTTopRight;
        if (bottom && left) return NativeMethods.HTBottomLeft;
        if (bottom && right) return NativeMethods.HTBottomRight;
        if (left) return NativeMethods.HTLeft;
        if (right) return NativeMethods.HTRight;
        if (top) return NativeMethods.HTTop;
        if (bottom) return NativeMethods.HTBottom;
        return 0;
    }

    // ─── Drag-restore from maximized ───────────────────────────────────

    /// <summary>
    /// Completes an armed drag-restore once the cursor moves past the system
    /// drag threshold with the button still held: restores the window with the
    /// cursor kept proportionally over the bar (what Windows itself does for
    /// framed windows), then hands the now-restored window to the native move
    /// loop so it sticks to the mouse — from there on, Aero Snap, cross-monitor
    /// drags and drag-to-top re-maximize all behave natively again.
    /// </summary>
    bool TryStartDragRestore()
    {
        if (NativeMethods.GetKeyState(NativeMethods.VK_LBUTTON) >= 0)
        {
            _pendingDragRestore = false; // released without dragging → plain click
            return false;
        }

        NativeMethods.GetCursorPos(out var pt);
        int thresholdX = Math.Max(NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG), 2);
        int thresholdY = Math.Max(NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG), 2);
        if (Math.Abs(pt.X - _dragRestoreStart.X) <= thresholdX
            && Math.Abs(pt.Y - _dragRestoreStart.Y) <= thresholdY)
        {
            return false; // still within click slop — keep waiting
        }

        _pendingDragRestore = false;
        RestoreForDrag(pt);

        // Re-send the caption press: the window is no longer maximized, so this
        // time it falls through to DefWindowProc's move loop (modal until drop).
        NativeMethods.SendMessage(_hwnd, NativeMethods.WM_NCLBUTTONDOWN,
            NativeMethods.HTCaption, PackXY(pt.X, pt.Y));
        return true;
    }

    void RestoreForDrag(NativeMethods.POINT cursor)
    {
        NativeMethods.GetWindowRect(_hwnd, out var windowRect);

        // Restored size from the placement; only the SIZE is used, so the
        // workspace-vs-screen coordinate mismatch of NormalPosition is moot.
        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            Length = (uint)sizeof(NativeMethods.WINDOWPLACEMENT),
        };
        NativeMethods.GetWindowPlacement(_hwnd, ref placement);
        int restoredW = placement.NormalPosition.Width;
        int restoredH = placement.NormalPosition.Height;
        if (restoredW < 200 || restoredH < 200) { restoredW = 1400; restoredH = 900; }

        // Cursor stays at the same PROPORTION of the bar horizontally and the
        // same offset from the top — the grab point remains under the finger.
        double fractionX = windowRect.Width > 0
            ? Math.Clamp((cursor.X - windowRect.Left) / (double)windowRect.Width, 0.0, 1.0)
            : 0.5;
        int offsetY = Math.Clamp(cursor.Y - windowRect.Top, 0, 64);

        int x = cursor.X - (int)(restoredW * fractionX);
        int y = cursor.Y - offsetY;

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, restoredW, restoredH,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Screen point → lParam packing (signed shorts survive multi-monitor
    /// coordinates left of / above the primary).</summary>
    static nint PackXY(int x, int y)
        => unchecked((nint)(uint)(((y & 0xFFFF) << 16) | (x & 0xFFFF)));

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }
}
