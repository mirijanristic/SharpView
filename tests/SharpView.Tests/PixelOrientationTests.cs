using SharpView.Services;
using Xunit;

namespace SharpView.Tests;

public sealed class PixelOrientationTests
{
    // Distinct, recognizable BGRA pixel values.
    const uint A = 0xFF0000AA, B = 0xFF0000BB, C = 0xFF0000CC,
               D = 0xFF0000DD, E = 0xFF0000EE, F = 0xFF0000FF;

    static byte[] ToBytes(uint[] pixels)
    {
        byte[] bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    static uint[] ToPixels(byte[] bytes)
    {
        uint[] pixels = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);
        return pixels;
    }

    static uint[] Apply(uint[] src, ref int width, ref int height, int orientation)
        => ToPixels(PixelOrientation.Apply(ToBytes(src), ref width, ref height, orientation));

    // ─── 2×2 source, all 8 orientations, hand-computed expectations ────
    //
    //   A B
    //   C D

    static readonly uint[] Square = { A, B, C, D };

    public static TheoryData<int, uint[]> SquareCases => new()
    {
        { 1, new[] { A, B, C, D } }, // identity
        { 2, new[] { B, A, D, C } }, // mirror horizontal
        { 3, new[] { D, C, B, A } }, // rotate 180
        { 4, new[] { C, D, A, B } }, // mirror vertical
        { 5, new[] { A, C, B, D } }, // transpose
        { 6, new[] { C, A, D, B } }, // rotate 90 CW  (left column → top row, bottom-up)
        { 7, new[] { D, B, C, A } }, // transverse    (transpose + 180)
        { 8, new[] { B, D, A, C } }, // rotate 90 CCW (right column → top row, top-down)
    };

    [Theory]
    [MemberData(nameof(SquareCases))]
    public void Apply_TwoByTwo_MatchesHandComputedResult(int orientation, uint[] expected)
    {
        int w = 2, h = 2;
        uint[] result = Apply(Square, ref w, ref h, orientation);

        Assert.Equal(2, w);
        Assert.Equal(2, h);
        Assert.Equal(expected, result);
    }

    // ─── Non-square source: dimension swap + full rotation checks ──────
    //
    //   A B        rot 90 CW:   E C A      rot 90 CCW:  B D F
    //   C D   →                 F D B                    A C E
    //   E F

    [Fact]
    public void Apply_Rotate90Cw_SwapsDimensions_2x3()
    {
        int w = 2, h = 3;
        uint[] result = Apply(new[] { A, B, C, D, E, F }, ref w, ref h, 6);

        Assert.Equal(3, w);
        Assert.Equal(2, h);
        Assert.Equal(new[] { E, C, A, F, D, B }, result);
    }

    [Fact]
    public void Apply_Rotate90Ccw_SwapsDimensions_2x3()
    {
        int w = 2, h = 3;
        uint[] result = Apply(new[] { A, B, C, D, E, F }, ref w, ref h, 8);

        Assert.Equal(3, w);
        Assert.Equal(2, h);
        Assert.Equal(new[] { B, D, F, A, C, E }, result);
    }

    [Fact]
    public void Apply_Rotate180_KeepsDimensions_2x3()
    {
        int w = 2, h = 3;
        uint[] result = Apply(new[] { A, B, C, D, E, F }, ref w, ref h, 3);

        Assert.Equal(2, w);
        Assert.Equal(3, h);
        Assert.Equal(new[] { F, E, D, C, B, A }, result);
    }

    // ─── Round trips: opposite rotations must cancel out ───────────────

    [Fact]
    public void Apply_CwThenCcw_IsIdentity()
    {
        int w = 2, h = 3;
        uint[] src = { A, B, C, D, E, F };
        uint[] rotated = Apply(src, ref w, ref h, 6);
        uint[] restored = Apply(rotated, ref w, ref h, 8);

        Assert.Equal(2, w);
        Assert.Equal(3, h);
        Assert.Equal(src, restored);
    }

    // ─── Degenerate inputs pass through untouched ──────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(-3)]
    public void Apply_IdentityOrInvalidOrientation_ReturnsSameArray(int orientation)
    {
        byte[] input = ToBytes(Square);
        int w = 2, h = 2;
        byte[] output = PixelOrientation.Apply(input, ref w, ref h, orientation);

        Assert.Same(input, output); // no copy — the fast path really is free
        Assert.Equal(2, w);
        Assert.Equal(2, h);
    }

    [Fact]
    public void Apply_SinglePixel_AllOrientations_Unchanged()
    {
        for (int orientation = 1; orientation <= 8; orientation++)
        {
            int w = 1, h = 1;
            uint[] result = Apply(new[] { A }, ref w, ref h, orientation);
            Assert.Equal(1, w);
            Assert.Equal(1, h);
            Assert.Equal(new[] { A }, result);
        }
    }
}
