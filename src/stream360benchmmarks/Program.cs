using System.Diagnostics;
using Stream360.Core.Decoding;
using Stream360.Core.Encoder;
using Stream360.Core.Encoding;

Console.WriteLine("Stream360 Phase 5 - Real Frame Latency Benchmark");
Console.WriteLine("================================================");
Console.WriteLine();

const int width = 1280;
const int height = 720;
const int fps = 60;
const int bitrate = 8_000_000;
const int frameCount = 600;

double frameIntervalMs =
    1000.0 / fps;

long frameDuration =
    10_000_000L / fps;

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

    byte[] frameBuffer =
        new byte[width * height * 3 / 2];

    var frameTimings =
        new Dictionary<long, FrameTiming>();

    var endToEndLatencies =
        new List<double>(frameCount);

    var generationTimes =
        new List<double>(frameCount);

    var encodeTimes =
        new List<double>(frameCount);

    var decodeSubmitTimes =
        new List<double>(frameCount);

    var decodeRetrieveTimes =
        new List<double>(frameCount);

    int encodedPackets = 0;
    int decodedFrames = 0;
    long encodedBytes = 0;

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

        var generationStart =
            Stopwatch.GetTimestamp();

        Nv12TestFrame.Fill(
            frameBuffer,
            width,
            height,
            i);

        generationTimes.Add(
            Stopwatch.GetElapsedTime(
                generationStart).TotalMilliseconds);

        long mediaTimestamp =
            i * frameDuration;

        long submissionTimestamp =
            Stopwatch.GetTimestamp();

        frameTimings[mediaTimestamp] =
            new FrameTiming(
                i,
                mediaTimestamp,
                submissionTimestamp);

        var encodeStart =
            Stopwatch.GetTimestamp();

        encoder.SubmitFrame(
            frameBuffer);

        encodeTimes.Add(
            Stopwatch.GetElapsedTime(
                encodeStart).TotalMilliseconds);

        while (encoder.TryGetEncodedFrame(
            out var encodedPacket))
        {
            encodedPackets++;

            encodedBytes +=
                encodedPacket!.Data.Length;

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
                out var decodedFrame))
            {
                decodedFrames++;

                if (frameTimings.TryGetValue(
                        decodedFrame!.Timestamp,
                        out var timing))
                {
                    double latencyMs =
                        Stopwatch.GetElapsedTime(
                            timing.SubmissionTimestamp)
                        .TotalMilliseconds;

                    endToEndLatencies.Add(
                        latencyMs);

                    frameTimings.Remove(
                        decodedFrame.Timestamp);

                    Console.WriteLine(
                        $"Frame {timing.FrameId:D3} | " +
                        $"Latency: {latencyMs:F3} ms");
                }
            }

            decodeRetrieveTimes.Add(
                Stopwatch.GetElapsedTime(
                    decodeRetrieveStart).TotalMilliseconds);
        }

        if ((i + 1) % fps == 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"Progress: {i + 1}/{frameCount} frames");

            Console.WriteLine(
                $"Decoded:  {decodedFrames}");

            Console.WriteLine();
        }
    }

    Console.WriteLine();
    Console.WriteLine(
        "Draining encoder...");

    encoder.Flush();

    while (encoder.TryGetFlushedFrame(
        out var encodedPacket))
    {
        encodedPackets++;

        encodedBytes +=
            encodedPacket!.Data.Length;

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
            out var decodedFrame))
        {
            decodedFrames++;

            if (frameTimings.TryGetValue(
                    decodedFrame!.Timestamp,
                    out var timing))
            {
                double latencyMs =
                    Stopwatch.GetElapsedTime(
                        timing.SubmissionTimestamp)
                    .TotalMilliseconds;

                endToEndLatencies.Add(
                    latencyMs);

                frameTimings.Remove(
                    decodedFrame.Timestamp);

                Console.WriteLine(
                    $"Drained frame {timing.FrameId:D3} | " +
                    $"Latency: {latencyMs:F3} ms");
            }
        }

        decodeRetrieveTimes.Add(
            Stopwatch.GetElapsedTime(
                decodeRetrieveStart).TotalMilliseconds);
    }

    Console.WriteLine();
    Console.WriteLine(
        "Draining decoder...");

    decoder.Flush();

    while (decoder.TryGetDecodedFrame(
        out var decodedFrame))
    {
        decodedFrames++;

        if (frameTimings.TryGetValue(
                decodedFrame!.Timestamp,
                out var timing))
        {
            double latencyMs =
                Stopwatch.GetElapsedTime(
                    timing.SubmissionTimestamp)
                .TotalMilliseconds;

            endToEndLatencies.Add(
                latencyMs);

            frameTimings.Remove(
                decodedFrame.Timestamp);

            Console.WriteLine(
                $"Drained decoder frame {timing.FrameId:D3} | " +
                $"Latency: {latencyMs:F3} ms");
        }
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
            $"  P99:     {Percentile(values, 0.99):F3} ms");

        Console.WriteLine(
            $"  Max:     {Maximum(values):F3} ms");

        Console.WriteLine();
    }

    Console.WriteLine();
    Console.WriteLine(
        "========== RESULTS ==========");

    Console.WriteLine();

    Console.WriteLine(
        $"Input frames:          {frameCount}");

    Console.WriteLine(
        $"Encoded packets:       {encodedPackets}");

    Console.WriteLine(
        $"Decoded frames:        {decodedFrames}");

    Console.WriteLine(
        $"Matched frames:        {endToEndLatencies.Count}");

    Console.WriteLine(
        $"Unmatched timestamps:  {frameTimings.Count}");

    Console.WriteLine(
        $"Encoded bytes:         {encodedBytes:N0}");

    Console.WriteLine(
        $"Benchmark duration:    {benchmarkSeconds:F2} s");

    Console.WriteLine(
        $"Effective FPS:         {frameCount / benchmarkSeconds:F2}");

    Console.WriteLine();

    PrintStats(
        "NV12 generation",
        generationTimes);

    PrintStats(
        "H.264 encode submission",
        encodeTimes);

    PrintStats(
        "Decoder submission",
        decodeSubmitTimes);

    PrintStats(
        "Decoded output retrieval",
        decodeRetrieveTimes);

    PrintStats(
        "ACTUAL FRAME LATENCY",
        endToEndLatencies);

    Console.WriteLine(
        $"Target frame interval: {frameIntervalMs:F3} ms");

    Console.WriteLine();

    if (decodedFrames == frameCount)
    {
        Console.WriteLine(
            "PASS: All frames decoded.");
    }
    else
    {
        Console.WriteLine(
            $"INCOMPLETE: {decodedFrames}/{frameCount} " +
            "frames decoded.");
    }

    if (endToEndLatencies.Count > 0)
    {
        Console.WriteLine(
            "PASS: Actual frame-to-frame latency was measured.");
    }
    else
    {
        Console.WriteLine(
            "WARNING: No frame-to-frame latency measurements were matched.");
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

internal sealed record FrameTiming(
    int FrameId,
    long MediaTimestamp,
    long SubmissionTimestamp);