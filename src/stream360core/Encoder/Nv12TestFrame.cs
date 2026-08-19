namespace Stream360.Core.Encoder;

public static class Nv12TestFrame
{
    public static void Fill(
        Span<byte> data,
        int width,
        int height,
        int frameNumber)
    {
        int expectedSize =
            width * height * 3 / 2;

        if (data.Length != expectedSize)
        {
            throw new ArgumentException(
                $"Expected {expectedSize:N0} bytes, " +
                $"received {data.Length:N0}.");
        }

        int ySize =
            width * height;

        Span<byte> yPlane =
            data[..ySize];

        Span<byte> uvPlane =
            data[ySize..];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                yPlane[y * width + x] =
                    (byte)((x + y + frameNumber * 4) & 0xFF);
            }
        }

        uvPlane.Fill(128);
    }
}