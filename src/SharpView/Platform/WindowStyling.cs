using System.Runtime.InteropServices;

namespace SharpView.Platform;

/// <summary>
/// DWM-based window styling: dark title bar (Windows 10 1809+ / Windows 11) and
/// the Mica caption backdrop (Windows 11 22H2+). All calls fail harmlessly on
/// older Windows versions, so no OS checks are needed.
/// </summary>
/// <remarks>
/// Note on transparency: true per-pixel window transparency is NOT possible with
/// the current presentation path. Flip-model HWND swap chains (FlipDiscard) are
/// opaque and incompatible with layered windows, so <c>Form.Opacity</c> or
/// <c>TransparencyKey</c> would break or be ignored. A see-through window would
/// require moving presentation to DirectComposition
/// (CreateSwapChainForComposition + premultiplied alpha + a DComp visual tree).
/// The dark title bar and Mica caption below give the modern look without that
/// rewrite.
/// </remarks>
internal static class WindowStyling
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Win10 20H1+ / Win11
    const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Win10 1809–1909
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;         // Win11 22H2+
    const int DWMSBT_MAINWINDOW = 2;                  // Mica

    /// <summary>
    /// Dark title bar + Mica caption backdrop, matching the app's dark UI.
    /// Call once after the window handle exists (before the form is shown).
    /// </summary>
    public static void ApplyDarkStyle(IntPtr hwnd)
    {
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

        // Mica is visible only on the caption here — the client area is fully
        // covered by the (opaque) swap chain. No-op before Windows 11 22H2.
        int backdrop = DWMSBT_MAINWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
