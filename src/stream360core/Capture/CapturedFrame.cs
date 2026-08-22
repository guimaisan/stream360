using Vortice.Direct3D11;

namespace Stream360.Core.Capture;

public sealed class CapturedFrame : IDisposable
{
    public required ID3D11Texture2D Texture
    {
        get;
        init;
    }

    public required int Width
    {
        get;
        init;
    }

    public required int Height
    {
        get;
        init;
    }

    public required long CaptureTimestamp
    {
        get;
        init;
    }

    public void Dispose()
    {
        Texture.Dispose();
    }
}