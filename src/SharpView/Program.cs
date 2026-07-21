using SharpView.Platform;
using SharpView.Services;

namespace SharpView;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Must run before ANY window or dialog is created
        // (applies HighDpiMode + visual styles from the .csproj).
        ApplicationConfiguration.Initialize();

        try
        {
            if (HasFlag(args, "--register"))
            {
                FileAssociations.Register();
                MessageBox.Show(
                    "SharpView is now registered for image files.\n\n" +
                    "To make it the default viewer: right-click an image → " +
                    "Open with → Choose another app → SharpView → \"Always\".",
                    "SharpView", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            if (HasFlag(args, "--unregister"))
            {
                FileAssociations.Unregister();
                MessageBox.Show("SharpView file associations removed.",
                    "SharpView", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            string? imagePath = args.FirstOrDefault(File.Exists);

            if (imagePath is null)
            {
                string exts = "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff";
                if (ImageDecoder.SupportsWebp) exts += ";*.webp";
                if (ImageDecoder.SupportsHeif) exts += ";*.heic;*.heif";

                using var ofd = new OpenFileDialog
                {
                    Title = "Select an image to view",
                    Filter = $"Image Files|{exts}|All Files|*.*",
                };
                if (ofd.ShowDialog() != DialogResult.OK) return 0;
                imagePath = ofd.FileName;
            }

            using var app = new ViewerApp(imagePath);
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "SharpView — Fatal Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static bool HasFlag(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
