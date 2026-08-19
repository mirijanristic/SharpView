using Vortice.Mathematics;
using Vortice.WIC;

using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace SharpView.Services;

/// <summary>
/// WIC (Windows Imaging Component) based image decoding. Considerably faster than
/// GDI+, and — crucially for thumbnails — supports scaled decode: the built-in
/// scaler, when attached directly to the decoder frame, lets codecs that implement
/// <c>IWICBitmapSourceTransform</c> (JPEG) decode straight to the requested size
/// via native DCT-domain scaling instead of decoding every pixel first.
/// Also provides runtime detection of optional codecs (WebP, HEIF/HEIC), and
/// decode-from-memory overloads used by the RAW path (embedded preview JPEGs
/// arrive as byte arrays, not files).
/// </summary>
/// <remarks>
/// A factory is created per call: WIC objects are not thread-safe, and decodes run
/// concurrently on the thread pool (main image, prefetch, thumbnails). Factory
/// creation is trivially cheap next to an actual image decode.
/// Memory-based decoding goes through <c>CreateDecoderFromStream(Stream)</c>: the
/// decoder wrapper owns the WIC stream, which in turn keeps the managed stream
/// proxy alive for the whole decode (Vortice ≥ 3.7.4 lifetime handling), so no
/// pinned-buffer games are needed. The <c>MemoryStream</c> is declared before the
/// decoder in each method, guaranteeing it outlives the decoder's disposal.
/// </remarks>
static class WicDecoder
{
    // Container format GUIDs from wincodec.h — spelled out here so this compiles
    // even if the referenced Vortice version predates the named constants.
    static readonly Guid ContainerWebp = new("e094b0e2-67f2-45b3-b0ea-115337ca7cf3");
    static readonly Guid ContainerHeif = new("e1e62521-6787-405b-a339-500715b5763f");

    /// <summary>False only if WIC could not be initialized at all (never on stock Windows).</summary>
    public static bool IsAvailable { get; }

    /// <summary>True when the "WebP Image Extensions" codec is installed.</summary>
    public static bool HasWebp { get; }

    /// <summary>True when the "HEIF Image Extensions" codec is installed.
    /// Decoding HEVC-coded .heic additionally requires the HEVC codec at runtime.</summary>
    public static bool HasHeif { get; }

    static WicDecoder()
    {
        try
        {
            using var factory = new IWICImagingFactory();
            IsAvailable = true;
            HasWebp = CanDecodeContainer(factory, ContainerWebp);
            HasHeif = CanDecodeContainer(factory, ContainerHeif);
        }
        catch
        {
            IsAvailable = false;
        }
    }

    static bool CanDecodeContainer(IWICImagingFactory factory, Guid containerFormat)
    {
        try
        {
            using var decoder = factory.CreateDecoder(containerFormat);
            return decoder is not null;
        }
        catch
        {
            // No decoder registered for this container on this machine.
            return false;
        }
    }

    /// <summary>
    /// Decode an image file to tightly packed 32bpp BGRA (straight alpha — matching
    /// the GDI+ fallback path and the shader's blending). Optionally scales to fit
    /// within <paramref name="maxDimension"/>; for JPEG the scaling happens inside
    /// the decoder at a fraction of the full-decode cost.
    /// </summary>
    public static byte[] DecodeToBgra(string path, out int width, out int height,
                                      int maxDimension, bool lowQuality)
    {
        using var factory = new IWICImagingFactory();
        using var decoder = factory.CreateDecoderFromFileName(
            path, FileAccess.Read, DecodeOptions.CacheOnDemand);
        return DecodeFirstFrame(factory, decoder, out width, out height,
                                maxDimension, lowQuality);
    }

