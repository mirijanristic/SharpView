using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpView.Services;

/// <summary>
/// Minimal interop surface over LibRaw's stable C API (libraw.h, 0.21.x).
/// Only version-independent C functions are used — no <c>libraw_data_t</c>
/// struct offsets — so swapping the native dll for a newer LibRaw build cannot
/// silently corrupt memory.
/// </summary>
/// <remarks>
/// <para>
/// Declared with <c>[LibraryImport]</c> (compile-time source-generated
/// marshalling, all argument types blittable, strings as UTF-16): the layer is
/// Native-AOT- and trimming-clean by construction — nothing here relies on
/// runtime marshalling or reflection.
/// </para>
/// <para>
/// The native binary comes from the <c>Sdcb.LibRaw.runtime.win64</c> NuGet
/// package: <c>raw_r.dll</c> (the thread-safe "_r" build — our decodes run
/// concurrently on the thread pool) plus its <c>lcms2</c>/<c>zlib1</c>/<c>jpeg8</c>
/// dependencies, all copied next to the executable by the package's targets.
/// The resolver below also accepts the official LibRaw-Win binary name
/// (<c>libraw.dll</c>) and the non-reentrant vcpkg name (<c>raw.dll</c>), so the
/// dll source can be swapped later without touching this file.
/// </para>
/// </remarks>
static partial class LibRawNative
{
    /// <summary>Logical library name; mapped to a real dll by the resolver below.</summary>
    const string Dll = "libraw";

    static LibRawNative()
    {
        // Runs before the first native call in this class resolves (guaranteed
        // by static-constructor semantics — the generated method bodies live in
        // this same class). Returning IntPtr.Zero for any other library name
        // leaves default resolution (shell32, dwmapi, ...) intact.
        // NativeLibrary.SetDllImportResolver is fully supported under Native AOT.
        NativeLibrary.SetDllImportResolver(typeof(LibRawNative).Assembly, Resolve);
    }

    static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Dll) return IntPtr.Zero;

        foreach (string candidate in new[] { "raw_r", "libraw", "raw" })
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
                return handle;
        }
        return IntPtr.Zero; // -> DllNotFoundException at the call site (probed once)
    }

    // ─── Error codes we branch on (libraw_const.h, enum LibRaw_errors) ─────────

    public const int Success = 0;
    public const int NoThumbnail = -5;          // LIBRAW_NO_THUMBNAIL
    public const int UnsupportedThumbnail = -6; // LIBRAW_UNSUPPORTED_THUMBNAIL

    // ─── libraw_processed_image_t (libraw_types.h) ─────────────────────────────

    /// <summary>type == JPEG: payload is a complete JPEG byte stream.</summary>
    public const int ImageJpeg = 1;   // LIBRAW_IMAGE_JPEG
    /// <summary>type == BITMAP: payload is interleaved raw pixels (colors × bits).</summary>
    public const int ImageBitmap = 2; // LIBRAW_IMAGE_BITMAP

    /// <summary>
    /// Header of <c>libraw_processed_image_t</c>. C layout: enum (int32) +
    /// 4 × ushort + uint32 = 16 bytes, no padding (max field alignment is 4);
    /// the payload starts immediately after, at <see cref="ProcessedDataOffset"/>.
    /// Fully blittable — read with a direct pointer cast, no marshalling.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessedImageHeader
    {
        public int Type;
        public ushort Height;
        public ushort Width;
        public ushort Colors;
        public ushort Bits;
        public uint DataSize;
    }

    /// <summary>Byte offset of the payload within <c>libraw_processed_image_t</c>.</summary>
    public const int ProcessedDataOffset = 16;

    // ─── C API (signatures pinned against the 0.21-stable libraw.h and the
    //     exact raw_r.dll build we ship) ──────────────────────────────────────

    /// <summary><c>libraw_init(flags)</c> — allocates a fresh, independent handle.</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_init")]
    public static partial IntPtr Init(uint flags);

    /// <summary><c>libraw_open_wfile</c> — wide-char open, safe for any Windows path
    /// (UTF-16 string marshalling matches <c>const wchar_t*</c>).</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_open_wfile", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int OpenFile(IntPtr handle, string path);

    /// <summary><c>libraw_unpack</c> — reads and decodes the Bayer/sensor data.</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_unpack")]
    public static partial int Unpack(IntPtr handle);

    /// <summary><c>libraw_unpack_thumb</c> — extracts the largest embedded preview
    /// without touching the sensor data (the fast path).</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_unpack_thumb")]
    public static partial int UnpackThumb(IntPtr handle);

    /// <summary><c>libraw_dcraw_process</c> — demosaic + white balance + gamma.
    /// Applies the camera orientation flip itself, so its output needs no rotation.</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_dcraw_process")]
    public static partial int DcrawProcess(IntPtr handle);

    /// <summary><c>libraw_dcraw_make_mem_image</c> — processed image as
    /// <c>libraw_processed_image_t*</c>; free with <see cref="ClearMem"/>.</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_dcraw_make_mem_image")]
    public static partial IntPtr MakeMemImage(IntPtr handle, out int errorCode);

    /// <summary><c>libraw_dcraw_make_mem_thumb</c> — unpacked thumbnail as
    /// <c>libraw_processed_image_t*</c>; free with <see cref="ClearMem"/>.</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_dcraw_make_mem_thumb")]
    public static partial IntPtr MakeMemThumb(IntPtr handle, out int errorCode);

    /// <summary><c>libraw_dcraw_clear_mem</c> — frees a processed image/thumb.</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_dcraw_clear_mem")]
    public static partial void ClearMem(IntPtr processedImage);

    /// <summary><c>libraw_close</c> — frees the handle and everything it owns.</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_close")]
    public static partial void Close(IntPtr handle);

    /// <summary><c>libraw_versionNumber</c> — cheap call used as the availability probe.</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_versionNumber")]
    public static partial int VersionNumber();

    [LibraryImport(Dll, EntryPoint = "libraw_strerror")]
    private static partial IntPtr StrErrorPtr(int errorCode);

    /// <summary>Human-readable message for a LibRaw error code.</summary>
    public static string StrError(int errorCode)
        => Marshal.PtrToStringAnsi(StrErrorPtr(errorCode)) ?? $"LibRaw error {errorCode}";

    /// <summary><c>libraw_get_iwidth</c> — output image width; valid right after
    /// <see cref="OpenFile"/> (the identify stage fills the sizes).</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_get_iwidth")]
    public static partial int GetWidth(IntPtr handle);

    /// <summary><c>libraw_get_iheight</c> — output image height (see <see cref="GetWidth"/>).</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_get_iheight")]
    public static partial int GetHeight(IntPtr handle);

    /// <summary><c>libraw_get_cam_mul</c> — camera-recorded white balance multiplier
    /// (index 0..3); 0 when the camera did not record one.</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_get_cam_mul")]
    public static partial float GetCamMul(IntPtr handle, int index);

    /// <summary><c>libraw_set_user_mul</c> — user white balance multiplier (index 0..3).
    /// The C API has no <c>use_camera_wb</c> setter; copying <c>cam_mul</c> into
    /// <c>user_mul</c> is the equivalent (LibRaw uses user_mul when user_mul[0] &gt; 0).</summary>
    [LibraryImport(Dll, EntryPoint = "libraw_set_user_mul")]
    public static partial void SetUserMul(IntPtr handle, int index, float value);
}
