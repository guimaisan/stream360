
using Stream360.Core.Encoder;
using Stream360.Core.Encoding;

Console.WriteLine("Stream360 Multi-Frame H.264 Test");
Console.WriteLine("================================");
Console.WriteLine();

var encoders =
    EncoderDetector.GetAvailableEncoders();

var h264 =
    encoders.FirstOrDefault(
        e => e.Codec.Equals(
            "H.264",
            StringComparison.OrdinalIgnoreCase));

if (h264 == null)
{
    Console.WriteLine(
        "No H.264 hardware encoder found.");

    return;
}

Console.WriteLine(
    $"Encoder: {h264.Name}");

Console.WriteLine();

using var encoder =
    new MediaFoundationEncoder(h264);

try
{
    encoder.Initialize(
        width: 1280,
        height: 720,
        fps: 60,
        bitrate: 8_000_000);

    const int frameCount = 30;

    Console.WriteLine();
    Console.WriteLine(
        $"Submitting {frameCount} test frames...");

    Console.WriteLine();

    for (int i = 0; i < frameCount; i++)
    {
        var frame =
            Nv12TestFrame.Create(
                1280,
                720,
                i);

        encoder.SubmitFrame(frame);

        Console.WriteLine(
            $"Submitted frame {i + 1}/{frameCount}");
    }

    Console.WriteLine();
    Console.WriteLine(
        "All frames submitted.");

    Console.WriteLine();
    Console.WriteLine(
        "Draining encoder...");

    encoder.Flush();

    int packetCount = 0;
    long totalBytes = 0;

    while (encoder.TryGetFlushedFrame(
        out byte[]? encoded))
    {
        packetCount++;

        totalBytes +=
            encoded!.Length;

        Console.WriteLine(
            $"Packet #{packetCount}: " +
            $"{encoded.Length:N0} bytes");
    }

    Console.WriteLine();

    Console.WriteLine(
        $"Input frames: {frameCount}");

    Console.WriteLine(
        $"H.264 packets: {packetCount}");

    Console.WriteLine(
        $"Encoded bytes: {totalBytes:N0}");

    Console.WriteLine();

    if (packetCount > 0)
    {
        Console.WriteLine(
            "SUCCESS: Quick Sync produced H.264 output.");
    }
    else
    {
        Console.WriteLine(
            "FAILURE: No H.264 packets were produced.");
    }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine(
        "ENCODING FAILED:");

    Console.WriteLine(
        ex);
}