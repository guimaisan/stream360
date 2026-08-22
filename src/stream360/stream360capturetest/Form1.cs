using Stream360.Core.Capture;
using Stream360.Core.Decoding;
using Stream360.Core.Encoder;
using Stream360.Core.Encoding;
using Stream360.Core.Models;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Stream360.CaptureTest;

public sealed class Form1 : Form
{
    private readonly ComboBox _displaySelector;
    private readonly ComboBox _resolutionSelector;
    private readonly ComboBox _fpsSelector;
    private readonly ComboBox _bitrateSelector;

    private readonly Button _refreshButton;
    private readonly Button _startButton;
    private readonly Button _stopButton;

    private readonly PictureBox _preview;
    private readonly Label _statusLabel;
    private readonly Label _statsLabel;

    private readonly System.Windows.Forms.Timer _displayTimer;
    private readonly System.Windows.Forms.Timer _displayRefreshTimer;

    private WindowsGraphicsCaptureSource? _captureSource;
    private VideoProcessorConverter? _videoProcessor;

    private MediaFoundationEncoder? _encoder;
    private MediaFoundationDecoder? _decoder;

    private byte[]? _bgraBuffer;
    private byte[]? _nv12Buffer;

    private Bitmap? _previewBitmap;

    private StreamSettings _settings =
        new();

    private int _captureWidth;
    private int _captureHeight;

    private long _frameIndex;

    private long _fpsWindowStart;
    private int _processedFrames;

    private long _encodedPackets;
    private long _encodedBytes;
    private long _decodedFrames;

    private double _latestFrameWaitMs;
    private double _latestCaptureMs;
    private double _latestConversionMs;
    private double _latestEncodeMs;
    private double _latestDecodeMs;
    private double _latestLatencyMs;
    private double _latestEncoderOutputWaitMs;
    private double _latestDecoderOutputWaitMs;
    private double _pipelineFps;
    private double _latestLoopIntervalMs;

    private long _lastPipelineIteration;
    private long _frameWaitIterations;

    private bool _previewEnabled = false;

    private CancellationTokenSource? _pipelineCts;
    private Task? _pipelineTask;

    private readonly List<double>
        _endToEndLatencies = new();

    private readonly Dictionary<long, long>
        _captureTimesByMediaTimestamp = new();

    public Form1()
    {
        Text =
            "Stream360 - Display Capture / H.264 Test";

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
                Height = 125
            };

        _displaySelector =
            new ComboBox
            {
                Left = 10,
                Top = 10,
                Width = 260,
                DropDownStyle =
                    ComboBoxStyle.DropDownList
            };

        _resolutionSelector =
            new ComboBox
            {
                Left = 280,
                Top = 10,
                Width = 150,
                DropDownStyle =
                    ComboBoxStyle.DropDownList
            };

        _fpsSelector =
            new ComboBox
            {
                Left = 450,
                Top = 10,
                Width = 100,
                DropDownStyle =
                    ComboBoxStyle.DropDownList
            };

        _bitrateSelector =
            new ComboBox
            {
                Left = 570,
                Top = 10,
                Width = 130,
                DropDownStyle =
                    ComboBoxStyle.DropDownList
            };

        _refreshButton =
            new Button
            {
                Left = 710,
                Top = 10,
                Width = 90,
                Text = "Refresh"
            };

        _startButton =
            new Button
            {
                Left = 810,
                Top = 10,
                Width = 90,
                Text = "Start"
            };

        _stopButton =
            new Button
            {
                Left = 910,
                Top = 10,
                Width = 90,
                Text = "Stop",
                Enabled = false
            };

        _statusLabel =
            new Label
            {
                Left = 10,
                Top = 55,
                Width = 1300,
                Height = 40,
                Text = "Select a display."
            };

        _statsLabel =
            new Label
            {
                Left = 10,
                Top = 90,
                Width = 1300,
                Height = 25,
                Text = "FPS: 0"
            };

