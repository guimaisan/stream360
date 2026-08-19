
using System.Diagnostics;
using Stream360.Core.Encoder;
using Stream360.Core.Encoding;

Console.WriteLine("Stream360 60 FPS Encoder Benchmark");
Console.WriteLine("==================================");
Console.WriteLine();

const int width = 1280;
const int height = 720;
const int fps = 60;
const int frameCount = 600;
const int bitrate = 8_000_000;

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
    $"Resolution: {width}x{height}");

Console.WriteLine(
    $"Target FPS: {fps}");

Console.WriteLine(
    $"Frames: {frameCount}");

Console.WriteLine();

using var encoder =
    new MediaFoundationEncoder(h264);

try
{
    encoder.Initialize(
        width,
        height,
        fps,
        bitrate);

    Console.WriteLine();
    Console.WriteLine("Running...");
    Console.WriteLine();

    var generationTimes = new List<double>(
        frameCount);

    var submitTimes = new List<double>(
        frameCount);

    var retrieveTimes = new List<double>(
        frameCount);

    var pipelineTimes = new List<double>(
        frameCount);

    long totalEncodedBytes = 0;
    int encodedPackets = 0;

    var benchmarkClock =
        Stopwatch.StartNew();

    long nextFrameDeadline =
        Stopwatch.GetTimestamp();

    double timestampFrequency =
        Stopwatch.Frequency;

    for (int i = 0; i < frameCount; i++)
    {
        // ---------------------------------------------------------
        // Pace the test to exactly 60 FPS.
        // ---------------------------------------------------------

        nextFrameDeadline +=
            (long)(
                timestampFrequency *
                frameIntervalMs /
                1000.0);

        while (Stopwatch.GetTimestamp() <
               nextFrameDeadline)
        {
            Thread.SpinWait(100);
        }

        var totalStart =
            Stopwatch.GetTimestamp();

        // ---------------------------------------------------------
        // 1. Generate NV12 frame.
        // ---------------------------------------------------------

        var stageStart =
            Stopwatch.GetTimestamp();

        byte[] frame =
            Nv12TestFrame.Create(
                width,
                height,
                i);

        double generationMs =
            Stopwatch.GetElapsedTime(
                stageStart).TotalMilliseconds;

        generationTimes.Add(
            generationMs);

        // ---------------------------------------------------------
        // 2. Submit to encoder.
        // ---------------------------------------------------------

        stageStart =
            Stopwatch.GetTimestamp();

        encoder.SubmitFrame(frame);

        double submitMs =
            Stopwatch.GetElapsedTime(
                stageStart).TotalMilliseconds;

        submitTimes.Add(
            submitMs);

        // ---------------------------------------------------------
        // 3. Retrieve every encoded packet currently available.
        // ---------------------------------------------------------

        stageStart =
            Stopwatch.GetTimestamp();

        int packetsThisFrame = 0;

        while (encoder.TryGetEncodedFrame(
            out byte[]? encoded))
        {
            packetsThisFrame++;

            encodedPackets++;

            totalEncodedBytes +=
                encoded!.Length;
        }

        double retrieveMs =
            Stopwatch.GetElapsedTime(
                stageStart).TotalMilliseconds;

        retrieveTimes.Add(
            retrieveMs);

        double totalFrameMs =
            Stopwatch.GetElapsedTime(
                totalStart).TotalMilliseconds;

        pipelineTimes.Add(
            totalFrameMs);

        // Progress every second.
        if ((i + 1) % fps == 0)
        {
            Console.WriteLine(
                $"Frame {i + 1}/{frameCount} | " +
                $"Gen {generationMs:F2} ms | " +
                $"Encode {submitMs:F2} ms | " +
                $"Retrieve {retrieveMs:F2} ms | " +
                $"Total {totalFrameMs:F2} ms | " +
                $"Packets +{packetsThisFrame}");
        }
    }

    // -------------------------------------------------------------
    // Drain everything remaining in the encoder.
    // -------------------------------------------------------------

    Console.WriteLine();
    Console.WriteLine("Draining encoder...");

    encoder.Flush();

    while (encoder.TryGetFlushedFrame(
        out byte[]? encoded))
    {
        encodedPackets++;

        totalEncodedBytes +=
            encoded!.Length;
    }

    double benchmarkSeconds =
        benchmarkClock.Elapsed.TotalSeconds;

    // -------------------------------------------------------------
    // Statistics.
    // -------------------------------------------------------------

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
            (sorted.Length - 1) * percentile;

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

    Console.WriteLine();
    Console.WriteLine("========== RESULTS ==========");
    Console.WriteLine();

    Console.WriteLine(
        $"Frames generated:      {frameCount}");

    Console.WriteLine(
        $"Encoded packets:       {encodedPackets}");

    Console.WriteLine(
        $"Encoded bytes:         {totalEncodedBytes:N0}");

    Console.WriteLine(
        $"Benchmark duration:    {benchmarkSeconds:F2} s");

    Console.WriteLine(
        $"Effective FPS:         {frameCount / benchmarkSeconds:F2}");

    Console.WriteLine();

    Console.WriteLine("Frame generation");
    Console.WriteLine(
        $"  Average:             {Average(generationTimes):F3} ms");

    Console.WriteLine(
        $"  P95:                 {Percentile(generationTimes, 0.95):F3} ms");

    Console.WriteLine(
        $"  Max:                 {Maximum(generationTimes):F3} ms");

    Console.WriteLine();

    Console.WriteLine("Encoder submission");
    Console.WriteLine(
        $"  Average:             {Average(submitTimes):F3} ms");

    Console.WriteLine(
        $"  P95:                 {Percentile(submitTimes, 0.95):F3} ms");

    Console.WriteLine(
        $"  Max:                 {Maximum(submitTimes):F3} ms");

    Console.WriteLine();

    Console.WriteLine("Encoded output retrieval");
    Console.WriteLine(
        $"  Average:             {Average(retrieveTimes):F3} ms");

    Console.WriteLine(
        $"  P95:                 {Percentile(retrieveTimes, 0.95):F3} ms");

    Console.WriteLine(
        $"  Max:                 {Maximum(retrieveTimes):F3} ms");

    Console.WriteLine();

    Console.WriteLine("Local frame pipeline");
    Console.WriteLine(
        $"  Average:             {Average(pipelineTimes):F3} ms");

    Console.WriteLine(
        $"  P95:                 {Percentile(pipelineTimes, 0.95):F3} ms");

    Console.WriteLine(
        $"  Max:                 {Maximum(pipelineTimes):F3} ms");

    Console.WriteLine();

    Console.WriteLine(
        "Target frame interval: " +
        $"{frameIntervalMs:F3} ms");

    Console.WriteLine();

    if (Average(pipelineTimes) < frameIntervalMs)
    {
        Console.WriteLine(
            "PASS: Average local pipeline time is below the 60 FPS frame budget.");
    }
    else
    {
        Console.WriteLine(
            "WARNING: Average local pipeline time exceeds the 60 FPS frame budget.");
    }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine("BENCHMARK FAILED:");
    Console.WriteLine(ex);
}
