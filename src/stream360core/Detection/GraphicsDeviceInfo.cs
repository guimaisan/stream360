namespace Stream360.Core.Detection;

public sealed class GraphicsDeviceInfo
{
    public required string Name { get; init; }

    public required string Vendor { get; init; }

    public required string DriverVersion { get; init; }

    public bool IsNvidia { get; init; }

    public bool IsIntel { get; init; }

    public bool IsAmd { get; init; }

    public override string ToString()
    {
        return $"{Name} ({Vendor})";
    }
}