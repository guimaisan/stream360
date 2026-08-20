
namespace Stream360.Core.Media;

public sealed record EncodedPacket(
    byte[] Data,
    long Timestamp);

