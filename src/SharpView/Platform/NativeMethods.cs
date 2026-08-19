using System.Runtime.InteropServices;

namespace SharpView.Platform;

/// <summary>
/// Flat Win32 interop for the window shell: window class + message loop, mouse
/// capture and cursors, monitors, message box and the classic open-file dialog.
/// Everything is <c>[LibraryImport]</c> with blittable arguments (strings as
/// UTF-16), so the whole layer is Native-AOT- and trimming-clean by construction
/// — no runtime marshalling, no delegates, no COM.
/// </summary>
internal static unsafe partial class NativeMethods
{
    // ─── Window messages ───────────────────────────────────────────────

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_SETICON = 0x0080;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCMOUSEMOVE = 0x00A0;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_CAPTURECHANGED = 0x0215;
    public const uint WM_EXITSIZEMOVE = 0x0232;
    public const uint WM_DPICHANGED = 0x02E0;

    // ─── WM_NCHITTEST results ──────────────────────────────────────────

    /// <summary>Ordinary client area (mouse events reach us).</summary>
    public const int HTClient = 1;
    /// <summary>Caption — Windows handles dragging, snapping, drag-restore.</summary>
    public const int HTCaption = 2;

    // ─── Window styles ─────────────────────────────────────────────────

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_SYSMENU = 0x00080000;      // right-click system menu on the caption zone
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;

    /// <summary>No GDI redirection surface: every pixel (and its alpha) comes from
    /// the DirectComposition swap chain — this is what makes per-pixel window
    /// transparency possible. Same flag the WinForms shell used.</summary>
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint WS_EX_APPWINDOW = 0x00040000; // taskbar button for a WS_POPUP window

    public const uint CS_DBLCLKS = 0x0008; // deliver WM_LBUTTONDBLCLK

    // ─── ShowWindow / SetWindowPos ─────────────────────────────────────

    public const int SW_SHOWMAXIMIZED = 3;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    // ─── Cursors / icons ───────────────────────────────────────────────

    public const int IDC_ARROW = 32512;
    public const int IDC_SIZEALL = 32646;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;

    // ─── Virtual-key codes used by the viewer ──────────────────────────

    public const int VK_ESCAPE = 0x1B;
    public const int VK_END = 0x23;
    public const int VK_HOME = 0x24;
    public const int VK_LEFT = 0x25;
    public const int VK_RIGHT = 0x27;
    public const int VK_0 = 0x30;
    public const int VK_1 = 0x31;
    public const int VK_NUMPAD0 = 0x60;
    public const int VK_NUMPAD1 = 0x61;
    public const int VK_ADD = 0x6B;
    public const int VK_SUBTRACT = 0x6D;
    public const int VK_OEM_PLUS = 0xBB;
    public const int VK_OEM_MINUS = 0xBD;

    // ─── MessageBox ────────────────────────────────────────────────────

    public const uint MB_OK = 0x0;
    public const uint MB_ICONERROR = 0x10;
    public const uint MB_ICONWARNING = 0x30;
    public const uint MB_ICONINFORMATION = 0x40;

    // ─── Structs (all blittable) ───────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public POINT Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc; // unmanaged function pointer (UnmanagedCallersOnly)
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct OPENFILENAMEW
    {
        public uint lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public uint nMaxCustFilter;
        public uint nFilterIndex;
        public IntPtr lpstrFile;
        public uint nMaxFile;
        public IntPtr lpstrFileTitle;
        public uint nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public uint dwReserved;
        public uint FlagsEx;
    }

    // ─── user32: window class, window, message loop ────────────────────

