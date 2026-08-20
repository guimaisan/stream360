
using Stream360.Core.Media;

namespace Stream360.Core.Decoding;

public interface IVideoDecoder : IDisposable
{
    void Initialize(
        int width,
        int height,
        int fps);

    void SubmitPacket(
        EncodedPacket packet);

    bool TryGetDecodedFrame(
        out DecodedFrame? frame);

    void Flush();

    bool IsInitialized { get; }
}
