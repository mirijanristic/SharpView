using System.Runtime.InteropServices;

namespace SharpView.Services;

/// <summary>
/// LibRaw-based decoding of camera RAW files. Policy is preview-first: the
/// full-resolution JPEG the camera embeds in the RAW is extracted (milliseconds —
/// only the JPEG blob is read from disk, the sensor data is never touched) and
/// decoded through the existing WIC pipeline. A real demosaic runs only as a
/// fallback when no adequately sized preview exists — rare in practice, since
/// every Nikon body embeds a full-size preview.
/// </summary>
/// <remarks>
/// <para>
/// The LibRaw layer itself is format-agnostic (LibRaw identifies files by
/// content, not extension), so enabling the next formats is mostly: add the
/// extension to <see cref="ExtensionList"/> + test. NEF is production format #1.
/// Planned next: <c>.nrw</c> (same Nikon pipeline, effectively free), <c>.dng</c>
/// (TIFF container — orientation reader already covers it; preview sizes vary by
/// producer, so test converted files too), <c>.raf</c> (Fuji: NOT a TIFF, so
/// orientation must come from the embedded JPEG's EXIF, and the X-Trans demosaic
/// fallback is markedly slower than Bayer).
/// </para>
/// <para>
/// Orientation: embedded previews are stored in sensor orientation, so the
/// preview path reads the TIFF IFD0 Orientation tag (<see cref="TiffOrientation"/>)
/// and rotates the pixels (<see cref="PixelOrientation"/>). The demosaic path
/// needs neither — LibRaw applies the camera flip itself.
/// </para>
/// <para>
/// Every decode creates its own LibRaw handle, and the shipped dll is the
/// thread-safe "_r" build, so concurrent decodes (main + prefetch + thumbnails)
/// are safe by construction.
/// </para>
/// </remarks>
static unsafe class RawDecoder
{
    // Production format #1; see the class remarks for the rollout plan.
    static readonly string[] ExtensionList = { ".nef" };

    /// <summary>Extensions routed to this decoder (lower-case, with leading dot).</summary>
    public static IReadOnlyList<string> Extensions => ExtensionList;

    /// <summary>
    /// True when the LibRaw native dll loaded AND WIC is available. WIC is a hard
    /// requirement here: the preview JPEG bytes and the thumbnail cover-crop both
    /// go through it (never an issue on stock Windows — the guard exists so a
    /// broken deployment degrades to the WIC/GDI+ chain instead of half-working).
    /// </summary>
    public static bool IsAvailable { get; } = ProbeLibRaw() && WicDecoder.IsAvailable;

