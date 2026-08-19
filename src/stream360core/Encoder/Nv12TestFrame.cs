namespace Stream360.Core.Encoder;

public static class Nv12TestFrame
{
    public static byte[] Create(
        int width,
        int height,
        int frameNumber)
    {
        int ySize = width * height;
        int uvSize = width * height / 2;

        byte[] data =
            new byte[ySize + uvSize];

        Span<byte> yPlane =
            data.AsSpan(0, ySize);

        Span<byte> uvPlane =
            data.AsSpan(ySize, uvSize);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int value =
                    (x + y + frameNumber * 4) & 0xFF;

                yPlane[
                    y * width + x] =
                    (byte)value;
            }
        }

        // Neutral chroma = grayscale.
        uvPlane.Fill(128);

        return data;
    }
}