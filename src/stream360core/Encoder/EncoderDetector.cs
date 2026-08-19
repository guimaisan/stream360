using Stream360.Core.Encoder;
using Vortice.MediaFoundation;

namespace Stream360.Core.Encoding;

public static class EncoderDetector
{
    public static IReadOnlyList<EncoderInfo> GetAvailableEncoders()
    {
        var encoders = new List<EncoderInfo>();

        MediaFactory.MFStartup();

        try
        {
            var activates = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                (uint)EnumFlag.EnumFlagHardware,
                null,
                null);

            var seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var activate in activates)
            {
                try
                {
                    var name = activate.GetString(
                        TransformAttributeKeys.MftFriendlyNameAttribute);

                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    // Multiple Media Foundation registrations can refer
                    // to the same underlying encoder.
                    if (!seen.Add(name))
                        continue;

                    var vendor = GetVendor(name);
                    var codec = GetCodec(name);

                    encoders.Add(new EncoderInfo
                    {
                        Name = name,
                        Vendor = vendor,
                        Codec = codec,
                        IsHardwareAccelerated = true
                    });
                }
                finally
                {
                    activate.Dispose();
                }
            }
        }
        finally
        {
            MediaFactory.MFShutdown();
        }

        return encoders;
    }

    private static string GetVendor(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("NVENC", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Quick Sync", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AMF", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        return "Unknown";
    }

    private static string GetCodec(string name)
    {
        if (name.Contains("H.264", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AVC", StringComparison.OrdinalIgnoreCase))
        {
            return "H.264";
        }

        if (name.Contains("H.265", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("H265", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("HEVC", StringComparison.OrdinalIgnoreCase))
        {
            return "HEVC";
        }

        if (name.Contains("AV1", StringComparison.OrdinalIgnoreCase))
        {
            return "AV1";
        }

        if (name.Contains("VP9", StringComparison.OrdinalIgnoreCase))
        {
            return "VP9";
        }

        return "Unknown";
    }
}