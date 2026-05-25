using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DataSync.LHYY.V2.Services;

internal static class AdvisoryLockKeyHelper
{
    public static long Build(string scope, params string?[] parts)
    {
        var value = scope + "|" + string.Join("|", parts.Select(p => p ?? ""));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }
}
