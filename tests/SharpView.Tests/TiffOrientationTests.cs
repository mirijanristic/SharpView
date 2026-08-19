using SharpView.Services;
using Xunit;

namespace SharpView.Tests;

public sealed class TiffOrientationTests
{
    // ─── Synthetic TIFF construction ───────────────────────────────────

    /// <summary>
    /// Builds a minimal classic TIFF: 8-byte header, IFD0 at offset 8 with the
    /// given entries. Each entry is (tag, type, count, inline value).
    /// </summary>
    static byte[] BuildTiff(bool little, params (ushort Tag, ushort Type, uint Count, ushort Value)[] entries)
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
        W16(42);   // classic TIFF magic
        W32(8);    // IFD0 immediately after the header

        W16((ushort)entries.Length);
        foreach (var (tag, type, count, value) in entries)
        {
            W16(tag);
            W16(type);
            W32(count);
            // Inline value: left-justified in the 4-byte field for both
            // endiannesses (SHORT occupies the first two bytes).
            W16(value);
            W16(0);
        }
        W32(0); // next-IFD offset: none

        return ms.ToArray();
    }

    static int Read(byte[] tiff) => TiffOrientation.Read(new MemoryStream(tiff));

    // ─── Happy paths ───────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]   // "II" — little endian (Nikon NEF)
    [InlineData(false)]  // "MM" — big endian
    public void Read_OrientationSix_BothEndiannesses(bool little)
    {
        byte[] tiff = BuildTiff(little,
            (0x0100, 3, 1, 4000),      // ImageWidth (noise before the tag)
            (0x0112, 3, 1, 6),         // Orientation = rotate 90 CW
            (0x011A, 3, 1, 300));      // XResolution-ish noise after
        Assert.Equal(6, Read(tiff));
    }

    [Fact]
    public void Read_OrientationEight_BigEndian()
    {
        byte[] tiff = BuildTiff(little: false, (0x0112, 3, 1, 8));
        Assert.Equal(8, Read(tiff));
    }

    [Fact]
    public void Read_OrientationOne_Explicit()
    {
        byte[] tiff = BuildTiff(little: true, (0x0112, 3, 1, 1));
        Assert.Equal(1, Read(tiff));
    }

    // ─── Fallback-to-1 paths (never throw, never guess) ────────────────

    [Fact]
    public void Read_MissingOrientationTag_ReturnsNormal()
    {
        byte[] tiff = BuildTiff(little: true, (0x0100, 3, 1, 4000));
        Assert.Equal(1, Read(tiff));
    }

    [Fact]
    public void Read_OutOfRangeValue_ReturnsNormal()
    {
        byte[] tiff = BuildTiff(little: true, (0x0112, 3, 1, 9));
        Assert.Equal(1, Read(tiff));
    }

    [Fact]
    public void Read_WrongFieldType_ReturnsNormal()
    {
        // Orientation must be SHORT (3); a LONG (4) entry is treated as invalid.
        byte[] tiff = BuildTiff(little: true, (0x0112, 4, 1, 6));
        Assert.Equal(1, Read(tiff));
    }

    [Fact]
    public void Read_BadMagic_ReturnsNormal()
    {
        byte[] tiff = BuildTiff(little: true, (0x0112, 3, 1, 6));
        tiff[2] = 99; // corrupt the 42
        Assert.Equal(1, Read(tiff));
    }

    [Fact]
    public void Read_NotATiff_ReturnsNormal()
    {
        Assert.Equal(1, Read(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 })); // JPEG SOI
    }

    [Fact]
    public void Read_TruncatedStream_ReturnsNormal()
    {
        byte[] tiff = BuildTiff(little: true, (0x0112, 3, 1, 6));
        Assert.Equal(1, Read(tiff[..10])); // header survives, IFD is cut off
    }

    [Fact]
    public void Read_EmptyStream_ReturnsNormal()
    {
        Assert.Equal(1, Read(Array.Empty<byte>()));
    }

    [Fact]
    public void Read_MissingFile_ReturnsNormal()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "SharpViewTests_missing_" + Guid.NewGuid().ToString("N") + ".nef");
        Assert.Equal(1, TiffOrientation.Read(path));
    }
}
