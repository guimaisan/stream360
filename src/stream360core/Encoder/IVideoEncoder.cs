
using Stream360.Core.Media;

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
        out EncodedPacket? packet);

    void Flush();

    bool TryGetFlushedFrame(
        out EncodedPacket? packet);

    void Stop();
}
