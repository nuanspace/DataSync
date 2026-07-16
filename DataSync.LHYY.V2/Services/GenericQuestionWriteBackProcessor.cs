using Bio.Core.Services;
using Bio.Models;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 仅使用已有患者，定位事件后回写 Question
/// </summary>
public class GenericQuestionWriteBackProcessor
{
    private readonly BioCoreIntegrationService _bioCore;
    private readonly FieldMappingExecutor _mappingExecutor;
    private readonly FilterRuleService _filterRuleService;
    private readonly ConfigService _configService;
    private readonly EventIdentityService _eventIdentityService;
    private readonly DirectTargetWriteService _directTargetWriteService;
    private readonly ILogger<GenericQuestionWriteBackProcessor> _logger;

    public GenericQuestionWriteBackProcessor(
        BioCoreIntegrationService bioCore,
        FieldMappingExecutor mappingExecutor,
        FilterRuleService filterRuleService,
        ConfigService configService,
        EventIdentityService eventIdentityService,
        DirectTargetWriteService directTargetWriteService,
        ILogger<GenericQuestionWriteBackProcessor> logger)
    {
        _bioCore = bioCore;
        _mappingExecutor = mappingExecutor;
        _filterRuleService = filterRuleService;
        _configService = configService;
        _eventIdentityService = eventIdentityService;
        _directTargetWriteService = directTargetWriteService;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessAsync(EsbMessage message, EsbInterfaceConfig config)
    {
        var result = new ProcessResult();

        if (!MessageJsonHelper.TryParseToken(message.RawJson, out var root, out var error))
            return ProcessResult.Fail(error ?? "Raw JSON 解析失败");
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(root, config.MainRecordArrayPath);

        var filterResult = await _filterRuleService.ApplyInterfaceFiltersAsync(
            root,
            message.TranCode,
            config.IntegrationProjectCode,
            config.MainRecordArrayPath);
        if (!filterResult.IsPassed)
            return ProcessResult.Filtered(filterResult.Reason);

        var mrn = string.IsNullOrWhiteSpace(config.MrnSourcePath)
            ? null
            : MessageJsonHelper.ReadString(root, config.MrnSourcePath, mainContext);
        var visitNo = MessageJsonHelper.ReadString(root, config.VisitNoSourcePath, mainContext);
        var inpatientNo = MessageJsonHelper.ReadString(root, config.InpatientNoSourcePath, mainContext);
        var eventStartTime = MessageJsonHelper.ReadDateTime(root, config.EventStartTimeSourcePath, mainContext);

        if (string.IsNullOrWhiteSpace(mrn) &&
            string.IsNullOrWhiteSpace(visitNo) &&
            string.IsNullOrWhiteSpace(inpatientNo))
        {
            return ProcessResult.Fail("未提取到病案号，也未提取到就诊号/住院号或住院次数");
        }

        var licenseCode = string.IsNullOrWhiteSpace(config.LicenseCode)
            ? await _configService.GetDefaultLicenseCodeAsync(config.IntegrationProjectCode)
            : config.LicenseCode;
        if (string.IsNullOrWhiteSpace(licenseCode))
            return ProcessResult.Fail("未配置 LicenseCode");

        if (string.IsNullOrWhiteSpace(config.EventTypeName))
            return ProcessResult.Fail("未配置 EventTypeName");

        var (formSet, hospitalId, projectId) = await _bioCore.FindFormSetAsync(licenseCode, config.EventTypeName);
        if (formSet == null)
            return ProcessResult.Fail($"未找到 FormSet: LicenseCode={licenseCode}, EventType={config.EventTypeName}");

        EsbEventIdentity? visitIdentity = null;
        patient? dbPatient;
        if (!string.IsNullOrWhiteSpace(mrn))
        {
            dbPatient = await result.LogStepAsync("获取已有患者",
                () => _bioCore.GetPatientByMrnAsync(mrn, hospitalId, projectId));
        }
        else
        {
            visitIdentity = await result.LogStepAsync("按就诊标识定位患者事件",
                () => _eventIdentityService.FindByVisitIdentityAsync(
                    config.IntegrationProjectCode,
                    null,
                    inpatientNo,
                    visitNo,
                    config.EventTypeName));

            if (visitIdentity == null)
                return BuildMissingVisitIdentityResult(config, "未提取到病案号且未能按就诊号/住院号或住院次数定位患者事件");

            mrn = visitIdentity.Mrn;
            dbPatient = await result.LogStepAsync("按事件身份获取已有患者",
                () => _bioCore.GetPatientByIdAsync(visitIdentity.PatientId, hospitalId, projectId));

            if (dbPatient == null && !string.IsNullOrWhiteSpace(mrn))
            {
                dbPatient = await result.LogStepAsync("按映射病案号获取已有患者",
                    () => _bioCore.GetPatientByMrnAsync(mrn, hospitalId, projectId));
            }
        }

        if (dbPatient == null)
        {
            var reason = string.IsNullOrWhiteSpace(mrn)
                ? "业务跳过：未找到已有患者"
                : $"业务跳过：未找到已有患者，MRN={mrn}";
            if (config.MissingEventIdentityPolicy == MissingEventIdentityPolicy.Pending)
            {
                var waitingResult = ProcessResult.WaitingIdentity(reason);
                waitingResult.Steps = result.Steps;
                return waitingResult;
            }

            var skippedResult = ProcessResult.BusinessSkipped(reason);
            skippedResult.Steps = result.Steps;
            return skippedResult;
        }

        var dbEvent = await ResolveEventAsync(config, result, dbPatient, mrn!, inpatientNo, visitNo, eventStartTime, projectId, visitIdentity);
        if (dbEvent.Result != null)
        {
            dbEvent.Result.Steps = result.Steps;
            return dbEvent.Result;
        }

        if (dbEvent.Event == null)
            return ProcessResult.Fail("未能定位事件");

        await _eventIdentityService.UpsertAsync(
            message.IntegrationProjectCode,
            message.TranCode,
            dbPatient.id,
            dbEvent.Event.id,
            hospitalId,
            projectId,
            mrn!,
            config.EventTypeName,
            inpatientNo,
            visitNo,
            eventStartTime);

        var formQuestionDict = await _bioCore.GetFormQuestionDictByFormSetAsync(formSet.id);
        var hasSubCardMappings = await _mappingExecutor.HasMappingsAsync(
            message.TranCode,
            config.IntegrationProjectCode,
            MappingTarget.SubCard);

        IFormsetImportService importService;
        try
        {
            importService = await _bioCore.CreateImportServiceAsync(formSet.id);
        }
        catch (Exception ex) when (IsBioCoreMetadataMismatch(ex))
        {
            _logger.LogWarning(ex, "Bio.Core 初始化失败，改用 target 表直接写入");
            return await ProcessWithDirectTargetWriteAsync(
                root,
                message,
                config,
                filterResult,
                result,
                formQuestionDict,
                dbPatient,
                dbEvent.Event,
                hasSubCardMappings);
        }

        try
        {
            var questionValues = await result.ExtractWithLogAsync(
                "提取Question映射",
                () => _mappingExecutor.ExtractQuestionValuesAsync(root, message.TranCode, config.IntegrationProjectCode, config.MainRecordArrayPath));

            var questionCount = 0;
            foreach (var questionValue in questionValues)
            {
                var questionId = questionValue.QuestionId;
                if (!formQuestionDict.TryGetValue(questionId, out var question))
                {
                    _logger.LogWarning("QuestionId {QuestionId} 不在当前 FormSet 中，已跳过写入", questionId);
                    continue;
                }

                if (GenericMessageProcessor.ShouldSkipQuestionValue(question, questionValue, _logger))
                    continue;

                var converted = GenericMessageProcessor.ConvertQuestionValue(question, questionValue.Value?.ToString(), _logger);
                if (converted == null)
                    continue;

                try
                {
                    await importService.SetQuestionValueImportAsync(dbEvent.Event.id, dbPatient.id, questionId, converted);
                    questionCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "设置 Question {QuestionId} 失败", questionId);
                }
            }

            result.AddStep("写入Question", true, $"成功写入 {questionCount} 个问题答案");

            if (hasSubCardMappings)
            {
                int subCardCount;
                try
                {
                    subCardCount = await WriteSubCardWithImportAsync(
                        root,
                        message,
                        config,
                        filterResult,
                        result,
                        formQuestionDict,
                        dbPatient,
                        dbEvent.Event,
                        importService);
                }
                catch (InvalidOperationException ex)
                {
                    return ProcessResult.Fail(ex.Message);
                }

                result.AddStep("写入SubCard", true, $"成功写入 {subCardCount} 行子卡片");
            }

            await result.LogStepAsync("CommitBatch", async () =>
            {
                await importService.CommitBatchAsync();
                return true;
            });

            var successResult = ProcessResult.Success("处理成功", dbPatient.id, dbEvent.Event.id);
            successResult.Steps = result.Steps;
            return successResult;
        }
        catch
        {
            try { await importService.DiscardImportAsync(); } catch { }
            throw;
        }
    }

