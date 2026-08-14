using System.Globalization;
using System.Text.Json;

namespace DataSync.CYYY.Services;

/// <summary>
/// 对需要特殊归并语义的组合子接口记录进行选择，不改变其他组合接口的既有顺序和数量。
/// </summary>
internal static class CompositeChildRecordSelector
{
    private const string HtmlFileContentServerCode = "JHIDS-BAS-FBC-027";
    private const string HtmlFileContentsMountField = "FileContents";
    private static readonly TimeSpan HospitalTimeZoneOffset = TimeSpan.FromHours(8);

    private static readonly string[] HospitalDateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss.FFFFFFF"
    ];

    public static IReadOnlyList<Dictionary<string, object>> SelectForMount(
        string serverCode,
        string mountField,
        IReadOnlyList<Dictionary<string, object>> records)
    {
        if (!IsHtmlFileContents(serverCode, mountField) || records.Count <= 1)
            return records;

        var candidates = records
            .Select((record, index) => CreateCandidate(record, index))
            .Where(candidate => candidate.UpdatedAt.HasValue)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ThenByDescending(candidate => candidate.RowKey)
            .ThenBy(candidate => candidate.OriginalIndex)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"子接口 {HtmlFileContentServerCode} 返回 {records.Count} 条文件记录，但均缺少有效的 PDL_LAST_UPDATE/UPDATED_T，无法选择最新记录");
        }

        return [candidates[0].Record];
    }

    private static bool IsHtmlFileContents(string serverCode, string mountField) =>
        string.Equals(serverCode, HtmlFileContentServerCode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(mountField, HtmlFileContentsMountField, StringComparison.OrdinalIgnoreCase);

    private static Candidate CreateCandidate(Dictionary<string, object> record, int originalIndex)
    {
        var updatedAt = TryParseHospitalTimestamp(GetStringValue(record, "PDL_LAST_UPDATE"))
            ?? TryParseOffsetTimestamp(GetStringValue(record, "UPDATED_T"));
        var rowKey = long.TryParse(
            GetStringValue(record, "FBC_ROWKEY"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedRowKey)
            ? parsedRowKey
            : long.MinValue;

        return new Candidate(record, updatedAt, rowKey, originalIndex);
    }

    private static DateTimeOffset? TryParseHospitalTimestamp(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTime.TryParseExact(
                value.Trim(),
                HospitalDateTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), HospitalTimeZoneOffset);
    }

    private static DateTimeOffset? TryParseOffsetTimestamp(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }

    private static string GetStringValue(Dictionary<string, object> record, string fieldName)
    {
        var pair = record.FirstOrDefault(item =>
            string.Equals(item.Key, fieldName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(pair.Key))
            return "";

        return pair.Value switch
        {
            JsonElement element => element.ToString(),
            _ => pair.Value?.ToString() ?? ""
        };
    }

    private sealed record Candidate(
        Dictionary<string, object> Record,
        DateTimeOffset? UpdatedAt,
        long RowKey,
        int OriginalIndex);
}
