namespace Stream360.Core.Capture;

public interface ICaptureSource : IDisposable
{
    bool IsCapturing { get; }

    int Width { get; }

    int Height { get; }

    void Start(
        IntPtr windowHandle);

    void Stop();

    bool TryGetLatestFrame(
        out CapturedFrame? frame);
}

public sealed class CapturedFrame : IDisposable
{
    public required Vortice.Direct3D11.ID3D11Texture2D Texture { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required long CaptureTimestamp { get; init; }

    public void Dispose()
    {
        Texture.Dispose();
    }
}