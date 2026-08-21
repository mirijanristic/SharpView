using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using GdiRectangle = System.Drawing.Rectangle;

namespace SharpView.Services;

/// <summary>
/// Decodes image files into raw 32bpp BGRA pixel data. Pure CPU work with no GPU
/// dependencies, so it is safe to call from any thread.
/// </summary>
/// <remarks>
/// Decoding uses WIC (<see cref="WicDecoder"/>) when available: several times
/// faster than GDI+, thumbnails of large JPEGs decode directly at reduced
/// resolution (native codec scaling), and WebP/HEIC become readable when the
/// Windows codec extensions are installed. If WIC fails for any reason, the
/// original GDI+ (System.Drawing) path below is used automatically, so behavior
/// can only improve, never regress.
/// Camera RAW files (<see cref="RawDecoder"/>) are routed by extension to
/// LibRaw before that chain; if LibRaw is unavailable or fails, the same
/// WIC→GDI+ fallback runs (WIC can still succeed when the Microsoft "Raw Image
/// Extension" happens to be installed).
/// BGRA output matches <see cref="Core.DeviceResources.TextureFormat"/>, so pixels
/// are uploaded with straight memcpy — no per-pixel channel swizzle.
/// </remarks>
static unsafe class ImageDecoder
{
    /// <summary>True when .webp is decodable on this machine (WIC + "WebP Image Extensions").</summary>
    public static bool SupportsWebp => WicDecoder.HasWebp;

    /// <summary>True when .heic/.heif is decodable on this machine (WIC + "HEIF Image Extensions").</summary>
    public static bool SupportsHeif => WicDecoder.HasHeif;

    /// <summary>True when camera RAW files are decodable (bundled LibRaw dll loaded).</summary>
    public static bool SupportsRaw => RawDecoder.IsAvailable;

    /// <summary>RAW extensions handled by <see cref="RawDecoder"/> (currently .nef).</summary>
    public static IReadOnlyList<string> RawExtensions => RawDecoder.Extensions;

    static bool IsRawFile(string path)
        => RawDecoder.IsAvailable && RawDecoder.HandlesExtension(Path.GetExtension(path));

    /// <summary>
    /// Decodes an image file into tightly packed BGRA pixel bytes. Returns dimensions
    /// via out params. Optionally resizes to fit within <paramref name="maxDimension"/>.
    /// <paramref name="lowQuality"/> uses cheaper scaling (fast previews).
    /// </summary>
    public static byte[] DecodeToBgra(string path, out int width, out int height,
                                      int maxDimension = 0, bool lowQuality = false)
    {
        if (IsRawFile(path))
        {
            try
            {
                return RawDecoder.DecodeToBgra(path, out width, out height,
                                               maxDimension, lowQuality);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageDecoder] LibRaw failed for '{path}', falling back: {ex.Message}");
            }
        }

