
namespace Stream360.Core.Media;

public sealed record DecodedFrame(
    byte[] Data,
    long Timestamp);