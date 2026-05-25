using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 消息查询、统计和重试服务
/// </summary>
public class MessageQueryService
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly IntegrationProjectService _integrationProjectService;

    public MessageQueryService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IntegrationProjectService integrationProjectService)
    {
        _contextFactory = contextFactory;
        _integrationProjectService = integrationProjectService;
    }

    public async Task<TodaySummary> GetTodaySummaryAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var today = DateTime.Today;
        var query = db.EsbMessages
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(m => m.CreatedAt >= today);

        return new TodaySummary
        {
            Total = await query.CountAsync(),
            Pending = await query.CountAsync(m => m.Status == MessageStatus.Pending),
            Processing = await query.CountAsync(m => m.Status == MessageStatus.Processing),
            Success = await query.CountAsync(m => m.Status == MessageStatus.Success),
            Failed = await query.CountAsync(m => m.Status == MessageStatus.Failed),
        };
    }

    public async Task<List<TranCodeStat>> GetTodayTranCodeStatsAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var today = DateTime.Today;

        return await db.EsbMessages
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(m => m.CreatedAt >= today)
            .GroupBy(m => new { m.TranCode, m.TranName })
            .Select(g => new TranCodeStat
            {
                TranCode = g.Key.TranCode,
                TranName = g.Key.TranName ?? "",
                Total = g.Count(),
                Pending = g.Count(m => m.Status == MessageStatus.Pending),
                Processing = g.Count(m => m.Status == MessageStatus.Processing),
                Success = g.Count(m => m.Status == MessageStatus.Success),
                Failed = g.Count(m => m.Status == MessageStatus.Failed),
            })
            .OrderBy(s => s.TranCode)
            .ToListAsync();
    }

    public async Task<List<EsbMessage>> GetRecentMessagesAsync(int count = 20)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        return await db.EsbMessages
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .OrderByDescending(m => m.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<(List<EsbMessage> Items, int TotalCount)> GetMessagesPagedAsync(
        int page,
        int pageSize,
        string? tranCode = null,
        MessageStatus? status = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? mrn = null,
        DateTime? eventStartTime = null,
        DateTime? eventEndTime = null)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var query = ApplyFilters(
            db.EsbMessages.AsNoTracking().WhereInProjectOrGlobal(currentProjectCode),
            tranCode,
            status,
            startTime,
            endTime,
            mrn,
            eventStartTime,
            eventEndTime);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<EsbMessage?> GetMessageByIdAsync(long id)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        return await db.EsbMessages
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<List<EsbProcessLog>> GetProcessLogsAsync(long messageId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        return await db.EsbProcessLogs
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(l => l.MessageId == messageId)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();
    }

    public async Task<bool> RetryMessageAsync(long id)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var message = await db.EsbMessages
            .WhereInProjectOrGlobal(currentProjectCode)
            .FirstOrDefaultAsync(m => m.Id == id);
        if (message == null || message.Status != MessageStatus.Failed)
            return false;

        message.Status = MessageStatus.Pending;
        message.ErrorMessage = null;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<int> BatchRetryAsync(
        string? tranCode = null,
        MessageStatus? status = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? mrn = null,
        DateTime? eventStartTime = null,
        DateTime? eventEndTime = null)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var query = ApplyFilters(
                db.EsbMessages.WhereInProjectOrGlobal(currentProjectCode),
                tranCode,
                status,
                startTime,
                endTime,
                mrn,
                eventStartTime,
                eventEndTime)
            .Where(m => m.Status == MessageStatus.Failed);

        var messages = await query.ToListAsync();
        foreach (var msg in messages)
        {
            msg.Status = MessageStatus.Pending;
            msg.ErrorMessage = null;
        }

        await db.SaveChangesAsync();
        return messages.Count;
    }

    public async Task<List<string>> GetDistinctTranCodesAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        return await db.EsbMessages
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(m => m.TranCode != "")
            .Select(m => m.TranCode)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    private static IQueryable<EsbMessage> ApplyFilters(
        IQueryable<EsbMessage> query,
        string? tranCode = null,
        MessageStatus? status = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? mrn = null,
        DateTime? eventStartTime = null,
        DateTime? eventEndTime = null)
    {
        if (!string.IsNullOrWhiteSpace(tranCode))
            query = query.Where(m => m.TranCode == tranCode);

        if (status.HasValue)
            query = query.Where(m => m.Status == status.Value);

        if (startTime.HasValue)
            query = query.Where(m => m.CreatedAt >= startTime.Value);

        if (endTime.HasValue)
        {
            var endExclusive = endTime.Value.AddMinutes(1);
            query = query.Where(m => m.CreatedAt < endExclusive);
        }

        if (!string.IsNullOrWhiteSpace(mrn))
        {
            var normalizedMrn = mrn.Trim();
            query = query.Where(m => m.Mrn == normalizedMrn);
        }

        if (eventStartTime.HasValue)
            query = query.Where(m => m.ResolvedEventTime >= eventStartTime.Value);

        if (eventEndTime.HasValue)
        {
            var eventEndExclusive = eventEndTime.Value.AddMinutes(1);
            query = query.Where(m => m.ResolvedEventTime < eventEndExclusive);
        }

        return query;
    }
}

public class TodaySummary
{
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
}

public class TranCodeStat
{
    public string TranCode { get; set; } = "";
    public string TranName { get; set; } = "";
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
}
