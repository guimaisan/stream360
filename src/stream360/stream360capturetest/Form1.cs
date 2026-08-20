using Stream360.Core.Capture;
using Stream360.Core.Decoding;
using Stream360.Core.Detection;
using Stream360.Core.Encoder;
using Stream360.Core.Encoding;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Stream360.CaptureTest;

public sealed class Form1 : Form
{
    private const int TargetFps = 60;
    private const int TargetBitrate = 8_000_000;

    private readonly ComboBox _windowSelector;
    private readonly Button _refreshButton;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly PictureBox _preview;
    private readonly Label _statusLabel;
    private readonly Label _statsLabel;

    private readonly System.Windows.Forms.Timer _displayTimer;
    private readonly System.Windows.Forms.Timer _windowRefreshTimer;

    private WindowsGraphicsCaptureSource? _captureSource;
    private long _captureFrameCount;
    private MediaFoundationEncoder? _encoder;
    private MediaFoundationDecoder? _decoder;

    private byte[]? _bgraBuffer;
    private byte[]? _nv12Buffer;

    private Bitmap? _previewBitmap;

    private long _frameIndex;
    private long _lastCaptureTimestamp;

    private long _fpsWindowStart;
    private int _processedFrames;

    private readonly List<double> _captureReadbackTimes = new();
    private readonly List<double> _conversionTimes = new();
    private readonly List<double> _encodeTimes = new();
    private readonly List<double> _decodeTimes = new();
    private readonly List<double> _endToEndLatencies = new();
    private void DisplayBgra(
    byte[] bgra,
    int width,
    int height,
    int stride)
    {
        if (_previewBitmap == null)
        {
            _previewBitmap =
                new Bitmap(
                    width,
                    height,
                    PixelFormat.Format32bppArgb);

            _preview.Image =
                _previewBitmap;
        }

        var bits =
            _previewBitmap.LockBits(
                new Rectangle(
                    0,
                    0,
                    width,
                    height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

        try
        {
            for (int y = 0;
                 y < height;
                 y++)
            {
                Marshal.Copy(
                    bgra,
                    y * stride,
                    IntPtr.Add(
                        bits.Scan0,
                        y * bits.Stride),
                    width * 4);
            }
        }
        finally
        {
            _previewBitmap.UnlockBits(
                bits);
        }

        _preview.Invalidate();
    }

    private readonly Dictionary<long, long>
        _captureTimesByMediaTimestamp = new();

    public Form1()
    {
        Text =
            "Stream360 - Capture / Encode / Decode Test";

        Width =
            1400;

        Height =
            900;

        StartPosition =
            FormStartPosition.CenterScreen;

        var topPanel =
            new Panel
            {
                Dock = DockStyle.Top,
                Height = 100
            };

        _windowSelector =
            new ComboBox
            {
                Left = 10,
                Top = 10,
                Width = 650,
                DropDownStyle =
                    ComboBoxStyle.DropDownList
            };

        _refreshButton =
            new Button
            {
                Left = 670,
                Top = 10,
                Width = 90,
                Text = "Refresh"
            };

        _startButton =
            new Button
            {
                Left = 770,
                Top = 10,
                Width = 90,
                Text = "Start"
            };

        _stopButton =
            new Button
            {
                Left = 870,
                Top = 10,
                Width = 90,
                Text = "Stop",
                Enabled = false
            };

        _statusLabel =
            new Label
            {
                Left = 10,
                Top = 50,
                Width = 1050,
                Height = 25,
                Text = "Select a window."
            };

        _statsLabel =
            new Label
            {
                Left = 1070,
                Top = 50,
                Width = 280,
                Height = 25,
                Text = "FPS: 0"
            };

        _preview =
            new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };

        topPanel.Controls.Add(
            _windowSelector);

        topPanel.Controls.Add(
            _refreshButton);

        topPanel.Controls.Add(
            _startButton);

        topPanel.Controls.Add(
            _stopButton);

        topPanel.Controls.Add(
            _statusLabel);

        topPanel.Controls.Add(
            _statsLabel);

        Controls.Add(
            _preview);

        Controls.Add(
            topPanel);

        _refreshButton.Click +=
            (_, _) => RefreshWindows();

        _startButton.Click +=
            (_, _) => StartCapture();

        _stopButton.Click +=
            (_, _) => StopCapture();

        _displayTimer =
            new System.Windows.Forms.Timer
            {
                Interval = 5
            };

        _displayTimer.Tick +=
            (_, _) => ProcessLatestFrame();

        _displayTimer.Start();

        _windowRefreshTimer =
            new System.Windows.Forms.Timer
            {
                Interval = 3000
            };

        _windowRefreshTimer.Tick +=
            (_, _) => RefreshWindows();

        _windowRefreshTimer.Start();

        FormClosed +=
            (_, _) =>
            {
                StopCapture();

                _displayTimer.Dispose();
                _windowRefreshTimer.Dispose();
            };

        RefreshWindows();
    }

    private void RefreshWindows()
    {
        var selected =
            _windowSelector.SelectedItem
            as WindowInfo;

        IntPtr selectedHandle =
            selected?.Handle ??
            IntPtr.Zero;

        _windowSelector.Items.Clear();

        var windows =
            EnumerateWindows().ToList();

        foreach (var window in windows)
        {
            _windowSelector.Items.Add(
                window);
        }

        if (windows.Count == 0)
        {
            _statusLabel.Text =
                "No visible windows found.";

            return;
        }

        if (selectedHandle !=
            IntPtr.Zero)
        {
            for (int i = 0;
                 i < _windowSelector.Items.Count;
                 i++)
            {
                if (_windowSelector.Items[i]
                    is WindowInfo info &&
                    info.Handle ==
                    selectedHandle)
                {
                    _windowSelector.SelectedIndex =
                        i;

                    return;
                }
            }
        }

        for (int i = 0;
             i < _windowSelector.Items.Count;
             i++)
        {
            if (_windowSelector.Items[i]
                is WindowInfo info &&
                info.Title.Contains(
                    "Chrome",
                    StringComparison.OrdinalIgnoreCase))
            {
                _windowSelector.SelectedIndex =
                    i;

                return;
            }
        }

        _windowSelector.SelectedIndex =
            0;
    }

    private void StartCapture()
    {
        if (_windowSelector.SelectedItem
            is not WindowInfo window)
        {
            MessageBox.Show(
                this,
                "Select a window first.",
                "Stream360");

            return;
        }

        try
        {
            StopCapture();

            _captureSource =
                new WindowsGraphicsCaptureSource();

            _captureSource.Start(
                window.Handle);

            int width =
                _captureSource.Width;

            int height =
                _captureSource.Height;

            if (width <= 0 ||
                height <= 0)
            {
                throw new InvalidOperationException(
                    "Capture returned an invalid size.");
            }

            if ((width & 1) != 0 ||
                (height & 1) != 0)
            {
                throw new InvalidOperationException(
                    $"Capture size {width}x{height} " +
                    "is not compatible with NV12. " +
                    "Resize the window to an even resolution.");
            }

            var encoders =
                EncoderDetector.GetAvailableEncoders();

            var h264 =
                encoders.FirstOrDefault(
                    e => e.Codec.Equals(
                        "H.264",
                        StringComparison.OrdinalIgnoreCase));

            if (h264 == null)
            {
                throw new InvalidOperationException(
                    "No H.264 hardware encoder was found.");
            }

            _encoder =
                new MediaFoundationEncoder(
                    h264);

            _decoder =
                new MediaFoundationDecoder();

            _encoder.Initialize(
                width,
                height,
                TargetFps,
                TargetBitrate);

            _decoder.Initialize(
                width,
                height,
                TargetFps);

            _bgraBuffer =
    new byte[
        width *
        height *
        4];

            _nv12Buffer =
                new byte[
                    width *
                    height *
                    3 /
                    2];

            _previewBitmap?.Dispose();

            _previewBitmap =
                new Bitmap(
                    width,
                    height,
                    PixelFormat.Format32bppArgb);

            _preview.Image =
                _previewBitmap;

            _frameIndex = 0;
            _lastCaptureTimestamp = 0;

            _fpsWindowStart =
                Stopwatch.GetTimestamp();

            _processedFrames =
                0;

            _captureReadbackTimes.Clear();
            _conversionTimes.Clear();
            _encodeTimes.Clear();
            _decodeTimes.Clear();
            _endToEndLatencies.Clear();
            _captureTimesByMediaTimestamp.Clear();

            _statusLabel.Text =
                $"Streaming {window.Title} | " +
                $"{width}x{height} @ {TargetFps} FPS";

            _startButton.Enabled =
                false;

            _stopButton.Enabled =
                true;
        }
        catch (Exception ex)
        {
            StopCapture();

            MessageBox.Show(
                this,
                ex.ToString(),
                "Stream360 pipeline failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ProcessLatestFrame()
    {
        if (_captureSource == null ||
            !_captureSource.IsCapturing ||
            _bgraBuffer == null)
        {
            return;
        }

        var captureStart =
            Stopwatch.GetTimestamp();

        if (!_captureSource.TryGetLatestFrameAsBgra(
                _bgraBuffer,
                out int width,
                out int height,
                out int stride,
                out long captureTimestamp))
        {
            return;
        }

        _captureReadbackTimes.Add(
            Stopwatch.GetElapsedTime(
                captureStart)
            .TotalMilliseconds);

        DisplayBgra(
            _bgraBuffer,
            width,
            height,
            stride);

        if (_lastCaptureTimestamp != 0)
        {
            double interval =
                Stopwatch.GetElapsedTime(
                    _lastCaptureTimestamp)
                .TotalMilliseconds;

            _statusLabel.Text =
                $"RAW CAPTURE {width}x{height} | " +
                $"interval {interval:F2} ms";
        }

        _lastCaptureTimestamp =
            captureTimestamp;

        _processedFrames++;

        double fpsElapsed =
            Stopwatch.GetElapsedTime(
                _fpsWindowStart)
            .TotalSeconds;

        if (fpsElapsed >= 1.0)
        {
            double fps =
                _processedFrames /
                fpsElapsed;

            _statsLabel.Text =
                $"Capture FPS: {fps:F1}";

            _processedFrames =
                0;

            _fpsWindowStart =
                Stopwatch.GetTimestamp();
        }
    }

    private double GetLatestLatency()
    {
        return _endToEndLatencies.Count == 0
            ? 0
            : _endToEndLatencies[^1];
    }

    private void DisplayNv12(
        byte[] nv12,
        int width,
        int height)
    {
        if (_previewBitmap == null)
            return;

        var bits =
            _previewBitmap.LockBits(
                new Rectangle(
                    0,
                    0,
                    width,
                    height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

        try
        {
            int yPlaneSize =
                width * height;

            unsafe
            {
                byte* destination =
                    (byte*)bits.Scan0;

                for (int y = 0;
                     y < height;
                     y++)
                {
                    int row =
                        y *
                        width;

                    for (int x = 0;
                         x < width;
                         x++)
                    {
                        int yValue =
                            nv12[
                                row +
                                x];

                        int uvRow =
                            (y / 2) *
                            width;

                        int uvIndex =
                            yPlaneSize +
                            uvRow +
                            (x & ~1);

                        int u =
                            nv12[uvIndex] -
                            128;

                        int v =
                            nv12[uvIndex + 1] -
                            128;

                        int c =
                            yValue -
                            16;

                        if (c < 0)
                            c = 0;

                        int r =
                            (298 * c +
                             459 * v +
                             128) >> 8;

                        int g =
                            (298 * c -
                             55 * u -
                             136 * v +
                             128) >> 8;

                        int b =
                            (298 * c +
                             541 * u +
                             128) >> 8;

                        int index =
                            y *
                            bits.Stride +
                            x * 4;

                        destination[index] =
                            ClampToByte(b);

                        destination[index + 1] =
                            ClampToByte(g);

                        destination[index + 2] =
                            ClampToByte(r);

                        destination[index + 3] =
                            255;
                    }
                }
            }
        }
        finally
        {
            _previewBitmap.UnlockBits(
                bits);
        }

        _preview.Invalidate();
    }

    private static byte ClampToByte(
        int value)
    {
        if (value < 0)
            return 0;

        if (value > 255)
            return 255;

        return (byte)value;
    }

    private void StopCapture()
    {
        _encoder?.Dispose();
        _encoder = null;

        _decoder?.Dispose();
        _decoder = null;

        _captureSource?.Dispose();
        _captureSource = null;

        _bgraBuffer = null;
        _nv12Buffer = null;

        _preview.Image = null;

        _previewBitmap?.Dispose();
        _previewBitmap = null;

        _startButton.Enabled =
            true;

        _stopButton.Enabled =
            false;

        if (!IsDisposed)
        {
            _statusLabel.Text =
                "Stopped.";
        }
    }

    private static IEnumerable<WindowInfo>
        EnumerateWindows()
    {
        var windows =
            new List<WindowInfo>();

        EnumWindows(
            (handle, _) =>
            {
                if (!IsWindowVisible(handle))
                    return true;

                if (handle ==
                    GetShellWindow())
                    return true;

                int length =
                    GetWindowTextLength(handle);

                if (length <= 0)
                    return true;

                var titleBuilder =
                    new System.Text.StringBuilder(
                        length + 1);

                GetWindowText(
                    handle,
                    titleBuilder,
                    titleBuilder.Capacity);

                string title =
                    titleBuilder.ToString();

                if (string.IsNullOrWhiteSpace(title))
                    return true;

                GetWindowThreadProcessId(
                    handle,
                    out uint processId);

                string processName;

                try
                {
                    processName =
                        Process.GetProcessById(
                            (int)processId)
                        .ProcessName;
                }
                catch
                {
                    processName =
                        "Unknown";
                }

                windows.Add(
                    new WindowInfo(
                        handle,
                        title,
                        processName));

                return true;
            },
            IntPtr.Zero);

        return windows.OrderBy(
            window => window.Title,
            StringComparer.OrdinalIgnoreCase);
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern bool EnumWindows(
        EnumWindowsProc lpEnumFunc,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(
        IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(
        IntPtr hWnd);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr hWnd,
        System.Text.StringBuilder lpString,
        int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    private delegate bool EnumWindowsProc(
        IntPtr hWnd,
        IntPtr lParam);

    private sealed record WindowInfo(
        IntPtr Handle,
        string Title,
        string ProcessName)
    {
        public override string ToString()
        {
            return $"{Title}  [{ProcessName}]";
        }
    }
}