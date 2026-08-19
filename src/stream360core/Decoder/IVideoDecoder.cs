namespace Stream360.Core.Decoding;

public interface IVideoDecoder : IDisposable
{
    void Initialize(
        int width,
        int height,
        int fps);

    void SubmitPacket(
        ReadOnlySpan<byte> encodedData);

    bool TryGetDecodedFrame(
        out byte[]? frame);

    void Flush();

    bool IsInitialized { get; }
}
