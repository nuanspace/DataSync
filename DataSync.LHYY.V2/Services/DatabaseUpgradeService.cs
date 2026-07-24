using DataSync.LHYY.V2.Services.FollowUp;
using DataSync.LHYY.V2.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace DataSync.LHYY.V2.Services;

public sealed class DatabaseUpgradeService : IHostedService, IDisposable
{
    public const long MaxSqlFileUploadBytes = 200L * 1024 * 1024;

    private const string ArchiveOptimizationRelativePath = @"DatabaseUpgrades\EsbMessagesPerformanceOptimization\upgrade_esb_messages_archive_optimization.sql";
    private const string ArchiveOptimizationDescription = "ESB 消息冷热归档与查询性能优化：创建归档分区表、统一视图、关键索引，并迁移历史终态消息。";
    private const int MaxTaskLogItems = 200;
    private const int MaxTaskSnapshots = 50;
    private static readonly TimeSpan TaskSnapshotRetention = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DatabaseUpgradeTaskSnapshot> _tasks = new();
    private readonly ConcurrentDictionary<string, Guid> _activeTasksByConnection = new(StringComparer.Ordinal);
    private readonly Channel<ManagedScriptTaskRequest> _managedScriptTaskQueue = Channel.CreateUnbounded<ManagedScriptTaskRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource _serviceStoppingCts = new();

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly FollowUpCubeOperationCoordinator _cubeOperationCoordinator;
    private readonly string _cubeConnectionKey;
    private Task? _managedScriptWorker;

    public DatabaseUpgradeService(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        FollowUpCubeOperationCoordinator cubeOperationCoordinator)
    {
        _configuration = configuration;
        _environment = environment;
        _cubeOperationCoordinator = cubeOperationCoordinator;
        var cubeConnectionString = configuration.GetConnectionString("CubeDb")
            ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");
        _cubeConnectionKey = BuildConnectionFingerprint(
            new DatabaseConnectionOption("CubeDb", cubeConnectionString, "CubeDb"));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _managedScriptWorker ??= ProcessManagedScriptQueueAsync(_serviceStoppingCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _managedScriptTaskQueue.Writer.TryComplete();
        await _serviceStoppingCts.CancelAsync();

        if (_managedScriptWorker is null)
            return;

        await Task.WhenAny(_managedScriptWorker, Task.Delay(Timeout.Infinite, cancellationToken));
    }

    public void Dispose()
    {
        _serviceStoppingCts.Cancel();
        _serviceStoppingCts.Dispose();
        _stateLock.Dispose();
    }

    public List<DatabaseConnectionOption> GetConnectionOptions() =>
        _configuration.GetSection("ConnectionStrings")
            .GetChildren()
            .Select(section => new DatabaseConnectionOption(
                section.Key,
                section.Value ?? "",
                DescribeConnection(section.Key, section.Value ?? "")))
            .Where(item => !string.IsNullOrWhiteSpace(item.ConnectionString))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool IsUpgradeExecutionBlocked(string? connectionName)
    {
        if (!DeploymentModePolicy.IsExternalCube(_configuration) || string.IsNullOrWhiteSpace(connectionName))
            return false;

        return IsCubeConnection(GetConnection(connectionName));
    }

    public async Task<DatabaseUpgradeCheckResult> CheckAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        var scripts = LoadScripts();
        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return await BuildCheckResultAsync(target, scripts, connection, cancellationToken);
    }

    public async Task<DatabaseUpgradeExecuteResult> ExecuteAsync(
        string connectionName,
        string? pgDumpPath,
        bool skipBackup,
        CancellationToken cancellationToken = default)
        => await ExecuteLegacyScriptsAsync(connectionName, pgDumpPath, skipBackup, cancellationToken);

