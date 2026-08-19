namespace SharpView.Services;

/// <summary>
/// Applies an EXIF orientation (1..8) to a tightly packed 32bpp BGRA buffer.
/// Used by the RAW preview path: embedded preview JPEGs are stored in sensor
/// orientation, so portrait shots need the same rotation the camera recorded.
/// (The demosaic path never needs this — LibRaw applies the flip itself.)
/// </summary>
internal static unsafe class PixelOrientation
{
    /// <summary>
    /// Returns the correctly oriented pixels, updating <paramref name="width"/> and
    /// <paramref name="height"/> (orientations 5..8 swap them). Orientation 1 or
    /// any out-of-range value returns the input array unchanged.
    /// </summary>
    /// <remarks>
    /// Per-destination-pixel source mapping (dst(x,y) = src(sx,sy)), verified by
    /// unit tests against hand-computed 2×2 / 2×3 patterns:
    /// 2 mirror-H, 3 rotate 180, 4 mirror-V, 5 transpose, 6 rotate 90° CW,
    /// 7 transverse (transpose + 180), 8 rotate 90° CCW.
    /// The per-pixel switch is on a loop-invariant value, so the branch predictor
    /// makes it free; the whole-buffer pass is memory-bound anyway.
    /// </remarks>
    public static byte[] Apply(byte[] bgra, ref int width, ref int height, int orientation)
    {
        if (orientation <= 1 || orientation > 8) return bgra;

        int w = width, h = height;
        bool swapDims = orientation >= 5;
        int dw = swapDims ? h : w;
        int dh = swapDims ? w : h;

        byte[] result = new byte[bgra.Length];
        fixed (byte* srcBytes = bgra)
        fixed (byte* dstBytes = result)
        {
            uint* src = (uint*)srcBytes;
            uint* dst = (uint*)dstBytes;

            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    int sx, sy;
                    switch (orientation)
                    {
                        case 2: sx = w - 1 - x; sy = y;         break; // mirror horizontal
                        case 3: sx = w - 1 - x; sy = h - 1 - y; break; // rotate 180
                        case 4: sx = x;         sy = h - 1 - y; break; // mirror vertical
                        case 5: sx = y;         sy = x;         break; // transpose
                        case 6: sx = y;         sy = h - 1 - x; break; // rotate 90 CW
                        case 7: sx = w - 1 - y; sy = h - 1 - x; break; // transverse
                        default: sx = w - 1 - y; sy = x;        break; // 8: rotate 90 CCW
                    }
                    dst[(long)y * dw + x] = src[(long)sy * w + sx];
                }
            }
        }

        width = dw;
        height = dh;
        return result;
    }
}
