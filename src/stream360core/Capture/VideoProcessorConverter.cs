using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Stream360.Core.Capture;

public sealed class VideoProcessorConverter : IDisposable
{
    private const int MfENotAccepting =
        unchecked((int)0xC00D36B5);

    private readonly int _inputWidth;
    private readonly int _inputHeight;

    private readonly int _outputWidth;
    private readonly int _outputHeight;

    private readonly int _fps;

    private IMFTransform? _transform;

    private IMFMediaBuffer? _inputBuffer;
    private IMFSample? _inputSample;

    private IMFMediaBuffer? _outputBuffer;
    private IMFSample? _outputSample;

    private bool _initialized;
    private bool _inputInFlight;

    public VideoProcessorConverter(
        int inputWidth,
        int inputHeight,
        int outputWidth,
        int outputHeight,
        int fps)
    {
        if (inputWidth <= 0 ||
            inputHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputWidth));
        }

        if (outputWidth <= 0 ||
            outputHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputWidth));
        }

        if ((outputWidth & 1) != 0 ||
            (outputHeight & 1) != 0)
        {
            throw new ArgumentException(
                "NV12 output dimensions must be even.");
        }

        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fps));
        }

        _inputWidth =
            inputWidth;

        _inputHeight =
            inputHeight;

        _outputWidth =
            outputWidth;

        _outputHeight =
            outputHeight;

        _fps =
            fps;

        Initialize();
    }

    private void Initialize()
    {
        MediaFactory.MFStartup();

        try
        {
            var activates =
                MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoProcessor,
                    0,
                    null,
                    null);

            IMFActivate? selected =
                null;

            foreach (var activate in activates)
            {
                try
                {
                    string name =
                        activate.GetString(
                            TransformAttributeKeys
                                .MftFriendlyNameAttribute);

                    if (string.Equals(
                            name,
                            "Microsoft Video Processor MFT",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        selected =
                            activate;

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
                    "Microsoft Video Processor MFT was not found.");
            }

            try
            {
                _transform =
                    selected.ActivateObject<
                        IMFTransform>();
            }
            finally
            {
                selected.Dispose();
            }

            ConfigureInputType();
            ConfigureOutputType();
            CreateBuffers();

            _transform.ProcessMessage(
                TMessageType.MessageNotifyBeginStreaming,
                UIntPtr.Zero);

            _transform.ProcessMessage(
                TMessageType.MessageNotifyStartOfStream,
                UIntPtr.Zero);

            _initialized =
                true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void ConfigureInputType()
    {
        if (_transform == null)
        {
            throw new InvalidOperationException();
        }

        using var inputType =
            MediaFactory.MFCreateMediaType();

        inputType.Set(
            MediaTypeAttributeKeys.MajorType,
            MediaTypeGuids.Video);

        // MFVideoFormat_RGB32
        inputType.Set(
            MediaTypeAttributeKeys.Subtype,
            new Guid(
                "00000016-0000-0010-8000-00AA00389B71"));

        inputType.Set(
            MediaTypeAttributeKeys.FrameSize,
            MediaFactory.PackSize(
                (uint)_inputWidth,
                (uint)_inputHeight));

        inputType.Set(
            MediaTypeAttributeKeys.FrameRate,
            MediaFactory.PackRatio(
                _fps,
                1));

        inputType.Set(
            MediaTypeAttributeKeys.InterlaceMode,
            (uint)VideoInterlaceMode.Progressive);

        inputType.Set(
            MediaTypeAttributeKeys.DefaultStride,
            unchecked(
                (uint)(
                    _inputWidth *
                    4)));

        _transform.SetInputType(
            0,
            inputType,
            0);
    }

    private void ConfigureOutputType()
    {
        if (_transform == null)
        {
            throw new InvalidOperationException();
        }

        IMFMediaType? outputType =
            null;

        for (int i = 0;
             i < 32;
             i++)
        {
            try
            {
                var candidate =
                    _transform.GetOutputAvailableType(
                        0,
                        i);

                using var attributes =
                    candidate.QueryInterface<
                        IMFAttributes>();

                var major =
                    attributes.GetGUID(
                        MediaTypeAttributeKeys.MajorType);

                var subtype =
                    attributes.GetGUID(
                        MediaTypeAttributeKeys.Subtype);

                if (major ==
                        MediaTypeGuids.Video &&
                    subtype ==
                        VideoFormatGuids.NV12)
                {
                    outputType =
                        candidate;

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
                "Microsoft Video Processor MFT did not " +
                "advertise NV12 output.");
        }

        using (outputType)
        {
            using var attributes =
                outputType.QueryInterface<
                    IMFAttributes>();

            attributes.Set(
                MediaTypeAttributeKeys.FrameSize,
                MediaFactory.PackSize(
                    (uint)_outputWidth,
                    (uint)_outputHeight));

            attributes.Set(
                MediaTypeAttributeKeys.FrameRate,
                MediaFactory.PackRatio(
                    _fps,
                    1));

            attributes.Set(
                MediaTypeAttributeKeys.InterlaceMode,
                (uint)VideoInterlaceMode.Progressive);

            _transform.SetOutputType(
                0,
                outputType,
                0);
        }
    }

    private void CreateBuffers()
    {
        if (_transform == null)
        {
            throw new InvalidOperationException();
        }

        int inputSize =
            checked(
                _inputWidth *
                _inputHeight *
                4);

        _inputBuffer =
            MediaFactory.MFCreateMemoryBuffer(
                inputSize);

        _inputSample =
            MediaFactory.MFCreateSample();

        _inputSample.AddBuffer(
            _inputBuffer);

        var outputInfo =
            _transform.GetOutputStreamInfo(
                0);

        int minimumOutputSize =
            checked(
                _outputWidth *
                _outputHeight *
                3 /
                2);

        int outputSize =
            Math.Max(
                outputInfo.Size,
                minimumOutputSize);

        _outputBuffer =
            MediaFactory.MFCreateMemoryBuffer(
                outputSize);

        _outputSample =
            MediaFactory.MFCreateSample();

        _outputSample.AddBuffer(
            _outputBuffer);
    }

    public bool Convert(
        ReadOnlySpan<byte> bgra,
        Span<byte> nv12,
        long timestamp,
        out int outputLength)
    {
        outputLength =
            0;

        if (!_initialized ||
            _transform == null ||
            _inputBuffer == null ||
            _inputSample == null ||
            _outputSample == null)
        {
            throw new InvalidOperationException(
                "Video Processor is not initialized.");
        }

        int inputSize =
            checked(
                _inputWidth *
                _inputHeight *
                4);

        int outputSize =
            checked(
                _outputWidth *
                _outputHeight *
                3 /
                2);

        if (bgra.Length <
            inputSize)
        {
            throw new ArgumentException(
                $"Expected at least {inputSize:N0} BGRA bytes.",
                nameof(bgra));
        }

        if (nv12.Length <
            outputSize)
        {
            throw new ArgumentException(
                $"Expected at least {outputSize:N0} NV12 bytes.",
                nameof(nv12));
        }

        if (_inputInFlight)
        {
            if (TryProcessOutput(
                    nv12,
                    out outputLength))
            {
                _inputInFlight =
                    false;

                return true;
            }

            return false;
        }

        CopyInput(
            bgra,
            inputSize);

        _inputSample.SampleTime =
            timestamp;

        _inputSample.SampleDuration =
            10_000_000L /
            _fps;

        try
        {
            _transform.ProcessInput(
                0,
                _inputSample,
                0);
        }
        catch (Exception ex)
            when (ex.HResult == MfENotAccepting)
        {
            _inputInFlight =
                true;

            return false;
        }

        _inputInFlight =
            true;

        if (TryProcessOutput(
                nv12,
                out outputLength))
        {
            _inputInFlight =
                false;

            return true;
        }

        return false;
    }

    private void CopyInput(
        ReadOnlySpan<byte> bgra,
        int inputSize)
    {
        if (_inputBuffer == null)
        {
            throw new InvalidOperationException();
        }

        _inputBuffer.Lock(
            out IntPtr inputData,
            out int inputMaxLength,
            out int _);

        try
        {
            if (inputMaxLength <
                inputSize)
            {
                throw new InvalidOperationException(
                    "Video Processor input buffer is too small.");
            }

            unsafe
            {
                fixed (byte* source =
                           bgra)
                {
                    new ReadOnlySpan<byte>(
                            source,
                            inputSize)
                        .CopyTo(
                            new Span<byte>(
                                (void*)inputData,
                                inputSize));
                }
            }
        }
        finally
        {
            _inputBuffer.Unlock();
        }

        _inputBuffer.CurrentLength =
            inputSize;
    }

    private bool TryProcessOutput(
        Span<byte> destination,
        out int outputLength)
    {
        outputLength =
            0;

        if (_transform == null ||
            _outputSample == null)
        {
            return false;
        }

        var outputData =
            new OutputDataBuffer
            {
                StreamID = 0,
                Sample = _outputSample,
                Status = 0,
                Events = null
            };

        var processStatus =
            default(ProcessOutputStatus);

        try
        {
            _transform.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref outputData,
                out processStatus);
        }
        catch
        {
            return false;
        }

        if (outputData.Sample == null)
        {
            return false;
        }

        using var buffer =
            outputData.Sample.GetBufferByIndex(
                0);

        buffer.Lock(
            out IntPtr data,
            out int _,
            out int length);

        try
        {
            if (length <= 0 ||
                length > destination.Length)
            {
                return false;
            }

            unsafe
            {
                new ReadOnlySpan<byte>(
                        (void*)data,
                        length)
                    .CopyTo(
                        destination);
            }

            outputLength =
                length;

            return true;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    public void Dispose()
    {
        if (_transform != null &&
            _initialized)
        {
            try
            {
                _transform.ProcessMessage(
                    TMessageType.MessageNotifyEndOfStream,
                    UIntPtr.Zero);
            }
            catch
            {
            }
        }

        _outputSample?.Dispose();
        _outputSample = null;

        _outputBuffer?.Dispose();
        _outputBuffer = null;

        _inputSample?.Dispose();
        _inputSample = null;

        _inputBuffer?.Dispose();
        _inputBuffer = null;

        _transform?.Dispose();
        _transform = null;

        _inputInFlight =
            false;

        _initialized =
            false;
    }
}