    /// <summary>
    /// Decode an in-memory image (any WIC-supported container; in practice the
    /// JPEG previews extracted from RAW files) to tightly packed 32bpp BGRA.
    /// </summary>
    public static byte[] DecodeToBgra(byte[] data, out int width, out int height,
                                      int maxDimension = 0, bool lowQuality = false)
    {
        using var factory = new IWICImagingFactory();
        using var stream = new MemoryStream(data, writable: false);
        using var decoder = factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnDemand);
        return DecodeFirstFrame(factory, decoder, out width, out height,
                                maxDimension, lowQuality);
    }

    /// <summary>
    /// Reads the pixel dimensions of an in-memory image without decoding any
    /// pixels (header parse only). Used to size-gate RAW previews cheaply.
    /// </summary>
    public static bool TryGetImageSize(byte[] data, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var factory = new IWICImagingFactory();
            using var stream = new MemoryStream(data, writable: false);
            using var decoder = factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnDemand);
            using var frame = decoder.GetFrame(0);
            var size = frame.Size;
            width = size.Width;
            height = size.Height;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decode an image straight into an exactly <paramref name="size"/>×<paramref name="size"/>,
    /// center-cropped ("cover") 32bpp BGRA square. The scaler again sits directly on
    /// the frame, so JPEG prescales natively, and always uses Fant filtering —
    /// proper prefiltered downscaling is what makes small thumbnails look clean
    /// instead of aliased (Linear only ever samples 2×2 source pixels, so at large
    /// ratios it effectively point-samples). Sources smaller than the square are
    /// scaled up to fill it, keeping the thumbnail grid uniform.
    /// </summary>
    public static byte[] DecodeSquareBgra(string path, int size)
    {
        using var factory = new IWICImagingFactory();
        using var decoder = factory.CreateDecoderFromFileName(
            path, FileAccess.Read, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);

        var srcSize = frame.Size;
        return SquareFromSource(factory, frame, srcSize.Width, srcSize.Height, size);
    }

    /// <summary>
    /// Cover-cropped square from an in-memory image (RAW preview JPEG →
    /// thumbnail-strip square, with the same native JPEG prescale as the file path).
    /// </summary>
    public static byte[] DecodeSquareBgra(byte[] data, int size)
    {
        using var factory = new IWICImagingFactory();
        using var stream = new MemoryStream(data, writable: false);
        using var decoder = factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);

        var srcSize = frame.Size;
        return SquareFromSource(factory, frame, srcSize.Width, srcSize.Height, size);
    }

    /// <summary>
    /// Cover-cropped square from raw BGRA pixels already in memory (RAW bitmap
    /// previews and demosaic output). Runs the identical Fant scale → center clip
    /// pipeline, so RAW thumbnails match the quality of every other thumbnail.
    /// </summary>
    internal static byte[] SquareFromBgraPixels(byte[] bgra, int width, int height, int size)
    {
        using var factory = new IWICImagingFactory();
        // WICCreateBitmapFromMemory copies the pixels into the WIC bitmap, so the
        // managed array's lifetime doesn't matter past this call.
        using var bitmap = factory.CreateBitmapFromMemory(
            (uint)width, (uint)height, WicPixelFormat.Format32bppBGRA,
            bgra, (uint)(width * 4));
        return SquareFromSource(factory, bitmap, width, height, size);
    }

    // ─── Shared cores ──────────────────────────────────────────────────

    static byte[] DecodeFirstFrame(IWICImagingFactory factory, IWICBitmapDecoder decoder,
                                   out int width, out int height,
                                   int maxDimension, bool lowQuality)
    {
        using var frame = decoder.GetFrame(0);

        var size = frame.Size;
        int srcW = size.Width, srcH = size.Height;

        int dstW = srcW, dstH = srcH;
        if (maxDimension > 0 && (srcW > maxDimension || srcH > maxDimension))
        {
            float scale = Math.Min((float)maxDimension / srcW,
                                   (float)maxDimension / srcH);
            dstW = Math.Max(1, (int)(srcW * scale));
            dstH = Math.Max(1, (int)(srcH * scale));
        }

        IWICBitmapScaler? scaler = null;
        try
        {
            IWICBitmapSource source = frame;
            if (dstW != srcW || dstH != srcH)
            {
                // IMPORTANT: the scaler goes directly on the frame, BEFORE the format
                // converter. That ordering lets WIC push the scale down into codecs
                // with native scaled decode (JPEG), so a 50 MP photo is never fully
                // decoded just to produce an 80 px thumbnail.
                scaler = factory.CreateBitmapScaler();
                scaler.Initialize(frame, (uint)dstW, (uint)dstH,
                    lowQuality ? BitmapInterpolationMode.Linear
                               : BitmapInterpolationMode.Fant);
                source = scaler;
            }

            using var converter = factory.CreateFormatConverter();
            converter.Initialize(source, WicPixelFormat.Format32bppBGRA,
                BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);

            width = dstW;
            height = dstH;
            int stride = dstW * 4;
            byte[] pixels = new byte[stride * dstH];
            converter.CopyPixels((uint)stride, pixels);
            return pixels;
        }
        finally
        {
            scaler?.Dispose();
        }
    }

    static byte[] SquareFromSource(IWICImagingFactory factory, IWICBitmapSource source,
                                   int srcW, int srcH, int size)
    {
        // Cover: scale so the SHORT side lands exactly on `size` (the long side
        // overshoots), then clip the centered square out of the overshoot.
        float scale = Math.Max((float)size / srcW, (float)size / srcH);
        int scaledW = Math.Max(size, (int)MathF.Round(srcW * scale));
        int scaledH = Math.Max(size, (int)MathF.Round(srcH * scale));

        using var scaler = factory.CreateBitmapScaler();
        scaler.Initialize(source, (uint)scaledW, (uint)scaledH, BitmapInterpolationMode.Fant);

        using var clipper = factory.CreateBitmapClipper();
        clipper.Initialize(scaler, new RectI(
            (scaledW - size) / 2, (scaledH - size) / 2, size, size));

        using var converter = factory.CreateFormatConverter();
        converter.Initialize(clipper, WicPixelFormat.Format32bppBGRA,
            BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);

        int stride = size * 4;
        byte[] pixels = new byte[stride * size];
        converter.CopyPixels((uint)stride, pixels);
        return pixels;
    }
}
