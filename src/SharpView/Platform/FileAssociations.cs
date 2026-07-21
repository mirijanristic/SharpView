using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SharpView.Platform;

/// <summary>
/// Registers SharpView as an image-opening application for the current user
/// (HKCU — no administrator rights required).
/// </summary>
/// <remarks>
/// After <see cref="Register"/> runs, SharpView appears in Explorer's
/// "Open with" menu and in Settings → Default apps for the supported extensions.
/// Windows 10/11 deliberately prevents applications from silently making
/// themselves the default handler, so the user confirms once via
/// right-click → Open with → SharpView → "Always".
/// Note: the registration stores the current executable path; re-run
/// <c>SharpView.exe --register</c> if the application is moved.
/// </remarks>
internal static class FileAssociations
{
    const string ProgId = "SharpView.Image";
    const string AppName = "SharpView";
    const string CapabilitiesKeyPath = @"Software\SharpView\Capabilities";

    public static readonly string[] Extensions =
    {
        ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".tif", ".tiff"
    };

    public static void Register()
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the executable path.");

        // 1. ProgId that describes how to open an image with SharpView.
        using (var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            progIdKey.SetValue(null, "SharpView Image");
            using (var iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                iconKey.SetValue(null, $"\"{exePath}\",0");
            using (var cmdKey = progIdKey.CreateSubKey(@"shell\open\command"))
                cmdKey.SetValue(null, $"\"{exePath}\" \"%1\"");
        }

        // 2. Attach the ProgId to each supported extension ("Open with" list).
        foreach (string ext in Extensions)
        {
            using var extKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{ext}\OpenWithProgids");
            extKey.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // 3. Application capabilities → shows up in Settings → Default apps.
        using (var capKey = Registry.CurrentUser.CreateSubKey(CapabilitiesKeyPath))
        {
            capKey.SetValue("ApplicationName", AppName);
            capKey.SetValue("ApplicationDescription", "Fast GPU-accelerated image viewer");
            using var assocKey = capKey.CreateSubKey("FileAssociations");
            foreach (string ext in Extensions)
                assocKey.SetValue(ext, ProgId);
        }

        using (var regApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            regApps.SetValue(AppName, CapabilitiesKeyPath);

        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);

        foreach (string ext in Extensions)
        {
            using var extKey = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            extKey?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree(@"Software\SharpView", throwOnMissingSubKey: false);

        using (var regApps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            regApps?.DeleteValue(AppName, throwOnMissingValue: false);

        NotifyShell();
    }

    // Tell Explorer that file associations changed so menus refresh immediately.
    static void NotifyShell()
        => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

    const int SHCNE_ASSOCCHANGED = 0x08000000;
    const int SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
