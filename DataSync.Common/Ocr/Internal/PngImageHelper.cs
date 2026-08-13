using System.Buffers.Binary;

namespace DataSync.Common.Ocr.Internal;

internal static class PngImageHelper
{
    public static async Task<(int Width, int Height)> ReadSizeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var header = new byte[24];
        await using var stream = File.OpenRead(path);
        await stream.ReadExactlyAsync(header, cancellationToken);
        return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }
}
