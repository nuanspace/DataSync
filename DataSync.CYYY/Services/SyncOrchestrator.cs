using System.Text.Json;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Services;

/// <summary>
/// 核心编排器 — 定时任务和补数据共用
/// </summary>
public class SyncOrchestrator
{
    private const string TriggerPushLogName = "本地触发记录";
    private const string StageSuccess = "成功";
    private const string StageNoData = "无数据/跳过";
    private const string StageFetchFailed = "获取失败";
    private const string StagePushFailed = "推送失败";
    private const string StageFailed = "失败";

    private readonly DataLakeClient _dataLakeClient;
    private readonly DynamicApiClient _dynamicApiClient;
    private readonly DatabaseQueryService _databaseQueryService;
    private readonly PushServiceFactory _pushServiceFactory;
    private readonly ApiPushService _apiPushService;
    private readonly SyncLogService _logService;
    private readonly IngestionService _ingestionService;
    private readonly LocalQueryService _localQueryService;
    private readonly IDbContextFactory<SyncDbContext> _dbFactory;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        DataLakeClient dataLakeClient,
        DynamicApiClient dynamicApiClient,
        DatabaseQueryService databaseQueryService,
        PushServiceFactory pushServiceFactory,
        ApiPushService apiPushService,
        SyncLogService logService,
        IngestionService ingestionService,
        LocalQueryService localQueryService,
        IDbContextFactory<SyncDbContext> dbFactory,
        ILogger<SyncOrchestrator> logger)
    {
        _dataLakeClient = dataLakeClient;
        _dynamicApiClient = dynamicApiClient;
        _databaseQueryService = databaseQueryService;
        _pushServiceFactory = pushServiceFactory;
        _apiPushService = apiPushService;
        _logService = logService;
        _ingestionService = ingestionService;
        _localQueryService = localQueryService;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// 通用同步流程：处理触发源记录列表
    /// </summary>
    public async Task<SyncResult> ExecuteSyncAsync(
        SyncTask task,
        List<Dictionary<string, object>> triggerRecords,
        string triggerType,
        CancellationToken ct,
        Func<BackfillProgressEvent, Task>? onProgress = null,
        bool skipTriggerRecordPush = false,
        string? sourceRecordKey = null,
        IReadOnlyCollection<int>? selectedInterfaceIds = null,
        bool executeTriggerRecordPush = true,
        bool reportNoDataAsSkipped = false)
    {
        var result = new SyncResult();
        var patientSemaphore = new SemaphoreSlim(task.PatientConcurrency);
        var apiSemaphore = new SemaphoreSlim(task.ApiConcurrency);
        var completedCounter = 0;

        var enabledInterfaces = task.Interfaces
            .Where(i => i.Enabled)
            .OrderBy(i => i.SortOrder)
            .ToList();
        var interfaces = FilterSelectedInterfaces(enabledInterfaces, selectedInterfaceIds);
        var now = DateTime.Now;
        var blockedInterface = interfaces.FirstOrDefault(iface => !InterfaceAccessWindow.IsOpen(iface, now));
        if (blockedInterface != null)
        {
            var nextOpen = InterfaceAccessWindow.GetNextOpen(blockedInterface, now);
            throw new InvalidOperationException(
                $"接口 [{blockedInterface.DisplayName}] 当前不在允许访问时段，下次可访问时间 {nextOpen:yyyy-MM-dd HH:mm}");
        }

        // 按患者ID分组：同一患者的多条记录串行处理，避免接口推送顺序交叉
        var patientGroups = triggerRecords
            .GroupBy(r => GetStringValue(r, task.PatientIdField))
            .ToList();

        var patientTasks = patientGroups.Select(async group =>
        {
            await patientSemaphore.WaitAsync(ct);
            try
            {
                foreach (var record in group)
                {
                    var completed = Interlocked.Increment(ref completedCounter);
                    await ProcessSinglePatientAsync(
                        task, interfaces, record, apiSemaphore, triggerType, result, ct,
                        onProgress, completed, skipTriggerRecordPush, sourceRecordKey,
                        executeTriggerRecordPush, reportNoDataAsSkipped);
                }
            }
            finally
            {
                patientSemaphore.Release();
            }
        });

        await Task.WhenAll(patientTasks);
        return result;
    }

    /// <summary>
    /// 复用同步任务接口配置查询 Active 病例数据，不执行推送。
    /// </summary>
    public Task<List<Dictionary<string, object>>> QueryInterfaceForActiveAsync(
        SyncTaskInterface iface,
        SyncTask task,
        Dictionary<string, object> triggerRecord,
        CancellationToken ct)
    {
        var hisPatId = GetStringValue(triggerRecord, task.PatientIdField);
        var visitSn = string.IsNullOrWhiteSpace(task.VisitSnField)
            ? null
            : GetStringValue(triggerRecord, task.VisitSnField);
        return QueryInterfaceFromTriggerAsync(iface, task, triggerRecord, hisPatId, visitSn, ct);
    }

    public async Task PushTriggerRecordAsync(
        SyncTask task,
        Dictionary<string, object> triggerRecord,
        string triggerType,
        CancellationToken ct,
        Dictionary<string, object>? payloadRecord = null,
        string? sourceRecordKey = null)
    {
        if (!task.EnableTriggerRecordPush)
            return;

        if (string.IsNullOrWhiteSpace(task.TriggerPushTarget))
            throw new InvalidOperationException("已启用触发记录单独推送，但未配置触发记录推送目标");

        var hisPatId = GetStringValue(triggerRecord, task.PatientIdField);
        var visitSn = task.VisitSnField != null ? GetStringValue(triggerRecord, task.VisitSnField) : null;
        var patName = GetPatientName(triggerRecord);
        var payload = BuildTriggerPushPayload(payloadRecord ?? triggerRecord);
        if (payload.Count == 0)
            throw new InvalidOperationException("触发记录无可推送的业务字段");

        try
        {
            var resolvedTarget = ResolvePushTarget(task.TriggerPushTarget, task.TriggerPushParams);
            await _apiPushService.PushAsync(resolvedTarget, task.TriggerServerCode, [payload], ct);
            await AddTriggerPushLogAsync(
                task,
                hisPatId,
                visitSn,
                patName,
                true,
                triggerType,
                null,
                sourceRecordKey,
                ct);
        }
        catch (Exception ex)
        {
            var errorMessage = ex.Message.StartsWith("[推送]", StringComparison.Ordinal)
                ? ex.Message
                : $"[推送] {ex.Message}";
            await AddTriggerPushLogAsync(
                task,
                hisPatId,
                visitSn,
                patName,
                false,
                triggerType,
                errorMessage,
                sourceRecordKey,
                ct);
            throw new InvalidOperationException(errorMessage, ex);
        }
    }

    /// <summary>
    /// 按患者ID、就诊号或两者配对补数据：采集 → 本地表过滤 → 推送
    /// </summary>
    public async Task<SyncResult> BackfillByPatientVisitAsync(
        List<string> hisPatIds,
        List<string> visitSns,
        List<string> taskCodes,
        bool excludeSynced,
        CancellationToken ct,
        Func<BackfillProgressEvent, Task>? onProgress = null,
        Dictionary<string, List<int>>? selectedInterfaceIdsByTask = null,
        Dictionary<string, bool>? includeTriggerRecordByTask = null)
    {
        var hasPatientIds = hisPatIds.Count > 0;
        var hasVisitSns = visitSns.Count > 0;
        if (!hasPatientIds && !hasVisitSns)
            throw new InvalidOperationException("患者ID和就诊号不能同时为空");
        if (hasPatientIds && hasVisitSns && hisPatIds.Count != visitSns.Count)
            throw new InvalidOperationException("患者ID和就诊号数量必须一致");

        var patientVisits = hasPatientIds && hasVisitSns
            ? hisPatIds.Zip(visitSns, (patientId, visitId) => (PatientId: patientId, VisitId: visitId))
                .DistinctBy(item => $"{item.PatientId}\u001f{item.VisitId}", StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        hisPatIds = hisPatIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        visitSns = visitSns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var modeName = patientVisits.Count > 0
            ? "患者ID+就诊号"
            : hasPatientIds ? "患者ID" : "就诊号";
        var result = new SyncResult();

        foreach (var taskCode in taskCodes)
        {
            var task = await _logService.GetTaskByCodeAsync(taskCode, ct);
            if (task == null)
            {
                _logger.LogWarning("未找到任务: {TaskCode}", taskCode);
                continue;
            }

            if (onProgress != null)
                await onProgress(new BackfillProgressEvent
                { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.TaskStart, SkipFilter = true });

            if (hasVisitSns && string.IsNullOrWhiteSpace(task.VisitSnField))
            {
                var errorMessage = "当前任务未配置就诊号字段";
                result.FailCount++;
                result.FailDetails.Add(new SyncFailDetail
                {
                    TaskName = task.Name,
                    ErrorMessage = errorMessage
                });
                _logger.LogWarning("任务 {TaskCode} 未配置就诊号字段，无法按{ModeName}补录", taskCode, modeName);

                if (onProgress != null)
                    await onProgress(new BackfillProgressEvent
                    { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.TaskDone });
                continue;
            }

            var source = await _ingestionService.GetSourceByServerCodeAsync(task.TriggerServerCode, ct);
            if (source == null)
            {
                _logger.LogWarning("未找到触发源 {ServerCode} 对应的采集源配置", task.TriggerServerCode);
                continue;
            }

            if (IngestionService.IsDynamicApiSource(source) && patientVisits.Count == 0)
                throw new InvalidOperationException($"DynamicApi 目标 [{task.Name}] 必须同时提供患者ID和就诊号");

            var conditions = new List<DataLakeCondition>();
            if (hasPatientIds)
            {
                conditions.Add(new DataLakeCondition
                { Column = task.PatientIdField, Type = "in", Value = string.Join(",", hisPatIds) });
            }
            if (hasVisitSns)
            {
                conditions.Add(new DataLakeCondition
                { Column = task.VisitSnField!, Type = "in", Value = string.Join(",", visitSns) });
            }

            await _ingestionService.IngestForBackfillAsync(
                source,
                conditions,
                ct,
                patientVisits.Count > 0 ? patientVisits : null);

            List<Dictionary<string, object>> records;
            if (patientVisits.Count > 0)
            {
                records = [];
                foreach (var (patientId, visitId) in patientVisits)
                {
                    records.AddRange(await _localQueryService.QueryCandidatesAsync(
                        task, ct,
                        scopeField: task.PatientIdField,
                        scopeValues: [patientId],
                        scopeOperator: "in",
                        excludeSyncedOverride: excludeSynced,
                        skipRules: true,
                        mainEqualsFilters: new Dictionary<string, string>
                        {
                            [task.VisitSnField!] = visitId
                        }));
                }
            }
            else
            {
                records = await _localQueryService.QueryCandidatesAsync(
                    task, ct,
                    scopeField: hasPatientIds ? task.PatientIdField : task.VisitSnField,
                    scopeValues: hasPatientIds ? hisPatIds : visitSns,
                    scopeOperator: "in",
                    excludeSyncedOverride: excludeSynced,
                    skipRules: true);
            }

            if (onProgress != null)
                await onProgress(new BackfillProgressEvent
                { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.Ingested, Count = records.Count, SkipFilter = true });

            if (records.Count == 0)
            {
                _logger.LogInformation("补录按{ModeName}未查到候选记录: {TaskCode}", modeName, taskCode);
                if (onProgress != null)
                    await onProgress(new BackfillProgressEvent
                    { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.TaskDone });
                continue;
            }

            if (onProgress != null)
                await onProgress(new BackfillProgressEvent
                { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.SyncStart, Total = records.Count });

            var subResult = await ExecuteSyncAsync(
                task,
                records,
                "Backfill",
                ct,
                onProgress,
                selectedInterfaceIds: GetSelectedInterfaceIds(selectedInterfaceIdsByTask, taskCode),
                executeTriggerRecordPush: ShouldIncludeTriggerRecord(includeTriggerRecordByTask, taskCode),
                reportNoDataAsSkipped: true);
            result.SuccessCount += subResult.SuccessCount;
            result.FailCount += subResult.FailCount;
            result.SkipCount += subResult.SkipCount;
            result.FailDetails.AddRange(subResult.FailDetails);

            if (onProgress != null)
                await onProgress(new BackfillProgressEvent
                { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.TaskDone });
        }

        if (onProgress != null)
            await onProgress(new BackfillProgressEvent { Phase = BackfillPhase.AllDone });

        return result;
    }

    /// <summary>
    /// 按时间范围补数据：采集 → 本地表过滤 → 推送
    /// </summary>
    public async Task<SyncResult> BackfillByTimeRangeAsync(
        DateTime from,
        DateTime to,
        List<string> taskCodes,
        bool excludeSynced,
        CancellationToken ct,
        Func<BackfillProgressEvent, Task>? onProgress = null,
        Dictionary<string, List<int>>? selectedInterfaceIdsByTask = null,
        Dictionary<string, bool>? includeTriggerRecordByTask = null)
    {
        var result = new SyncResult();

        foreach (var taskCode in taskCodes)
        {
            var task = await _logService.GetTaskByCodeAsync(taskCode, ct);
            if (task == null)
            {
                _logger.LogWarning("未找到任务: {TaskCode}", taskCode);
                continue;
            }

            // 报告任务开始
            if (onProgress != null)
                await onProgress(new BackfillProgressEvent
                { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.TaskStart });

            var source = await _ingestionService.GetSourceByServerCodeAsync(task.TriggerServerCode, ct);
            if (source == null)
            {
                _logger.LogWarning("未找到触发源 {ServerCode} 对应的采集源配置", task.TriggerServerCode);
                continue;
            }

            // 1. 采集到本地：用时间范围构建数据湖查询条件
            var conditions = new List<DataLakeCondition>
            {
                new() { Column = source.TimeField, Type = "ge", Value = from.ToString("yyyy-MM-dd HH:mm:ss") },
                new() { Column = source.TimeField, Type = "le", Value = to.ToString("yyyy-MM-dd HH:mm:ss") }
            };
            await _ingestionService.IngestForBackfillAsync(source, conditions, ct);

            // 查询本地表在该时间范围内的原始记录总数
            var fromStr = from.ToString("yyyy-MM-dd HH:mm:ss");
            var toStr = to.ToString("yyyy-MM-dd HH:mm:ss");
            var rawCount = await _localQueryService.CountRawRecordsAsync(
                task.TriggerServerCode, source.TimeField, fromStr, toStr, ct);

            // 报告采集完成（本地表原始数据总数）
            if (onProgress != null)
                await onProgress(new BackfillProgressEvent
                { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.Ingested, Count = rawCount });

            // 2. 从本地表按条件过滤
            var records = await _localQueryService.QueryCandidatesAsync(
                task, ct,
                scopeField: source.TimeField,
                scopeValues: [fromStr, toStr],
                scopeOperator: "between",
                excludeSyncedOverride: excludeSynced);

            // 报告过滤完成
            if (onProgress != null)
                await onProgress(new BackfillProgressEvent
                { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.Filtered, Count = records.Count });

            if (records.Count == 0)
            {
                _logger.LogInformation("补录按时间范围未查到候选记录: {TaskCode}", taskCode);
                // 报告任务完成（无数据）
                if (onProgress != null)
                    await onProgress(new BackfillProgressEvent
                    { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.TaskDone });
                continue;
            }

            // 报告开始同步
            if (onProgress != null)
                await onProgress(new BackfillProgressEvent
                { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.SyncStart, Total = records.Count });

            // 3. 推送
            var subResult = await ExecuteSyncAsync(
                task,
                records,
                "Backfill",
                ct,
                onProgress,
                selectedInterfaceIds: GetSelectedInterfaceIds(selectedInterfaceIdsByTask, taskCode),
                executeTriggerRecordPush: ShouldIncludeTriggerRecord(includeTriggerRecordByTask, taskCode),
                reportNoDataAsSkipped: true);
            result.SuccessCount += subResult.SuccessCount;
            result.FailCount += subResult.FailCount;
            result.SkipCount += subResult.SkipCount;
            result.FailDetails.AddRange(subResult.FailDetails);

            // 报告任务完成
            if (onProgress != null)
                await onProgress(new BackfillProgressEvent
                { TaskCode = taskCode, TaskName = task.Name, Phase = BackfillPhase.TaskDone });
        }

        // 报告全部完成
        if (onProgress != null)
            await onProgress(new BackfillProgressEvent { Phase = BackfillPhase.AllDone });

        return result;
    }

    /// <summary>
    /// 处理单个患者
    /// </summary>
    private async Task ProcessSinglePatientAsync(
        SyncTask task,
        List<SyncTaskInterface> interfaces,
        Dictionary<string, object> triggerRecord,
        SemaphoreSlim apiSemaphore,
        string triggerType,
        SyncResult result,
        CancellationToken ct,
        Func<BackfillProgressEvent, Task>? onProgress = null,
        int completedRef = 0,
        bool skipTriggerRecordPush = false,
        string? sourceRecordKey = null,
        bool executeTriggerRecordPush = true,
        bool reportNoDataAsSkipped = false)
    {
        var hisPatId = GetStringValue(triggerRecord, task.PatientIdField);
        var visitSn = task.VisitSnField != null ? GetStringValue(triggerRecord, task.VisitSnField) : null;
        var patName = GetPatientName(triggerRecord);
        var pushService = _pushServiceFactory.GetPushService(task.PushType);
        var allInterfaceDetails = new List<InterfaceSyncDetail>();
        var patientSuccess = true;
        var patientSkipped = false;
        var hadSuccessfulInterface = false;
        var hadSkippedInterface = false;
        Exception? fatalException = null;

        try
        {
            if (!skipTriggerRecordPush && executeTriggerRecordPush && task.EnableTriggerRecordPush)
            {
                var triggerDetail = new InterfaceSyncDetail
                {
                    ServerCode = task.TriggerServerCode,
                    InterfaceName = TriggerPushLogName
                };

                try
                {
                    await PushTriggerRecordAsync(
                        task,
                        triggerRecord,
                        triggerType,
                        ct,
                        sourceRecordKey: sourceRecordKey);

                    triggerDetail.Success = true;
                    triggerDetail.Stage = StageSuccess;
                    allInterfaceDetails.Add(triggerDetail);
                    hadSuccessfulInterface = true;
                }
                catch (Exception ex)
                {
                    triggerDetail.Success = false;
                    triggerDetail.Stage = GetFailureStage(ex.Message);
                    triggerDetail.ErrorMessage = ex.Message;
                    allInterfaceDetails.Add(triggerDetail);
                    throw;
                }
            }

            var topLevelInterfaces = interfaces
                .Where(i => string.IsNullOrWhiteSpace(i.ParentInterfaceKey))
                .ToList();

            // 按 SortOrder 分组执行
            var groups = topLevelInterfaces.GroupBy(i => i.SortOrder).OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                var groupTasks = group.Select(async iface =>
                {
                    await apiSemaphore.WaitAsync(ct);
                    try
                    {
                        return (iface, execution: await FetchAndPushInterfaceAsync(
                            iface, interfaces, task, triggerRecord, hisPatId, visitSn, patName, pushService, triggerType, sourceRecordKey, ct,
                            reportNoDataAsSkipped));
                    }
                    finally
                    {
                        apiSemaphore.Release();
                    }
                }).ToList();

                var groupResults = await Task.WhenAll(groupTasks);

                foreach (var (iface, execution) in groupResults)
                {
                    var detail = execution.Detail;
                    allInterfaceDetails.Add(detail);

                    if (execution.Status == InterfaceExecutionStatus.Success)
                    {
                        hadSuccessfulInterface = true;
                        AddCompletedInterface(result, iface);
                    }
                    else if (execution.Status == InterfaceExecutionStatus.Skipped)
                    {
                        hadSkippedInterface = true;
                        AddCompletedInterface(result, iface);
                    }
                    else if (iface.IsRequired)
                    {
                        var reason = string.IsNullOrWhiteSpace(detail.ErrorMessage)
                            ? "未返回具体错误"
                            : detail.ErrorMessage;
                        fatalException ??= new InvalidOperationException(
                            $"必要接口 {iface.ServerCode}({iface.DisplayName}) 失败：{reason}");
                    }
                    else
                    {
                        AddCompletedInterface(result, iface);
                    }
                }

                if (fatalException != null)
                    break;
            }

            if (fatalException != null)
                throw fatalException;

            patientSkipped = !hadSuccessfulInterface && hadSkippedInterface;
            if (patientSkipped)
                Interlocked.Increment(ref result.SkipCount);
            else
                Interlocked.Increment(ref result.SuccessCount);
        }
        catch (Exception ex)
        {
            patientSuccess = false;
            Interlocked.Increment(ref result.FailCount);
            lock (result.FailDetails)
            {
                result.FailDetails.Add(new SyncFailDetail
                {
                    HisPatId = hisPatId,
                    PatName = patName,
                    TaskName = task.Name,
                    ErrorMessage = ex.Message
                });
            }

            _logger.LogError(ex, "患者 {HisPatId} 同步失败（任务 {TaskCode}）", hisPatId, task.Code);
        }

        // 报告患者完成进度
        if (onProgress != null)
        {
            await onProgress(new BackfillProgressEvent
            {
                TaskCode = task.Code,
                TaskName = task.Name,
                Phase = BackfillPhase.PatientDone,
                Completed = completedRef,
                Patient = new PatientSyncDetail
                {
                    HisPatId = hisPatId,
                    PatVisitSn = visitSn,
                    PatName = patName,
                    Success = patientSuccess,
                    Skipped = patientSkipped,
                    Interfaces = allInterfaceDetails
                }
            });
        }
    }

    /// <summary>
    /// 查询单个接口并推送，每个接口独立记录日志
    /// </summary>
    private async Task<InterfaceExecutionResult> FetchAndPushInterfaceAsync(
        SyncTaskInterface iface,
        List<SyncTaskInterface> interfaces,
        SyncTask task,
        Dictionary<string, object> triggerRecord,
        string hisPatId,
        string? visitSn,
        string? patName,
        IPushService pushService,
        string triggerType,
        string? sourceRecordKey,
        CancellationToken ct,
        bool reportNoDataAsSkipped)
    {
        var childInterfaces = interfaces
            .Where(i => i.Enabled && i.ParentInterfaceKey == iface.InterfaceKey)
            .OrderBy(i => i.ServerCode)
            .ToList();

        if (childInterfaces.Count == 0)
        {
            return await FetchAndPushStandaloneInterfaceAsync(
                iface, task, triggerRecord, hisPatId, visitSn, patName, pushService, triggerType, sourceRecordKey, ct,
                reportNoDataAsSkipped);
        }

        return await FetchAndPushCompositeInterfaceAsync(
            iface, childInterfaces, task, triggerRecord, hisPatId, visitSn, patName, pushService, triggerType, sourceRecordKey, ct,
            reportNoDataAsSkipped);
    }

    private async Task<InterfaceExecutionResult> FetchAndPushStandaloneInterfaceAsync(
        SyncTaskInterface iface,
        SyncTask task,
        Dictionary<string, object> triggerRecord,
        string hisPatId,
        string? visitSn,
        string? patName,
        IPushService pushService,
        string triggerType,
        string? sourceRecordKey,
        CancellationToken ct,
        bool reportNoDataAsSkipped)
    {
        var detail = new InterfaceSyncDetail
        {
            ServerCode = iface.ServerCode,
            InterfaceName = iface.DisplayName
        };
        try
        {
            var data = await QueryInterfaceFromTriggerAsync(
                iface, task, triggerRecord, hisPatId, visitSn, ct);

            if (data.Count == 0)
            {
                detail.Success = true;
                detail.Skipped = reportNoDataAsSkipped;
                detail.Stage = reportNoDataAsSkipped ? StageNoData : StageSuccess;

                if (!reportNoDataAsSkipped)
                    await AddInterfaceLogAsync(task, iface, hisPatId, visitSn, patName, true, triggerType, null, sourceRecordKey, ct);

                return new InterfaceExecutionResult
                {
                    Status = reportNoDataAsSkipped ? InterfaceExecutionStatus.Skipped : InterfaceExecutionStatus.Success,
                    Detail = detail
                };
            }

            if (data.Count > 0)
            {
                // 注入触发源字段到查询结果
                InjectTriggerFields(data, triggerRecord, iface.InjectFields);

                await PushInterfaceDataAsync(task.PushTarget, iface, pushService, data, ct);
            }

            await AddInterfaceLogAsync(task, iface, hisPatId, visitSn, patName, true, triggerType, null, sourceRecordKey, ct);

            detail.Success = true;
            detail.Stage = StageSuccess;
            return new InterfaceExecutionResult
            {
                Status = InterfaceExecutionStatus.Success,
                Detail = detail
            };
        }
        catch (Exception ex)
        {
            await AddInterfaceLogAsync(task, iface, hisPatId, visitSn, patName, false, triggerType, ex.Message, sourceRecordKey, ct);

            _logger.LogWarning(ex, "接口 {ServerCode} 查询/推送失败（患者 {HisPatId}）",
                iface.ServerCode, hisPatId);

            detail.Success = false;
            detail.Stage = GetFailureStage(ex.Message);
            detail.ErrorMessage = ex.Message;
            return new InterfaceExecutionResult
            {
                Status = InterfaceExecutionStatus.Failed,
                Detail = detail
            };
        }
    }

    private async Task<InterfaceExecutionResult> FetchAndPushCompositeInterfaceAsync(
        SyncTaskInterface rootInterface,
        List<SyncTaskInterface> childInterfaces,
        SyncTask task,
        Dictionary<string, object> triggerRecord,
        string hisPatId,
        string? visitSn,
        string? patName,
        IPushService pushService,
        string triggerType,
        string? sourceRecordKey,
        CancellationToken ct,
        bool reportNoDataAsSkipped)
    {
        var detail = new InterfaceSyncDetail
        {
            ServerCode = rootInterface.ServerCode,
            InterfaceName = rootInterface.DisplayName
        };
        try
        {
            var rootData = await QueryInterfaceFromTriggerAsync(
                rootInterface, task, triggerRecord, hisPatId, visitSn, ct);

            if (rootData.Count == 0)
            {
                detail.Success = true;
                detail.Skipped = reportNoDataAsSkipped;
                detail.Stage = reportNoDataAsSkipped ? StageNoData : StageSuccess;

                if (!reportNoDataAsSkipped)
                    await AddInterfaceLogAsync(task, rootInterface, hisPatId, visitSn, patName, true, triggerType, null, sourceRecordKey, ct);

                return new InterfaceExecutionResult
                {
                    Status = reportNoDataAsSkipped ? InterfaceExecutionStatus.Skipped : InterfaceExecutionStatus.Success,
                    Detail = detail
                };
            }

            InjectTriggerFields(rootData, triggerRecord, rootInterface.InjectFields);

            var compositePlans = new List<CompositePlan>();
            var skippedReasons = new List<string>();
            var skippedCount = 0;

            foreach (var rootRecord in rootData)
            {
                var matchedChildren = ResolveMatchedChildInterfaces(childInterfaces, rootRecord);
                if (matchedChildren.Count == 0)
                {
                    skippedCount++;
                    continue;
                }

                if (matchedChildren.Count > 1)
                {
                    skippedCount++;
                    skippedReasons.Add($"{DescribeRecord(rootRecord, rootInterface.QueryField)} 同时命中多个子接口");
                    continue;
                }

                var childInterface = matchedChildren[0];
                var linkMappings = GetInterfaceLinkMappings(childInterface);
                if (linkMappings.Count == 0)
                {
                    skippedCount++;
                    skippedReasons.Add($"{DescribeRecord(rootRecord, rootInterface.QueryField)} 子接口 {childInterface.ServerCode} 未配置关联字段");
                    continue;
                }

                var linkValues = new List<InterfaceLinkValue>();
                foreach (var mapping in linkMappings)
                {
                    var parentValue = GetStringValue(rootRecord, mapping.ParentField);
                    if (string.IsNullOrWhiteSpace(parentValue))
                    {
                        skippedReasons.Add($"{DescribeRecord(rootRecord, mapping.ParentField)} 缺少父接口取值字段 {mapping.ParentField}");
                        linkValues.Clear();
                        break;
                    }

                    linkValues.Add(new InterfaceLinkValue(mapping.ChildField, parentValue));
                }

                if (linkValues.Count == 0)
                {
                    skippedCount++;
                    continue;
                }

                compositePlans.Add(new CompositePlan
                {
                    ChildInterface = childInterface,
                    RootRecord = rootRecord,
                    LinkValues = linkValues
                });
            }

            if (compositePlans.Count == 0)
            {
                LogCompositeSkippedRecords(rootInterface, skippedReasons);

                detail.Success = true;
                detail.Skipped = skippedCount > 0;
                detail.Stage = skippedCount > 0 ? StageNoData : StageSuccess;

                if (!reportNoDataAsSkipped || skippedCount == 0)
                    await AddInterfaceLogAsync(task, rootInterface, hisPatId, visitSn, patName, true, triggerType, null, sourceRecordKey, ct);

                return new InterfaceExecutionResult
                {
                    Status = skippedCount > 0 ? InterfaceExecutionStatus.Skipped : InterfaceExecutionStatus.Success,
                    Detail = detail
                };
            }

            var childDataMap = await QueryChildDataMapAsync(compositePlans, triggerRecord, task, skippedReasons, ct);
            var payloads = new List<Dictionary<string, object>>();

            foreach (var plan in compositePlans)
            {
                var childKey = BuildChildMapKey(plan.ChildInterface.InterfaceKey, plan.LinkValues);
                if (!childDataMap.TryGetValue(childKey, out var childRecords) || childRecords.Count == 0)
                {
                    skippedCount++;
                    skippedReasons.Add(
                        $"{DescribeRecord(plan.RootRecord, plan.LinkValues.FirstOrDefault()?.ChildField)} 未查到子接口 {plan.ChildInterface.ServerCode} 数据");
                    continue;
                }

                payloads.Add(BuildCompositePayload(rootInterface, plan.RootRecord, plan.ChildInterface.MountField!, childRecords));
            }

            LogCompositeSkippedRecords(rootInterface, skippedReasons);

            if (payloads.Count == 0)
            {
                detail.Success = true;
                detail.Skipped = true;
                detail.Stage = StageNoData;

                if (!reportNoDataAsSkipped)
                    await AddInterfaceLogAsync(task, rootInterface, hisPatId, visitSn, patName, true, triggerType, null, sourceRecordKey, ct);

                return new InterfaceExecutionResult
                {
                    Status = InterfaceExecutionStatus.Skipped,
                    Detail = detail
                };
            }

            await PushInterfaceDataAsync(task.PushTarget, rootInterface, pushService, payloads, ct);
            await AddInterfaceLogAsync(task, rootInterface, hisPatId, visitSn, patName, true, triggerType, null, sourceRecordKey, ct);

            detail.Success = true;
            detail.Stage = StageSuccess;
            return new InterfaceExecutionResult
            {
                Status = InterfaceExecutionStatus.Success,
                Detail = detail
            };
        }
        catch (Exception ex)
        {
            await AddInterfaceLogAsync(task, rootInterface, hisPatId, visitSn, patName, false, triggerType, ex.Message, sourceRecordKey, ct);
            _logger.LogWarning(ex, "组合接口 {ServerCode} 查询/组装失败（患者 {HisPatId}）",
                rootInterface.ServerCode, hisPatId);

            detail.Success = false;
            detail.Stage = GetFailureStage(ex.Message);
            detail.ErrorMessage = ex.Message;
            return new InterfaceExecutionResult
            {
                Status = InterfaceExecutionStatus.Failed,
                Detail = detail
            };
        }
    }

    private static List<SyncTaskInterface> FilterSelectedInterfaces(
        List<SyncTaskInterface> interfaces,
        IReadOnlyCollection<int>? selectedInterfaceIds)
    {
        if (selectedInterfaceIds == null)
            return interfaces;

        var selectedIds = selectedInterfaceIds.ToHashSet();
        if (selectedIds.Count == 0)
            return [];

        var selectedTopLevelKeys = interfaces
            .Where(i => selectedIds.Contains(i.Id) && string.IsNullOrWhiteSpace(i.ParentInterfaceKey))
            .Select(i => i.InterfaceKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return interfaces
            .Where(i =>
                selectedIds.Contains(i.Id) && string.IsNullOrWhiteSpace(i.ParentInterfaceKey)
                || !string.IsNullOrWhiteSpace(i.ParentInterfaceKey)
                   && selectedTopLevelKeys.Contains(i.ParentInterfaceKey))
            .ToList();
    }

    private static void AddCompletedInterface(SyncResult result, SyncTaskInterface iface)
    {
        lock (result.CompletedInterfaceKeys)
            result.CompletedInterfaceKeys.Add(InterfaceAccessWindow.GetProgressKey(iface));
    }

    private static IReadOnlyCollection<int>? GetSelectedInterfaceIds(
        Dictionary<string, List<int>>? selectedInterfaceIdsByTask,
        string taskCode)
    {
        if (selectedInterfaceIdsByTask == null)
            return null;

        return selectedInterfaceIdsByTask.TryGetValue(taskCode, out var ids) ? ids : [];
    }

    private static bool ShouldIncludeTriggerRecord(
        Dictionary<string, bool>? includeTriggerRecordByTask,
        string taskCode)
    {
        if (includeTriggerRecordByTask == null)
            return true;

        return includeTriggerRecordByTask.TryGetValue(taskCode, out var include) && include;
    }

    private static string GetFailureStage(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return StageFailed;

        if (errorMessage.StartsWith("[数据湖查询]", StringComparison.Ordinal))
            return StageFetchFailed;

        if (errorMessage.StartsWith("[动态接口查询]", StringComparison.Ordinal))
            return StageFetchFailed;

        if (errorMessage.StartsWith("[数据库查询]", StringComparison.Ordinal) ||
            errorMessage.StartsWith("[SQL查询]", StringComparison.Ordinal))
            return StageFetchFailed;

        return errorMessage.StartsWith("[推送]", StringComparison.Ordinal)
            ? StagePushFailed
            : StageFailed;
    }

    private async Task<List<Dictionary<string, object>>> QueryInterfaceFromTriggerAsync(
        SyncTaskInterface iface,
        SyncTask task,
        Dictionary<string, object> triggerRecord,
        string hisPatId,
        string? visitSn,
        CancellationToken ct)
    {
        if (IsDynamicApiInterface(iface))
        {
            try
            {
                return await _dynamicApiClient.QueryAllPagesAsync(
                    iface.QueryPath ?? "",
                    hisPatId,
                    visitSn ?? "",
                    iface.UseTodayTimeRange,
                    ct);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"[动态接口查询] {ex.Message}", ex);
            }
        }

        string queryValue;
        if (!string.IsNullOrEmpty(iface.QueryValueField))
        {
            queryValue = GetStringValue(triggerRecord, iface.QueryValueField);
        }
        else
        {
            queryValue = iface.QueryField == task.PatientIdField ? hisPatId : (visitSn ?? hisPatId);
        }

        return await QueryInterfaceDataAsync(iface, task, [queryValue], ct);
    }

    private async Task<List<Dictionary<string, object>>> QueryInterfaceDataAsync(
        SyncTaskInterface iface,
        SyncTask task,
        IReadOnlyCollection<string> queryValues,
        CancellationToken ct)
    {
        try
        {
            if (IsDynamicApiInterface(iface))
                throw new InvalidOperationException("动态接口只支持按患者触发记录查询");

            var validValues = queryValues
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validValues.Count == 0)
                return [];

            var allData = new List<Dictionary<string, object>>();
            var databaseConnection = IsDatabaseInterface(iface)
                ? await ResolveDatabaseConnectionAsync(iface, task, ct)
                : null;
            foreach (var chunk in validValues.Chunk(200))
            {
                var chunkData = IsDatabaseInterface(iface)
                    ? await _databaseQueryService.QueryByValuesAsync(
                        databaseConnection!.DatabaseType,
                        databaseConnection.ConnectionStringName,
                        databaseConnection.Host,
                        databaseConnection.Database,
                        databaseConnection.Username,
                        databaseConnection.Password,
                        databaseConnection.TrustCertificate,
                        iface.QuerySql ?? "",
                        iface.QueryField,
                        chunk,
                        ct)
                    : await _dataLakeClient.QueryAllPagesAsync(iface.ServerCode, BuildQueryConditions(iface, chunk), ct);
                allData.AddRange(chunkData);
            }

            return allData;
        }
        catch (Exception ex)
        {
            var prefix = GetQueryErrorPrefix(iface);
            throw new InvalidOperationException($"{prefix} {ex.Message}", ex);
        }
    }

    private async Task<List<Dictionary<string, object>>> QueryInterfaceDataByLinkValueSetsAsync(
        SyncTaskInterface iface,
        SyncTask task,
        IReadOnlyCollection<InterfaceLinkValueSet> queryValueSets,
        CancellationToken ct)
    {
        try
        {
            if (IsDynamicApiInterface(iface))
                throw new InvalidOperationException("动态接口不支持父子关联查询");

            var validSets = queryValueSets
                .Where(set => set.Values.Count > 0 && set.Values.All(value => !string.IsNullOrWhiteSpace(value.Value)))
                .DistinctBy(set => BuildLinkValuesKey(set.Values))
                .ToList();

            if (validSets.Count == 0)
                return [];

            var databaseConnection = IsDatabaseInterface(iface)
                ? await ResolveDatabaseConnectionAsync(iface, task, ct)
                : null;

            if (IsDatabaseInterface(iface))
            {
                return await _databaseQueryService.QueryByFieldValueSetsAsync(
                    databaseConnection!.DatabaseType,
                    databaseConnection.ConnectionStringName,
                    databaseConnection.Host,
                    databaseConnection.Database,
                    databaseConnection.Username,
                    databaseConnection.Password,
                    databaseConnection.TrustCertificate,
                    iface.QuerySql ?? "",
                    validSets
                        .Select(set => (IReadOnlyDictionary<string, string>)set.Values
                            .ToDictionary(value => value.ChildField, value => value.Value, StringComparer.OrdinalIgnoreCase))
                        .ToList(),
                    ct);
            }

            var allData = new List<Dictionary<string, object>>();
            foreach (var chunk in validSets.Chunk(200))
            {
                var chunkList = chunk.ToList();
                var chunkData = await _dataLakeClient.QueryAllPagesAsync(
                    iface.ServerCode,
                    BuildLinkQueryConditions(iface, chunkList),
                    ct);
                allData.AddRange(FilterByLinkValueSets(chunkData, chunkList));
            }

            return allData;
        }
        catch (Exception ex)
        {
            var prefix = GetQueryErrorPrefix(iface);
            throw new InvalidOperationException($"{prefix} {ex.Message}", ex);
        }
    }

    private static bool IsDatabaseInterface(SyncTaskInterface iface) =>
        IngestionService.IsDatabaseSourceType(iface.SourceType);

    private static bool IsDynamicApiInterface(SyncTaskInterface iface) =>
        IngestionService.IsDynamicApiSourceType(iface.SourceType);

    private static string GetQueryErrorPrefix(SyncTaskInterface iface)
    {
        if (IsDatabaseInterface(iface))
            return "[数据库查询]";

        return IsDynamicApiInterface(iface) ? "[动态接口查询]" : "[数据湖查询]";
    }

    private async Task<DatabaseConnectionConfig> ResolveDatabaseConnectionAsync(
        SyncTaskInterface iface,
        SyncTask task,
        CancellationToken ct)
    {
        var interfaceDatabaseType = IngestionService.NormalizeDatabaseType(iface.DatabaseType, iface.SourceType);
        if (iface.DatabaseResourceId.HasValue)
        {
            var resource = await GetDatabaseResourceAsync(iface.DatabaseResourceId.Value, ct);
            if (resource == null)
                throw new InvalidOperationException($"数据库资源不存在：{iface.DatabaseResourceId.Value}");

            return ToDatabaseConnectionConfig(resource);
        }

        if (HasInterfaceDatabaseConnection(iface) || !string.IsNullOrWhiteSpace(iface.ConnectionStringName))
        {
            return new DatabaseConnectionConfig(
                interfaceDatabaseType,
                iface.ConnectionStringName,
                iface.SqlServerHost,
                iface.SqlServerDatabase,
                iface.SqlServerUsername,
                iface.SqlServerPassword,
                iface.SqlServerTrustCertificate);
        }

        var source = await _ingestionService.GetSourceByServerCodeAsync(task.TriggerServerCode, ct);
        if (source == null)
            return new DatabaseConnectionConfig(
                interfaceDatabaseType,
                null,
                null,
                null,
                null,
                null,
                true);

        if (source.DatabaseResource != null)
            return ToDatabaseConnectionConfig(source.DatabaseResource);

        var sourceDatabaseType = IngestionService.NormalizeDatabaseType(source.DatabaseType, source.SourceType);
        if (!string.Equals(sourceDatabaseType, interfaceDatabaseType, StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseConnectionConfig(interfaceDatabaseType, null, null, null, null, null, true);
        }

        return new DatabaseConnectionConfig(
            sourceDatabaseType,
            source.ConnectionStringName,
            source.SqlServerHost,
            source.SqlServerDatabase,
            source.SqlServerUsername,
            source.SqlServerPassword,
            source.SqlServerTrustCertificate);
    }

    private static bool HasInterfaceDatabaseConnection(SyncTaskInterface iface) =>
        !string.IsNullOrWhiteSpace(iface.SqlServerHost) ||
        !string.IsNullOrWhiteSpace(iface.SqlServerDatabase) ||
        !string.IsNullOrWhiteSpace(iface.SqlServerUsername) ||
        !string.IsNullOrWhiteSpace(iface.SqlServerPassword);

    private async Task<DatabaseResource?> GetDatabaseResourceAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DatabaseResources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    private static DatabaseConnectionConfig ToDatabaseConnectionConfig(DatabaseResource resource) =>
        new(
            IngestionService.NormalizeDatabaseType(resource.DatabaseType),
            null,
            resource.Host,
            resource.DatabaseName,
            resource.Username,
            resource.Password,
            resource.TrustCertificate);

    private List<DataLakeCondition> BuildQueryConditions(
        SyncTaskInterface iface,
        IReadOnlyCollection<string> queryValues)
    {
        var conditions = new List<DataLakeCondition>();
        if (queryValues.Count == 1)
        {
            conditions.Add(new DataLakeCondition
            {
                Column = iface.QueryField,
                Type = "eq",
                Value = queryValues.First()
            });
        }
        else
        {
            conditions.Add(new DataLakeCondition
            {
                Column = iface.QueryField,
                Type = "in",
                Value = string.Join(",", queryValues)
            });
        }

        if (!string.IsNullOrEmpty(iface.FilterConditions))
        {
            try
            {
                var filters = JsonSerializer.Deserialize<List<DataLakeCondition>>(iface.FilterConditions);
                if (filters != null)
                    conditions.AddRange(filters);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "接口 {ServerCode} 过滤条件 JSON 解析失败：{FilterConditions}",
                    iface.ServerCode, iface.FilterConditions);
            }
        }

        return conditions;
    }

    private List<DataLakeCondition> BuildLinkQueryConditions(
        SyncTaskInterface iface,
        IReadOnlyCollection<InterfaceLinkValueSet> queryValueSets)
    {
        var conditions = new List<DataLakeCondition>();
        var fields = queryValueSets
            .SelectMany(set => set.Values.Select(value => value.ChildField))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var field in fields)
        {
            var values = queryValueSets
                .SelectMany(set => set.Values
                    .Where(value => string.Equals(value.ChildField, field, StringComparison.OrdinalIgnoreCase))
                    .Select(value => value.Value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (values.Count == 0)
                continue;

            conditions.Add(new DataLakeCondition
            {
                Column = field,
                Type = values.Count == 1 ? "eq" : "in",
                Value = values.Count == 1 ? values[0] : string.Join(",", values)
            });
        }

        if (!string.IsNullOrEmpty(iface.FilterConditions))
        {
            try
            {
                var filters = JsonSerializer.Deserialize<List<DataLakeCondition>>(iface.FilterConditions);
                if (filters != null)
                    conditions.AddRange(filters);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "接口 {ServerCode} 过滤条件 JSON 解析失败：{FilterConditions}",
                    iface.ServerCode, iface.FilterConditions);
            }
        }

        return conditions;
    }

    private static List<Dictionary<string, object>> FilterByLinkValueSets(
        List<Dictionary<string, object>> data,
        IReadOnlyCollection<InterfaceLinkValueSet> queryValueSets)
    {
        if (data.Count == 0 || queryValueSets.Count == 0)
            return data;

        var fields = queryValueSets.First().Values.Select(value => value.ChildField).ToList();
        var allowedKeys = queryValueSets
            .Select(set => BuildLinkValuesKey(set.Values))
            .ToHashSet(StringComparer.Ordinal);

        return data
            .Where(record =>
            {
                var values = fields
                    .Select(field => new InterfaceLinkValue(field, GetStringValue(record, field)))
                    .ToList();
                return values.All(value => !string.IsNullOrWhiteSpace(value.Value)) &&
                    allowedKeys.Contains(BuildLinkValuesKey(values));
            })
            .ToList();
    }

    private List<InterfaceLinkMapping> GetInterfaceLinkMappings(SyncTaskInterface iface)
    {
        if (!string.IsNullOrWhiteSpace(iface.LinkMappings))
        {
            try
            {
                var mappings = JsonSerializer.Deserialize<List<InterfaceLinkMapping>>(iface.LinkMappings);
                if (mappings != null)
                {
                    var validMappings = mappings
                        .Where(mapping =>
                            !string.IsNullOrWhiteSpace(mapping.ParentField) &&
                            !string.IsNullOrWhiteSpace(mapping.ChildField))
                        .Select(mapping => new InterfaceLinkMapping
                        {
                            ParentField = mapping.ParentField.Trim(),
                            ChildField = mapping.ChildField.Trim()
                        })
                        .ToList();
                    if (validMappings.Count > 0)
                        return validMappings;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "接口 {ServerCode} 关联字段 JSON 解析失败：{LinkMappings}",
                    iface.ServerCode, iface.LinkMappings);
            }
        }

        return string.IsNullOrWhiteSpace(iface.ParentResultField) || string.IsNullOrWhiteSpace(iface.QueryField)
            ? []
            :
            [
                new InterfaceLinkMapping
                {
                    ParentField = iface.ParentResultField,
                    ChildField = iface.QueryField
                }
            ];
    }

    private async Task PushInterfaceDataAsync(
        string pushTarget,
        SyncTaskInterface iface,
        IPushService pushService,
        List<Dictionary<string, object>> data,
        CancellationToken ct)
    {
        try
        {
            var resolvedTarget = ResolvePushTarget(pushTarget, iface.PushParams);
            await pushService.PushAsync(resolvedTarget, iface.ServerCode, data, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"[推送] {ex.Message}", ex);
        }
    }

    private async Task<Dictionary<string, List<Dictionary<string, object>>>> QueryChildDataMapAsync(
        List<CompositePlan> compositePlans,
        Dictionary<string, object> triggerRecord,
        SyncTask task,
        List<string> skippedReasons,
        CancellationToken ct)
    {
        var result = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in compositePlans.GroupBy(p => p.ChildInterface.InterfaceKey))
        {
            var childInterface = group.First().ChildInterface;
            var queryValueSets = group
                .Select(p => new InterfaceLinkValueSet(p.LinkValues))
                .DistinctBy(set => BuildLinkValuesKey(set.Values))
                .ToList();

            var childData = await QueryChildInterfaceDataAsync(childInterface, task, queryValueSets, skippedReasons, ct);
            if (childData.Count > 0)
                InjectTriggerFields(childData, triggerRecord, childInterface.InjectFields);

            var linkMappings = GetInterfaceLinkMappings(childInterface);
            foreach (var record in childData)
            {
                var recordValues = linkMappings
                    .Select(mapping => new InterfaceLinkValue(mapping.ChildField, GetStringValue(record, mapping.ChildField)))
                    .ToList();
                if (recordValues.Any(value => string.IsNullOrWhiteSpace(value.Value)))
                    continue;

                var key = BuildChildMapKey(childInterface.InterfaceKey, recordValues);
                if (!result.TryGetValue(key, out var list))
                {
                    list = [];
                    result[key] = list;
                }

                list.Add(CloneRecord(record));
            }
        }

        return result;
    }

    private async Task<List<Dictionary<string, object>>> QueryChildInterfaceDataAsync(
        SyncTaskInterface childInterface,
        SyncTask task,
        List<InterfaceLinkValueSet> queryValueSets,
        List<string> skippedReasons,
        CancellationToken ct)
    {
        try
        {
            return await QueryInterfaceDataByLinkValueSetsAsync(childInterface, task, queryValueSets, ct);
        }
        catch (Exception ex) when (queryValueSets.Count > 1 && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "子接口 {ServerCode} 批量查询失败，改为逐条查询", childInterface.ServerCode);

            var result = new List<Dictionary<string, object>>();
            foreach (var valueSet in queryValueSets)
            {
                try
                {
                    result.AddRange(await QueryInterfaceDataByLinkValueSetsAsync(childInterface, task, [valueSet], ct));
                }
                catch (Exception itemEx) when (!ct.IsCancellationRequested)
                {
                    skippedReasons.Add(
                        $"{BuildLinkValueDescription(valueSet.Values)} 子接口 {childInterface.ServerCode} 查询失败：{itemEx.Message}");
                }
            }

            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            skippedReasons.Add(
                $"{BuildLinkValueDescription(queryValueSets.FirstOrDefault()?.Values ?? [])} 子接口 {childInterface.ServerCode} 查询失败：{ex.Message}");
            return [];
        }
    }

    private static string BuildChildMapKey(string interfaceKey, IReadOnlyList<InterfaceLinkValue> values) =>
        $"{interfaceKey}|{BuildLinkValuesKey(values)}";

    private static string BuildLinkValuesKey(IReadOnlyList<InterfaceLinkValue> values) =>
        JsonSerializer.Serialize(values.Select(value => value.Value).ToArray());

    private static string BuildLinkValueDescription(IReadOnlyList<InterfaceLinkValue> values) =>
        values.Count == 0
            ? "关联字段为空"
            : string.Join("，", values.Select(value => $"{value.ChildField}={value.Value}"));

    private List<SyncTaskInterface> ResolveMatchedChildInterfaces(
        List<SyncTaskInterface> childInterfaces,
        Dictionary<string, object> rootRecord)
    {
        if (childInterfaces.Count == 1 && !HasRouteRule(childInterfaces[0]))
            return [childInterfaces[0]];

        return childInterfaces
            .Where(child => EvaluateChildRoute(child, rootRecord))
            .ToList();
    }

    private static bool HasRouteRule(SyncTaskInterface childInterface) =>
        !string.IsNullOrWhiteSpace(childInterface.RouteField) &&
        !string.IsNullOrWhiteSpace(childInterface.RouteValue);

    private bool EvaluateChildRoute(SyncTaskInterface childInterface, Dictionary<string, object> rootRecord)
    {
        if (!HasRouteRule(childInterface))
            return false;

        var actualValue = GetStringValue(rootRecord, childInterface.RouteField!);
        if (string.IsNullOrWhiteSpace(actualValue))
            return false;

        var routeValues = childInterface.RouteValue!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (routeValues.Count == 0)
            return false;

        return string.Equals(childInterface.RouteOperator, "in", StringComparison.OrdinalIgnoreCase)
            ? routeValues.Contains(actualValue, StringComparer.OrdinalIgnoreCase)
            : routeValues.Any(v => string.Equals(v, actualValue, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, object> BuildCompositePayload(
        SyncTaskInterface rootInterface,
        Dictionary<string, object> rootRecord,
        string mountField,
        List<Dictionary<string, object>> childRecords)
    {
        var payload = BuildRootOutputRecord(rootInterface, rootRecord);
        payload[mountField] = childRecords.Select(CloneRecord).ToList();
        return payload;
    }

    private static Dictionary<string, object> BuildRootOutputRecord(
        SyncTaskInterface rootInterface,
        Dictionary<string, object> rootRecord)
    {
        var outputFields = ParseOutputFields(rootInterface.OutputFields);
        if (outputFields.Count == 0)
            return CloneRecord(rootRecord);

        var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in outputFields)
        {
            if (TryGetRecordValue(rootRecord, field, out var actualKey, out var value))
                payload[actualKey] = value;
        }

        return payload;
    }

    private static List<string> ParseOutputFields(string? outputFields) =>
        string.IsNullOrWhiteSpace(outputFields)
            ? []
            : outputFields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static bool TryGetRecordValue(
        Dictionary<string, object> record,
        string lookupKey,
        out string actualKey,
        out object value)
    {
        foreach (var pair in record)
        {
            if (string.Equals(pair.Key, lookupKey, StringComparison.OrdinalIgnoreCase))
            {
                actualKey = pair.Key;
                value = pair.Value;
                return true;
            }
        }

        actualKey = "";
        value = "";
        return false;
    }

    private static Dictionary<string, object> CloneRecord(Dictionary<string, object> record) =>
        record.ToDictionary(pair => pair.Key, pair => pair.Value);

    private static Dictionary<string, object> BuildTriggerPushPayload(Dictionary<string, object> record)
    {
        var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in record)
        {
            if (IsLocalSystemField(key))
                continue;

            payload[key] = value;
        }

        return payload;
    }

    private static bool IsLocalSystemField(string fieldName) =>
        string.Equals(fieldName, "ingested_at", StringComparison.OrdinalIgnoreCase);

    private static string DescribeRecord(Dictionary<string, object> record, string? fallbackField)
    {
        if (!string.IsNullOrWhiteSpace(fallbackField))
        {
            var fallbackValue = GetStringValue(record, fallbackField);
            if (!string.IsNullOrWhiteSpace(fallbackValue))
                return $"{fallbackField}={fallbackValue}";
        }

        var rowKey = GetStringValue(record, "ROWKEY");
        return string.IsNullOrWhiteSpace(rowKey) ? "当前记录" : $"ROWKEY={rowKey}";
    }

    private static string BuildCompositeFailureMessage(List<string> errors)
    {
        if (errors.Count <= 3)
            return string.Join("；", errors);

        return $"{string.Join("；", errors.Take(3))}；另有 {errors.Count - 3} 条关联错误";
    }

    private void LogCompositeSkippedRecords(SyncTaskInterface rootInterface, List<string> skippedReasons)
    {
        if (skippedReasons.Count == 0)
            return;

        _logger.LogWarning(
            "组合接口 {ServerCode} 跳过 {Count} 条异常关联记录：{Reason}",
            rootInterface.ServerCode,
            skippedReasons.Count,
            BuildCompositeFailureMessage(skippedReasons));
    }

    private async Task AddInterfaceLogAsync(
        SyncTask task,
        SyncTaskInterface iface,
        string hisPatId,
        string? visitSn,
        string? patName,
        bool success,
        string triggerType,
        string? errorMessage,
        string? sourceRecordKey,
        CancellationToken ct)
    {
        await _logService.AddLogAsync(new SyncLog
        {
            TaskCode = task.Code,
            HisPatId = hisPatId,
            PatVisitSn = visitSn,
            PatName = patName,
            ServerCode = iface.ServerCode,
            InterfaceName = iface.DisplayName,
            SourceRecordKey = sourceRecordKey,
            Success = success,
            ErrorMessage = errorMessage,
            TriggerType = triggerType,
            CreatedAt = DateTime.Now
        }, ct);
    }

    private async Task AddTriggerPushLogAsync(
        SyncTask task,
        string hisPatId,
        string? visitSn,
        string? patName,
        bool success,
        string triggerType,
        string? errorMessage,
        string? sourceRecordKey,
        CancellationToken ct)
    {
        await _logService.AddLogAsync(new SyncLog
        {
            TaskCode = task.Code,
            HisPatId = hisPatId,
            PatVisitSn = visitSn,
            PatName = patName,
            ServerCode = task.TriggerServerCode,
            InterfaceName = TriggerPushLogName,
            SourceRecordKey = sourceRecordKey,
            Success = success,
            ErrorMessage = errorMessage,
            TriggerType = triggerType,
            CreatedAt = DateTime.Now
        }, ct);
    }

    /// <summary>
    /// 将触发记录中的指定字段注入到每条查询结果中（仅当记录中不存在该字段时才注入）
    /// </summary>
    private void InjectTriggerFields(
        List<Dictionary<string, object>> data,
        Dictionary<string, object> triggerRecord,
        string? injectFieldsJson)
    {
        if (string.IsNullOrEmpty(injectFieldsJson)) return;

        List<string>? fields;
        try
        {
            fields = JsonSerializer.Deserialize<List<string>>(injectFieldsJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "注入字段 JSON 解析失败：{InjectFields}", injectFieldsJson);
            return;
        }

        if (fields == null || fields.Count == 0) return;

        foreach (var record in data)
        {
            foreach (var field in fields)
            {
                if (!record.ContainsKey(field) && triggerRecord.TryGetValue(field, out var value))
                    record[field] = value;
            }
        }
    }

    /// <summary>
    /// 解析路由参数并替换 PushTarget 中的 {xxx} 占位符
    /// </summary>
    private string ResolvePushTarget(string pushTarget, string? pushParams)
    {
        if (string.IsNullOrEmpty(pushParams) || !pushTarget.Contains('{'))
            return pushTarget;

        try
        {
            var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(pushParams);
            if (parameters == null) return pushTarget;

            var resolved = pushTarget;
            foreach (var (key, value) in parameters)
                resolved = resolved.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);

            if (resolved.Contains('{'))
                _logger.LogWarning("URL 中仍有未解析的占位符：{Url}", resolved);

            return resolved;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "路由参数 JSON 解析失败：{PushParams}", pushParams);
            return pushTarget;
        }
    }

    private static string GetStringValue(Dictionary<string, object> record, string key)
    {
        if (!record.TryGetValue(key, out var value))
            return "";

        return value switch
        {
            JsonElement je => je.ToString(),
            _ => value?.ToString() ?? ""
        };
    }

    private static string GetPatientName(Dictionary<string, object> record)
    {
        var patName = GetStringValue(record, "PAT_NAME");
        return string.IsNullOrWhiteSpace(patName)
            ? GetStringValue(record, "PATIENT_NAME")
            : patName;
    }

    private sealed class CompositePlan
    {
        public SyncTaskInterface ChildInterface { get; init; } = default!;
        public Dictionary<string, object> RootRecord { get; init; } = [];
        public List<InterfaceLinkValue> LinkValues { get; init; } = [];
    }

    private sealed record InterfaceLinkValue(string ChildField, string Value);

    private sealed class InterfaceLinkValueSet
    {
        public InterfaceLinkValueSet(IEnumerable<InterfaceLinkValue> values)
        {
            Values = values.ToList();
        }

        public List<InterfaceLinkValue> Values { get; }
    }

    private sealed class InterfaceExecutionResult
    {
        public InterfaceExecutionStatus Status { get; init; }
        public InterfaceSyncDetail Detail { get; init; } = new();
    }

    private sealed record DatabaseConnectionConfig(
        string DatabaseType,
        string? ConnectionStringName,
        string? Host,
        string? Database,
        string? Username,
        string? Password,
        bool TrustCertificate);

    private enum InterfaceExecutionStatus
    {
        Success,
        Skipped,
        Failed
    }
}