    public async Task<DatabaseUpgradeExecuteResult> ExecuteLegacyScriptsAsync(
        string connectionName,
        string? pgDumpPath,
        bool skipBackup,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        var lease = BeginExclusiveUpgradeOperation(target);
        try
        {
            await using var cubeLease = await AcquireCubeMaintenanceLeaseAsync(target, cancellationToken);
            var scripts = LoadScripts();
            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var checkResult = await BuildCheckResultAsync(target, scripts, connection, cancellationToken);
            var legacyKeys = checkResult.LegacyScripts
                .Select(script => script.ScriptKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var legacyScripts = scripts
                .Where(script => legacyKeys.Contains(script.RelativePath))
                .ToList();

            if (legacyScripts.Count == 0)
                return new DatabaseUpgradeExecuteResult(target, "", []);

            var backupFile = skipBackup
                ? "已手工备份，跳过自动备份"
                : await BackupDatabaseAsync(target.ConnectionString, pgDumpPath, cancellationToken);

            var executedScripts = new List<string>();
            foreach (var script in legacyScripts)
            {
                await ExecuteScriptAsync(connection, script, cancellationToken);
                executedScripts.Add(script.RelativePath);
            }

            return new DatabaseUpgradeExecuteResult(target, backupFile, executedScripts);
        }
        finally
        {
            EndExclusiveUpgradeOperation(lease);
        }
    }

    public async Task<T> RunExclusiveUpgradeOperationAsync<T>(
        string connectionName,
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        var lease = BeginExclusiveUpgradeOperation(target);
        try
        {
            await using var cubeLease = await AcquireCubeMaintenanceLeaseAsync(target, cancellationToken);
            return await operation();
        }
        finally
        {
            EndExclusiveUpgradeOperation(lease);
        }
    }

    public async Task<DatabaseUpgradeTaskSnapshot> StartManagedScriptTaskAsync(
        string connectionName,
        string scriptKey,
        string? pgDumpPath,
        bool skipBackup,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        EnsureUpgradeExecutionAllowed(target);
        var connectionKey = BuildConnectionFingerprint(target);
        if (HasRunningTask(connectionKey))
            throw new InvalidOperationException("当前目标库已有数据库升级任务正在执行，请等待完成后再操作。");

        var scripts = LoadScripts();
        await using (var connection = new NpgsqlConnection(target.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var checkResult = await BuildCheckResultAsync(target, scripts, connection, cancellationToken);
            if (!checkResult.IsStateWritable)
                throw new InvalidOperationException("状态文件不可写，不能执行优化脚本。请确认程序目录权限或保留 DatabaseUpgradeState 目录。");

            var script = checkResult.ManagedScripts.FirstOrDefault(item =>
                string.Equals(item.ScriptKey, scriptKey, StringComparison.OrdinalIgnoreCase));
            if (script is null)
                throw new InvalidOperationException("未找到优化脚本。");
            if (script.Status != DatabaseUpgradeScriptStatus.Pending)
                throw new InvalidOperationException("该脚本当前不是未执行状态，不能执行。");
        }

        var taskId = Guid.NewGuid();
        var snapshot = new DatabaseUpgradeTaskSnapshot(
            taskId,
            target.Name,
            scriptKey,
            DatabaseUpgradeTaskStatus.Queued,
            "已排队",
            "等待执行",
            null,
            null,
            [],
            [new DatabaseUpgradeTaskLogItem(DateTime.Now, "Info", "等待执行", "后台任务已排队")],
            null,
            DateTime.Now,
            null);

        _tasks[taskId] = snapshot;
        if (!_activeTasksByConnection.TryAdd(connectionKey, taskId))
        {
            _tasks.TryRemove(taskId, out _);
            throw new InvalidOperationException("当前目标库已有数据库升级任务正在执行，请等待完成后再操作。");
        }

        var request = new ManagedScriptTaskRequest(taskId, target, connectionKey, scriptKey, pgDumpPath, skipBackup);
        if (!_managedScriptTaskQueue.Writer.TryWrite(request))
        {
            _tasks.TryRemove(taskId, out _);
            EndExclusiveUpgradeOperation(new UpgradeOperationLease(connectionKey, taskId));
            throw new InvalidOperationException("数据库升级后台队列已停止，不能提交新任务。");
        }

        CleanupTaskSnapshots(taskId);
        return snapshot;
    }

    public DatabaseUpgradeTaskSnapshot? GetTaskSnapshot(Guid taskId) =>
        _tasks.TryGetValue(taskId, out var snapshot) ? snapshot : null;

    public async Task MarkScriptStatusAsync(
        string connectionName,
        string scriptKey,
        DatabaseUpgradeScriptStatus status,
        CancellationToken cancellationToken = default)
    {
        if (status is not DatabaseUpgradeScriptStatus.Executed and not DatabaseUpgradeScriptStatus.Pending)
            throw new InvalidOperationException("只能将脚本标记为已执行或未执行。");

        var target = GetConnection(connectionName);
        var connectionKey = BuildConnectionFingerprint(target);
        if (HasRunningTask(connectionKey))
            throw new InvalidOperationException("当前目标库已有数据库升级任务正在执行，不能标记脚本状态。");

        var scripts = LoadScripts();
        var script = scripts.FirstOrDefault(item => string.Equals(item.RelativePath, scriptKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未找到脚本。");
        if (script.Kind != UpgradeScriptKind.ManagedSql)
            throw new InvalidOperationException("该脚本状态不能手工标记。");

        await using (var connection = new NpgsqlConnection(target.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var checkResult = await BuildCheckResultAsync(target, scripts, connection, cancellationToken);
            if (!checkResult.IsStateWritable)
                throw new InvalidOperationException("状态文件不可写，不能标记脚本状态。");

            var currentScript = checkResult.ManagedScripts.FirstOrDefault(item =>
                string.Equals(item.ScriptKey, scriptKey, StringComparison.OrdinalIgnoreCase));
            if (currentScript is null)
                throw new InvalidOperationException("未找到优化脚本。");
            if (!currentScript.CanMarkStatus || currentScript.Status != DatabaseUpgradeScriptStatus.Unknown)
                throw new InvalidOperationException("只有当前状态为未知的普通优化脚本才能手工标记。");
        }

        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (HasRunningTask(connectionKey))
                throw new InvalidOperationException("当前目标库已有数据库升级任务正在执行，不能标记脚本状态。");

            var stateResult = await LoadStateDocumentNoLockAsync(cancellationToken);
            if (!stateResult.IsWritable)
                throw new InvalidOperationException("状态文件不可写，不能标记脚本状态。");

            var currentRecord = FindRecord(stateResult.Document, BuildConnectionFingerprintAliases(target), script.RelativePath);
            var currentStatus = ResolveStatus(
                script,
                currentRecord,
                ArchiveOptimizationState.NotInstalled,
                ArchiveEligibility.NotRequired,
                isLegacy: false);
            if (currentStatus != DatabaseUpgradeScriptStatus.Unknown)
                throw new InvalidOperationException("只有当前状态为未知的普通优化脚本才能手工标记。");

            var record = GetOrAddRecord(stateResult.Document, target, script);
            record.IsLegacy = false;
            record.BaselineUnknown = false;
            record.ScriptHash = script.Hash;
            record.ScriptName = script.Name;
            record.LastError = null;
            record.LastStatus = status == DatabaseUpgradeScriptStatus.Executed
                ? UpgradeScriptStateValues.Executed
                : null;
            record.ExecutedAt = status == DatabaseUpgradeScriptStatus.Executed
                ? DateTime.Now
                : null;
            var saveResult = await TrySaveStateDocumentNoLockAsync(stateResult.Document, cancellationToken);
            if (!saveResult.IsWritable)
                throw new InvalidOperationException(saveResult.ErrorMessage ?? "状态文件不可写，不能标记脚本状态。");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<string> SaveSqlFileAsync(
        string fileName,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        if (!fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许上传 .sql 文件。");

        var safeFileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var directory = Path.Combine(_environment.ContentRootPath, "DatabaseSqlFiles");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}_{safeFileName}");
        await using var target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
        return path;
    }

    public async Task CheckSqlFileAsync(
        string connectionName,
        string sqlFilePath,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        var path = ResolveSqlFilePath(sqlFilePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("SQL 文件不存在。", path);

        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(cancellationToken);
    }

    public async Task<DatabaseUpgradeExecuteResult> ExecuteSqlFileAsync(
        string connectionName,
        string sqlFilePath,
        string? pgDumpPath,
        bool skipBackup,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        var lease = BeginExclusiveUpgradeOperation(target);
        try
        {
            await using var cubeLease = await AcquireCubeMaintenanceLeaseAsync(target, cancellationToken);
            var path = ResolveSqlFilePath(sqlFilePath);
            if (!File.Exists(path))
                throw new FileNotFoundException("SQL 文件不存在。", path);

            var backupFile = skipBackup
                ? "已手工备份，跳过自动备份"
                : await BackupDatabaseAsync(target.ConnectionString, pgDumpPath, cancellationToken);
            await ExecuteSqlFileByPsqlAsync(target.ConnectionString, path, pgDumpPath, cancellationToken);

            return new DatabaseUpgradeExecuteResult(target, backupFile, [path]);
        }
        finally
        {
            EndExclusiveUpgradeOperation(lease);
        }
    }

    private async Task RunManagedScriptTaskAsync(
        Guid taskId,
        DatabaseConnectionOption target,
        string connectionKey,
        string scriptKey,
        string? pgDumpPath,
        bool skipBackup,
        CancellationToken cancellationToken)
    {
        var executedScripts = new List<string>();
        string? backupFile = null;
        UpgradeScript? script = null;
        var databaseWorkCompleted = false;

        try
        {
            await using var cubeLease = await AcquireCubeMaintenanceLeaseAsync(target, cancellationToken);
            UpdateTask(taskId, DatabaseUpgradeTaskStatus.Running, "脚本正在执行中", "正在连接目标数据库", null, backupFile, executedScripts, null);
            var scripts = LoadScripts();
            script = scripts.FirstOrDefault(item => string.Equals(item.RelativePath, scriptKey, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("未找到脚本。");

            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var checkResult = await BuildCheckResultAsync(target, scripts, connection, cancellationToken);
            if (!checkResult.IsStateWritable)
                throw new InvalidOperationException("状态文件不可写，不能执行优化脚本。请确认程序目录权限或保留 DatabaseUpgradeState 目录。");

            var currentScript = checkResult.ManagedScripts.FirstOrDefault(item =>
                string.Equals(item.ScriptKey, scriptKey, StringComparison.OrdinalIgnoreCase));
            if (currentScript is null)
                throw new InvalidOperationException("未找到优化脚本。");
            if (currentScript.Status != DatabaseUpgradeScriptStatus.Pending)
                throw new InvalidOperationException("该脚本当前不是未执行状态，不能执行。");

            UpdateTask(taskId, DatabaseUpgradeTaskStatus.Running, "备份数据库", skipBackup ? "已选择跳过自动备份" : "正在备份目标数据库", null, backupFile, executedScripts, null);
            backupFile = skipBackup
                ? "已手工备份，跳过自动备份"
                : await BackupDatabaseAsync(target.ConnectionString, pgDumpPath, cancellationToken);
            UpdateTask(taskId, DatabaseUpgradeTaskStatus.Running, "备份完成", backupFile, null, backupFile, executedScripts, null);

            if (script.Kind == UpgradeScriptKind.EsbMessagesArchiveOptimization)
            {
                await MessageArchiveTool.RunIntegratedUpgradeAsync(
                    target.ConnectionString,
                    _environment.ContentRootPath,
                    progress =>
                    {
                        UpdateTask(taskId, DatabaseUpgradeTaskStatus.Running, progress, progress, null, backupFile, executedScripts, null);
                    },
                    archiveProgress: progress =>
                    {
                        var taskProgress = new DatabaseUpgradeTaskProgress(
                            progress.TotalMessages,
                            progress.MigratedMessages,
                            progress.MigratedLogs,
                            progress.BatchIndex,
                            progress.CurrentBatchMessages,
                            progress.Threshold,
                            progress.Percent,
                            progress.IsCompleted);
                        var message = progress.IsCompleted
                            ? $"迁移完成：消息 {progress.MigratedMessages} 条，处理日志 {progress.MigratedLogs} 条。"
                            : $"迁移历史终态消息：已迁移 {progress.MigratedMessages}/{progress.TotalMessages} 条（{progress.Percent:0.##}%）。";
                        UpdateTask(taskId, DatabaseUpgradeTaskStatus.Running, "迁移历史终态消息", message, null, backupFile, executedScripts, null, taskProgress);
                    },
                    cancellationToken: cancellationToken);
            }
            else
            {
                UpdateTask(taskId, DatabaseUpgradeTaskStatus.Running, "执行脚本", $"正在执行脚本：{script.Name}", null, backupFile, executedScripts, null);
                await ExecuteScriptAsync(connection, script, cancellationToken);
            }

            databaseWorkCompleted = true;
            executedScripts.Add(script.RelativePath);
            UpdateTask(taskId, DatabaseUpgradeTaskStatus.Running, "执行完成", $"脚本执行完成：{script.Name}", null, backupFile, executedScripts, null);
            UpdateTask(taskId, DatabaseUpgradeTaskStatus.Running, "更新状态文件", "正在记录脚本执行状态", null, backupFile, executedScripts, null);
            await MarkScriptExecutedAsync(target, script, CancellationToken.None);
            UpdateTask(taskId, DatabaseUpgradeTaskStatus.Succeeded, "执行完成", "数据库升级完成。", null, backupFile, executedScripts, DateTime.Now);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            var errorMessage = databaseWorkCompleted
                ? $"数据库升级已执行，但任务停止前状态文件更新未完成：{ex.Message}"
                : "应用正在停止，数据库升级任务已取消。";
            UpdateTask(taskId, DatabaseUpgradeTaskStatus.Failed, "执行取消", "数据库升级任务已取消。", errorMessage, backupFile, executedScripts, DateTime.Now);

            if (script is not null && !databaseWorkCompleted)
            {
                try
                {
                    await MarkScriptFailedAsync(target, script, errorMessage, CancellationToken.None);
                }
                catch (Exception stateEx)
                {
                    UpdateTask(taskId, DatabaseUpgradeTaskStatus.Failed, "执行取消", "数据库升级任务已取消。", $"{errorMessage}；状态文件写入失败：{stateEx.Message}", backupFile, executedScripts, DateTime.Now);
                }
            }
        }
        catch (Exception ex)
        {
            var errorMessage = databaseWorkCompleted
                ? $"数据库升级已执行，但状态文件更新失败：{ex.Message}"
                : ex.Message;
            UpdateTask(taskId, DatabaseUpgradeTaskStatus.Failed, "执行失败", "数据库升级失败。", errorMessage, backupFile, executedScripts, DateTime.Now);

            if (script is not null && !databaseWorkCompleted)
            {
                try
                {
                    await MarkScriptFailedAsync(target, script, ex.Message, CancellationToken.None);
                }
                catch (Exception stateEx)
                {
                    errorMessage = $"{ex.Message}；状态文件写入失败：{stateEx.Message}";
                    UpdateTask(taskId, DatabaseUpgradeTaskStatus.Failed, "执行失败", "数据库升级失败。", errorMessage, backupFile, executedScripts, DateTime.Now);
                }
            }
        }
        finally
        {
            EndExclusiveUpgradeOperation(new UpgradeOperationLease(connectionKey, taskId));
        }
    }

    private bool HasRunningTask(string connectionKey)
    {
        if (!_activeTasksByConnection.TryGetValue(connectionKey, out var taskId))
            return false;

        if (!_tasks.TryGetValue(taskId, out var task))
            return true;

        if (!task.IsTerminal)
            return true;

        _activeTasksByConnection.TryRemove(new KeyValuePair<string, Guid>(connectionKey, taskId));
        return false;
    }

    private UpgradeOperationLease BeginExclusiveUpgradeOperation(DatabaseConnectionOption target)
    {
        EnsureUpgradeExecutionAllowed(target);
        var connectionKey = BuildConnectionFingerprint(target);
        if (HasRunningTask(connectionKey))
            throw new InvalidOperationException("当前目标库已有数据库升级任务正在执行，请等待完成后再操作。");

        var operationId = Guid.NewGuid();
        if (!_activeTasksByConnection.TryAdd(connectionKey, operationId))
            throw new InvalidOperationException("当前目标库已有数据库升级任务正在执行，请等待完成后再操作。");

        return new UpgradeOperationLease(connectionKey, operationId);
    }

    private void EndExclusiveUpgradeOperation(UpgradeOperationLease lease) =>
        _activeTasksByConnection.TryRemove(new KeyValuePair<string, Guid>(lease.ConnectionKey, lease.OperationId));

    private async ValueTask<IAsyncDisposable?> AcquireCubeMaintenanceLeaseAsync(
        DatabaseConnectionOption target,
        CancellationToken cancellationToken)
    {
        if (!IsCubeConnection(target))
            return null;

        var lease = await _cubeOperationCoordinator.TryAcquireExclusiveAsync(cancellationToken);
        return lease ?? throw new InvalidOperationException(
            "CubeDb 当前处于 FollowUp 导入、恢复或其他数据库维护操作中，请稍后重试。");
    }

    private bool IsCubeConnection(DatabaseConnectionOption target) =>
        string.Equals(BuildConnectionFingerprint(target), _cubeConnectionKey, StringComparison.Ordinal);

    private void EnsureUpgradeExecutionAllowed(DatabaseConnectionOption target)
    {
        if (DeploymentModePolicy.IsExternalCube(_configuration) && IsCubeConnection(target))
            throw new InvalidOperationException(DeploymentModePolicy.ExternalCubeUpgradeBlockedMessage);
    }

    private void CleanupTaskSnapshots(Guid preserveTaskId)
    {
        var now = DateTime.Now;
        var terminalTasks = _tasks.Values
            .Where(task => task.IsTerminal && task.TaskId != preserveTaskId)
            .OrderByDescending(task => task.FinishedAt ?? task.StartedAt)
            .ToList();

        foreach (var task in terminalTasks.Where(task =>
                     task.FinishedAt is not null && now - task.FinishedAt.Value > TaskSnapshotRetention))
        {
            RemoveTaskSnapshot(task.TaskId);
        }

        var remainingTerminalTasks = _tasks.Values
            .Where(task => task.IsTerminal && task.TaskId != preserveTaskId)
            .OrderByDescending(task => task.FinishedAt ?? task.StartedAt)
            .Skip(MaxTaskSnapshots)
            .ToList();

        foreach (var task in remainingTerminalTasks)
            RemoveTaskSnapshot(task.TaskId);
    }

    private void RemoveTaskSnapshot(Guid taskId)
    {
        _tasks.TryRemove(taskId, out _);
        foreach (var activeTask in _activeTasksByConnection.Where(item => item.Value == taskId))
            _activeTasksByConnection.TryRemove(activeTask);
    }

    private void UpdateTask(
        Guid taskId,
        DatabaseUpgradeTaskStatus status,
        string currentStep,
        string statusText,
        string? errorMessage,
        string? backupFile,
        List<string> executedScripts,
        DateTime? finishedAt,
        DatabaseUpgradeTaskProgress? progress = null)
    {
        _tasks.AddOrUpdate(
            taskId,
            _ => new DatabaseUpgradeTaskSnapshot(
                taskId,
                "",
                "",
                status,
                statusText,
                currentStep,
                errorMessage,
                backupFile,
                [.. executedScripts],
                [CreateTaskLog(status, currentStep, statusText, errorMessage)],
                progress,
                DateTime.Now,
                finishedAt),
            (_, current) => current with
            {
                Status = status,
                StatusText = statusText,
                CurrentStep = currentStep,
                ErrorMessage = errorMessage,
                BackupFile = backupFile,
                ExecutedScripts = [.. executedScripts],
                Logs = AppendTaskLog(current.Logs, CreateTaskLog(status, currentStep, statusText, errorMessage)),
                Progress = progress ?? current.Progress,
                FinishedAt = finishedAt
            });

        if (status is DatabaseUpgradeTaskStatus.Succeeded or DatabaseUpgradeTaskStatus.Failed)
            CleanupTaskSnapshots(taskId);
    }

    private static DatabaseUpgradeTaskLogItem CreateTaskLog(
        DatabaseUpgradeTaskStatus status,
        string step,
        string message,
        string? errorMessage)
    {
        var level = status switch
        {
            DatabaseUpgradeTaskStatus.Succeeded => "Success",
            DatabaseUpgradeTaskStatus.Failed => "Error",
            _ => "Info"
        };
        var text = string.IsNullOrWhiteSpace(errorMessage)
            ? message
            : $"{message}：{errorMessage}";
        return new DatabaseUpgradeTaskLogItem(DateTime.Now, level, step, text);
    }

    private static List<DatabaseUpgradeTaskLogItem> AppendTaskLog(
        IReadOnlyList<DatabaseUpgradeTaskLogItem> currentLogs,
        DatabaseUpgradeTaskLogItem log)
    {
        if (currentLogs.Count > 0)
        {
            var last = currentLogs[^1];
            if (string.Equals(last.Level, log.Level, StringComparison.Ordinal)
                && string.Equals(last.Step, log.Step, StringComparison.Ordinal)
                && string.Equals(last.Message, log.Message, StringComparison.Ordinal))
            {
                return [.. currentLogs];
            }
        }

        var logs = currentLogs.Count >= MaxTaskLogItems
            ? currentLogs.Skip(currentLogs.Count - MaxTaskLogItems + 1).ToList()
            : [.. currentLogs];
        logs.Add(log);
        return logs;
    }

    private DatabaseConnectionOption GetConnection(string connectionName)
    {
        var target = GetConnectionOptions().FirstOrDefault(item =>
            string.Equals(item.Name, connectionName, StringComparison.OrdinalIgnoreCase));
        return target ?? throw new InvalidOperationException($"未找到连接字符串：{connectionName}");
    }

    private List<UpgradeScript> LoadScripts()
    {
        var rootPath = _environment.ContentRootPath;
        var scripts = new List<UpgradeScript>();
        AddScriptIfExists(scripts, rootPath, Path.Combine(rootPath, "init_database.sql"), UpgradeScriptKind.LegacySql);

        var scriptsPath = Path.Combine(rootPath, "Scripts");
        if (Directory.Exists(scriptsPath))
        {
            foreach (var path in Directory.GetFiles(scriptsPath, "*.sql", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                AddScriptIfExists(scripts, rootPath, path, UpgradeScriptKind.LegacySql);
            }

            foreach (var path in Directory.GetFiles(scriptsPath, "*.sql", SearchOption.AllDirectories)
                         .Where(path => !string.Equals(Path.GetDirectoryName(path), scriptsPath, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => Path.GetRelativePath(scriptsPath, path), StringComparer.OrdinalIgnoreCase))
            {
                AddScriptIfExists(scripts, rootPath, path, UpgradeScriptKind.LegacySql);
            }
        }

        var upgradesPath = Path.Combine(rootPath, "DatabaseUpgrades");
        if (Directory.Exists(upgradesPath))
        {
            foreach (var path in Directory.GetFiles(upgradesPath, "*.sql", SearchOption.AllDirectories)
                         .OrderBy(path => Path.GetRelativePath(upgradesPath, path), StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(rootPath, path).Replace('/', '\\');
                var kind = string.Equals(relativePath, ArchiveOptimizationRelativePath, StringComparison.OrdinalIgnoreCase)
                    ? UpgradeScriptKind.EsbMessagesArchiveOptimization
                    : UpgradeScriptKind.ManagedSql;
                AddScriptIfExists(scripts, rootPath, path, kind);
            }
        }

        return scripts;
    }

    private static void AddScriptIfExists(List<UpgradeScript> scripts, string rootPath, string path, UpgradeScriptKind kind)
    {
        if (!File.Exists(path))
            return;

        var text = File.ReadAllText(path, Encoding.UTF8);
        var relativePath = Path.GetRelativePath(rootPath, path).Replace('/', '\\');
        scripts.Add(new UpgradeScript(
            relativePath,
            path,
            text,
            ComputeSha256(text),
            BuildDescription(kind, text),
            ResolveCreatedAt(path, relativePath),
            kind));
    }

    private async Task<DatabaseUpgradeCheckResult> BuildCheckResultAsync(
        DatabaseConnectionOption target,
        List<UpgradeScript> scripts,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var archiveState = scripts.Any(script => script.Kind == UpgradeScriptKind.EsbMessagesArchiveOptimization)
            ? await ArchiveOptimizationCheck.GetStateAsync(connection, cancellationToken)
            : ArchiveOptimizationState.NotInstalled;
        var archiveEligibility = archiveState == ArchiveOptimizationState.Ready
            ? await GetArchiveEligibilityAsync(connection, cancellationToken)
            : ArchiveEligibility.NotRequired;

        var connectionKey = BuildConnectionFingerprint(target);
        var connectionKeys = BuildConnectionFingerprintAliases(target);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            var stateResult = await LoadStateDocumentNoLockAsync(cancellationToken);
            var state = stateResult.Document;
            var firstBaseline = !state.BaselineConnectionKeys.Any(key => connectionKeys.Contains(key));

            foreach (var script in scripts)
            {
                var record = FindRecord(state, connectionKeys, script.RelativePath);
                if (record is not null)
                {
                    record.ConnectionKey = connectionKey;
                    if (record.IsLegacy is null)
                    {
                        record.IsLegacy = script.Kind == UpgradeScriptKind.LegacySql;
                    }

                    continue;
                }

                state.Scripts.Add(new UpgradeScriptStateRecord
                {
                    ConnectionKey = connectionKey,
                    ScriptKey = script.RelativePath,
                    ScriptHash = script.Hash,
                    ScriptName = script.Name,
                    FirstSeenAt = DateTime.Now,
                    BaselineUnknown = script.Kind == UpgradeScriptKind.LegacySql,
                    IsLegacy = script.Kind == UpgradeScriptKind.LegacySql
                });
            }

            if (firstBaseline)
            {
                state.BaselineInitialized = true;
                state.CreatedAt = DateTime.Now;
            }

            if (!state.BaselineConnectionKeys.Contains(connectionKey, StringComparer.Ordinal))
                state.BaselineConnectionKeys.Add(connectionKey);

            var saveResult = stateResult.IsWritable
                ? await TrySaveStateDocumentNoLockAsync(state, cancellationToken)
                : stateResult;

            var items = scripts
                .Select(script =>
                {
                    var record = FindRecord(state, connectionKeys, script.RelativePath);
                    var isLegacy = script.Kind == UpgradeScriptKind.LegacySql;
                    var status = ResolveStatus(script, record, archiveState, archiveEligibility, isLegacy);
                    return new DatabaseUpgradeScriptItem(
                        script.RelativePath,
                        script.Name,
                        script.Description,
                        script.CreatedAt,
                        status,
                        isLegacy ? DatabaseUpgradeScriptGroup.Legacy : DatabaseUpgradeScriptGroup.Managed,
                        script.Kind == UpgradeScriptKind.ManagedSql);
                })
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.ScriptKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DatabaseUpgradeCheckResult(
                target,
                scripts.Count,
                items,
                saveResult.IsWritable,
                saveResult.ErrorMessage);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private static DatabaseUpgradeScriptStatus ResolveStatus(
        UpgradeScript script,
        UpgradeScriptStateRecord? record,
        ArchiveOptimizationState archiveState,
        ArchiveEligibility archiveEligibility,
        bool isLegacy)
    {
        if (isLegacy)
            return DatabaseUpgradeScriptStatus.Unknown;

        if (script.Kind == UpgradeScriptKind.EsbMessagesArchiveOptimization)
        {
            return archiveState switch
            {
                ArchiveOptimizationState.Ready when archiveEligibility == ArchiveEligibility.Unknown => DatabaseUpgradeScriptStatus.Unknown,
                ArchiveOptimizationState.Ready when record is not null
                    && string.Equals(record.LastStatus, UpgradeScriptStateValues.Executed, StringComparison.Ordinal)
                    && string.Equals(record.ScriptHash, script.Hash, StringComparison.Ordinal)
                    && archiveEligibility == ArchiveEligibility.None
                    => DatabaseUpgradeScriptStatus.Executed,
                ArchiveOptimizationState.Ready when archiveEligibility == ArchiveEligibility.HasRows => DatabaseUpgradeScriptStatus.Pending,
                ArchiveOptimizationState.Ready => DatabaseUpgradeScriptStatus.Executed,
                ArchiveOptimizationState.NotInstalled => DatabaseUpgradeScriptStatus.Pending,
                _ => DatabaseUpgradeScriptStatus.Unknown
            };
        }

        if (record is null)
            return DatabaseUpgradeScriptStatus.Pending;

        if (string.Equals(record.LastStatus, UpgradeScriptStateValues.Executed, StringComparison.Ordinal)
            && string.Equals(record.ScriptHash, script.Hash, StringComparison.Ordinal))
        {
            return DatabaseUpgradeScriptStatus.Executed;
        }

        if (string.Equals(record.LastStatus, UpgradeScriptStateValues.Executed, StringComparison.Ordinal)
            && !string.Equals(record.ScriptHash, script.Hash, StringComparison.Ordinal))
        {
            return DatabaseUpgradeScriptStatus.Unknown;
        }

        return record.BaselineUnknown
            ? DatabaseUpgradeScriptStatus.Unknown
            : DatabaseUpgradeScriptStatus.Pending;
    }

    private async Task MarkScriptExecutedAsync(
        DatabaseConnectionOption target,
        UpgradeScript script,
        CancellationToken cancellationToken)
    {
        await UpdateScriptStateAsync(target, script, record =>
        {
            record.IsLegacy = false;
            record.BaselineUnknown = false;
            record.ScriptHash = script.Hash;
            record.ScriptName = script.Name;
            record.LastStatus = UpgradeScriptStateValues.Executed;
            record.ExecutedAt = DateTime.Now;
            record.LastError = null;
        }, cancellationToken);
    }

    private async Task MarkScriptFailedAsync(
        DatabaseConnectionOption target,
        UpgradeScript script,
        string error,
        CancellationToken cancellationToken)
    {
        await UpdateScriptStateAsync(target, script, record =>
        {
            record.IsLegacy = false;
            record.ScriptHash = script.Hash;
            record.ScriptName = script.Name;
            record.LastStatus = UpgradeScriptStateValues.Failed;
            record.LastError = error;
        }, cancellationToken);
    }

    private async Task UpdateScriptStateAsync(
        DatabaseConnectionOption target,
        UpgradeScript script,
        Action<UpgradeScriptStateRecord> update,
        CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            var stateResult = await LoadStateDocumentNoLockAsync(cancellationToken);
            if (!stateResult.IsWritable)
                throw new InvalidOperationException("状态文件不可写，无法保存脚本状态。");

            var state = stateResult.Document;
            var record = GetOrAddRecord(state, target, script);
            update(record);
            var saveResult = await TrySaveStateDocumentNoLockAsync(state, cancellationToken);
            if (!saveResult.IsWritable)
                throw new InvalidOperationException(saveResult.ErrorMessage ?? "状态文件不可写，无法保存脚本状态。");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private UpgradeScriptStateRecord GetOrAddRecord(
        UpgradeStateDocument state,
        DatabaseConnectionOption target,
        UpgradeScript script)
    {
        var connectionKey = BuildConnectionFingerprint(target);
        var record = FindRecord(state, BuildConnectionFingerprintAliases(target), script.RelativePath);
        if (record is not null)
        {
            record.ConnectionKey = connectionKey;
            return record;
        }

        record = new UpgradeScriptStateRecord
        {
            ConnectionKey = connectionKey,
            ScriptKey = script.RelativePath,
            ScriptHash = script.Hash,
            ScriptName = script.Name,
            FirstSeenAt = DateTime.Now,
            IsLegacy = false
        };
        state.Scripts.Add(record);
        return record;
    }

    private static UpgradeScriptStateRecord? FindRecord(
        UpgradeStateDocument state,
        IReadOnlyCollection<string> connectionKeys,
        string scriptKey)
    {
        foreach (var connectionKey in connectionKeys)
        {
            var record = FindRecord(state, connectionKey, scriptKey);
            if (record is not null)
                return record;
        }

        return null;
    }

    private static UpgradeScriptStateRecord? FindRecord(
        UpgradeStateDocument state,
        string connectionKey,
        string scriptKey)
        => state.Scripts.FirstOrDefault(item =>
            string.Equals(item.ConnectionKey, connectionKey, StringComparison.Ordinal)
            && string.Equals(item.ScriptKey, scriptKey, StringComparison.OrdinalIgnoreCase));

    private async Task<StateAccessResult> LoadStateDocumentNoLockAsync(CancellationToken cancellationToken)
    {
        var path = GetStateFilePath();
        try
        {
            if (!File.Exists(path))
                return new StateAccessResult(new UpgradeStateDocument(), true, null);

            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<UpgradeStateDocument>(stream, JsonOptions, cancellationToken)
                ?? new UpgradeStateDocument();
            return new StateAccessResult(state, true, null);
        }
        catch (Exception ex)
        {
            return new StateAccessResult(new UpgradeStateDocument(), false, $"状态文件读取失败：{ex.Message}");
        }
    }

    private async Task<StateAccessResult> TrySaveStateDocumentNoLockAsync(
        UpgradeStateDocument state,
        CancellationToken cancellationToken)
    {
        var path = GetStateFilePath();
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);

            return new StateAccessResult(state, true, null);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }

            return new StateAccessResult(state, false, $"状态文件不可写：{ex.Message}");
        }
    }

    private async Task ProcessManagedScriptQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _managedScriptTaskQueue.Reader.ReadAllAsync(cancellationToken))
            {
                await RunManagedScriptTaskAsync(
                    request.TaskId,
                    request.Target,
                    request.ConnectionKey,
                    request.ScriptKey,
                    request.PgDumpPath,
                    request.SkipBackup,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private string GetStateFilePath() =>
        Path.Combine(_environment.ContentRootPath, "DatabaseUpgradeState", "database-upgrade-state.json");

    private static async Task<ArchiveEligibility> GetArchiveEligibilityAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var hotDays = 30;
            await using (var configCommand = new NpgsqlCommand("""
                SELECT config_value
                FROM lhyy.esb_global_config
                WHERE config_key = 'MessageHotRetentionDays'
                LIMIT 1;
                """, connection))
            {
                var value = await configCommand.ExecuteScalarAsync(cancellationToken);
                if (value is not null && int.TryParse(value.ToString(), out var parsed) && parsed > 0)
                    hotDays = parsed;
            }

            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM lhyy.esb_messages
                    WHERE created_at < @threshold
                      AND status IN (2, 4, 5, 6)
                    LIMIT 1
                );
                """, connection);
            command.Parameters.AddWithValue("threshold", DateTime.Now.AddDays(-hotDays));
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is bool hasRows && hasRows
                ? ArchiveEligibility.HasRows
                : ArchiveEligibility.None;
        }
        catch
        {
            return ArchiveEligibility.Unknown;
        }
    }

    private static async Task ExecuteScriptAsync(
        NpgsqlConnection connection,
        UpgradeScript script,
        CancellationToken cancellationToken)
        => await SqlScriptExecutionHelper.ExecuteAsync(connection, script.Sql, cancellationToken);

    private async Task<string> BackupDatabaseAsync(
        string connectionString,
        string? configuredPgDumpPath,
        CancellationToken cancellationToken)
    {
        var pgDumpPath = ResolvePgDumpPath(configuredPgDumpPath)
            ?? throw new InvalidOperationException("未找到 pg_dump.exe，无法备份，升级已停止。");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var (host, port) = ResolveHostAndPort(builder);
        var username = !string.IsNullOrWhiteSpace(builder.Username)
            ? builder.Username
            : throw new InvalidOperationException("连接字符串缺少 Username，无法执行备份。");
        var database = !string.IsNullOrWhiteSpace(builder.Database)
            ? builder.Database
            : throw new InvalidOperationException("连接字符串缺少 Database，无法执行备份。");
        var backupDirectory = Path.Combine(_environment.ContentRootPath, "DatabaseBackups");
        Directory.CreateDirectory(backupDirectory);

        var backupFile = Path.Combine(
            backupDirectory,
            $"{database}_{DateTime.Now:yyyyMMdd_HHmmss}.backup");

        using var process = new Process();
        process.StartInfo.FileName = pgDumpPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.Environment["PGPASSWORD"] = builder.Password ?? "";
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("--username");
        process.StartInfo.ArgumentList.Add(username);
        process.StartInfo.ArgumentList.Add("--dbname");
        process.StartInfo.ArgumentList.Add(database);
        process.StartInfo.ArgumentList.Add("--format");
        process.StartInfo.ArgumentList.Add("c");
        process.StartInfo.ArgumentList.Add("--file");
        process.StartInfo.ArgumentList.Add(backupFile);
        process.StartInfo.ArgumentList.Add("--no-password");

        var (output, error) = await RunProcessAndCaptureOutputAsync(process, cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump 备份失败：{error}{output}");

        return backupFile;
    }

    private async Task ExecuteSqlFileByPsqlAsync(
        string connectionString,
        string sqlFilePath,
        string? configuredToolPath,
        CancellationToken cancellationToken)
    {
        var psqlPath = ResolvePsqlPath(configuredToolPath)
            ?? throw new InvalidOperationException("未找到 psql.exe，无法执行 SQL 文件。");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var (host, port) = ResolveHostAndPort(builder);
        var username = !string.IsNullOrWhiteSpace(builder.Username)
            ? builder.Username
            : throw new InvalidOperationException("连接字符串缺少 Username，无法执行 SQL 文件。");
        var database = !string.IsNullOrWhiteSpace(builder.Database)
            ? builder.Database
            : throw new InvalidOperationException("连接字符串缺少 Database，无法执行 SQL 文件。");

        using var process = new Process();
        process.StartInfo.FileName = psqlPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.Environment["PGPASSWORD"] = builder.Password ?? "";
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("--username");
        process.StartInfo.ArgumentList.Add(username);
        process.StartInfo.ArgumentList.Add("--dbname");
        process.StartInfo.ArgumentList.Add(database);
        process.StartInfo.ArgumentList.Add("--no-password");
        process.StartInfo.ArgumentList.Add("--set");
        process.StartInfo.ArgumentList.Add("ON_ERROR_STOP=on");
        process.StartInfo.ArgumentList.Add("--file");
        process.StartInfo.ArgumentList.Add(sqlFilePath);

        var (output, error) = await RunProcessAndCaptureOutputAsync(process, cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"psql 执行失败：{error}{output}");
    }

    private static async Task<(string Output, string Error)> RunProcessAndCaptureOutputAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return (await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                }
            }

            await ObserveProcessOutputTaskAsync(outputTask);
            await ObserveProcessOutputTaskAsync(errorTask);
            throw;
        }
    }

    private static async Task ObserveProcessOutputTaskAsync(Task<string> task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static string? ResolvePgDumpPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath)
                && string.Equals(Path.GetFileName(configuredPath), "pg_dump.exe", StringComparison.OrdinalIgnoreCase))
            {
                return configuredPath;
            }

            var directory = File.Exists(configuredPath)
                ? Path.GetDirectoryName(configuredPath)
                : configuredPath;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var executableName = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var postgresRoot = Path.Combine(programFiles, "PostgreSQL");
        if (!Directory.Exists(postgresRoot))
            return null;

        return Directory.GetFiles(postgresRoot, executableName, SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? ResolvePsqlPath(string? configuredToolPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredToolPath))
        {
            if (File.Exists(configuredToolPath)
                && string.Equals(Path.GetFileName(configuredToolPath), "psql.exe", StringComparison.OrdinalIgnoreCase))
            {
                return configuredToolPath;
            }

            var directory = File.Exists(configuredToolPath)
                ? Path.GetDirectoryName(configuredToolPath)
                : configuredToolPath;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "psql.exe" : "psql");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var executableName = OperatingSystem.IsWindows() ? "psql.exe" : "psql";
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var postgresRoot = Path.Combine(programFiles, "PostgreSQL");
        if (!Directory.Exists(postgresRoot))
            return null;

        return Directory.GetFiles(postgresRoot, executableName, SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private string ResolveSqlFilePath(string sqlFilePath)
    {
        if (string.IsNullOrWhiteSpace(sqlFilePath))
            throw new InvalidOperationException("请先选择或填写 SQL 文件。");

        var path = Path.IsPathRooted(sqlFilePath)
            ? sqlFilePath
            : Path.Combine(_environment.ContentRootPath, sqlFilePath);
        path = Path.GetFullPath(path);

        if (!path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许执行 .sql 文件。");

        return path;
    }

    private static string DescribeConnection(string name, string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var (host, port) = ResolveHostAndPort(builder);
            return $"{name}（Host={host}; Port={port}; Database={builder.Database}; Username={builder.Username}）";
        }
        catch
        {
            return name;
        }
    }

    private static (string Host, int Port) ResolveHostAndPort(NpgsqlConnectionStringBuilder builder)
    {
        var host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;
        var port = builder.Port > 0 ? builder.Port : 5432;
        var parts = host.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort))
            return (parts[0], parsedPort);

        return (host, port);
    }

    private static string BuildConnectionFingerprint(DatabaseConnectionOption target)
    {
        var builder = new NpgsqlConnectionStringBuilder(target.ConnectionString);
        var (host, port) = ResolveHostAndPort(builder);
        return ComputeSha256($"{NormalizeFingerprintPart(host)}|{port}|{NormalizeFingerprintPart(builder.Database)}");
    }

    private static IReadOnlyCollection<string> BuildConnectionFingerprintAliases(DatabaseConnectionOption target)
    {
        var current = BuildConnectionFingerprint(target);
        var legacy = BuildLegacyConnectionFingerprint(target);
        return string.Equals(current, legacy, StringComparison.Ordinal)
            ? [current]
            : [current, legacy];
    }

    private static string BuildLegacyConnectionFingerprint(DatabaseConnectionOption target)
    {
        var builder = new NpgsqlConnectionStringBuilder(target.ConnectionString);
        var (host, port) = ResolveHostAndPort(builder);
        return ComputeSha256($"{target.Name}|{host}|{port}|{builder.Database}|{builder.Username}");
    }

    private static string NormalizeFingerprintPart(string? value) =>
        (value ?? "").Trim().ToLowerInvariant();

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildDescription(UpgradeScriptKind kind, string sql)
    {
        if (kind == UpgradeScriptKind.EsbMessagesArchiveOptimization)
            return ArchiveOptimizationDescription;

        var comments = sql
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("--", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line)
                && !line.StartsWith("DATASYNC:", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();

        return comments.Count == 0
            ? "内置数据库升级脚本。"
            : string.Join("；", comments);
    }

    private static DateTime ResolveCreatedAt(string fullPath, string relativePath)
    {
        var match = Regex.Match(relativePath, @"(?<!\d)(20\d{6})(?!\d)");
        if (match.Success && DateTime.TryParseExact(match.Value, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;

        return File.GetCreationTime(fullPath);
    }

    private sealed record UpgradeScript(
        string RelativePath,
        string FullPath,
        string Sql,
        string Hash,
        string Description,
        DateTime CreatedAt,
        UpgradeScriptKind Kind)
    {
        public string Name => Path.GetFileName(RelativePath);
    }

    private enum UpgradeScriptKind
    {
        LegacySql = 0,
        ManagedSql = 1,
        EsbMessagesArchiveOptimization = 2
    }

    private enum ArchiveEligibility
    {
        NotRequired = 0,
        None = 1,
        HasRows = 2,
        Unknown = 3
    }

    private sealed record StateAccessResult(
        UpgradeStateDocument Document,
        bool IsWritable,
        string? ErrorMessage);

    private sealed record UpgradeOperationLease(string ConnectionKey, Guid OperationId);

    private sealed record ManagedScriptTaskRequest(
        Guid TaskId,
        DatabaseConnectionOption Target,
        string ConnectionKey,
        string ScriptKey,
        string? PgDumpPath,
        bool SkipBackup);

    private sealed class UpgradeStateDocument
    {
        public bool BaselineInitialized { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<string> BaselineConnectionKeys { get; set; } = [];
        public List<UpgradeScriptStateRecord> Scripts { get; set; } = [];
    }

    private sealed class UpgradeScriptStateRecord
    {
        public string ConnectionKey { get; set; } = "";
        public string ScriptKey { get; set; } = "";
        public string ScriptHash { get; set; } = "";
        public string ScriptName { get; set; } = "";
        public DateTime FirstSeenAt { get; set; }
        public bool BaselineUnknown { get; set; }
        public bool? IsLegacy { get; set; }
        public string? LastStatus { get; set; }
        public DateTime? ExecutedAt { get; set; }
        public string? LastError { get; set; }
    }

    private static class UpgradeScriptStateValues
    {
        public const string Executed = "Executed";
        public const string Failed = "Failed";
    }
}

public sealed record DatabaseConnectionOption(string Name, string ConnectionString, string DisplayName);

public enum DatabaseUpgradeScriptStatus
{
    Executed = 0,
    Pending = 1,
    Unknown = 2
}

public enum DatabaseUpgradeScriptGroup
{
    Legacy = 0,
    Managed = 1
}

public enum DatabaseUpgradeTaskStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

public sealed record DatabaseUpgradeScriptItem(
    string ScriptKey,
    string ScriptName,
    string Description,
    DateTime CreatedAt,
    DatabaseUpgradeScriptStatus Status,
    DatabaseUpgradeScriptGroup Group,
    bool CanMarkStatus)
{
    public string StatusText => Status switch
    {
        DatabaseUpgradeScriptStatus.Executed => "已执行",
        DatabaseUpgradeScriptStatus.Pending => "未执行",
        _ => "未知"
    };
}

public sealed record DatabaseUpgradeTaskSnapshot(
    Guid TaskId,
    string ConnectionName,
    string ScriptKey,
    DatabaseUpgradeTaskStatus Status,
    string StatusText,
    string CurrentStep,
    string? ErrorMessage,
    string? BackupFile,
    List<string> ExecutedScripts,
    List<DatabaseUpgradeTaskLogItem> Logs,
    DatabaseUpgradeTaskProgress? Progress,
    DateTime StartedAt,
    DateTime? FinishedAt)
{
    public bool IsTerminal => Status is DatabaseUpgradeTaskStatus.Succeeded or DatabaseUpgradeTaskStatus.Failed;
}

public sealed record DatabaseUpgradeTaskProgress(
    long TotalMessages,
    long MigratedMessages,
    long MigratedLogs,
    int BatchIndex,
    long CurrentBatchMessages,
    DateTime Threshold,
    double Percent,
    bool IsCompleted);

public sealed record DatabaseUpgradeTaskLogItem(
    DateTime Timestamp,
    string Level,
    string Step,
    string Message);

public sealed record DatabaseUpgradeCheckResult(
    DatabaseConnectionOption Connection,
    int TotalScriptCount,
    List<DatabaseUpgradeScriptItem> Scripts,
    bool IsStateWritable,
    string? StateErrorMessage)
{
    public List<DatabaseUpgradeScriptItem> LegacyScripts => Scripts
        .Where(script => script.Group == DatabaseUpgradeScriptGroup.Legacy)
        .ToList();

    public List<DatabaseUpgradeScriptItem> ManagedScripts => Scripts
        .Where(script => script.Group == DatabaseUpgradeScriptGroup.Managed)
        .ToList();

    public List<string> PendingScripts => ManagedScripts
        .Where(script => script.Status == DatabaseUpgradeScriptStatus.Pending)
        .Select(script => script.ScriptKey)
        .ToList();
}

public sealed record DatabaseUpgradeExecuteResult(
    DatabaseConnectionOption Connection,
    string BackupFile,
    List<string> ExecutedScripts);
