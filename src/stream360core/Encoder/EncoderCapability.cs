namespace Stream360.Core.Encoder;

public sealed class EncoderCapability
{
    public required EncoderInfo Encoder { get; init; }

    public bool SupportsCodec { get; init; }

    public bool SupportsNv12 { get; init; }

    public bool Supports720p60 { get; init; }

    public bool IsUsable =>
        SupportsCodec &&
        SupportsNv12;
}