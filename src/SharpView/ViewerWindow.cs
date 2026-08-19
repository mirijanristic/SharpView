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

    public IntPtr Handle => _hwnd;

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

    /// <summary>The user asked to close (Alt+F4 / system menu). The app decides;
    /// the window is destroyed in <see cref="Dispose"/>.</summary>
    public Action? CloseRequested;

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

    /// <summary>Cursor position in client pixels + whether it is inside the client
    /// area right now (polled by the top bar's hover logic every frame).</summary>
    public void GetCursorClientPosition(out int x, out int y, out bool insideClient)
    {
        NativeMethods.GetCursorPos(out var pt);
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
            case NativeMethods.WM_NCHITTEST when HitTestHandler is not null:
            {
                // lParam packs SCREEN coords as signed shorts (monitors left of /
                // above the primary yield negative values).
                var pt = new NativeMethods.POINT { X = LoWordX(lParam), Y = HiWordY(lParam) };
                NativeMethods.ScreenToClient(hwnd, ref pt);
                int hit = HitTestHandler(pt.X, pt.Y);
                if (hit != 0) return hit;
                break; // 0 → default handling
            }

            case NativeMethods.WM_NCMOUSEMOVE:
                if (_pendingDragRestore && TryStartDragRestore()) return 0;
                NonClientMouseMove?.Invoke();
                break;

            case NativeMethods.WM_NCLBUTTONDOWN:
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

            case NativeMethods.WM_KEYDOWN:
                if (KeyHandler?.Invoke((int)wParam) == true) return 0;
                break; // unhandled → DefWindowProc (lets Alt+F4 & friends work)

            case NativeMethods.WM_LBUTTONDOWN:
                // Explicit capture: keep receiving WM_MOUSEMOVE while a pan drags
                // the cursor outside the window (WinForms captured implicitly).
                NativeMethods.SetCapture(hwnd);
                MouseDown?.Invoke(LoWordX(lParam), HiWordY(lParam));
                return 0;

            case NativeMethods.WM_LBUTTONUP:
                NativeMethods.ReleaseCapture();
                MouseUp?.Invoke(LoWordX(lParam), HiWordY(lParam));
                return 0;

            case NativeMethods.WM_LBUTTONDBLCLK:
                MouseDoubleClick?.Invoke(LoWordX(lParam), HiWordY(lParam));
                return 0;

            case NativeMethods.WM_MOUSEMOVE:
                // A fast caption drag can cross into the client area before the
                // threshold trips — the pending restore must still fire.
                if (_pendingDragRestore && TryStartDragRestore()) return 0;
                MouseMove?.Invoke(LoWordX(lParam), HiWordY(lParam));
                return 0;

            case NativeMethods.WM_MOUSEWHEEL:
            {
                // Wheel coords are SCREEN pixels (unlike the other mouse messages).
                var pt = new NativeMethods.POINT { X = LoWordX(lParam), Y = HiWordY(lParam) };
                NativeMethods.ScreenToClient(hwnd, ref pt);
                int delta = unchecked((short)(((ulong)wParam >> 16) & 0xFFFF));
                MouseWheel?.Invoke(pt.X, pt.Y, delta);
                return 0;
            }

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

            case NativeMethods.WM_SIZE:
            case NativeMethods.WM_EXITSIZEMOVE:
                // WM_SIZE covers maximize/restore/DPI moves; EXITSIZEMOVE is a
                // cheap safety net after a native move loop (same-size resizes
                // dedupe in DeviceResources anyway).
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
