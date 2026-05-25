using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataSync.CYYY.Services;

public class TaskManagementService
{
    public sealed class TaskConflictInfo
    {
        public int TaskId { get; init; }

        public string TaskName { get; init; } = "";

        public string TaskCode { get; init; } = "";
    }

    public sealed class TaskCopyResult
    {
        public int TaskId { get; init; }

        public string TaskName { get; init; } = "";

        public string TaskCode { get; init; } = "";
    }

    private readonly IDbContextFactory<SyncDbContext> _dbFactory;

    public TaskManagementService(IDbContextFactory<SyncDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<TaskConflictInfo>> GetEnabledConflictsAsync(
        SyncTask task,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.TriggerServerCode) ||
            string.IsNullOrWhiteSpace(task.PushType) ||
            string.IsNullOrWhiteSpace(task.PushTarget))
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SyncTasks
            .AsNoTracking()
            .Where(t => t.Enabled &&
                t.Id != task.Id &&
                t.TriggerServerCode == task.TriggerServerCode &&
                t.PushType == task.PushType &&
                t.PushTarget == task.PushTarget)
            .OrderBy(t => t.Id)
            .Select(t => new TaskConflictInfo
            {
                TaskId = t.Id,
                TaskName = t.Name,
                TaskCode = t.Code
            })
            .ToListAsync(ct);
    }

    public async Task<TaskCopyResult> CopyTaskAsync(int taskId, CancellationToken ct)
    {
        await using var readDb = await _dbFactory.CreateDbContextAsync(ct);
        var source = await readDb.SyncTasks
            .AsNoTracking()
            .Include(t => t.Interfaces.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);

        if (source is null)
            throw new InvalidOperationException("原任务不存在，无法复制");

        for (var suffix = 1; suffix <= 999; suffix++)
        {
            var copyName = $"{source.Name}_副本{suffix}";
            var copyCode = $"{source.Code}_copy{suffix}";

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var exists = await db.SyncTasks
                .AnyAsync(t => t.Name == copyName || t.Code == copyCode, ct);
            if (exists)
                continue;

            var copy = CreateTaskCopy(source, copyName, copyCode);
            db.SyncTasks.Add(copy);

            try
            {
                await db.SaveChangesAsync(ct);
                return new TaskCopyResult
                {
                    TaskId = copy.Id,
                    TaskName = copy.Name,
                    TaskCode = copy.Code
                };
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                continue;
            }
        }

        throw new InvalidOperationException("副本数量已超过 999 个，请手动整理后再试");
    }

    public static string BuildEnableConflictMessage(
        SyncTask task,
        IReadOnlyCollection<TaskConflictInfo> conflicts)
    {
        var samples = string.Join("、", conflicts
            .Take(3)
            .Select(item => $"{item.TaskName}（{item.TaskCode}）"));
        var taskSummary = conflicts.Count <= 3
            ? samples
            : $"{samples} 等 {conflicts.Count} 个任务";

        return $"检测到已启用任务 {taskSummary} 与当前任务“{task.Name}”使用相同的患者来源、推送方式和推送目标。继续启用后可能重复同步，确定继续吗？";
    }

    private static SyncTask CreateTaskCopy(SyncTask source, string copyName, string copyCode)
    {
        return new SyncTask
        {
            Name = copyName,
            Code = copyCode,
            TriggerServerCode = source.TriggerServerCode,
            PushType = source.PushType,
            PushTarget = source.PushTarget,
            EnableTriggerRecordPush = source.EnableTriggerRecordPush,
            TriggerPushTarget = source.TriggerPushTarget,
            TriggerPushParams = source.TriggerPushParams,
            PatientIdField = source.PatientIdField,
            VisitSnField = source.VisitSnField,
            PollingIntervalSeconds = source.PollingIntervalSeconds,
            PatientConcurrency = source.PatientConcurrency,
            ApiConcurrency = source.ApiConcurrency,
            Enabled = false,
            TriggerConditions = source.TriggerConditions,
            Interfaces = source.Interfaces
                .OrderBy(i => i.SortOrder)
                .Select(CloneInterface)
                .ToList()
        };
    }

    private static SyncTaskInterface CloneInterface(SyncTaskInterface source)
    {
        return new SyncTaskInterface
        {
            ServerCode = source.ServerCode,
            DisplayName = source.DisplayName,
            QueryField = source.QueryField,
            IsRequired = source.IsRequired,
            SortOrder = source.SortOrder,
            Enabled = source.Enabled,
            InterfaceKey = source.InterfaceKey,
            ParentInterfaceKey = source.ParentInterfaceKey,
            QueryValueField = source.QueryValueField,
            ParentResultField = source.ParentResultField,
            MountField = source.MountField,
            RouteField = source.RouteField,
            RouteOperator = source.RouteOperator,
            RouteValue = source.RouteValue,
            OutputFields = source.OutputFields,
            PushParams = source.PushParams,
            InjectFields = source.InjectFields,
            FilterConditions = source.FilterConditions
        };
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
