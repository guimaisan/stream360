
namespace Stream360.Core.Encoder;

public interface IVideoEncoder : IDisposable
{
    EncoderInfo Info { get; }

    bool IsInitialized { get; }

    void Initialize(
        int width,
        int height,
        int fps,
        int bitrate);

    void SubmitFrame(
        ReadOnlySpan<byte> frame);

    bool TryGetEncodedFrame(
        out byte[]? encodedFrame);

    void Flush();

    bool TryGetFlushedFrame(
        out byte[]? encodedFrame);

    void Stop();
}
