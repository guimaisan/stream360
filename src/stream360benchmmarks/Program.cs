using Stream360.Core.Encoder;
using Stream360.Core.Encoding;

Console.WriteLine("Stream360 Encoder Initialization Test");
Console.WriteLine("=====================================");
Console.WriteLine();

var encoders = EncoderDetector.GetAvailableEncoders();

var h264 = encoders.FirstOrDefault(e =>
    e.Codec.Equals(
        "H.264",
        StringComparison.OrdinalIgnoreCase));

if (h264 == null)
{
    Console.WriteLine("No H.264 hardware encoder found.");
    return;
}

Console.WriteLine($"Selected: {h264.Name}");
Console.WriteLine();

using var encoder = new MediaFoundationEncoder(h264);

try
{
    encoder.Initialize(
        width: 1280,
        height: 720,
        fps: 60,
        bitrate: 8_000_000);

    Console.WriteLine();
    Console.WriteLine("SUCCESS: encoder initialized.");
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine("INITIALIZATION FAILED:");
    Console.WriteLine(ex.Message);
}