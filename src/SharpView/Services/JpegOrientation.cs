namespace SharpView.Services;

/// <summary>
/// Reads the EXIF Orientation tag out of a JPEG byte stream. Exists for RAW
/// containers that are NOT TIFF-based — RAF (Fuji) — where the file itself has
/// no IFD0 to read, but the embedded preview JPEG (which the RAW path already
/// holds in memory) carries a full EXIF block. An EXIF APP1 payload is just a
/// TIFF stream behind a 6-byte signature, so after locating it this delegates
/// to <see cref="TiffOrientation.Read(Stream)"/> — one parser, two containers.
/// </summary>
/// <remarks>
/// Any structural problem returns 1 ("normal"), never throws — same contract as
/// <see cref="TiffOrientation"/>: a missing rotation is cosmetic, a
/// decode-blocking exception is not.
/// </remarks>
internal static class JpegOrientation
{
    /// <summary>Reads EXIF orientation (1..8) from JPEG bytes; 1 when the stream
    /// is not a JPEG, has no EXIF APP1 segment, or the tag is absent/invalid.</summary>
    public static int Read(byte[] jpeg)
    {
        try
        {
            // SOI marker.
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return 1;

            int pos = 2;
            while (pos + 4 <= jpeg.Length)
            {
                if (jpeg[pos] != 0xFF) return 1; // lost segment sync — bail out

                int marker = jpeg[pos + 1];

                // Standalone markers without a length field can appear between
                // segments (padding FFs, restart markers); skip them.
                if (marker == 0xFF) { pos++; continue; }
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { pos += 2; continue; }

                // Entropy-coded data starts at SOS; EXIF can only precede it.
                // EOI likewise ends the metadata region.
                if (marker == 0xDA || marker == 0xD9) return 1;

                // Segment length includes its own two bytes.
                int length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
                if (length < 2 || pos + 2 + length > jpeg.Length) return 1;

                if (marker == 0xE1) // APP1 — EXIF lives here (XMP uses APP1 too)
                {
                    int payload = pos + 4;
                    int payloadLength = length - 2;

                    // "Exif\0\0" signature, then the embedded TIFF stream. The
                    // MemoryStream slice makes TIFF offsets relative to its own
                    // start, exactly what TiffOrientation expects.
                    if (payloadLength > 6
                        && jpeg[payload] == (byte)'E' && jpeg[payload + 1] == (byte)'x'
                        && jpeg[payload + 2] == (byte)'i' && jpeg[payload + 3] == (byte)'f'
                        && jpeg[payload + 4] == 0 && jpeg[payload + 5] == 0)
                    {
                        using var tiff = new MemoryStream(jpeg, payload + 6,
                            payloadLength - 6, writable: false);
                        return TiffOrientation.Read(tiff);
                    }
                    // Non-EXIF APP1 (XMP, ...) — keep scanning.
                }

                pos += 2 + length;
            }
            return 1;
        }
        catch
        {
            return 1;
        }
    }
}
