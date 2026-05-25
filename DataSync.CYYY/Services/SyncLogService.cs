using System.Data.Common;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Services;

/// <summary>
/// 推送日志 + 检查点读写（线程安全：每次操作创建独立 DbContext）
/// </summary>
public class SyncLogService
{
    private readonly IDbContextFactory<SyncDbContext> _dbFactory;

    public SyncLogService(IDbContextFactory<SyncDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// 写入推送日志
    /// </summary>
    public async Task AddLogAsync(SyncLog log, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.SyncLogs.Add(log);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 批量写入推送日志
    /// </summary>
    public async Task AddLogsAsync(IEnumerable<SyncLog> logs, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.SyncLogs.AddRange(logs);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 获取检查点时间
    /// </summary>
    public async Task<DateTime?> GetCheckpointAsync(string taskCode, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cp = await db.SyncCheckpoints.FindAsync([taskCode], ct);
        return cp?.LastSuccessTime;
    }

    /// <summary>
    /// 更新检查点时间
    /// </summary>
    public async Task UpdateCheckpointAsync(string taskCode, DateTime lastSuccessTime, CancellationToken ct, int pollCount = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cp = await db.SyncCheckpoints.FindAsync([taskCode], ct);
        if (cp == null)
        {
            cp = new SyncCheckpoint
            {
                TaskCode = taskCode,
                LastSuccessTime = lastSuccessTime,
                LastPollCount = pollCount,
                UpdatedAt = DateTime.Now
            };
            db.SyncCheckpoints.Add(cp);
        }
        else
        {
            cp.LastSuccessTime = lastSuccessTime;
            cp.LastPollCount = pollCount;
            cp.UpdatedAt = DateTime.Now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 更新推送统计（仅在实际有数据推送时调用）
    /// </summary>
    public async Task UpdatePushCheckpointAsync(string taskCode, int successCount, int failCount, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cp = await db.SyncCheckpoints.FindAsync([taskCode], ct);
        if (cp == null)
        {
            cp = new SyncCheckpoint
            {
                TaskCode = taskCode,
                LastSuccessTime = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            db.SyncCheckpoints.Add(cp);
        }

        cp.LastPushTime = DateTime.Now;
        cp.LastPushSuccess = successCount;
        cp.LastPushFail = failCount;
        cp.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 查询推送日志（分页）
    /// </summary>
    public async Task<(List<SyncLog> Items, int Total)> QueryLogsAsync(
        string? taskCode, string? hisPatId, DateTime? from, DateTime? to,
        int page, int pageSize, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.SyncLogs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(taskCode))
            query = query.Where(l => l.TaskCode == taskCode);
        if (!string.IsNullOrWhiteSpace(hisPatId))
            query = query.Where(l => l.HisPatId == hisPatId);
        if (from.HasValue)
            query = query.Where(l => l.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(l => l.CreatedAt <= to.Value);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    /// <summary>
    /// 获取所有检查点
    /// </summary>
    public async Task<List<SyncCheckpoint>> GetAllCheckpointsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SyncCheckpoints
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// 获取所有已启用的任务（含接口列表）
    /// </summary>
    public async Task<List<SyncTask>> GetEnabledTasksAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SyncTasks
            .Where(t => t.Enabled)
            .Include(t => t.Interfaces.Where(i => i.Enabled))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 按 Code 获取任务
    /// </summary>
    public async Task<SyncTask?> GetTaskByCodeAsync(string code, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SyncTasks
            .Include(t => t.Interfaces.Where(i => i.Enabled))
            .FirstOrDefaultAsync(t => t.Code == code, ct);
    }

    /// <summary>
    /// 获取所有采集源（含禁用的）
    /// </summary>
    public async Task<List<IngestionSource>> GetAllSourcesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.IngestionSources
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// 清理指定天数之前的推送日志
    /// </summary>
    public async Task<int> CleanupLogsAsync(int retentionDays, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        return await db.SyncLogs.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// 获取采集源本地表记录数（表不存在返回 0）
    /// </summary>
    public async Task<int> GetLocalCountAsync(string serverCode, CancellationToken ct)
    {
        var tableName = IngestionService.GetLocalTableName(serverCode);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        // 先检查表是否存在
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table";
        var parts = tableName.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "public";
        var table = parts.Length == 2 ? parts[1] : parts[0];

        var schemaParam = checkCmd.CreateParameter();
        schemaParam.ParameterName = "@schema";
        schemaParam.Value = schema;
        checkCmd.Parameters.Add(schemaParam);

        var tableParam = checkCmd.CreateParameter();
        tableParam.ParameterName = "@table";
        tableParam.Value = table;
        checkCmd.Parameters.Add(tableParam);

        var exists = await checkCmd.ExecuteScalarAsync(ct);
        if (exists is null)
            return 0;

        // 查询记录数
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        var result = await countCmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// 获取所有任务的待同步对象统计
    /// </summary>
    public async Task<List<TaskPendingStats>> GetPendingStatsByTaskAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        return await db.PendingSyncItems
            .AsNoTracking()
            .GroupBy(item => item.TaskCode)
            .Select(group => new TaskPendingStats
            {
                TaskCode = group.Key,
                PendingCount = group.Count(item => item.Status == PendingSyncStatuses.Pending),
                WaitingCount = group.Count(item => item.Status == PendingSyncStatuses.Waiting),
                FailedCount = group.Count(item => item.Status == PendingSyncStatuses.Failed),
                RunningCount = group.Count(item => item.Status == PendingSyncStatuses.Running),
                SuccessCount = group.Count(item => item.Status == PendingSyncStatuses.Success),
                SkippedCount = group.Count(item => item.Status == PendingSyncStatuses.Skipped),
                LastUpdatedAt = group.Max(item => (DateTime?)item.UpdatedAt)
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// 分页查询待同步对象
    /// </summary>
    public async Task<(List<PendingSyncItem> Items, int Total)> QueryPendingItemsAsync(
        string? taskCode,
        string? status,
        string? keyword,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.PendingSyncItems
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(taskCode))
            query = query.Where(item => item.TaskCode == taskCode);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(item => item.Status == status);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var text = keyword.Trim();
            query = query.Where(item =>
                item.ObjectKey.Contains(text) ||
                item.HisPatId.Contains(text) ||
                item.PatVisitSn.Contains(text) ||
                item.PatName.Contains(text) ||
                item.SourceRecordKey.Contains(text) ||
                item.SourceServerCode.Contains(text) ||
                (item.LastError != null && item.LastError.Contains(text)) ||
                (item.TriggerPushError != null && item.TriggerPushError.Contains(text)));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    /// <summary>
    /// 查询单个待同步对象关联的接口执行明细
    /// </summary>
    public async Task<List<SyncLog>> QueryRelatedLogsAsync(
        string taskCode,
        string? hisPatId,
        string? patVisitSn,
        string? sourceRecordKey,
        int take,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskCode))
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.SyncLogs.AsNoTracking().Where(log => log.TaskCode == taskCode);

        if (!string.IsNullOrWhiteSpace(sourceRecordKey))
        {
            query = query.Where(log => log.SourceRecordKey == sourceRecordKey);
        }
        else if (!string.IsNullOrWhiteSpace(hisPatId))
        {
            query = query.Where(log => log.HisPatId == hisPatId);
        }
        else
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(sourceRecordKey))
        {
            return await query
                .OrderBy(log => log.CreatedAt)
                .ThenBy(log => log.Id)
                .Take(Math.Max(1, take))
                .ToListAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(patVisitSn))
            query = query.Where(log => log.PatVisitSn == patVisitSn);

        return await query
            .OrderBy(log => log.CreatedAt)
            .ThenBy(log => log.Id)
            .Take(Math.Max(1, take))
            .ToListAsync(ct);
    }
}
