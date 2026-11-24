using System.Buffers.Binary;

namespace PocketAgent;

public static class MessageImage
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxDimension = 2048;
    internal const string ReferencePrefix = "pocketagent-image:";

    internal static string? Validate(byte[]? png)
    {
        if (png is null) return null;
        if (png.Length > MaxBytes) throw new ApiException(413, "Pasted images must be 2 MiB or smaller.");
        if (png.Length < 45 ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8)) != 13 ||
            !png.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            !png.AsSpan(png.Length - 12).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }))
            throw new ApiException(400, "The pasted image must be a PNG.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
        if (width is 0 or > MaxDimension || height is 0 or > MaxDimension)
            throw new ApiException(400, "Pasted images must be at most 2048 pixels per side.");
        return DataUrl(png);
    }

    public static string DataUrl(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);
}
