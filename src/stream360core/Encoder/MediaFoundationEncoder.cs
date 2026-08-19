using Vortice.MediaFoundation;

namespace Stream360.Core.Encoder;

public sealed class MediaFoundationEncoder : IVideoEncoder
{
    private IMFTransform? _transform;
    private bool _mediaFoundationStarted;

    public EncoderInfo Info { get; }

    public bool IsInitialized { get; private set; }

    public MediaFoundationEncoder(EncoderInfo info)
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

        if (!string.Equals(
                Info.Codec,
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
            // Find the exact encoder activation again.
            var activates = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                (uint)EnumFlag.EnumFlagHardware,
                null,
                null);

            IMFActivate? selected = null;

            foreach (var activate in activates)
            {
                var name = activate.GetString(
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

            // Unlock asynchronous hardware MFTs.
            try
            {
                _transform.Attributes.Set(
                    TransformAttributeKeys.TransformAsyncUnlock,
                    (uint)1);
            }
            catch
            {
                // Some MFTs don't require async unlocking.
            }

            // ---------------------------------------------------------
            // OUTPUT: use a type advertised by the actual encoder.
            // ---------------------------------------------------------

            IMFMediaType? outputType = null;

            for (int i = 0; i < 20; i++)
            {
                try
                {
                    var candidate =
                        _transform.GetOutputAvailableType(0, i);

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
                    "The selected encoder did not provide an H.264 output type.");
            }

            using (outputType)
            {
                using var outputAttributes =
                    outputType.QueryInterface<IMFAttributes>();

                // Modify the encoder-provided type instead of constructing
                // a completely new one.
                outputAttributes.Set(
                    MediaTypeAttributeKeys.FrameSize,
                    MediaFactory.PackSize(
                        (uint)width,
                        (uint)height));

                outputAttributes.Set(
                    MediaTypeAttributeKeys.FrameRate,
                    MediaFactory.PackRatio(
                        fps,
                        1));

                outputAttributes.Set(
                    MediaTypeAttributeKeys.AvgBitrate,
                    (uint)bitrate);

                outputAttributes.Set(
                    MediaTypeAttributeKeys.InterlaceMode,
                    (uint)VideoInterlaceMode.Progressive);

                _transform.SetOutputType(
                    0,
                    outputType,
                    0);
            }

            // ---------------------------------------------------------
            // INPUT: find an NV12 type advertised by the encoder.
            // ---------------------------------------------------------

            IMFMediaType? inputType = null;

            for (int i = 0; i < 20; i++)
            {
                try
                {
                    var candidate =
                        _transform.GetInputAvailableType(0, i);

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
                    "The selected encoder did not advertise NV12 input.");
            }

            using (inputType)
            {
                using var inputAttributes =
                    inputType.QueryInterface<IMFAttributes>();

                inputAttributes.Set(
                    MediaTypeAttributeKeys.FrameSize,
                    MediaFactory.PackSize(
                        (uint)width,
                        (uint)height));

                inputAttributes.Set(
                    MediaTypeAttributeKeys.FrameRate,
                    MediaFactory.PackRatio(
                        fps,
                        1));

                inputAttributes.Set(
                    MediaTypeAttributeKeys.InterlaceMode,
                    (uint)VideoInterlaceMode.Progressive);

                _transform.SetInputType(
                    0,
                    inputType,
                    0);
            }

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
            _transform?.Dispose();
            _transform = null;

            if (_mediaFoundationStarted)
            {
                MediaFactory.MFShutdown();
                _mediaFoundationStarted = false;
            }

            throw;
        }
    }

    public byte[] EncodeFrame(ReadOnlySpan<byte> frame)
    {
        if (!IsInitialized || _transform == null)
        {
            throw new InvalidOperationException(
                "Encoder has not been initialized.");
        }

        throw new NotImplementedException(
            "Actual NV12 frame encoding is the next step.");
    }

    public void Flush()
    {
        if (!IsInitialized || _transform == null)
            return;

        _transform.ProcessMessage(
            TMessageType.MessageCommandDrain,
            UIntPtr.Zero);
    }

    public void Stop()
    {
        if (_transform != null)
        {
            _transform.Dispose();
            _transform = null;
        }

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