    private async Task<ProcessResult> ProcessWithDirectTargetWriteAsync(
        JToken root,
        EsbMessage message,
        EsbInterfaceConfig config,
        FilterResult filterResult,
        ProcessResult result,
        Dictionary<Guid, form_question> formQuestionDict,
        patient dbPatient,
        patient_event dbEvent,
        bool hasSubCardMappings)
    {
        var questionValues = await result.ExtractWithLogAsync(
            "提取Question映射",
            () => _mappingExecutor.ExtractQuestionValuesAsync(root, message.TranCode, config.IntegrationProjectCode, config.MainRecordArrayPath));

        var questionCount = 0;
        foreach (var questionValue in questionValues)
        {
            var questionId = questionValue.QuestionId;
            if (!formQuestionDict.TryGetValue(questionId, out var question))
            {
                _logger.LogWarning("QuestionId {QuestionId} 不在当前 FormSet 中，已跳过写入", questionId);
                continue;
            }

            if (GenericMessageProcessor.ShouldSkipQuestionValue(question, questionValue, _logger))
                continue;

            var converted = GenericMessageProcessor.ConvertQuestionValue(question, questionValue.Value?.ToString(), _logger);
            if (converted == null)
                continue;

            await _directTargetWriteService.WriteAsync(question, dbPatient.id, dbEvent.id, converted);
            questionCount++;
        }

        result.AddStep("直接写入Question", true, $"成功写入 {questionCount} 个问题答案");

        if (hasSubCardMappings)
        {
            int subCardCount;
            try
            {
                subCardCount = await WriteSubCardDirectAsync(
                    root,
                    message,
                    config,
                    filterResult,
                    result,
                    formQuestionDict,
                    dbPatient,
                    dbEvent);
            }
            catch (InvalidOperationException ex)
            {
                return ProcessResult.Fail(ex.Message);
            }

            result.AddStep("直接写入SubCard", true, $"成功写入 {subCardCount} 行子卡片");
        }

        var successResult = ProcessResult.Success("处理成功", dbPatient.id, dbEvent.id);
        successResult.Steps = result.Steps;
        return successResult;
    }

