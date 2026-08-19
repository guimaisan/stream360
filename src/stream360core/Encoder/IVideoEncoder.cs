
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

    byte[] EncodeFrame(
        ReadOnlySpan<byte> frame);

    void Flush();

    void Stop();
}