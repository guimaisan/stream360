using System.Runtime.InteropServices;
using Stream360.Core.Media;
using Vortice.MediaFoundation;

namespace Stream360.Core.Decoding;

public sealed class MediaFoundationDecoder : IVideoDecoder
{
    private readonly Queue<DecodedFrame> _pendingOutput = new();

    private IMFTransform? _transform;
    private bool _mediaFoundationStarted;

    private int _width;
    private int _height;
    private int _fps;

    public bool IsInitialized { get; private set; }

    public void Initialize(
        int width,
        int height,
        int fps)
    {
        if (IsInitialized)
            return;

        MediaFactory.MFStartup();
        _mediaFoundationStarted = true;

        try
        {
            _width = width;
            _height = height;
            _fps = fps;

            _pendingOutput.Clear();

            var decoders =
                MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoDecoder,
                    0,
                    null,
                    null);

            IMFActivate? selected = null;

            foreach (var decoder in decoders)
            {
                try
                {
                    var name =
                        decoder.GetString(
                            TransformAttributeKeys.MftFriendlyNameAttribute);

                    if (name.Equals(
                            "Microsoft H264 Video Decoder MFT",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        selected = decoder;
                        break;
                    }

                    decoder.Dispose();
                }
                catch
                {
                    decoder.Dispose();
                }
            }

            if (selected == null)
            {
                throw new InvalidOperationException(
                    "Microsoft H264 Video Decoder MFT was not found.");
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

            // ---------------------------------------------------------
            // H.264 INPUT
            // ---------------------------------------------------------

            using var inputType =
                MediaFactory.MFCreateMediaType();

            inputType.Set(
                MediaTypeAttributeKeys.MajorType,
                MediaTypeGuids.Video);

            inputType.Set(
                MediaTypeAttributeKeys.Subtype,
                VideoFormatGuids.H264);

            inputType.Set(
                MediaTypeAttributeKeys.FrameSize,
                MediaFactory.PackSize(
                    (uint)width,
                    (uint)height));

            inputType.Set(
                MediaTypeAttributeKeys.FrameRate,
                MediaFactory.PackRatio(
                    fps,
                    1));

            inputType.Set(
                MediaTypeAttributeKeys.InterlaceMode,
                (uint)VideoInterlaceMode.Progressive);

            _transform.SetInputType(
                0,
                inputType,
                0);

            // ---------------------------------------------------------
            // NV12 OUTPUT
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
                        subtype == VideoFormatGuids.NV12)
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
                    "The H.264 decoder did not advertise NV12 output.");
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
                    MediaTypeAttributeKeys.InterlaceMode,
                    (uint)VideoInterlaceMode.Progressive);

                _transform.SetOutputType(
                    0,
                    outputType,
                    0);
            }

            IsInitialized = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void SubmitPacket(
        EncodedPacket packet)
    {
        if (!IsInitialized ||
            _transform == null)
        {
            throw new InvalidOperationException(
                "Decoder has not been initialized.");
        }

        if (packet.Data.Length == 0)
        {
            throw new ArgumentException(
                "Encoded H.264 packet is empty.");
        }

        DrainAvailableOutput();

        using var inputBuffer =
            MediaFactory.MFCreateMemoryBuffer(
                packet.Data.Length);

        inputBuffer.Lock(
            out IntPtr inputData,
            out int inputMaxLength,
            out int inputCurrentLength);

        try
        {
            Marshal.Copy(
                packet.Data,
                0,
                inputData,
                packet.Data.Length);
        }
        finally
        {
            inputBuffer.Unlock();
        }

        inputBuffer.CurrentLength =
            packet.Data.Length;

        using var inputSample =
            MediaFactory.MFCreateSample();

        inputSample.AddBuffer(
            inputBuffer);

        inputSample.SampleTime =
            packet.Timestamp;

        _transform.ProcessInput(
            0,
            inputSample,
            0);

        DrainAvailableOutput();
    }

    private void DrainAvailableOutput()
    {
        if (_transform == null)
            return;

        while (true)
        {
            var output =
                TryProcessOneOutput();

            if (output == null)
                break;

            _pendingOutput.Enqueue(
                output);
        }
    }

    private DecodedFrame? TryProcessOneOutput()
    {
        if (_transform == null)
            return null;

        var streamInfo =
            _transform.GetOutputStreamInfo(0);

        int outputSize =
            Math.Max(
                streamInfo.Size,
                _width * _height * 3 / 2);

        using var outputBuffer =
            MediaFactory.MFCreateMemoryBuffer(
                outputSize);

        using var outputSample =
            MediaFactory.MFCreateSample();

        outputSample.AddBuffer(
            outputBuffer);

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

        try
        {
            _transform.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref outputData,
                out status);
        }
        catch
        {
            return null;
        }

        if (outputData.Sample == null)
            return null;

        using var decodedBuffer =
            outputData.Sample.GetBufferByIndex(0);

        decodedBuffer.Lock(
            out IntPtr decodedData,
            out int decodedMaxLength,
            out int decodedCurrentLength);

        try
        {
            if (decodedCurrentLength <= 0)
                return null;

            var frame =
                new byte[decodedCurrentLength];

            Marshal.Copy(
                decodedData,
                frame,
                0,
                decodedCurrentLength);

            long timestamp =
                outputData.Sample.SampleTime;

            return new DecodedFrame(
                frame,
                timestamp);
        }
        finally
        {
            decodedBuffer.Unlock();
        }
    }

    public bool TryGetDecodedFrame(
        out DecodedFrame? frame)
    {
        if (_pendingOutput.Count > 0)
        {
            frame =
                _pendingOutput.Dequeue();

            return true;
        }

        frame = null;

        return false;
    }

    public void Flush()
    {
        if (_transform == null)
            return;

        _transform.ProcessMessage(
            TMessageType.MessageCommandDrain,
            UIntPtr.Zero);

        while (true)
        {
            var output =
                TryProcessOneOutput();

            if (output == null)
                break;

            _pendingOutput.Enqueue(
                output);
        }
    }

    public void Stop()
    {
        _pendingOutput.Clear();

        _transform?.Dispose();
        _transform = null;

        if (_mediaFoundationStarted)
        {
            MediaFactory.MFShutdown();
            _mediaFoundationStarted = false;
        }

        IsInitialized = false;
    }

    public void Dispose()
    {
        Stop();
    }
}