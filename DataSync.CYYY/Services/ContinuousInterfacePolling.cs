using System.Globalization;
using System.Text.Json;
using DataSync.CYYY.Models;

namespace DataSync.CYYY.Services;

public readonly record struct ContinuousPollingTimeRange(DateTime From, DateTime To, bool Discharged);

public sealed class ContinuousPollingDataNotReadyException(string message) : Exception(message);

public static class ContinuousInterfacePolling
{
    public static async Task<ContinuousPollingTimeRange> ResolveTimeRangeAsync(
        SyncTaskInterface iface,
        SyncTask task,
        IReadOnlyDictionary<string, object> triggerRecord,
        LocalQueryService localQueryService,
        DateTime now,
        CancellationToken ct)
    {
        Validate(iface, task);

        var visitSn = ReadText(triggerRecord, task.VisitSnField!);
        if (string.IsNullOrWhiteSpace(visitSn))
            throw new ContinuousPollingDataNotReadyException(
                $"接口 [{iface.DisplayName}] 的触发记录缺少就诊号字段 [{task.VisitSnField}]");

        var startText = await localQueryService.QueryLatestFieldValueAsync(
            iface.QueryStartTimeSourceServerCode!,
            iface.QueryStartTimeSourceField!,
            task.VisitSnField!,
            visitSn,
            ct);
        if (string.IsNullOrWhiteSpace(startText))
            throw new ContinuousPollingDataNotReadyException(
                $"接口 [{iface.DisplayName}] 尚未查询到入院时间");

        var endText = await localQueryService.QueryLatestFieldValueAsync(
            iface.QueryEndTimeSourceServerCode!,
            iface.QueryEndTimeSourceField!,
            task.VisitSnField!,
            visitSn,
            ct);
        var discharged = !string.IsNullOrWhiteSpace(endText);
        var from = ParseTime(startText, iface.QueryStartTimeSourceField!, iface.DisplayName, "入院");
        var to = discharged
            ? ParseTime(endText!, iface.QueryEndTimeSourceField!, iface.DisplayName, "出院")
            : now;

        if (to < from)
            throw new InvalidOperationException($"接口 [{iface.DisplayName}] 的查询结束时间早于入院时间");

        return new ContinuousPollingTimeRange(from, to, discharged);
    }

    private static void Validate(SyncTaskInterface iface, SyncTask task)
    {
        if (string.IsNullOrWhiteSpace(task.VisitSnField))
            throw new InvalidOperationException($"任务 [{task.Name}] 未配置统一就诊号字段");
        if (string.IsNullOrWhiteSpace(iface.QueryStartTimeSourceServerCode) ||
            string.IsNullOrWhiteSpace(iface.QueryStartTimeSourceField))
            throw new InvalidOperationException($"接口 [{iface.DisplayName}] 未完整配置入院时间来源");
        if (string.IsNullOrWhiteSpace(iface.QueryEndTimeSourceServerCode) ||
            string.IsNullOrWhiteSpace(iface.QueryEndTimeSourceField))
            throw new InvalidOperationException($"接口 [{iface.DisplayName}] 未完整配置出院时间来源");
    }

    private static DateTime ParseTime(string value, string field, string interfaceName, string timeName)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var result) ||
            DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out result))
        {
            return result;
        }

        throw new InvalidOperationException(
            $"接口 [{interfaceName}] 的{timeName}时间字段 [{field}] 格式无效：{value}");
    }

    private static string? ReadText(IReadOnlyDictionary<string, object> record, string field)
    {
        var pair = record.FirstOrDefault(item =>
            string.Equals(item.Key, field, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(pair.Key) || pair.Value == null || pair.Value is DBNull)
            return null;

        return pair.Value is JsonElement element
            ? element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : element.ToString()
            : pair.Value.ToString();
    }
}
