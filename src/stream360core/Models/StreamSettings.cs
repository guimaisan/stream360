namespace Stream360.Core.Models;

public sealed class StreamSettings
{
    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    public int Fps { get; set; } = 60;

    public int Bitrate { get; set; } = 8_000_000;
}