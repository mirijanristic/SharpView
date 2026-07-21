using System.Drawing.Imaging;

using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using GdiRectangle = System.Drawing.Rectangle;

namespace SharpView.Services;

/// <summary>
/// Decodes image files into raw RGBA pixel data using GDI+ (System.Drawing).
/// Pure CPU work with no GPU dependencies, so it is safe to call from any thread.
/// </summary>
static unsafe class ImageDecoder
{
    /// <summary>
    /// Decodes an image file into RGBA pixel bytes. Returns dimensions via out params.
    /// Optionally resizes to fit within <paramref name="maxDimension"/>.
    /// <paramref name="lowQuality"/> uses nearest-neighbor scaling (fast, for thumbnails).
    /// </summary>
    public static byte[] DecodeToRgba(string path, out int width, out int height,
                                      int maxDimension = 0, bool lowQuality = false)
    {
        using var original = new Bitmap(path);
        Bitmap bmp;

        if (maxDimension > 0 && (original.Width > maxDimension || original.Height > maxDimension))
        {
            float scale = Math.Min((float)maxDimension / original.Width,
                                   (float)maxDimension / original.Height);
            int nw = Math.Max(1, (int)(original.Width * scale));
            int nh = Math.Max(1, (int)(original.Height * scale));

            bmp = new Bitmap(nw, nh, GdiPixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                if (lowQuality)
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                }
                else
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                }
                g.DrawImage(original, 0, 0, nw, nh);
            }
        }
        else
        {
            bmp = new Bitmap(original); // copy to guarantee 32bpp ARGB access
        }

        try
        {
            width = bmp.Width;
            height = bmp.Height;
            int rowPitch = width * 4;
            byte[] pixels = new byte[rowPitch * height];

            var rect = new GdiRectangle(0, 0, width, height);
            var bits = bmp.LockBits(rect, ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);

            byte* src = (byte*)bits.Scan0;
            fixed (byte* dst = pixels)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* sRow = src + y * bits.Stride;
                    byte* dRow = dst + y * rowPitch;
                    for (int x = 0; x < width; x++)
                    {
                        int o = x * 4;
                        dRow[o + 0] = sRow[o + 2]; // R ← B  (BGRA → RGBA)
                        dRow[o + 1] = sRow[o + 1]; // G
                        dRow[o + 2] = sRow[o + 0]; // B ← R
                        dRow[o + 3] = sRow[o + 3]; // A
                    }
                }
            }
            bmp.UnlockBits(bits);
            return pixels;
        }
        finally
        {
            if (bmp != original) bmp.Dispose();
        }
    }
}
