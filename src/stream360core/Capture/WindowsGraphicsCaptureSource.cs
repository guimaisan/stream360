using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace Stream360.Core.Capture;

public sealed class WindowsGraphicsCaptureSource : ICaptureSource
{
    private static readonly Guid GraphicsCaptureItemIid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly object _sync =
        new();

    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDirect3DDevice? _winRtDevice;

    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private ID3D11Texture2D? _stagingTexture;

    private CapturedFrame? _latestFrame;

    private long _receivedFrameCount;
    private Exception? _lastCaptureError;

    public bool IsCapturing
    {
        get;
        private set;
    }

    public int Width =>
        _captureItem?.Size.Width ?? 0;

    public int Height =>
        _captureItem?.Size.Height ?? 0;

    public long ReceivedFrameCount =>
        Interlocked.Read(
            ref _receivedFrameCount);

    public Exception? LastCaptureError =>
        _lastCaptureError;

    public void Start(
        IntPtr windowHandle)
    {
        if (IsCapturing)
            return;

        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException(
                "Window handle cannot be zero.",
                nameof(windowHandle));
        }

        try
        {
            Interlocked.Exchange(
                ref _receivedFrameCount,
                0);

            _lastCaptureError =
                null;

            CreateD3DDevice();

            _winRtDevice =
                CreateWinRtDevice();

            _captureItem =
                CreateCaptureItemForWindow(
                    windowHandle);

            if (!GraphicsCaptureSession.IsSupported())
            {
                throw new NotSupportedException(
                    "Windows Graphics Capture is not supported.");
            }

            _framePool =
                Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winRtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    3,
                    _captureItem.Size);

            _framePool.FrameArrived +=
                OnFrameArrived;

            _session =
                _framePool.CreateCaptureSession(
                    _captureItem);

            _session.IsCursorCaptureEnabled =
                false;

            _session.StartCapture();

            IsCapturing =
                true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private void OnFrameArrived(
        Direct3D11CaptureFramePool sender,
        object args)
    {
        Interlocked.Increment(
            ref _receivedFrameCount);

        try
        {
            using var frame =
                sender.TryGetNextFrame();

            if (frame == null)
                return;

            var dxgiSurface =
                GetDxgiSurface(
                    frame.Surface);

            try
            {
                var texture =
                    dxgiSurface.QueryInterface<
                        ID3D11Texture2D>();

                var captured =
                    new CapturedFrame
                    {
                        Texture =
                            texture,

                        Width =
                            frame.ContentSize.Width,

                        Height =
                            frame.ContentSize.Height,

                        CaptureTimestamp =
                            Stopwatch.GetTimestamp()
                    };

                lock (_sync)
                {
                    _latestFrame?.Dispose();

                    _latestFrame =
                        captured;
                }
            }
            finally
            {
                dxgiSurface.Dispose();
            }
        }
        catch (Exception ex)
        {
            _lastCaptureError =
                ex;

            Debug.WriteLine(
                $"Capture frame error: {ex}");
        }
    }

    public bool TryGetLatestFrame(
        out CapturedFrame? frame)
    {
        lock (_sync)
        {
            frame =
                _latestFrame;

            _latestFrame =
                null;

            return frame != null;
        }
    }

    public bool TryGetLatestFrameAsBgra(
    Span<byte> destination,
    out int width,
    out int height,
    out int stride,
    out long captureTimestamp)
    {
        width = 0;
        height = 0;
        stride = 0;
        captureTimestamp = 0;

        CapturedFrame? frame = null;

        try
        {
            if (!TryGetLatestFrame(
                    out frame) ||
                frame == null)
            {
                return false;
            }

            // NV12 requires even dimensions.
            // If Windows gives us an odd-sized content rectangle,
            // crop the final column/row rather than resizing it.
            width =
                frame.Width & ~1;

            height =
                frame.Height & ~1;

            if (width <= 0 ||
                height <= 0)
            {
                return false;
            }

            stride =
                width * 4;

            captureTimestamp =
                frame.CaptureTimestamp;

            int requiredSize =
                stride * height;

            if (destination.Length <
                requiredSize)
            {
                throw new ArgumentException(
                    $"Destination buffer is too small. " +
                    $"Required {requiredSize:N0} bytes, " +
                    $"received {destination.Length:N0} bytes.",
                    nameof(destination));
            }

            EnsureStagingTexture(
                frame.Texture);

            if (_stagingTexture == null ||
                _d3dContext == null)
            {
                return false;
            }

            _d3dContext.CopyResource(
                _stagingTexture,
                frame.Texture);

            _d3dContext.Flush();

            var mapped =
                _d3dContext.Map(
                    _stagingTexture,
                    0,
                    MapMode.Read,
                    Vortice.Direct3D11.MapFlags.None);

            try
            {
                unsafe
                {
                    byte* source =
                        (byte*)mapped.DataPointer;

                    for (int y = 0;
                         y < height;
                         y++)
                    {
                        var sourceRow =
                            new ReadOnlySpan<byte>(
                                source +
                                y * mapped.RowPitch,
                                stride);

                        sourceRow.CopyTo(
                            destination.Slice(
                                y * stride,
                                stride));
                    }
                }

                return true;
            }
            finally
            {
                _d3dContext.Unmap(
                    _stagingTexture,
                    0);
            }
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void EnsureStagingTexture(
        ID3D11Texture2D source)
    {
        if (_d3dDevice == null)
        {
            throw new InvalidOperationException(
                "D3D11 device is not initialized.");
        }

        var description =
            source.Description;

        if (_stagingTexture != null)
        {
            var current =
                _stagingTexture.Description;

            if (current.Width ==
                    description.Width &&
                current.Height ==
                    description.Height &&
                current.Format ==
                    description.Format)
            {
                return;
            }

            _stagingTexture.Dispose();
            _stagingTexture =
                null;
        }

        var stagingDescription =
            new Texture2DDescription(
                description.Format,
                description.Width,
                description.Height,
                1,
                1)
            {
                Usage =
                    ResourceUsage.Staging,

                BindFlags =
                    BindFlags.None,

                CPUAccessFlags =
                    CpuAccessFlags.Read,

                MiscFlags =
                    ResourceOptionFlags.None,

                SampleDescription =
                    new SampleDescription(
                        1,
                        0)
            };

        _stagingTexture =
            _d3dDevice.CreateTexture2D(
                stagingDescription);
    }

    private void CreateD3DDevice()
    {
        var result =
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport |
                DeviceCreationFlags.VideoSupport,
                Array.Empty<FeatureLevel>(),
                out _d3dDevice,
                out _d3dContext);

        result.CheckError();

        if (_d3dDevice == null ||
            _d3dContext == null)
        {
            throw new InvalidOperationException(
                "Failed to create the D3D11 device.");
        }
    }

    private IDirect3DDevice CreateWinRtDevice()
    {
        if (_d3dDevice == null)
        {
            throw new InvalidOperationException(
                "D3D11 device has not been initialized.");
        }

        using var dxgiDevice =
            _d3dDevice.QueryInterface<IDXGIDevice>();

        int hr =
            CreateDirect3D11DeviceFromDXGIDevice(
                dxgiDevice.NativePointer,
                out IntPtr graphicsDevice);

        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(
                hr);
        }

        try
        {
            return MarshalInterface<IDirect3DDevice>
                .FromAbi(graphicsDevice);
        }
        finally
        {
            Marshal.Release(
                graphicsDevice);
        }
    }

