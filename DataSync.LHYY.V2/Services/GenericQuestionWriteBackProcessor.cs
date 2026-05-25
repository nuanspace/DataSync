using Bio.Core.Services;
using Bio.Models;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
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

        if (string.IsNullOrWhiteSpace(config.MrnSourcePath))
            return ProcessResult.Fail($"配置错误：接口 {message.TranCode} 未配置病案号路径（MrnSourcePath）");

        var filterResult = await _filterRuleService.ApplyInterfaceFiltersAsync(
            root,
            message.TranCode,
            config.IntegrationProjectCode,
            config.MainRecordArrayPath);
        if (!filterResult.IsPassed)
            return ProcessResult.Filtered(filterResult.Reason);

        var mrn = MessageJsonHelper.ReadString(root, config.MrnSourcePath, mainContext);
        if (string.IsNullOrWhiteSpace(mrn))
            return ProcessResult.Fail($"未提取到病案号: 路径 {config.MrnSourcePath} 在消息中无匹配");

        var visitNo = MessageJsonHelper.ReadString(root, config.VisitNoSourcePath, mainContext);
        var inpatientNo = MessageJsonHelper.ReadString(root, config.InpatientNoSourcePath, mainContext);
        var eventStartTime = MessageJsonHelper.ReadDateTime(root, config.EventStartTimeSourcePath, mainContext);

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

        var dbPatient = await result.LogStepAsync("获取已有患者",
            () => _bioCore.GetPatientByMrnAsync(mrn, hospitalId, projectId));
        if (dbPatient == null)
            return ProcessResult.Fail($"未找到已有患者: MRN={mrn}");

        var dbEvent = await ResolveEventAsync(config, result, dbPatient, mrn, inpatientNo, visitNo, eventStartTime, projectId);
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
            mrn,
            config.EventTypeName,
            inpatientNo,
            visitNo,
            eventStartTime);

        var formQuestionDict = await _bioCore.GetFormQuestionDictByFormSetAsync(formSet.id);

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
                result,
                formQuestionDict,
                dbPatient,
                dbEvent.Event);
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
                    return ProcessResult.Fail($"QuestionId {questionId} 不在当前 FormSet 中，请重新绑定映射");
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
        ProcessResult result,
        Dictionary<Guid, form_question> formQuestionDict,
        patient dbPatient,
        patient_event dbEvent)
    {
        var questionValues = await result.ExtractWithLogAsync(
            "提取Question映射",
            () => _mappingExecutor.ExtractQuestionValuesAsync(root, message.TranCode, config.IntegrationProjectCode, config.MainRecordArrayPath));

        var questionCount = 0;
        foreach (var questionValue in questionValues)
        {
            var questionId = questionValue.QuestionId;
            if (!formQuestionDict.TryGetValue(questionId, out var question))
                return ProcessResult.Fail($"QuestionId {questionId} 不在当前 FormSet 中，请重新绑定映射");

            if (GenericMessageProcessor.ShouldSkipQuestionValue(question, questionValue, _logger))
                continue;

            var converted = GenericMessageProcessor.ConvertQuestionValue(question, questionValue.Value?.ToString(), _logger);
            if (converted == null)
                continue;

            await _directTargetWriteService.WriteAsync(question, dbPatient.id, dbEvent.id, converted);
            questionCount++;
        }

        result.AddStep("直接写入Question", true, $"成功写入 {questionCount} 个问题答案");

        var successResult = ProcessResult.Success("处理成功", dbPatient.id, dbEvent.id);
        successResult.Steps = result.Steps;
        return successResult;
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
        Guid projectId)
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
                return (null, ProcessResult.Fail(
                    $"未找到已有事件: MRN={mrn}, EventType={eventTypeName}, EventStartTime={eventStartTime.Value:yyyy-MM-dd}"));
            }

            return (dbEvent, null);
        }

        if (!config.AllowMissingEventTime)
            return (null, ProcessResult.Fail("未提取到事件开始时间"));

        var identity = await result.LogStepAsync("按住院标识定位住院时间",
            () => _eventIdentityService.FindByVisitIdentityAsync(config.IntegrationProjectCode, mrn, inpatientNo, visitNo));

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
            Models.Enums.MissingEventIdentityPolicy.Pending => (null, ProcessResult.Deferred("缺少事件时间且未能按住院号/住院次数定位住院时间")),
            _ => (null, ProcessResult.Fail("缺少事件时间且未能按住院号/住院次数定位住院时间"))
        };
    }
}
