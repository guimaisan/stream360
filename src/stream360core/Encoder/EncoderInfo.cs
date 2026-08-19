
namespace Stream360.Core.Encoder;

public sealed class EncoderInfo
{
    public required string Name { get; init; }

    public required string Vendor { get; init; }

    public required string Codec { get; init; }

    public bool IsHardwareAccelerated { get; init; }

    public override string ToString()
    {
        return $"{Name} - {Codec} - {Vendor}";
    }
}