        if (WicDecoder.IsAvailable)
        {
            try
            {
                return WicDecoder.DecodeToBgra(path, out width, out height, maxDimension, lowQuality);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageDecoder] WIC failed for '{path}', falling back to GDI+: {ex.Message}");
            }
        }

        return GdiDecodeToBgra(path, out width, out height, maxDimension, lowQuality);
    }

    /// <summary>Provides the destination for a decode: given the final pixel
    /// dimensions, returns a writable BGRA buffer pointer and its row pitch
    /// (≥ width×4; total capacity RowPitch × height). May be invoked AGAIN if a
    /// codec fails mid-decode and the fallback chain retries — each invocation
    /// supersedes the previous destination, which the provider should release.</summary>
    public delegate (IntPtr Destination, uint RowPitch) BgraDestinationProvider(int width, int height);

    /// <summary>
    /// Decode an image file DIRECTLY into caller-provided memory (typically a
    /// mapped GPU upload heap) instead of a managed byte[]. The WIC path writes
    /// straight from the format converter — no intermediate array exists at
    /// all; the RAW and GDI+ fallbacks still decode to a temp array (orientation
    /// transforms and LockBits need readable memory, and the destination is
    /// write-combined) and copy once, on THIS thread. Full resolution only —
    /// the main-image path never scales.
    /// </summary>
    public static void DecodeToBgraDestination(string path, BgraDestinationProvider getDestination,
                                               out int width, out int height)
    {
        if (IsRawFile(path))
        {
            try
            {
                byte[] raw = RawDecoder.DecodeToBgra(path, out width, out height, 0, false);
                var (rawDst, rawPitch) = getDestination(width, height);
                CopyRowsTo(raw, width, height, rawDst, rawPitch);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageDecoder] LibRaw failed for '{path}', falling back: {ex.Message}");
            }
        }

        if (WicDecoder.IsAvailable)
        {
            try
            {
                WicDecoder.DecodeToBgraDestination(path, getDestination, out width, out height);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageDecoder] WIC failed for '{path}', falling back to GDI+: {ex.Message}");
            }
        }

        byte[] pixels = GdiDecodeToBgra(path, out width, out height, 0, false);
        var (gdiDst, gdiPitch) = getDestination(width, height);
        CopyRowsTo(pixels, width, height, gdiDst, gdiPitch);
    }

    /// <summary>Row-copy tightly packed BGRA into pitched destination memory —
    /// sequential writes only, matching a write-combined upload heap.</summary>
    static void CopyRowsTo(byte[] pixels, int width, int height,
                           IntPtr destination, uint rowPitch)
    {
        int tightPitch = width * 4;
        fixed (byte* src = pixels)
        {
            byte* dst = (byte*)destination;
            if (rowPitch == (uint)tightPitch)
            {
                Unsafe.CopyBlock(dst, src, (uint)(tightPitch * height));
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    Unsafe.CopyBlock(
                        dst + y * (long)rowPitch,
                        src + y * (long)tightPitch,
                        (uint)tightPitch);
                }
            }
        }
    }

    /// <summary>
    /// Decode an image straight into an exactly <paramref name="size"/>×<paramref name="size"/>
    /// center-cropped ("cover") BGRA square — the thumbnail path. WIC decodes with
    /// Fant prefiltering (clean downscales); the GDI+ fallback uses bicubic.
    /// Cropped, not stretched, so nothing is distorted; smaller sources are scaled
    /// up to fill the square so the thumbnail grid stays uniform.
    /// </summary>
    public static byte[] DecodeSquareBgra(string path, int size)
    {
        if (IsRawFile(path))
        {
            try
            {
                return RawDecoder.DecodeSquareBgra(path, size);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageDecoder] LibRaw square decode failed for '{path}', falling back: {ex.Message}");
            }
        }

        if (WicDecoder.IsAvailable)
        {
            try
            {
                return WicDecoder.DecodeSquareBgra(path, size);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageDecoder] WIC square decode failed for '{path}', falling back to GDI+: {ex.Message}");
            }
        }

        return GdiDecodeSquareBgra(path, size);
    }

    static byte[] GdiDecodeSquareBgra(string path, int size)
    {
        using var original = new Bitmap(path);

        // Centered source square (side = the shorter dimension), drawn to fill the
        // whole size×size destination — same cover-crop the WIC path produces.
        int side = Math.Min(original.Width, original.Height);
        int srcX = (original.Width - side) / 2;
        int srcY = (original.Height - side) / 2;

        using var bmp = new Bitmap(size, size, GdiPixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.DrawImage(original,
                new GdiRectangle(0, 0, size, size),
                srcX, srcY, side, side, GraphicsUnit.Pixel);
        }

        int rowPitch = size * 4;
        byte[] pixels = new byte[rowPitch * size];

        var bits = bmp.LockBits(new GdiRectangle(0, 0, size, size),
            ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
        byte* src = (byte*)bits.Scan0;
        fixed (byte* dst = pixels)
        {
            for (int y = 0; y < size; y++)
            {
                Unsafe.CopyBlock(
                    dst + (long)y * rowPitch,
                    src + (long)y * bits.Stride,
                    (uint)rowPitch);
            }
        }
        bmp.UnlockBits(bits);
        return pixels;
    }

    // ─── GDI+ fallback (original path) ────────────────────────────────

    static byte[] GdiDecodeToBgra(string path, out int width, out int height,
                                  int maxDimension, bool lowQuality)
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
                    // Bicubic + high-quality pixel offset: properly prefiltered
                    // downscaling. Plain bilinear only samples 2×2 source pixels,
                    // so at large ratios it skips most of them and aliases badly.
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                }
                g.DrawImage(original, 0, 0, nw, nh);
            }
        }
        else if (!CanLockAs32Bpp(original.PixelFormat))
        {
            // Exotic source formats (indexed, 16bpp, CMYK, ...) — normalize with one copy.
            bmp = new Bitmap(original);
            ownsBmp = true;
        }
        // else: LockBits converts 24/32bpp sources to 32bppArgb in place, so no
        // extra full-image copy is needed.

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
    private static bool CanLockAs32Bpp(GdiPixelFormat format) => format
        is GdiPixelFormat.Format32bppArgb
        or GdiPixelFormat.Format32bppPArgb
        or GdiPixelFormat.Format32bppRgb
        or GdiPixelFormat.Format24bppRgb;
}
