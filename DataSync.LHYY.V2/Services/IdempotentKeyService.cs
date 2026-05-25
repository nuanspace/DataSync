using System.Security.Cryptography;
using System.Text;
using DataSync.LHYY.V2.Models.Entities;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 幂等键解析服务
/// </summary>
public class IdempotentKeyService
{
    private readonly ConfigService _configService;

    public IdempotentKeyService(ConfigService configService)
    {
        _configService = configService;
    }

    public string? ResolveSourceMessageId(JToken root, EsbInterfaceConfig config)
    {
        var context = MessageJsonHelper.ResolveMainRecordContext(root, config.MainRecordArrayPath);
        var value = MessageJsonHelper.ResolveFirstScopedToken(root, context, config.SourceMessageIdPath)?.ToString();
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        return MessageJsonHelper.TryGetLegacyMessageId(root);
    }

    public async Task<string?> BuildIdempotentKeyAsync(
        JToken root,
        EsbInterfaceConfig config,
        bool includeGlobalFallback = true)
    {
        var parts = await _configService.GetIdempotentKeyPartsAsync(
            config.TranCode,
            config.IntegrationProjectCode,
            includeGlobalFallback);
        if (parts.Count == 0)
            return null;

        var context = MessageJsonHelper.ResolveMainRecordContext(root, config.MainRecordArrayPath);
        var values = new List<string>();
        foreach (var part in parts)
        {
            var value = ResolvePartValue(root, context, part);
            values.Add(value ?? string.Empty);
        }

        if (values.All(string.IsNullOrWhiteSpace))
            return null;

        var raw = $"{config.TranCode}|{string.Join("|", values)}";
        return $"{config.TranCode}:{ComputeSha256(raw)}";
    }

    private static string? ResolvePartValue(JToken root, JToken context, EsbIdempotentKeyPart part)
    {
        if (!string.IsNullOrWhiteSpace(part.LiteralValue))
            return part.LiteralValue;

        var value = MessageJsonHelper.ResolveFirstScopedToken(root, context, part.SourcePath)?.ToString();
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        return part.DefaultValue;
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
