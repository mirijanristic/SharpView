using SharpView.Platform;
using SharpView.Services;

namespace SharpView;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Per-Monitor V2 DPI: primarily declared in app.manifest; this call is a
        // harmless fallback and must run before ANY window or dialog is created.
        NativeMethods.EnablePerMonitorDpiV2();

        try
        {
            if (HasFlag(args, "--register"))
            {
                FileAssociations.Register();
                NativeMethods.MessageBox(IntPtr.Zero,
                    "SharpView is now registered for image files.\n\n" +
                    "To make it the default viewer: right-click an image → " +
                    "Open with → Choose another app → SharpView → \"Always\".",
                    "SharpView", NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
                return 0;
            }

            if (HasFlag(args, "--unregister"))
            {
                FileAssociations.Unregister();
                NativeMethods.MessageBox(IntPtr.Zero,
                    "SharpView file associations removed.",
                    "SharpView", NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
                return 0;
            }

            string? imagePath = args.FirstOrDefault(File.Exists);

            if (imagePath is null)
            {
                string exts = "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff";
                if (ImageDecoder.SupportsWebp) exts += ";*.webp";
                if (ImageDecoder.SupportsHeif) exts += ";*.heic;*.heif";
                if (ImageDecoder.SupportsRaw)
                    exts += ";" + string.Join(';', ImageDecoder.RawExtensions.Select(e => "*" + e));

                // Win32 filter format: display/pattern pairs separated by '\0'.
                imagePath = NativeMethods.ShowOpenFileDialog(
                    "Select an image to view",
                    $"Image Files\0{exts}\0All Files\0*.*\0");
                if (imagePath is null) return 0; // cancelled
            }

            using var app = new ViewerApp(imagePath);
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            NativeMethods.MessageBox(IntPtr.Zero, ex.ToString(),
                "SharpView — Fatal Error", NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
            return 1;
        }
    }

    static bool HasFlag(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
