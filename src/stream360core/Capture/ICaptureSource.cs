namespace Stream360.Core.Capture;

public interface ICaptureSource : IDisposable
{
    bool IsCapturing
    {
        get;
    }

    int Width
    {
        get;
    }

    int Height
    {
        get;
    }

    void Start(
        IntPtr monitorHandle);

    void Stop();

    bool TryGetLatestFrame(
        out CapturedFrame? frame);
}