using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using GdiRectangle = System.Drawing.Rectangle;

namespace SharpView.Services;

/// <summary>
/// Decodes image files into raw 32bpp BGRA pixel data using GDI+ (System.Drawing).
/// Pure CPU work with no GPU dependencies, so it is safe to call from any thread.
/// </summary>
/// <remarks>
/// BGRA is GDI+'s native 32bpp memory layout, so no per-pixel channel swizzle is
/// needed — rows are copied with straight memcpy and uploaded into
/// <c>B8G8R8A8_UNorm</c> textures (see <see cref="Core.DeviceResources.TextureFormat"/>).
/// This removed the old per-pixel BGRA→RGBA loop, which dominated decode time for
/// large images.
/// </remarks>
static unsafe class ImageDecoder
{
    /// <summary>
    /// Decodes an image file into tightly packed BGRA pixel bytes. Returns dimensions
    /// via out params. Optionally resizes to fit within <paramref name="maxDimension"/>.
    /// <paramref name="lowQuality"/> uses nearest-neighbor scaling (fast, for thumbnails).
    /// </summary>
    public static byte[] DecodeToBgra(string path, out int width, out int height,
                                      int maxDimension = 0, bool lowQuality = false)
    {
        using var original = new Bitmap(path);
        Bitmap bmp = original;
        bool ownsBmp = false;

        if (maxDimension > 0 && (original.Width > maxDimension || original.Height > maxDimension))
        {
            float scale = Math.Min((float)maxDimension / original.Width,
                                   (float)maxDimension / original.Height);
            int nw = Math.Max(1, (int)(original.Width * scale));
            int nh = Math.Max(1, (int)(original.Height * scale));

            bmp = new Bitmap(nw, nh, GdiPixelFormat.Format32bppArgb);
            ownsBmp = true;
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
        else if (!CanLockAs32bpp(original.PixelFormat))
        {
            // Exotic source formats (indexed, 16bpp, CMYK, ...) — normalize with one copy.
            bmp = new Bitmap(original);
            ownsBmp = true;
        }
        // else: LockBits converts 24/32bpp sources to 32bppArgb in place, so the
        // previous unconditional "new Bitmap(original)" full-image copy is avoided.

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
                if (bits.Stride == rowPitch)
                {
                    // Tightly packed — one big copy. Format32bppArgb memory order is
                    // B,G,R,A, which is exactly the layout the GPU texture expects.
                    Unsafe.CopyBlock(dst, src, (uint)(rowPitch * height));
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        Unsafe.CopyBlock(
                            dst + (long)y * rowPitch,
                            src + (long)y * bits.Stride, // handles negative stride too
                            (uint)rowPitch);
                    }
                }
            }
            bmp.UnlockBits(bits);
            return pixels;
        }
        finally
        {
            if (ownsBmp) bmp.Dispose();
        }
    }

    /// <summary>Formats GDI+ can convert to 32bppArgb directly inside LockBits.</summary>
    static bool CanLockAs32bpp(GdiPixelFormat format) => format
        is GdiPixelFormat.Format32bppArgb
        or GdiPixelFormat.Format32bppPArgb
        or GdiPixelFormat.Format32bppRgb
        or GdiPixelFormat.Format24bppRgb;
}
