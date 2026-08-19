
using System.Diagnostics;
using Stream360.Core.Decoding;
using Stream360.Core.Encoder;
using Stream360.Core.Encoding;

Console.WriteLine("Stream360 60 FPS Encode/Decode Benchmark");
Console.WriteLine("========================================");
Console.WriteLine();

const int width = 1280;
const int height = 720;
const int fps = 60;
const int bitrate = 8_000_000;
const int frameCount = 600;

double frameIntervalMs =
    1000.0 / fps;

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

Console.WriteLine(
    "Decoder: Microsoft H264 Video Decoder MFT");

Console.WriteLine(
    $"Resolution: {width}x{height}");

Console.WriteLine(
    $"Target FPS: {fps}");

Console.WriteLine(
    $"Frames: {frameCount}");

Console.WriteLine();

using var encoder =
    new MediaFoundationEncoder(h264);

using var decoder =
    new MediaFoundationDecoder();

try
{
    encoder.Initialize(
        width,
        height,
        fps,
        bitrate);

    decoder.Initialize(
        width,
        height,
        fps);

    var generationTimes =
        new List<double>(frameCount);

    var encodeTimes =
        new List<double>(frameCount);

    var outputTimes =
        new List<double>(frameCount);

    var decodeSubmitTimes =
        new List<double>(frameCount);

    var decodeRetrieveTimes =
        new List<double>(frameCount);

    var totalTimes =
        new List<double>(frameCount);

    int encodedPackets = 0;
    int decodedFrames = 0;

    long encodedBytes = 0;

    // Reuse one NV12 buffer for the entire benchmark.
    byte[] frameBuffer =
        new byte[width * height * 3 / 2];

    var benchmarkClock =
        Stopwatch.StartNew();

    long nextDeadline =
        Stopwatch.GetTimestamp();

    double frequency =
        Stopwatch.Frequency;

    Console.WriteLine();
    Console.WriteLine("Running...");
    Console.WriteLine();

    for (int i = 0; i < frameCount; i++)
    {
        // ---------------------------------------------------------
        // Pace the benchmark at 60 FPS.
        // No Thread.Sleep() is used here.
        // ---------------------------------------------------------

        nextDeadline +=
            (long)(
                frequency *
                frameIntervalMs /
                1000.0);

        while (Stopwatch.GetTimestamp() <
               nextDeadline)
        {
            Thread.SpinWait(100);
        }

        var totalStart =
            Stopwatch.GetTimestamp();

        // ---------------------------------------------------------
        // 1. Fill reusable NV12 buffer.
        // ---------------------------------------------------------

        var stageStart =
            Stopwatch.GetTimestamp();

        Nv12TestFrame.Fill(
            frameBuffer,
            width,
            height,
            i);

        double generationMs =
            Stopwatch.GetElapsedTime(
                stageStart).TotalMilliseconds;

        generationTimes.Add(
            generationMs);

        // ---------------------------------------------------------
        // 2. Submit NV12 frame to encoder.
        // ---------------------------------------------------------

        stageStart =
            Stopwatch.GetTimestamp();

        encoder.SubmitFrame(
            frameBuffer);

        double encodeMs =
            Stopwatch.GetElapsedTime(
                stageStart).TotalMilliseconds;

        encodeTimes.Add(
            encodeMs);

        // ---------------------------------------------------------
        // 3. Retrieve encoded packets.
        // ---------------------------------------------------------

        stageStart =
            Stopwatch.GetTimestamp();

        while (encoder.TryGetEncodedFrame(
            out byte[]? encodedPacket))
        {
            encodedPackets++;

            encodedBytes +=
                encodedPacket!.Length;

            // -----------------------------------------------------
            // 4. Submit H.264 packet to decoder.
            // -----------------------------------------------------

            var decodeSubmitStart =
                Stopwatch.GetTimestamp();

            decoder.SubmitPacket(
                encodedPacket);

            decodeSubmitTimes.Add(
                Stopwatch.GetElapsedTime(
                    decodeSubmitStart).TotalMilliseconds);

            // -----------------------------------------------------
            // 5. Retrieve decoded frames.
            // -----------------------------------------------------

            var decodeRetrieveStart =
                Stopwatch.GetTimestamp();

            while (decoder.TryGetDecodedFrame(
                out byte[]? decodedFrame))
            {
                decodedFrames++;

                _ = decodedFrame;
            }

            decodeRetrieveTimes.Add(
                Stopwatch.GetElapsedTime(
                    decodeRetrieveStart).TotalMilliseconds);
        }

        double outputMs =
            Stopwatch.GetElapsedTime(
                stageStart).TotalMilliseconds;

        outputTimes.Add(
            outputMs);

        double totalMs =
            Stopwatch.GetElapsedTime(
                totalStart).TotalMilliseconds;

        totalTimes.Add(
            totalMs);

        if ((i + 1) % fps == 0)
        {
            Console.WriteLine(
                $"Frame {i + 1}/{frameCount} | " +
                $"Gen {generationMs:F2} ms | " +
                $"Encode {encodeMs:F2} ms | " +
                $"Output {outputMs:F2} ms | " +
                $"Total {totalMs:F2} ms");
        }
    }

    // -------------------------------------------------------------
    // Drain encoder.
    // -------------------------------------------------------------

    Console.WriteLine();
    Console.WriteLine("Draining encoder...");

    encoder.Flush();

    while (encoder.TryGetFlushedFrame(
        out byte[]? encodedPacket))
    {
        encodedPackets++;

        encodedBytes +=
            encodedPacket!.Length;

        var decodeSubmitStart =
            Stopwatch.GetTimestamp();

        decoder.SubmitPacket(
            encodedPacket);

        decodeSubmitTimes.Add(
            Stopwatch.GetElapsedTime(
                decodeSubmitStart).TotalMilliseconds);

        var decodeRetrieveStart =
            Stopwatch.GetTimestamp();

        while (decoder.TryGetDecodedFrame(
            out byte[]? decodedFrame))
        {
            decodedFrames++;

            _ = decodedFrame;
        }

        decodeRetrieveTimes.Add(
            Stopwatch.GetElapsedTime(
                decodeRetrieveStart).TotalMilliseconds);
    }

    // -------------------------------------------------------------
    // Drain decoder.
    // -------------------------------------------------------------

    Console.WriteLine(
        "Draining decoder...");

    decoder.Flush();

    while (decoder.TryGetDecodedFrame(
        out byte[]? decodedFrame))
    {
        decodedFrames++;

        _ = decodedFrame;
    }

    double benchmarkSeconds =
        benchmarkClock.Elapsed.TotalSeconds;

    static double Average(
        IReadOnlyList<double> values)
    {
        return values.Count == 0
            ? 0
            : values.Average();
    }

    static double Maximum(
        IReadOnlyList<double> values)
    {
        return values.Count == 0
            ? 0
            : values.Max();
    }

    static double Percentile(
        IReadOnlyList<double> values,
        double percentile)
    {
        if (values.Count == 0)
            return 0;

        var sorted =
            values.OrderBy(v => v).ToArray();

        double position =
            (sorted.Length - 1) *
            percentile;

        int lower =
            (int)Math.Floor(position);

        int upper =
            (int)Math.Ceiling(position);

        if (lower == upper)
            return sorted[lower];

        double fraction =
            position - lower;

        return
            sorted[lower] +
            (sorted[upper] - sorted[lower]) *
            fraction;
    }

    static void PrintStats(
        string name,
        IReadOnlyList<double> values)
    {
        Console.WriteLine(name);
        Console.WriteLine(
            $"  Average: {Average(values):F3} ms");
        Console.WriteLine(
            $"  P95:     {Percentile(values, 0.95):F3} ms");
        Console.WriteLine(
            $"  Max:     {Maximum(values):F3} ms");
        Console.WriteLine();
    }

    Console.WriteLine();
    Console.WriteLine("========== RESULTS ==========");
    Console.WriteLine();

    Console.WriteLine(
        $"Input frames:        {frameCount}");

    Console.WriteLine(
        $"Encoded packets:     {encodedPackets}");

    Console.WriteLine(
        $"Decoded frames:      {decodedFrames}");

    Console.WriteLine(
        $"Encoded bytes:       {encodedBytes:N0}");

    Console.WriteLine(
        $"Benchmark duration:  {benchmarkSeconds:F2} s");

    Console.WriteLine(
        $"Effective FPS:       {frameCount / benchmarkSeconds:F2}");

    Console.WriteLine();

    PrintStats(
        "NV12 generation",
        generationTimes);

    PrintStats(
        "H.264 encode submission",
        encodeTimes);

    PrintStats(
        "Encoded output processing",
        outputTimes);

    PrintStats(
        "Decoder submission",
        decodeSubmitTimes);

    PrintStats(
        "Decoded output retrieval",
        decodeRetrieveTimes);

    PrintStats(
        "Total local processing",
        totalTimes);

    Console.WriteLine(
        $"Target frame interval: {frameIntervalMs:F3} ms");

    Console.WriteLine();

    if (decodedFrames == frameCount)
    {
        Console.WriteLine(
            "PASS: All input frames produced decoded output.");
    }
    else
    {
        Console.WriteLine(
            $"INCOMPLETE: {decodedFrames}/{frameCount} " +
            "decoded frames were observed.");
    }

    if (Average(totalTimes) <
        frameIntervalMs)
    {
        Console.WriteLine(
            "PASS: Average local processing is under " +
            "the 60 FPS frame budget.");
    }
    else
    {
        Console.WriteLine(
            "WARNING: Average local processing exceeds " +
            "the 60 FPS frame budget.");
    }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine(
        "BENCHMARK FAILED:");

    Console.WriteLine(
        ex);
}