    private async Task<int> WriteSubCardWithImportAsync(
        JToken root,
        EsbMessage message,
        EsbInterfaceConfig config,
        FilterResult filterResult,
        ProcessResult result,
        Dictionary<Guid, form_question> formQuestionDict,
        patient dbPatient,
        patient_event dbEvent,
        IFormsetImportService importService)
    {
        var subCardDataList = await result.ExtractWithLogAsync(
            "提取SubCard映射",
            () => _mappingExecutor.ExtractSubCardDataAsync(
                root,
                message.TranCode,
                filterResult.RowFilterResults,
                config.IntegrationProjectCode,
                config.MainRecordArrayPath));

        var subCardCount = 0;
        foreach (var subCardData in subCardDataList)
        {
            foreach (var row in subCardData.Rows)
            {
                try
                {
                    var subCardId = Guid.Empty;
                    var written = false;

                    foreach (var questionValue in row)
                    {
                        var questionId = questionValue.QuestionId;
                        if (!formQuestionDict.TryGetValue(questionId, out var question))
                        {
                            _logger.LogWarning("SubCard QuestionId {QuestionId} 不在当前 FormSet 中，已跳过写入", questionId);
                            continue;
                        }

                        if (GenericMessageProcessor.ShouldSkipQuestionValue(question, questionValue, _logger))
                            continue;

                        var converted = GenericMessageProcessor.ConvertQuestionValue(question, questionValue.Value?.ToString(), _logger);
                        if (converted == null)
                            continue;

                        if (subCardId == Guid.Empty)
                        {
                            subCardId = await importService.AddSubCardImportAsync(
                                dbEvent.id,
                                dbPatient.id,
                                subCardData.CardId);

                            if (subCardId == Guid.Empty)
                            {
                                _logger.LogWarning("创建 SubCard 失败: CardId={CardId}", subCardData.CardId);
                                break;
                            }
                        }

                        try
                        {
                            await importService.SetQuestionValueImportAsync(
                                dbEvent.id,
                                dbPatient.id,
                                questionId,
                                converted,
                                subCardId);
                            written = true;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "SubCard Question {QuestionId} 写入失败", questionId);
                        }
                    }

                    if (written)
                        subCardCount++;
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "处理 SubCard 行数据失败: CardId={CardId}", subCardData.CardId);
                }
            }
        }

        return subCardCount;
    }

    private async Task<int> WriteSubCardDirectAsync(
        JToken root,
        EsbMessage message,
        EsbInterfaceConfig config,
        FilterResult filterResult,
        ProcessResult result,
        Dictionary<Guid, form_question> formQuestionDict,
        patient dbPatient,
        patient_event dbEvent)
    {
        var subCardDataList = await result.ExtractWithLogAsync(
            "提取SubCard映射",
            () => _mappingExecutor.ExtractSubCardDataAsync(
                root,
                message.TranCode,
                filterResult.RowFilterResults,
                config.IntegrationProjectCode,
                config.MainRecordArrayPath));

        var subCardCount = 0;
        foreach (var subCardData in subCardDataList)
        {
            foreach (var row in subCardData.Rows)
            {
                var subCardId = Guid.NewGuid();
                var written = false;

                foreach (var questionValue in row)
                {
                    var questionId = questionValue.QuestionId;
                    if (!formQuestionDict.TryGetValue(questionId, out var question))
                    {
                        _logger.LogWarning("SubCard QuestionId {QuestionId} 不在当前 FormSet 中，已跳过写入", questionId);
                        continue;
                    }

                    if (GenericMessageProcessor.ShouldSkipQuestionValue(question, questionValue, _logger))
                        continue;

                    var converted = GenericMessageProcessor.ConvertQuestionValue(question, questionValue.Value?.ToString(), _logger);
                    if (converted == null)
                        continue;

                    await _directTargetWriteService.WriteAsync(question, dbPatient.id, dbEvent.id, converted, subCardId);
                    written = true;
                }

                if (written)
                    subCardCount++;
            }
        }

        return subCardCount;
    }

    private static bool IsBioCoreMetadataMismatch(Exception ex)
        => ex.Message.Contains("V2 元数据与表单字段定义不一致", StringComparison.Ordinal);

    private async Task<(patient_event? Event, ProcessResult? Result)> ResolveEventAsync(
        EsbInterfaceConfig config,
        ProcessResult result,
        patient dbPatient,
        string mrn,
        string? inpatientNo,
        string? visitNo,
        DateTime? eventStartTime,
        Guid projectId,
        EsbEventIdentity? knownIdentity = null)
    {
        var eventTypeName = config.EventTypeName!;

        if (eventStartTime.HasValue)
        {
            var dbEvent = await result.LogStepAsync("查找已有事件",
                () => _bioCore.GetExistingEventAsync(
                    dbPatient.id,
                    projectId,
                    eventStartTime.Value,
                    eventTypeName));

            if (dbEvent == null)
            {
                return (null, BuildMissingEventResult(config,
                    $"未找到已有事件: MRN={mrn}, EventType={eventTypeName}, EventStartTime={eventStartTime.Value:yyyy-MM-dd}"));
            }

            return (dbEvent, null);
        }

        var canLookupByVisitIdentity = knownIdentity != null ||
            !string.IsNullOrWhiteSpace(inpatientNo) ||
            !string.IsNullOrWhiteSpace(visitNo);

        if (!config.AllowMissingEventTime && !canLookupByVisitIdentity)
            return (null, ProcessResult.Fail("未提取到事件开始时间"));

        var identity = knownIdentity ?? await result.LogStepAsync("按住院标识定位住院时间",
            () => _eventIdentityService.FindByVisitIdentityAsync(
                config.IntegrationProjectCode,
                mrn,
                inpatientNo,
                visitNo,
                eventTypeName));

        if (identity is { EventId: var eventId } && eventId != Guid.Empty)
        {
            var dbEvent = await result.LogStepAsync("按事件身份查找已有事件",
                () => _bioCore.GetEventByIdAsync(eventId));

            if (dbEvent != null &&
                dbEvent.patient_id == dbPatient.id &&
                dbEvent.project_id == projectId &&
                string.Equals(dbEvent.event_type, eventTypeName, StringComparison.Ordinal))
            {
                return (dbEvent, null);
            }
        }

        if (identity?.EventStartTime != null)
        {
            var dbEvent = await result.LogStepAsync("按住院时间查找已有事件",
                () => _bioCore.GetExistingEventAsync(
                    dbPatient.id,
                    projectId,
                    identity.EventStartTime.Value,
                    eventTypeName));

            if (dbEvent != null)
                return (dbEvent, null);
        }

        return config.MissingEventIdentityPolicy switch
        {
            MissingEventIdentityPolicy.Pending => (null, ProcessResult.WaitingIdentity("缺少事件时间且未能按就诊号/住院号或住院次数定位住院时间")),
            _ => (null, ProcessResult.Fail("缺少事件时间且未能按就诊号/住院号或住院次数定位住院时间"))
        };
    }

    private static ProcessResult BuildMissingVisitIdentityResult(EsbInterfaceConfig config, string message)
        => config.MissingEventIdentityPolicy == MissingEventIdentityPolicy.Pending
            ? ProcessResult.WaitingIdentity(message)
            : ProcessResult.Fail(message);

    private static ProcessResult BuildMissingEventResult(EsbInterfaceConfig config, string message)
        => config.MissingEventIdentityPolicy == MissingEventIdentityPolicy.Pending
            ? ProcessResult.WaitingIdentity(message)
            : ProcessResult.Fail(message);
}