    [LibraryImport("user32", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(ref WNDCLASSEXW wndClass);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hwnd, int cmdShow);

    [LibraryImport("user32", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out MSG msg, IntPtr hwnd,
        uint filterMin, uint filterMax, uint removeMsg); // removeMsg: 1 = PM_REMOVE

    [LibraryImport("user32", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG msg);

    [LibraryImport("user32", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in MSG msg);

    [LibraryImport("user32", EntryPoint = "PostQuitMessage")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(IntPtr hwnd, string text);

    [LibraryImport("user32", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(IntPtr hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    // ─── user32: state queries ─────────────────────────────────────────

    [LibraryImport("user32", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32", EntryPoint = "IsZoomed")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(IntPtr hwnd);

    [LibraryImport("user32", EntryPoint = "GetForegroundWindow")]
    public static partial IntPtr GetForegroundWindow();

    // ─── user32: mouse, cursor, capture ────────────────────────────────

    [LibraryImport("user32", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32", EntryPoint = "ScreenToClient")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [LibraryImport("user32", EntryPoint = "SetCapture")]
    public static partial IntPtr SetCapture(IntPtr hwnd);

    [LibraryImport("user32", EntryPoint = "ReleaseCapture")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32", EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [LibraryImport("user32", EntryPoint = "SetCursor")]
    public static partial IntPtr SetCursor(IntPtr cursor);

    // ─── user32: monitors ──────────────────────────────────────────────

    [LibraryImport("user32", EntryPoint = "MonitorFromPoint")]
    public static partial IntPtr MonitorFromPoint(POINT point, uint flags); // 2 = MONITOR_DEFAULTTONEAREST

    [LibraryImport("user32", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>Full bounds of the monitor containing the cursor — the same
    /// "CenterScreen picks the cursor's screen" rule WinForms used, kept so the
    /// startup swap chain is pre-sized for the screen the window will land on.</summary>
    public static RECT MonitorBoundsFromCursor()
    {
        GetCursorPos(out POINT pt);
        IntPtr monitor = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return info.rcMonitor;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }; // defensive default
    }

    // ─── user32: MessageBox / DPI ──────────────────────────────────────

    [LibraryImport("user32", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(IntPtr owner, string text, string caption, uint type);

    [LibraryImport("user32", EntryPoint = "SetProcessDpiAwarenessContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(IntPtr context);

    /// <summary>
    /// Per-Monitor V2 DPI awareness. The app.manifest is the primary source of
    /// this setting (and wins if present); this call is a harmless belt-and-braces
    /// fallback for builds where the manifest got lost. Must run before any window
    /// is created. No-ops (returns false) when already set — that is fine.
    /// </summary>
    public static void EnablePerMonitorDpiV2()
    {
        try { SetProcessDpiAwarenessContext((IntPtr)(-4) /* PER_MONITOR_AWARE_V2 */); }
        catch (EntryPointNotFoundException) { /* pre-1703 Windows 10 — accept system DPI */ }
    }

    // ─── kernel32 / shell32 ────────────────────────────────────────────

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string? moduleName);

    /// <summary>First icon embedded in a file — used to reuse the exe's own icon
    /// (from &lt;ApplicationIcon&gt;) for the title bar / Alt-Tab without System.Drawing.</summary>
    [LibraryImport("shell32", EntryPoint = "ExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr ExtractIcon(IntPtr instance, string exeFileName, int iconIndex);

    [LibraryImport("user32", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr icon);

    // ─── comdlg32: classic open-file dialog (zero COM — AOT-safe) ──────

    [LibraryImport("comdlg32", EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileName(ref OPENFILENAMEW ofn);

    const uint OFN_HIDEREADONLY = 0x00000004;
    const uint OFN_NOCHANGEDIR = 0x00000008;   // do NOT change the process CWD (GetOpenFileName default does!)
    const uint OFN_PATHMUSTEXIST = 0x00000800;
    const uint OFN_FILEMUSTEXIST = 0x00001000;
    const uint OFN_EXPLORER = 0x00080000;

    /// <summary>
    /// Classic Explorer-style open dialog via <c>GetOpenFileNameW</c> — the fully
    /// C-API, COM-free path, so nothing here can trip Native AOT. If the modern
    /// IFileOpenDialog look is ever wanted, the AOT-clean route is a
    /// <c>[GeneratedComInterface]</c> wrapper — swap inside this method only.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filterPairs">Win32 filter pairs separated by '\0', e.g.
    /// "Image Files\0*.png;*.jpg\0All Files\0*.*\0" (this method appends the
    /// required terminating '\0').</param>
    /// <returns>The selected full path, or null when the user cancelled.</returns>
    public static string? ShowOpenFileDialog(string title, string filterPairs)
    {
        char[] fileBuffer = new char[32768]; // roomy: long paths supported
        char[] filter = (filterPairs + "\0").ToCharArray();

        fixed (char* pFile = fileBuffer)
        fixed (char* pFilter = filter)
        fixed (char* pTitle = title)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                lpstrFilter = (IntPtr)pFilter,
                nFilterIndex = 1,
                lpstrFile = (IntPtr)pFile,
                nMaxFile = (uint)fileBuffer.Length,
                lpstrTitle = (IntPtr)pTitle,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST
                      | OFN_HIDEREADONLY | OFN_NOCHANGEDIR,
            };

            if (!GetOpenFileName(ref ofn)) return null; // cancel (or dialog error)
        }

        int length = Array.IndexOf(fileBuffer, '\0');
        if (length == 0) return null;
        return new string(fileBuffer, 0, length < 0 ? fileBuffer.Length : length);
    }
}
