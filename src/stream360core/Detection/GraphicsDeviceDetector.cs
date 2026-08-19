using System.Management;
#pragma warning disable CA1416
namespace Stream360.Core.Detection;

public static class GraphicsDeviceDetector
{
    public static IReadOnlyList<GraphicsDeviceInfo> GetGraphicsDevices()
    {
        var devices = new List<GraphicsDeviceInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, AdapterCompatibility, DriverVersion " +
            "FROM Win32_VideoController");

        using var results = searcher.Get();

        foreach (ManagementObject device in results)
        {
            var name =
                device["Name"]?.ToString() ?? "Unknown GPU";

            var vendor =
                device["AdapterCompatibility"]?.ToString() ?? "Unknown";

            var driverVersion =
                device["DriverVersion"]?.ToString() ?? "Unknown";

            devices.Add(new GraphicsDeviceInfo
            {
                Name = name,
                Vendor = vendor,
                DriverVersion = driverVersion,

                IsNvidia = vendor.Contains(
                    "NVIDIA",
                    StringComparison.OrdinalIgnoreCase),

                IsIntel = vendor.Contains(
                    "Intel",
                    StringComparison.OrdinalIgnoreCase),

                IsAmd = vendor.Contains(
                    "AMD",
                    StringComparison.OrdinalIgnoreCase)
            });
        }

        return devices;
    }
}