    static bool ProbeLibRaw()
    {
        try
        {
            _ = LibRawNative.VersionNumber();
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    public static bool HandlesExtension(string? extension)
        => extension is not null
        && ExtensionList.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes a RAW file to tightly packed 32bpp BGRA. <paramref name="maxDimension"/>
    /// and <paramref name="lowQuality"/> apply to the preview path (WIC scaled
    /// decode); the demosaic fallback always produces full resolution.
    /// </summary>
    public static byte[] DecodeToBgra(string path, out int width, out int height,
                                      int maxDimension = 0, bool lowQuality = false)
    {
        IntPtr handle = LibRawNative.Init(0);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("libraw_init failed (out of memory?).");
        try
        {
            Check(LibRawNative.OpenFile(handle, path), path, "open");

            // Output dimensions are valid right after open (identify stage) and
            // gate the preview: a tiny 160 px thumb must never become the
            // fullscreen image.
            int sensorW = LibRawNative.GetWidth(handle);
            int sensorH = LibRawNative.GetHeight(handle);

            if (TryReadThumb(handle, out RawThumb thumb))
            {
                byte[]? preview = TryDecodePreviewToBgra(thumb, sensorW, sensorH,
                    maxDimension, lowQuality, out int pw, out int ph);
                if (preview is not null)
                {
                    int orientation = TiffOrientation.Read(path);
                    preview = PixelOrientation.Apply(preview, ref pw, ref ph, orientation);
                    width = pw;
                    height = ph;
                    return preview;
                }
            }

            // No usable preview → real demosaic (already correctly oriented).
            return Demosaic(handle, path, out width, out height);
        }
        finally
        {
            LibRawNative.Close(handle);
        }
    }

    /// <summary>
    /// Decodes a RAW file straight into an exactly <paramref name="size"/>×<paramref name="size"/>
    /// center-cropped ("cover") BGRA square — the thumbnail-strip path. Built on
    /// the embedded preview whenever one exists; WIC's native JPEG scaled decode
    /// makes even a full-size 45 MP preview cheap to shrink to 55 px.
    /// </summary>
    public static byte[] DecodeSquareBgra(string path, int size)
    {
        IntPtr handle = LibRawNative.Init(0);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("libraw_init failed (out of memory?).");
        try
        {
            Check(LibRawNative.OpenFile(handle, path), path, "open");

            if (TryReadThumb(handle, out RawThumb thumb))
            {
                // Rotating the finished square is equivalent to rotating first and
                // cropping after: the cover scale uses both dimensions symmetrically
                // and the crop is centered, so the two operations commute — and the
                // post-rotation touches 55×55 pixels instead of a whole preview.
                if (thumb.Type == LibRawNative.ImageJpeg)
                {
                    byte[] square = WicDecoder.DecodeSquareBgra(thumb.Data, size);
                    int sw = size, sh = size;
                    return PixelOrientation.Apply(square, ref sw, ref sh,
                        TiffOrientation.Read(path));
                }

                if (IsUsableRgbBitmap(thumb))
                {
                    byte[] bgra = RgbToBgra(thumb.Data, thumb.Width, thumb.Height);
                    byte[] square = WicDecoder.SquareFromBgraPixels(
                        bgra, thumb.Width, thumb.Height, size);
                    int sw = size, sh = size;
                    return PixelOrientation.Apply(square, ref sw, ref sh,
                        TiffOrientation.Read(path));
                }
            }

            // Pathological case (no preview at all): demosaic, then cover-crop.
            // Expensive, but correct — and the thumbnail cache keeps the result.
            byte[] full = Demosaic(handle, path, out int fullW, out int fullH);
            return WicDecoder.SquareFromBgraPixels(full, fullW, fullH, size);
        }
        finally
        {
            LibRawNative.Close(handle);
        }
    }

    // ─── Embedded preview ──────────────────────────────────────────────

    readonly record struct RawThumb(int Type, int Width, int Height,
                                    int Colors, int Bits, byte[] Data);

    /// <summary>
    /// Extracts the largest embedded thumbnail into a managed buffer. False on
    /// any failure (no thumbnail, unsupported kind, ...) — the caller then
    /// decides between the bitmap-preview and demosaic routes.
    /// </summary>
    static bool TryReadThumb(IntPtr handle, out RawThumb thumb)
    {
        thumb = default;

        if (LibRawNative.UnpackThumb(handle) != LibRawNative.Success)
            return false; // NO_THUMBNAIL / UNSUPPORTED_THUMBNAIL → fallback

        IntPtr image = LibRawNative.MakeMemThumb(handle, out int error);
        if (image == IntPtr.Zero) return false;
        try
        {
            if (error != LibRawNative.Success) return false;

            // Blittable header — direct pointer read, no runtime marshalling (AOT-clean).
            var header = *(LibRawNative.ProcessedImageHeader*)image;
            if (header.DataSize == 0 || header.DataSize > int.MaxValue) return false;

            byte[] data = new byte[header.DataSize];
            Marshal.Copy(image + LibRawNative.ProcessedDataOffset, data, 0, (int)header.DataSize);

            thumb = new RawThumb(header.Type, header.Width, header.Height,
                                 header.Colors, header.Bits, data);
            return true;
        }
        finally
        {
            LibRawNative.ClearMem(image);
        }
    }

    /// <summary>
    /// Decodes the extracted thumbnail to BGRA if it is large enough to stand in
    /// for the real image; null otherwise (→ demosaic). Not oriented yet.
    /// </summary>
    static byte[]? TryDecodePreviewToBgra(in RawThumb thumb, int sensorW, int sensorH,
                                          int maxDimension, bool lowQuality,
                                          out int width, out int height)
    {
        width = height = 0;

        if (thumb.Type == LibRawNative.ImageJpeg)
        {
            // Size gate BEFORE the pixel decode: reading JPEG dimensions is a
            // header parse, so rejecting a small preview costs ~nothing.
            if (!WicDecoder.TryGetImageSize(thumb.Data, out int jpegW, out int jpegH))
                return null;
            if (!PreviewLargeEnough(jpegW, jpegH, sensorW, sensorH))
                return null;
            try
            {
                return WicDecoder.DecodeToBgra(thumb.Data, out width, out height,
                                               maxDimension, lowQuality);
            }
            catch
            {
                return null; // corrupt embedded JPEG → demosaic still saves the day
            }
        }

        if (IsUsableRgbBitmap(thumb)
            && PreviewLargeEnough(thumb.Width, thumb.Height, sensorW, sensorH))
        {
            width = thumb.Width;
            height = thumb.Height;
            return RgbToBgra(thumb.Data, thumb.Width, thumb.Height);
        }

        return null;
    }

    /// <summary>Uncompressed 8-bit RGB thumbnail with self-consistent dimensions.</summary>
    static bool IsUsableRgbBitmap(in RawThumb thumb)
        => thumb.Type == LibRawNative.ImageBitmap
        && thumb.Colors == 3 && thumb.Bits == 8
        && thumb.Width > 0 && thumb.Height > 0
        && (long)thumb.Width * thumb.Height * 3 <= thumb.Data.LongLength;

    /// <summary>
    /// A preview qualifies as the main image when it carries at least half the
    /// sensor's linear resolution (pixel count within 4×) — visually identical to
    /// a demosaic at fit-to-window sizes, and honest enough at 1:1. When the
    /// sensor size is unknown, a 1024 px floor keeps obvious thumbnails out.
    /// </summary>
    static bool PreviewLargeEnough(int previewW, int previewH, int sensorW, int sensorH)
    {
        if (previewW <= 0 || previewH <= 0) return false;
        if (sensorW <= 0 || sensorH <= 0) return Math.Max(previewW, previewH) >= 1024;
        return (long)previewW * previewH * 4 >= (long)sensorW * sensorH;
    }

    // ─── Demosaic fallback ─────────────────────────────────────────────

    /// <summary>
    /// Full LibRaw processing: unpack → camera white balance → demosaic → 8-bit
    /// sRGB RGB → BGRA. LibRaw applies the orientation flip during processing,
    /// so the output is already upright.
    /// </summary>
    static byte[] Demosaic(IntPtr handle, string path, out int width, out int height)
    {
        Check(LibRawNative.Unpack(handle), path, "unpack");

        // The C API has no use_camera_wb setter; copying the camera-recorded
        // multipliers into user_mul is the documented equivalent (user_mul is
        // used whenever user_mul[0] > 0). Without this, dcraw defaults to
        // daylight WB and indoor shots come out visibly warm.
        if (LibRawNative.GetCamMul(handle, 0) > 0.0001f)
        {
            for (int i = 0; i < 4; i++)
                LibRawNative.SetUserMul(handle, i, LibRawNative.GetCamMul(handle, i));
        }

        Check(LibRawNative.DcrawProcess(handle), path, "process");

        IntPtr image = LibRawNative.MakeMemImage(handle, out int error);
        if (image == IntPtr.Zero)
            throw new InvalidOperationException(
                $"LibRaw make_mem_image failed for '{path}': {LibRawNative.StrError(error)} ({error})");
        try
        {
            if (error != LibRawNative.Success)
                throw new InvalidOperationException(
                    $"LibRaw make_mem_image failed for '{path}': {LibRawNative.StrError(error)} ({error})");

            // Blittable header — direct pointer read, no runtime marshalling (AOT-clean).
            var header = *(LibRawNative.ProcessedImageHeader*)image;
            if (header.Type != LibRawNative.ImageBitmap
                || header.Colors != 3 || header.Bits != 8
                || header.Width == 0 || header.Height == 0)
            {
                throw new NotSupportedException(
                    $"Unexpected LibRaw output for '{path}': type={header.Type}, " +
                    $"colors={header.Colors}, bits={header.Bits}.");
            }

            long expected = (long)header.Width * header.Height * 3;
            if (expected > header.DataSize)
                throw new InvalidOperationException(
                    $"LibRaw output truncated for '{path}' ({header.DataSize} < {expected} bytes).");

            width = header.Width;
            height = header.Height;
            return SwizzleRgbToBgra((byte*)(image + LibRawNative.ProcessedDataOffset),
                                    width, height);
        }
        finally
        {
            LibRawNative.ClearMem(image);
        }
    }

    // ─── Pixel helpers ─────────────────────────────────────────────────

    static byte[] RgbToBgra(byte[] rgb, int width, int height)
    {
        fixed (byte* src = rgb)
            return SwizzleRgbToBgra(src, width, height);
    }

    /// <summary>Interleaved 8-bit RGB → tightly packed BGRA (alpha 255) in one pass.</summary>
    static byte[] SwizzleRgbToBgra(byte* src, int width, int height)
    {
        long pixelCount = (long)width * height;
        byte[] bgra = new byte[checked((int)(pixelCount * 4))];
        fixed (byte* dstBase = bgra)
        {
            byte* s = src;
            byte* d = dstBase;
            for (long i = 0; i < pixelCount; i++)
            {
                d[0] = s[2]; // B
                d[1] = s[1]; // G
                d[2] = s[0]; // R
                d[3] = 255;  // A (opaque — matches the rest of the BGRA pipeline)
                s += 3;
                d += 4;
            }
        }
        return bgra;
    }

    static void Check(int result, string path, string stage)
    {
        if (result != LibRawNative.Success)
            throw new InvalidOperationException(
                $"LibRaw {stage} failed for '{path}': {LibRawNative.StrError(result)} ({result})");
    }
}
