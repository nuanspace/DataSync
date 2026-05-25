using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace DataSync.CYYY.Services;

/// <summary>
/// 数据库推送实现（NTCare 等）
/// </summary>
public partial class DatabasePushService : IPushService
{
    private readonly ILogger<DatabasePushService> _logger;

    /// <summary>
    /// SQL 标识符白名单：仅允许字母、数字、下划线
    /// </summary>
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SafeIdentifierRegex();

    public DatabasePushService(ILogger<DatabasePushService> logger)
    {
        _logger = logger;
    }

    public async Task PushAsync(string pushTarget, string serverCode, List<Dictionary<string, object>> data, CancellationToken ct)
    {
        if (data.Count == 0) return;

        // 表名取 serverCode 的最后一段作为标识，统一小写
        var tableName = $"dl_{serverCode.Replace("-", "_").ToLower()}";
        ValidateIdentifier(tableName, "表名");

        await using var conn = new NpgsqlConnection(pushTarget);
        await conn.OpenAsync(ct);

        foreach (var row in data)
        {
            ct.ThrowIfCancellationRequested();

            // 校验所有列名
            foreach (var key in row.Keys)
                ValidateIdentifier(key, "列名");

            var columns = string.Join(", ", row.Keys.Select(k => $"\"{k}\""));
            var paramNames = string.Join(", ", row.Keys.Select((_, i) => $"@p{i}"));

            var sql = $"INSERT INTO \"{tableName}\" ({columns}) VALUES ({paramNames}) ON CONFLICT DO NOTHING";

            await using var cmd = new NpgsqlCommand(sql, conn);
            var idx = 0;
            foreach (var value in row.Values)
            {
                // JsonElement 转为字符串存储
                var dbValue = value is JsonElement je ? je.ToString() : value;
                cmd.Parameters.AddWithValue($"@p{idx}", dbValue ?? DBNull.Value);
                idx++;
            }

            await cmd.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("数据库推送成功：表 {Table}，{Count} 条", tableName, data.Count);
    }

    /// <summary>
    /// 校验 SQL 标识符是否安全（仅允许字母、数字、下划线）
    /// </summary>
    private static void ValidateIdentifier(string identifier, string label)
    {
        if (!SafeIdentifierRegex().IsMatch(identifier))
            throw new ArgumentException($"{label} \"{identifier}\" 包含非法字符，仅允许字母、数字和下划线");
    }
}
