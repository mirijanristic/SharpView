using SharpView.Services;
using Xunit;

namespace SharpView.Tests;

public sealed class JpegOrientationTests
{
    // ─── Synthetic JPEG construction ───────────────────────────────────

    /// <summary>Minimal classic TIFF with a single-entry IFD0 (same layout the
    /// TiffOrientation tests use), for embedding inside an EXIF APP1 segment.</summary>
    static byte[] BuildTiff(bool little, ushort orientation)
    {
        using var ms = new MemoryStream();

        void W16(ushort v)
        {
            if (little) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
            else { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        }
        void W32(uint v)
        {
            if (little)
            {
                ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8));
                ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 24));
            }
            else
            {
                ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16));
                ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
            }
        }

        byte order = little ? (byte)'I' : (byte)'M';
        ms.WriteByte(order); ms.WriteByte(order);
        W16(42);
        W32(8);          // IFD0 immediately after the header
        W16(1);          // one entry
        W16(0x0112);     // Orientation
        W16(3);          // SHORT
        W32(1);          // count
        W16(orientation);
        W16(0);          // value field padding
        W32(0);          // no next IFD

        return ms.ToArray();
    }

    static byte[] Segment(byte marker, byte[] payload)
    {
        var ms = new MemoryStream();
        ms.WriteByte(0xFF);
        ms.WriteByte(marker);
        int length = payload.Length + 2; // length field includes itself
        ms.WriteByte((byte)(length >> 8));
        ms.WriteByte((byte)length);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    static byte[] ExifApp1(byte[] tiff)
    {
        var payload = new byte[6 + tiff.Length];
        "Exif\0\0"u8.CopyTo(payload);
        tiff.CopyTo(payload, 6);
        return Segment(0xE1, payload);
    }

    static byte[] Jpeg(params byte[][] segments)
    {
        var ms = new MemoryStream();
        ms.WriteByte(0xFF); ms.WriteByte(0xD8); // SOI
        foreach (byte[] segment in segments) ms.Write(segment, 0, segment.Length);
        ms.WriteByte(0xFF); ms.WriteByte(0xD9); // EOI
        return ms.ToArray();
    }

    static byte[] App0Jfif() => Segment(0xE0, "JFIF\0"u8.ToArray());

    // ─── Happy paths ───────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 6)]   // little-endian TIFF inside EXIF (typical)
    [InlineData(false, 8)]  // big-endian TIFF inside EXIF
    public void Read_ExifApp1_ReturnsOrientation(bool little, int orientation)
    {
        byte[] jpeg = Jpeg(ExifApp1(BuildTiff(little, (ushort)orientation)));
        Assert.Equal(orientation, JpegOrientation.Read(jpeg));
    }

    [Fact]
    public void Read_ExifAfterApp0_IsStillFound()
    {
        // Real files often lead with JFIF APP0; EXIF must be found behind it.
        byte[] jpeg = Jpeg(App0Jfif(), ExifApp1(BuildTiff(little: true, 6)));
        Assert.Equal(6, JpegOrientation.Read(jpeg));
    }

    [Fact]
    public void Read_NonExifApp1_IsSkipped_ExifBehindItFound()
    {
        // APP1 is also used for XMP — a non-EXIF APP1 must not end the scan.
        byte[] xmp = Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0"u8.ToArray());
        byte[] jpeg = Jpeg(xmp, ExifApp1(BuildTiff(little: true, 3)));
        Assert.Equal(3, JpegOrientation.Read(jpeg));
    }

    // ─── Fallback-to-1 paths (never throw, never guess) ────────────────

    [Fact]
    public void Read_NoExifSegment_ReturnsNormal()
    {
        byte[] jpeg = Jpeg(App0Jfif());
        Assert.Equal(1, JpegOrientation.Read(jpeg));
    }

    [Fact]
    public void Read_NotAJpeg_ReturnsNormal()
    {
        Assert.Equal(1, JpegOrientation.Read(BuildTiff(little: true, 6))); // bare TIFF
        Assert.Equal(1, JpegOrientation.Read(new byte[] { 1, 2, 3 }));
        Assert.Equal(1, JpegOrientation.Read(Array.Empty<byte>()));
    }

    [Fact]
    public void Read_TruncatedSegment_ReturnsNormal()
    {
        byte[] jpeg = Jpeg(ExifApp1(BuildTiff(little: true, 6)));
        Assert.Equal(1, JpegOrientation.Read(jpeg[..10])); // cut inside APP1
    }

    [Fact]
    public void Read_GarbageAfterSoi_ReturnsNormal()
    {
        Assert.Equal(1, JpegOrientation.Read(new byte[] { 0xFF, 0xD8, 0x00, 0x42, 0x13 }));
    }

    [Fact]
    public void Read_ExifWithCorruptTiff_ReturnsNormal()
    {
        byte[] tiff = BuildTiff(little: true, 6);
        tiff[2] = 99; // break the TIFF magic — TiffOrientation bails to 1
        Assert.Equal(1, JpegOrientation.Read(Jpeg(ExifApp1(tiff))));
    }

    [Fact]
    public void Read_OrientationOutOfRange_ReturnsNormal()
    {
        Assert.Equal(1, JpegOrientation.Read(Jpeg(ExifApp1(BuildTiff(little: true, 9)))));
    }
}
