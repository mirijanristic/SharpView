namespace SharpView.Services;

/// <summary>
/// Minimal TIFF IFD0 reader that extracts the EXIF Orientation tag (0x0112).
/// Covers TIFF-based RAW containers — NEF/NRW, DNG, CR2, ARW — which is exactly
/// what the RAW preview path needs: LibRaw's C API exposes no getter for
/// <c>sizes.flip</c>, and poking the <c>libraw_data_t</c> struct at hardcoded
/// offsets would break silently on a dll upgrade. Fifty lines of stable file
/// format beat one fragile offset.
/// </summary>
/// <remarks>
/// RAF (Fuji) is NOT a TIFF container — when Fuji support lands, orientation
/// must come from the EXIF block of the embedded JPEG instead.
/// Any parse problem returns 1 ("normal"), never throws: a missing rotation is
/// a cosmetic issue, a decode-blocking exception is not.
/// </remarks>
internal static class TiffOrientation
{
    /// <summary>Reads EXIF orientation (1..8) from a TIFF-container file; 1 on any failure.</summary>
    public static int Read(string path)
    {
        try
        {
            // Maximally permissive sharing: LibRaw holds its own read handle on
            // the same file while this runs.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Reads EXIF orientation (1..8) from a seekable TIFF stream; 1 when the tag
    /// is absent, out of range, or the stream is not a classic TIFF.
    /// </summary>
    public static int Read(Stream stream)
    {
        try
        {
            // Header: byte order ("II" little / "MM" big), magic 42, IFD0 offset.
            Span<byte> header = stackalloc byte[8];
            if (!Fill(stream, header)) return 1;

            bool little;
            if (header[0] == (byte)'I' && header[1] == (byte)'I') little = true;
            else if (header[0] == (byte)'M' && header[1] == (byte)'M') little = false;
            else return 1;

            if (U16(header.Slice(2, 2), little) != 42) return 1; // classic TIFF only

            uint ifdOffset = U32(header.Slice(4, 4), little);
            if (ifdOffset < 8 || ifdOffset > int.MaxValue) return 1;
            stream.Position = ifdOffset;

            Span<byte> countBuf = stackalloc byte[2];
            if (!Fill(stream, countBuf)) return 1;
            int count = U16(countBuf, little);
            if (count <= 0 || count > 4096) return 1; // sanity cap for corrupt files

            // IFD entry: tag(2) type(2) count(4) value/offset(4). Entries are
            // scanned rather than binary-searched: writers occasionally violate
            // the tag-sorted requirement, and 4096 × 12 B is nothing.
            Span<byte> entry = stackalloc byte[12];
            for (int i = 0; i < count; i++)
            {
                if (!Fill(stream, entry)) return 1;
                if (U16(entry.Slice(0, 2), little) != 0x0112) continue;

                // Orientation must be a single SHORT; the value is inline,
                // left-justified in the 4-byte value field (both endiannesses).
                if (U16(entry.Slice(2, 2), little) != 3) return 1;
                if (U32(entry.Slice(4, 4), little) != 1) return 1;

                int value = U16(entry.Slice(8, 2), little);
                return value is >= 1 and <= 8 ? value : 1;
            }
            return 1;
        }
        catch
        {
            return 1;
        }
    }

    static bool Fill(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer.Slice(offset));
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    static ushort U16(ReadOnlySpan<byte> b, bool little)
        => little ? (ushort)(b[0] | (b[1] << 8))
                  : (ushort)((b[0] << 8) | b[1]);

    static uint U32(ReadOnlySpan<byte> b, bool little)
        => little ? (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24))
                  : (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
}