    private static GraphicsCaptureItem
        CreateCaptureItemForWindow(
            IntPtr windowHandle)
    {
        var interop =
            GraphicsCaptureItem.As<
                IGraphicsCaptureItemInterop>();

        IntPtr nativeItem =
            IntPtr.Zero;

        try
        {
            nativeItem =
                interop.CreateForWindow(
                    windowHandle,
                    GraphicsCaptureItemIid);

            if (nativeItem == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "CreateForWindow returned a null pointer.");
            }

            var item =
                GraphicsCaptureItem.FromAbi(
                    nativeItem);

            return item
                ?? throw new InvalidOperationException(
                    "Failed to create GraphicsCaptureItem.");
        }
        finally
        {
            if (nativeItem != IntPtr.Zero)
            {
                Marshal.Release(
                    nativeItem);
            }
        }
    }

    private static unsafe IDXGISurface
    GetDxgiSurface(
        IDirect3DSurface surface)
    {
        IObjectReference surfaceReference =
            ((IWinRTObject)surface).NativeObject;

        IntPtr surfaceAbi =
            surfaceReference.ThisPtr;

        if (surfaceAbi == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The Direct3D surface has no ABI pointer.");
        }

        Guid accessIid =
            new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

        int hr =
            Marshal.QueryInterface(
                surfaceAbi,
                in accessIid,
                out IntPtr accessPointer);

        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        if (accessPointer == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The Direct3D surface does not expose " +
                "IDirect3DDxgiInterfaceAccess.");
        }

        try
        {
            IntPtr vtable =
                Marshal.ReadIntPtr(
                    accessPointer);

            IntPtr getInterfaceAddress =
                Marshal.ReadIntPtr(
                    vtable,
                    IntPtr.Size * 3);

            var getInterface =
                (delegate* unmanaged<
                    IntPtr,
                    Guid*,
                    out IntPtr,
                    int>)
                getInterfaceAddress;

            Guid surfaceIid =
                typeof(IDXGISurface).GUID;

            IntPtr nativeSurface;

            hr =
                getInterface(
                    accessPointer,
                    &surfaceIid,
                    out nativeSurface);

            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (nativeSurface == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "GetInterface returned a null IDXGISurface.");
            }

            try
            {
                return new IDXGISurface(
                    nativeSurface);
            }
            catch
            {
                Marshal.Release(
                    nativeSurface);

                throw;
            }
        }
        finally
        {
            Marshal.Release(
                accessPointer);
        }
    }

    public void Stop()
    {
        IsCapturing =
            false;

        if (_framePool != null)
        {
            _framePool.FrameArrived -=
                OnFrameArrived;
        }

        _session?.Dispose();
        _session =
            null;

        _framePool?.Dispose();
        _framePool =
            null;

        _captureItem =
            null;

        lock (_sync)
        {
            _latestFrame?.Dispose();
            _latestFrame =
                null;
        }

        _stagingTexture?.Dispose();
        _stagingTexture =
            null;

        _winRtDevice =
            null;

        _d3dContext?.Dispose();
        _d3dContext =
            null;

        _d3dDevice?.Dispose();
        _d3dDevice =
            null;
    }

    public void Dispose()
    {
        Stop();
    }
    [DllImport(
    "d3d11.dll",
    ExactSpelling = true)]
    private static extern int
    CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
   

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(
            IntPtr window,
            [In] Guid iid);

        IntPtr CreateForMonitor(
            IntPtr monitor,
            [In] Guid iid);
    }
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(
            [In] Guid iid);
    }
}