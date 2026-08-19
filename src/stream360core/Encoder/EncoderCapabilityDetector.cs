using Vortice.MediaFoundation;

namespace Stream360.Core.Encoder;

public static class EncoderCapabilityDetector
{
    public static EncoderCapability TestEncoder(EncoderInfo encoderInfo)
    {
        MediaFactory.MFStartup();

        try
        {
            IMFActivate? target = null;

            var activates = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                (uint)EnumFlag.EnumFlagHardware,
                null,
                null);

            foreach (var activate in activates)
            {
                try
                {
                    var name = activate.GetString(
                        TransformAttributeKeys.MftFriendlyNameAttribute);

                    if (string.Equals(
                        name,
                        encoderInfo.Name,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        target = activate;
                        break;
                    }
                }
                catch
                {
                    activate.Dispose();
                }
            }

            if (target == null)
            {
                return EmptyResult(encoderInfo);
            }

            try
            {
                using var transform =
                    target.ActivateObject<IMFTransform>();

                // Intel/other asynchronous hardware MFTs need this unlocked
                // before we query their types.
                try
                {
                    transform.Attributes.Set(
                        TransformAttributeKeys.TransformAsyncUnlock,
                        (uint)1);
                }
                catch
                {
                    // Some MFTs don't require this.
                }

                Console.WriteLine(
                    $"  Encoder activated: {encoderInfo.Name}");

                // ---------------------------------------------------------
                // Find an output type that the encoder itself advertises.
                // We do NOT construct one ourselves.
                // ---------------------------------------------------------

                IMFMediaType? outputType = null;

                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        var candidate =
                            transform.GetOutputAvailableType(0, i);

                        using var attributes =
                            candidate.QueryInterface<IMFAttributes>();

                        var major =
                            attributes.GetGUID(
                                MediaTypeAttributeKeys.MajorType);

                        if (major != MediaTypeGuids.Video)
                        {
                            candidate.Dispose();
                            continue;
                        }

                        var subtype =
                            attributes.GetGUID(
                                MediaTypeAttributeKeys.Subtype);

                        Console.WriteLine(
                            $"  Output #{i}: {subtype}");

                        // Use the encoder's own advertised type.
                        outputType = candidate;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"  Output type #{i} failed: {ex.Message}");

                        break;
                    }
                }

                if (outputType == null)
                {
                    return EmptyResult(encoderInfo);
                }

                try
                {
                    transform.SetOutputType(
                        0,
                        outputType,
                        0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"  Output type rejected: {ex.Message}");

                    return EmptyResult(encoderInfo);
                }
                finally
                {
                    outputType.Dispose();
                }

                // At this point the encoder has accepted one of its own
                // output types, so its codec is usable.
                bool supportsCodec = true;

                bool supportsNv12 = false;
                bool supports720p60 = false;

                // ---------------------------------------------------------
                // Ask the configured encoder what input types it supports.
                // ---------------------------------------------------------

                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        using var inputType =
                            transform.GetInputAvailableType(0, i);

                        using var attributes =
                            inputType.QueryInterface<IMFAttributes>();

                        var major =
                            attributes.GetGUID(
                                MediaTypeAttributeKeys.MajorType);

                        if (major != MediaTypeGuids.Video)
                            continue;

                        var subtype =
                            attributes.GetGUID(
                                MediaTypeAttributeKeys.Subtype);

                        Console.WriteLine(
                            $"  Input #{i}: {subtype}");

                        if (subtype != VideoFormatGuids.NV12)
                            continue;

                        supportsNv12 = true;

                        // -------------------------------------------------
                        // Read the encoder's advertised frame size.
                        // -------------------------------------------------

                        try
                        {
                            ulong packedSize =
                                attributes.GetUInt64(
                                    MediaTypeAttributeKeys.FrameSize);

                            uint width =
                                (uint)(packedSize >> 32);

                            uint height =
                                (uint)(packedSize & 0xFFFFFFFF);

                            // -------------------------------------------------
                            // Read its advertised frame rate.
                            // -------------------------------------------------

                            ulong packedRate =
                                attributes.GetUInt64(
                                    MediaTypeAttributeKeys.FrameRate);

                            uint numerator =
                                (uint)(packedRate >> 32);

                            uint denominator =
                                (uint)(packedRate & 0xFFFFFFFF);

                            if (width == 1280 &&
                                height == 720 &&
                                numerator == 60 &&
                                denominator == 1)
                            {
                                supports720p60 = true;
                            }

                            Console.WriteLine(
                                $"       {width}x{height} @ {numerator}/{denominator} FPS");
                        }
                        catch
                        {
                            // This input type didn't provide size/rate
                            // information, but it is still a valid NV12 type.
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"  Input type #{i} query ended: {ex.Message}");

                        break;
                    }
                }

                return new EncoderCapability
                {
                    Encoder = encoderInfo,
                    SupportsCodec = supportsCodec,
                    SupportsNv12 = supportsNv12,
                    Supports720p60 = supports720p60
                };
            }
            finally
            {
                target.Dispose();
            }
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static EncoderCapability EmptyResult(
        EncoderInfo encoder)
    {
        return new EncoderCapability
        {
            Encoder = encoder,
            SupportsCodec = false,
            SupportsNv12 = false,
            Supports720p60 = false
        };
    }
}