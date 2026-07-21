using System.Runtime.InteropServices;

namespace SharpView.Platform;

/// <summary>
/// DWM-based window styling: dark title bar (Windows 10 1809+ / Windows 11).
/// The calls fail harmlessly on older Windows versions, so no OS checks are needed.
/// </summary>
/// <remarks>
/// Per-pixel window transparency is now handled by the presentation path itself:
/// <c>DeviceResources</c> creates a composition swap chain (premultiplied alpha)
/// hosted in a DirectComposition visual tree, and <c>ViewerForm</c> drops the GDI
/// redirection surface via <c>WS_EX_NOREDIRECTIONBITMAP</c>. DWM then blends every
/// rendered pixel with the desktop behind the window using the alpha the shader
/// writes.
/// The Mica caption backdrop that used to be applied here was removed on purpose:
/// a DWM system backdrop is drawn <b>behind the window's content</b>, so it would
/// fill all translucent pixels with opaque Mica material and replace the
/// see-through-to-desktop effect. Do not re-add DWMWA_SYSTEMBACKDROP_TYPE while
/// the transparent presentation path is in use.
/// </remarks>
internal static class WindowStyling
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Win10 20H1+ / Win11
    const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Win10 1809–1909

    /// <summary>
    /// Dark title bar, matching the app's dark UI. A no-op while the window is
    /// borderless; kept so a framed-window fallback looks right immediately.
    /// Call once after the window handle exists (before the form is shown).
    /// </summary>
    public static void ApplyDarkStyle(IntPtr hwnd)
    {
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
