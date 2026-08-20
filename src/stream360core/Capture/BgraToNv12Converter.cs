namespace Stream360.Core.Capture;

public static class BgraToNv12Converter
{
    public static void Convert(
        ReadOnlySpan<byte> bgra,
        Span<byte> nv12,
        int width,
        int height)
    {
        if ((width & 1) != 0 ||
            (height & 1) != 0)
        {
            throw new ArgumentException(
                "NV12 conversion requires even width and height.");
        }

        int expectedBgraSize =
            width *
            height *
            4;

        int expectedNv12Size =
            width *
            height *
            3 /
            2;

        if (bgra.Length <
            expectedBgraSize)
        {
            throw new ArgumentException(
                $"BGRA buffer is too small. " +
                $"Expected at least {expectedBgraSize:N0} bytes.");
        }

        if (nv12.Length <
            expectedNv12Size)
        {
            throw new ArgumentException(
                $"NV12 buffer is too small. " +
                $"Expected at least {expectedNv12Size:N0} bytes.");
        }

        Span<byte> yPlane =
            nv12.Slice(
                0,
                width * height);

        Span<byte> uvPlane =
            nv12.Slice(
                width * height,
                width * height / 2);

        // ---------------------------------------------------------
        // Y plane
        //
        // Approximate BT.709 limited-range conversion.
        // ---------------------------------------------------------

        for (int y = 0;
             y < height;
             y++)
        {
            int bgraRow =
                y *
                width *
                4;

            int yRow =
                y *
                width;

            for (int x = 0;
                 x < width;
                 x++)
            {
                int index =
                    bgraRow +
                    x * 4;

                int b =
                    bgra[index];

                int g =
                    bgra[index + 1];

                int r =
                    bgra[index + 2];

                int luma =
                    ((54 * r +
                      183 * g +
                      18 * b +
                      128) >> 8) +
                    16;

                yPlane[yRow + x] =
                    ClampToByte(luma);
            }
        }

        // ---------------------------------------------------------
        // UV plane
        //
        // One UV pair for each 2x2 block.
        // ---------------------------------------------------------

        int uvRowWidth =
            width / 2;

        for (int y = 0;
             y < height;
             y += 2)
        {
            int topRow =
                y *
                width *
                4;

            int bottomRow =
                (y + 1) *
                width *
                4;

            int uvRow =
                (y / 2) *
                width;

            for (int x = 0;
                 x < width;
                 x += 2)
            {
                int topLeft =
                    topRow +
                    x * 4;

                int topRight =
                    topLeft +
                    4;

                int bottomLeft =
                    bottomRow +
                    x * 4;

                int bottomRight =
                    bottomLeft +
                    4;

                int b =
                    (bgra[topLeft] +
                     bgra[topRight] +
                     bgra[bottomLeft] +
                     bgra[bottomRight]) /
                    4;

                int g =
                    (bgra[topLeft + 1] +
                     bgra[topRight + 1] +
                     bgra[bottomLeft + 1] +
                     bgra[bottomRight + 1]) /
                    4;

                int r =
                    (bgra[topLeft + 2] +
                     bgra[topRight + 2] +
                     bgra[bottomLeft + 2] +
                     bgra[bottomRight + 2]) /
                    4;

                int u =
                    ((-29 * r -
                       99 * g +
                       128 * b +
                       32768) >> 8) +
                    128;

                int v =
                    ((128 * r -
                       116 * g -
                       12 * b +
                       32768) >> 8) +
                    128;

                int uvIndex =
                    uvRow +
                    x;

                uvPlane[uvIndex] =
                    ClampToByte(u);

                uvPlane[uvIndex + 1] =
                    ClampToByte(v);
            }
        }
    }

    private static byte ClampToByte(
        int value)
    {
        if (value < 0)
            return 0;

        if (value > 255)
            return 255;

        return (byte)value;
    }
}