        _preview =
            new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode =
                    PictureBoxSizeMode.Zoom
            };

        topPanel.Controls.Add(
            _displaySelector);

        topPanel.Controls.Add(
            _resolutionSelector);

        topPanel.Controls.Add(
            _fpsSelector);

        topPanel.Controls.Add(
            _bitrateSelector);

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

        PopulateSettings();

        _refreshButton.Click +=
            (_, _) => RefreshDisplays();

        _startButton.Click +=
            (_, _) => StartCapture();

        _stopButton.Click +=
            (_, _) => StopCapture();

        _displayTimer =
            new System.Windows.Forms.Timer
            {
                Interval = 100
            };

        _displayTimer.Tick +=
            (_, _) => UpdateUiStats();

        _displayTimer.Start();

        _displayRefreshTimer =
            new System.Windows.Forms.Timer
            {
                Interval = 3000
            };

        _displayRefreshTimer.Tick +=
            (_, _) => RefreshDisplays();

        _displayRefreshTimer.Start();

        FormClosed +=
            (_, _) =>
            {
                StopCapture();

                _displayTimer.Dispose();
                _displayRefreshTimer.Dispose();
            };

        RefreshDisplays();
    }

    private void PopulateSettings()
    {
        _resolutionSelector.Items.Add(
            new ResolutionOption(
                1280,
                720,
                "1280 × 720"));

        _resolutionSelector.Items.Add(
            new ResolutionOption(
                1920,
                1080,
                "1920 × 1080"));

        _resolutionSelector.Items.Add(
            new ResolutionOption(
                960,
                540,
                "960 × 540"));

        _resolutionSelector.SelectedIndex =
            0;

        _fpsSelector.Items.Add(
            30);

        _fpsSelector.Items.Add(
            60);

        _fpsSelector.SelectedItem =
            60;

        _bitrateSelector.Items.Add(
            new BitrateOption(
                4_000_000,
                "4 Mbps"));

        _bitrateSelector.Items.Add(
            new BitrateOption(
                8_000_000,
                "8 Mbps"));

        _bitrateSelector.Items.Add(
            new BitrateOption(
                12_000_000,
                "12 Mbps"));

        _bitrateSelector.SelectedIndex =
            1;
    }

    private void RefreshDisplays()
    {
        var previous =
            _displaySelector.SelectedItem
            as DisplayInfo;

        IntPtr previousHandle =
            previous?.MonitorHandle ??
            IntPtr.Zero;

        _displaySelector.Items.Clear();

        foreach (var display in
                 EnumerateDisplays())
        {
            _displaySelector.Items.Add(
                display);
        }

        if (_displaySelector.Items.Count == 0)
        {
            _statusLabel.Text =
                "No displays found.";

            return;
        }

        if (previousHandle !=
            IntPtr.Zero)
        {
            for (int i = 0;
                 i < _displaySelector.Items.Count;
                 i++)
            {
                if (_displaySelector.Items[i]
                    is DisplayInfo info &&
                    info.MonitorHandle ==
                    previousHandle)
                {
                    _displaySelector.SelectedIndex =
                        i;

                    return;
                }
            }
        }

        var primary =
            Screen.PrimaryScreen;

        if (primary != null)
        {
            for (int i = 0;
                 i < _displaySelector.Items.Count;
                 i++)
            {
                if (_displaySelector.Items[i]
                    is DisplayInfo info &&
                    info.DeviceName ==
                    primary.DeviceName)
                {
                    _displaySelector.SelectedIndex =
                        i;

                    return;
                }
            }
        }

        _displaySelector.SelectedIndex =
            0;
    }

    private void StartCapture()
    {
        if (_displaySelector.SelectedItem
            is not DisplayInfo display)
        {
            MessageBox.Show(
                this,
                "Select a display first.",
                "Stream360");

            return;
        }

        if (_resolutionSelector.SelectedItem
            is not ResolutionOption resolution)
        {
            MessageBox.Show(
                this,
                "Select a resolution.",
                "Stream360");

            return;
        }

        if (_fpsSelector.SelectedItem
            is not int fps)
        {
            MessageBox.Show(
                this,
                "Select an FPS value.",
                "Stream360");

            return;
        }

        if (_bitrateSelector.SelectedItem
            is not BitrateOption bitrate)
        {
            MessageBox.Show(
                this,
                "Select a bitrate.",
                "Stream360");

            return;
        }

        try
        {
            StopCapture();

            _settings =
                new StreamSettings
                {
                    Width =
                        resolution.Width,

                    Height =
                        resolution.Height,

                    Fps =
                        fps,

                    Bitrate =
                        bitrate.Bitrate
                };

            _captureSource =
                new WindowsGraphicsCaptureSource();

            _captureSource.Start(
                display.MonitorHandle);

            _captureWidth =
                _captureSource.Width;

            _captureHeight =
                _captureSource.Height;

            if (_captureWidth <= 0 ||
                _captureHeight <= 0)
            {
                throw new InvalidOperationException(
                    "The display capture returned an invalid size.");
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

            _videoProcessor =
                new VideoProcessorConverter(
                    _captureWidth,
                    _captureHeight,
                    _settings.Width,
                    _settings.Height,
                    _settings.Fps);

            _encoder =
                new MediaFoundationEncoder(
                    h264);

            _decoder =
                new MediaFoundationDecoder();

            _encoder.Initialize(
                _settings.Width,
                _settings.Height,
                _settings.Fps,
                _settings.Bitrate);

            _decoder.Initialize(
                _settings.Width,
                _settings.Height,
                _settings.Fps);

            _bgraBuffer =
                new byte[
                    checked(
                        _captureWidth *
                        _captureHeight *
                        4)];

            _nv12Buffer =
                new byte[
                    checked(
                        _settings.Width *
                        _settings.Height *
                        3 /
                        2)];

            _previewBitmap =
                new Bitmap(
                    _settings.Width,
                    _settings.Height,
                    PixelFormat.Format32bppArgb);

            _preview.Image =
                _previewEnabled
                    ? _previewBitmap
                    : null;

            _frameIndex =
                0;

            _fpsWindowStart =
                Stopwatch.GetTimestamp();

            _processedFrames =
                0;

            _encodedPackets =
                0;

            _encodedBytes =
                0;

            _decodedFrames =
                0;

            _latestFrameWaitMs =
                0;

            _latestCaptureMs =
                0;

            _latestConversionMs =
                0;

            _latestEncodeMs =
                0;

            _latestDecodeMs =
                0;

            _latestLatencyMs =
                0;

            _latestEncoderOutputWaitMs =
                0;

            _latestDecoderOutputWaitMs =
                0;

            _pipelineFps =
                0;

            _latestLoopIntervalMs =
                0;

            _lastPipelineIteration =
                0;

            _frameWaitIterations =
                0;

            _endToEndLatencies.Clear();
            _captureTimesByMediaTimestamp.Clear();

            _statusLabel.Text =
                $"Display {display.Name} | " +
                $"capture {_captureWidth}x{_captureHeight} → " +
                $"stream {_settings.Width}x{_settings.Height} @ " +
                $"{_settings.Fps} FPS | " +
                $"{_settings.Bitrate / 1_000_000} Mbps | " +
                $"H.264: {h264.Name}";

            _statsLabel.Text =
                "Pipeline: 0 FPS";

            _pipelineCts =
                new CancellationTokenSource();

            _pipelineTask =
                Task.Run(
                    () => RunPipeline(
                        _pipelineCts.Token));

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
                "Stream360 capture failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void RunPipeline(
        CancellationToken cancellationToken)
    {
        if (_captureSource == null ||
            _videoProcessor == null ||
            _encoder == null ||
            _decoder == null ||
            _bgraBuffer == null ||
            _nv12Buffer == null)
        {
            return;
        }

        long fpsStart =
            Stopwatch.GetTimestamp();

        int processedFrames =
            0;

        while (!cancellationToken.IsCancellationRequested)
        {
            long loopStart =
                Stopwatch.GetTimestamp();

            if (_lastPipelineIteration != 0)
            {
                _latestLoopIntervalMs =
                    Stopwatch.GetElapsedTime(
                        _lastPipelineIteration)
                    .TotalMilliseconds;
            }

            _lastPipelineIteration =
                loopStart;

            // -----------------------------------------------------
            // BGRA readback + frame availability wait
            // -----------------------------------------------------

            var frameWaitStart =
                Stopwatch.GetTimestamp();

            bool gotFrame =
                _captureSource.TryGetLatestFrameAsBgra(
                    _bgraBuffer,
                    out int captureWidth,
                    out int captureHeight,
                    out int stride,
                    out long captureTimestamp);

            _latestFrameWaitMs =
                Stopwatch.GetElapsedTime(
                    frameWaitStart)
                .TotalMilliseconds;

            if (!gotFrame)
            {
                _frameWaitIterations++;
                Thread.Yield();
                continue;
            }

            if (captureWidth != _captureWidth ||
                captureHeight != _captureHeight)
            {
                continue;
            }

            _latestCaptureMs =
                0;

            // -----------------------------------------------------
            // BGRA -> NV12
            // -----------------------------------------------------

            var conversionStart =
                Stopwatch.GetTimestamp();

            if (!_videoProcessor.Convert(
                    _bgraBuffer,
                    _nv12Buffer,
                    captureTimestamp,
                    out int nv12Length))
            {
                continue;
            }

            _latestConversionMs =
                Stopwatch.GetElapsedTime(
                    conversionStart)
                .TotalMilliseconds;

            if (nv12Length <= 0)
            {
                continue;
            }

            // -----------------------------------------------------
            // Timestamp
            // -----------------------------------------------------

            long frameDuration =
                10_000_000L /
                _settings.Fps;

            long mediaTimestamp =
                _frameIndex *
                frameDuration;

            _captureTimesByMediaTimestamp[
                mediaTimestamp] =
                captureTimestamp;

            _frameIndex++;

            // -----------------------------------------------------
            // H.264 encode
            // -----------------------------------------------------

            var encodeStart =
                Stopwatch.GetTimestamp();

            _encoder.SubmitFrame(
                _nv12Buffer);

            _latestEncodeMs =
                Stopwatch.GetElapsedTime(
                    encodeStart)
                .TotalMilliseconds;

            // -----------------------------------------------------
            // Encoder output
            // -----------------------------------------------------

            var encoderOutputStart =
                Stopwatch.GetTimestamp();

            while (_encoder.TryGetEncodedFrame(
                       out var encodedPacket))
            {
                if (encodedPacket == null)
                {
                    continue;
                }

                _encodedPackets++;

                _encodedBytes +=
                    encodedPacket.Data.Length;

                // -------------------------------------------------
                // H.264 decode
                // -------------------------------------------------

                var decodeStart =
                    Stopwatch.GetTimestamp();

                _decoder.SubmitPacket(
                    encodedPacket);

                _latestDecodeMs =
                    Stopwatch.GetElapsedTime(
                        decodeStart)
                    .TotalMilliseconds;

                // -------------------------------------------------
                // Decoder output
                // -------------------------------------------------

                var decoderOutputStart =
                    Stopwatch.GetTimestamp();

                while (_decoder.TryGetDecodedFrame(
                           out var decodedFrame))
                {
                    if (decodedFrame == null)
                    {
                        continue;
                    }

                    _decodedFrames++;

                    if (_captureTimesByMediaTimestamp
                        .TryGetValue(
                            decodedFrame.Timestamp,
                            out long originalCapture))
                    {
                        _latestLatencyMs =
                            Stopwatch.GetElapsedTime(
                                originalCapture)
                            .TotalMilliseconds;

                        _endToEndLatencies.Add(
                            _latestLatencyMs);

                        _captureTimesByMediaTimestamp
                            .Remove(
                                decodedFrame.Timestamp);
                    }

                    if (_previewEnabled)
                    {
                        var frameCopy =
                            decodedFrame.Data.ToArray();

                        try
                        {
                            BeginInvoke(
                                () =>
                                {
                                    if (!IsDisposed)
                                    {
                                        DisplayNv12(
                                            frameCopy,
                                            _settings.Width,
                                            _settings.Height);
                                    }
                                });
                        }
                        catch
                        {
                            // Form may be shutting down.
                        }
                    }
                }

                _latestDecoderOutputWaitMs =
                    Stopwatch.GetElapsedTime(
                        decoderOutputStart)
                    .TotalMilliseconds;
            }

            _latestEncoderOutputWaitMs =
                Stopwatch.GetElapsedTime(
                    encoderOutputStart)
                .TotalMilliseconds;

            // -----------------------------------------------------
            // Pipeline FPS
            // -----------------------------------------------------

            processedFrames++;

            double elapsed =
                Stopwatch.GetElapsedTime(
                    fpsStart)
                .TotalSeconds;

            if (elapsed >= 1.0)
            {
                _pipelineFps =
                    processedFrames /
                    elapsed;

                processedFrames =
                    0;

                fpsStart =
                    Stopwatch.GetTimestamp();
            }
        }
    }

    private void UpdateUiStats()
    {
        if (_captureSource == null ||
            !_captureSource.IsCapturing)
        {
            return;
        }

        _statusLabel.Text =
            $"Loop {_latestLoopIntervalMs:F2} ms | " +
            $"Frame wait {_latestFrameWaitMs:F2} ms | " +
            $"Convert {_latestConversionMs:F2} ms | " +
            $"Encode {_latestEncodeMs:F2} ms | " +
            $"Encoder out {_latestEncoderOutputWaitMs:F2} ms";

        _statsLabel.Text =
            $"Decode {_latestDecodeMs:F2} ms | " +
            $"Decoder out {_latestDecoderOutputWaitMs:F2} ms | " +
            $"Latency {_latestLatencyMs:F2} ms | " +
            $"FPS {_pipelineFps:F1} | " +
            $"Encoded {_encodedPackets} | " +
            $"Decoded {_decodedFrames}";
    }

    private void DisplayNv12(
        byte[] nv12,
        int width,
        int height)
    {
        if (_previewBitmap == null)
        {
            return;
        }

        if (_previewBitmap.Width != width ||
            _previewBitmap.Height != height)
        {
            _previewBitmap.Dispose();

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
            int yPlaneSize =
                width *
                height;

            unsafe
            {
                byte* destination =
                    (byte*)bits.Scan0;

                for (int y = 0;
                     y < height;
                     y++)
                {
                    int yRow =
                        y *
                        width;

                    int uvRow =
                        (y / 2) *
                        width;

                    byte* destinationRow =
                        destination +
                        y *
                        bits.Stride;

                    for (int x = 0;
                         x < width;
                         x++)
                    {
                        int yValue =
                            nv12[
                                yRow +
                                x];

                        int uvIndex =
                            yPlaneSize +
                            uvRow +
                            (x & ~1);

                        int u =
                            nv12[
                                uvIndex];

                        int v =
                            nv12[
                                uvIndex + 1];

                        double c =
                            yValue -
                            16.0;

                        double d =
                            u -
                            128.0;

                        double e =
                            v -
                            128.0;

                        if (c < 0)
                        {
                            c = 0;
                        }

                        int r =
                            (int)Math.Round(
                                1.16438356 *
                                c +
                                1.79274107 *
                                e);

                        int g =
                            (int)Math.Round(
                                1.16438356 *
                                c -
                                0.21324861 *
                                d -
                                0.53290933 *
                                e);

                        int b =
                            (int)Math.Round(
                                1.16438356 *
                                c +
                                2.11240179 *
                                d);

                        int index =
                            x *
                            4;

                        destinationRow[index] =
                            ClampToByte(b);

                        destinationRow[index + 1] =
                            ClampToByte(g);

                        destinationRow[index + 2] =
                            ClampToByte(r);

                        destinationRow[index + 3] =
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

    private void StopCapture()
    {
        _pipelineCts?.Cancel();

        try
        {
            _pipelineTask?.Wait(
                TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore worker shutdown errors.
        }

        _pipelineTask =
            null;

        _pipelineCts?.Dispose();
        _pipelineCts =
            null;

        _encoder?.Dispose();
        _encoder =
            null;

        _decoder?.Dispose();
        _decoder =
            null;

        _videoProcessor?.Dispose();
        _videoProcessor =
            null;

        _captureSource?.Dispose();
        _captureSource =
            null;

        _bgraBuffer =
            null;

        _nv12Buffer =
            null;

        _preview.Image =
            null;

        _previewBitmap?.Dispose();
        _previewBitmap =
            null;

        _captureWidth =
            0;

        _captureHeight =
            0;

        _frameIndex =
            0;

        _lastPipelineIteration =
            0;

        _frameWaitIterations =
            0;

        _captureTimesByMediaTimestamp.Clear();

        _startButton.Enabled =
            true;

        _stopButton.Enabled =
            false;

        if (!IsDisposed)
        {
            _statusLabel.Text =
                "Stopped.";

            _statsLabel.Text =
                "Pipeline: 0 FPS";
        }
    }

    private static IEnumerable<DisplayInfo>
        EnumerateDisplays()
    {
        foreach (var screen in
                 Screen.AllScreens)
        {
            int x =
                screen.Bounds.Left + 1;

            int y =
                screen.Bounds.Top + 1;

            IntPtr monitor =
                MonitorFromPoint(
                    new POINT
                    {
                        X = x,
                        Y = y
                    },
                    MonitorDefaultToNearest);

            if (monitor == IntPtr.Zero)
            {
                continue;
            }

            string name =
                string.IsNullOrWhiteSpace(
                    screen.DeviceName)
                    ? $"Display {screen.GetHashCode()}"
                    : screen.DeviceName;

            yield return
                new DisplayInfo(
                    name,
                    screen.Bounds,
                    screen.DeviceName,
                    monitor,
                    screen.Primary);
        }
    }

    private static byte ClampToByte(
        int value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 255)
        {
            return 255;
        }

        return (byte)value;
    }

    private sealed record ResolutionOption(
        int Width,
        int Height,
        string Name)
    {
        public override string ToString()
        {
            return Name;
        }
    }

    private sealed record BitrateOption(
        int Bitrate,
        string Name)
    {
        public override string ToString()
        {
            return Name;
        }
    }

    private sealed record DisplayInfo(
        string Name,
        Rectangle Bounds,
        string DeviceName,
        IntPtr MonitorHandle,
        bool IsPrimary)
    {
        public override string ToString()
        {
            return
                $"{Name}" +
                (IsPrimary
                    ? " (Primary)"
                    : string.Empty) +
                $" — {Bounds.Width}×{Bounds.Height}";
        }
    }

    private const uint MonitorDefaultToNearest =
        0x00000002;

    [StructLayout(
        LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport(
        "user32.dll",
        ExactSpelling = true)]
    private static extern IntPtr
        MonitorFromPoint(
            POINT point,
            uint flags);
}