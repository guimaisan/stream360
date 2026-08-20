using System.Runtime.InteropServices;
using Stream360.Core.Media;
using Vortice.MediaFoundation;

namespace Stream360.Core.Encoder;

public sealed class MediaFoundationEncoder : IVideoEncoder
{
    private readonly Queue<EncodedPacket> _pendingOutput = new();

    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _eventGenerator;
    private bool _mediaFoundationStarted;

    private int _width;
    private int _height;
    private int _fps;
    private long _nextTimestamp;

    public EncoderInfo Info { get; }

    public bool IsInitialized { get; private set; }

    public MediaFoundationEncoder(
        EncoderInfo info)
    {
        Info = info;
    }

    public void Initialize(
        int width,
        int height,
        int fps,
        int bitrate)
    {
        if (IsInitialized)
            return;

        if (!Info.Codec.Equals(
                "H.264",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"MediaFoundationEncoder currently supports H.264 only. " +
                $"Selected codec: {Info.Codec}");
        }

        MediaFactory.MFStartup();
        _mediaFoundationStarted = true;

        try
        {
            _width = width;
            _height = height;
            _fps = fps;
            _nextTimestamp = 0;

            _pendingOutput.Clear();

            var activates =
                MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoEncoder,
                    (uint)EnumFlag.EnumFlagHardware,
                    null,
                    null);

            IMFActivate? selected = null;

            foreach (var activate in activates)
            {
                try
                {
                    var name =
                        activate.GetString(
                            TransformAttributeKeys.MftFriendlyNameAttribute);

                    if (string.Equals(
                            name,
                            Info.Name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        selected = activate;
                        break;
                    }

                    activate.Dispose();
                }
                catch
                {
                    activate.Dispose();
                }
            }

            if (selected == null)
            {
                throw new InvalidOperationException(
                    $"Could not find encoder: {Info.Name}");
            }

            try
            {
                _transform =
                    selected.ActivateObject<IMFTransform>();
            }
            finally
            {
                selected.Dispose();
            }

            // ---------------------------------------------------------
            // Low-latency mode.
            // ---------------------------------------------------------

            Guid lowLatency =
                new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

            _transform.Attributes.Set(
                lowLatency,
                (uint)1);

            // Hardware encoder MFTs such as Quick Sync can be asynchronous.
            try
            {
                _transform.Attributes.Set(
                    TransformAttributeKeys.TransformAsyncUnlock,
                    (uint)1);
            }
            catch
            {
                // Some encoders do not require this.
            }

            _eventGenerator =
                _transform.QueryInterface<IMFMediaEventGenerator>();

            // ---------------------------------------------------------
            // OUTPUT TYPE
            // ---------------------------------------------------------

            IMFMediaType? outputType = null;

            for (int i = 0; i < 20; i++)
            {
                try
                {
                    var candidate =
                        _transform.GetOutputAvailableType(
                            0,
                            i);

                    using var attributes =
                        candidate.QueryInterface<IMFAttributes>();

                    var major =
                        attributes.GetGUID(
                            MediaTypeAttributeKeys.MajorType);

                    var subtype =
                        attributes.GetGUID(
                            MediaTypeAttributeKeys.Subtype);

                    if (major == MediaTypeGuids.Video &&
                        subtype == VideoFormatGuids.H264)
                    {
                        outputType = candidate;
                        break;
                    }

                    candidate.Dispose();
                }
                catch
                {
                    break;
                }
            }

            if (outputType == null)
            {
                throw new InvalidOperationException(
                    "The encoder did not advertise an H.264 output type.");
            }

            using (outputType)
            {
                using var attributes =
                    outputType.QueryInterface<IMFAttributes>();

                attributes.Set(
                    MediaTypeAttributeKeys.FrameSize,
                    MediaFactory.PackSize(
                        (uint)width,
                        (uint)height));

                attributes.Set(
                    MediaTypeAttributeKeys.FrameRate,
                    MediaFactory.PackRatio(
                        fps,
                        1));

                attributes.Set(
                    MediaTypeAttributeKeys.AvgBitrate,
                    (uint)bitrate);

                attributes.Set(
                    MediaTypeAttributeKeys.InterlaceMode,
                    (uint)VideoInterlaceMode.Progressive);

                _transform.SetOutputType(
                    0,
                    outputType,
                    0);
            }

            // ---------------------------------------------------------
            // INPUT TYPE
            // ---------------------------------------------------------

            IMFMediaType? inputType = null;

            for (int i = 0; i < 20; i++)
            {
                try
                {
                    var candidate =
                        _transform.GetInputAvailableType(
                            0,
                            i);

                    using var attributes =
                        candidate.QueryInterface<IMFAttributes>();

                    var major =
                        attributes.GetGUID(
                            MediaTypeAttributeKeys.MajorType);

                    var subtype =
                        attributes.GetGUID(
                            MediaTypeAttributeKeys.Subtype);

                    if (major == MediaTypeGuids.Video &&
                        subtype == VideoFormatGuids.NV12)
                    {
                        inputType = candidate;
                        break;
                    }

                    candidate.Dispose();
                }
                catch
                {
                    break;
                }
            }

            if (inputType == null)
            {
                throw new InvalidOperationException(
                    "The encoder did not advertise NV12 input.");
            }

            using (inputType)
            {
                using var attributes =
                    inputType.QueryInterface<IMFAttributes>();

                attributes.Set(
                    MediaTypeAttributeKeys.FrameSize,
                    MediaFactory.PackSize(
                        (uint)width,
                        (uint)height));

                attributes.Set(
                    MediaTypeAttributeKeys.FrameRate,
                    MediaFactory.PackRatio(
                        fps,
                        1));

                attributes.Set(
                    MediaTypeAttributeKeys.InterlaceMode,
                    (uint)VideoInterlaceMode.Progressive);

                _transform.SetInputType(
                    0,
                    inputType,
                    0);
            }

            _transform.ProcessMessage(
                TMessageType.MessageNotifyBeginStreaming,
                UIntPtr.Zero);

            _transform.ProcessMessage(
                TMessageType.MessageNotifyStartOfStream,
                UIntPtr.Zero);

            IsInitialized = true;

            Console.WriteLine(
                $"Initialized {Info.Name}");

            Console.WriteLine(
                $"Format: {width}x{height} @ {fps} FPS");

            Console.WriteLine(
                $"Bitrate: {bitrate} bits/s");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public unsafe void SubmitFrame(
    ReadOnlySpan<byte> frame)
    {
        if (!IsInitialized ||
            _transform == null ||
            _eventGenerator == null)
        {
            throw new InvalidOperationException(
                "Encoder has not been initialized.");
        }

        int expectedSize =
            _width *
            _height *
            3 /
            2;

        if (frame.Length != expectedSize)
        {
            throw new ArgumentException(
                $"Expected {expectedSize:N0} NV12 bytes, " +
                $"received {frame.Length:N0}.");
        }

        while (true)
        {
            using var mediaEvent =
                _eventGenerator.GetEvent(0);

            string eventName =
                mediaEvent.EventType.ToString();

            if (eventName.Contains(
                    "TransformNeedInput",
                    StringComparison.OrdinalIgnoreCase))
            {
                using var inputBuffer =
                    MediaFactory.MFCreateMemoryBuffer(
                        frame.Length);

                inputBuffer.Lock(
                    out IntPtr inputData,
                    out int inputMaxLength,
                    out int inputCurrentLength);

                try
                {
                    fixed (byte* frameData = frame)
                    {
                        var destination =
                            new Span<byte>(
                                (void*)inputData,
                                frame.Length);

                        frame.CopyTo(destination);
                    }
                }
                finally
                {
                    inputBuffer.Unlock();
                }

                inputBuffer.CurrentLength =
                    frame.Length;

                using var inputSample =
                    MediaFactory.MFCreateSample();

                inputSample.AddBuffer(
                    inputBuffer);

                long frameDuration =
                    10_000_000L / _fps;

                inputSample.SampleTime =
                    _nextTimestamp;

                inputSample.SampleDuration =
                    frameDuration;

                _nextTimestamp +=
                    frameDuration;

                _transform.ProcessInput(
                    0,
                    inputSample,
                    0);

                return;
            }

            if (eventName.Contains(
                    "TransformHaveOutput",
                    StringComparison.OrdinalIgnoreCase))
            {
                EncodedPacket? output =
                    ProcessOneOutput();

                if (output != null &&
                    output.Data.Length > 0)
                {
                    _pendingOutput.Enqueue(
                        output);
                }

                continue;
            }
        }
    }

    private EncodedPacket? ProcessOneOutput()
    {
        if (_transform == null)
            return null;

        var streamInfo =
            _transform.GetOutputStreamInfo(0);

        const int ProvidesSamples = 0x100;

        bool mftProvidesSamples =
            (streamInfo.Flags & ProvidesSamples) != 0;

        IMFMediaBuffer? outputBuffer = null;
        IMFSample? outputSample = null;

        try
        {
            if (!mftProvidesSamples)
            {
                int outputSize =
                    Math.Max(
                        streamInfo.Size,
                        1_048_576);

                outputBuffer =
                    MediaFactory.MFCreateMemoryBuffer(
                        outputSize);

                outputSample =
                    MediaFactory.MFCreateSample();

                outputSample.AddBuffer(
                    outputBuffer);
            }

            var outputData =
                new OutputDataBuffer
                {
                    StreamID = 0,
                    Sample = outputSample,
                    Status = 0,
                    Events = null
                };

            var status =
                default(ProcessOutputStatus);

            _transform.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref outputData,
                out status);

            if (outputData.Status == 0x100)
            {
                ReconfigureOutputType();

                return null;
            }

            if (outputData.Status == 0x300 ||
                outputData.Sample == null)
            {
                return null;
            }

            using var encodedBuffer =
                outputData.Sample.GetBufferByIndex(0);

            encodedBuffer.Lock(
                out IntPtr encodedData,
                out int encodedMaxLength,
                out int encodedCurrentLength);

            try
            {
                if (encodedCurrentLength <= 0)
                    return null;

                var result =
                    new byte[encodedCurrentLength];

                Marshal.Copy(
                    encodedData,
                    result,
                    0,
                    encodedCurrentLength);

                long timestamp =
                    outputData.Sample.SampleTime;

                return new EncodedPacket(
                    result,
                    timestamp);
            }
            finally
            {
                encodedBuffer.Unlock();
            }
        }
        finally
        {
            outputSample?.Dispose();
            outputBuffer?.Dispose();
        }
    }

    private void ReconfigureOutputType()
    {
        if (_transform == null)
        {
            throw new InvalidOperationException(
                "Encoder transform is unavailable.");
        }

        for (int i = 0; i < 20; i++)
        {
            try
            {
                var candidate =
                    _transform.GetOutputAvailableType(
                        0,
                        i);

                using var attributes =
                    candidate.QueryInterface<IMFAttributes>();

                var major =
                    attributes.GetGUID(
                        MediaTypeAttributeKeys.MajorType);

                var subtype =
                    attributes.GetGUID(
                        MediaTypeAttributeKeys.Subtype);

                if (major == MediaTypeGuids.Video &&
                    subtype == VideoFormatGuids.H264)
                {
                    _transform.SetOutputType(
                        0,
                        candidate,
                        0);

                    return;
                }

                candidate.Dispose();
            }
            catch
            {
                break;
            }
        }

        throw new InvalidOperationException(
            "The encoder requested an output format change, " +
            "but no usable H.264 output type was available.");
    }

    public bool TryGetEncodedFrame(
        out EncodedPacket? packet)
    {
        if (_pendingOutput.Count > 0)
        {
            packet =
                _pendingOutput.Dequeue();

            return true;
        }

        packet = null;

        return false;
    }

    public void Flush()
    {
        if (!IsInitialized ||
            _transform == null ||
            _eventGenerator == null)
        {
            throw new InvalidOperationException(
                "Encoder has not been initialized.");
        }

        _transform.ProcessMessage(
            TMessageType.MessageCommandDrain,
            UIntPtr.Zero);

        while (true)
        {
            using var mediaEvent =
                _eventGenerator.GetEvent(0);

            string eventName =
                mediaEvent.EventType.ToString();

            if (eventName.Contains(
                    "TransformHaveOutput",
                    StringComparison.OrdinalIgnoreCase))
            {
                EncodedPacket? output =
                    ProcessOneOutput();

                if (output != null &&
                    output.Data.Length > 0)
                {
                    _pendingOutput.Enqueue(
                        output);
                }

                continue;
            }

            if (eventName.Contains(
                    "TransformDrainComplete",
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    public bool TryGetFlushedFrame(
        out EncodedPacket? packet)
    {
        return TryGetEncodedFrame(
            out packet);
    }

    public void Stop()
    {
        if (_transform != null &&
            IsInitialized)
        {
            try
            {
                _transform.ProcessMessage(
                    TMessageType.MessageNotifyEndOfStream,
                    UIntPtr.Zero);
            }
            catch
            {
                // Ignore shutdown errors.
            }
        }

        _eventGenerator?.Dispose();
        _eventGenerator = null;

        _transform?.Dispose();
        _transform = null;

        if (_mediaFoundationStarted)
        {
            MediaFactory.MFShutdown();
            _mediaFoundationStarted = false;
        }

        IsInitialized = false;

        _pendingOutput.Clear();
        _nextTimestamp = 0;
    }

    public void Dispose()
    {
        Stop();
    }
}