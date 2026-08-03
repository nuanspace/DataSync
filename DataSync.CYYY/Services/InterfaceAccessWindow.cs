using System.Globalization;
using DataSync.CYYY.Models;

namespace DataSync.CYYY.Services;

/// <summary>
/// 接口每日访问时间窗判断。
/// </summary>
public static class InterfaceAccessWindow
{
    private const string TimeFormat = "HH:mm";

    public static bool IsOpen(SyncTaskInterface iface, DateTime now)
    {
        if (!iface.AccessWindowEnabled)
            return true;

        var (start, end) = Parse(iface);
        var current = TimeOnly.FromDateTime(now);
        return start < end
            ? current >= start && current < end
            : current >= start || current < end;
    }

    public static string GetProgressKey(SyncTaskInterface iface)
        => string.IsNullOrWhiteSpace(iface.InterfaceKey) ? $"ID:{iface.Id}" : iface.InterfaceKey;

    public static DateTime GetNextOpen(SyncTaskInterface iface, DateTime now)
    {
        if (IsOpen(iface, now))
            return now;

        var (start, _) = Parse(iface);
        var next = now.Date.Add(start.ToTimeSpan());
        return next > now ? next : next.AddDays(1);
    }

    public static void Validate(SyncTaskInterface iface)
    {
        if (iface.AccessWindowEnabled)
            Parse(iface);
    }

    private static (TimeOnly Start, TimeOnly End) Parse(SyncTaskInterface iface)
    {
        if (!TimeOnly.TryParseExact(iface.AccessWindowStart, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(iface.AccessWindowEnd, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end))
        {
            throw new InvalidOperationException($"接口 [{iface.DisplayName}] 的访问时间必须使用 HH:mm 格式");
        }

        if (start == end)
            throw new InvalidOperationException($"接口 [{iface.DisplayName}] 的访问开始时间和结束时间不能相同");

        return (start, end);
    }
}
