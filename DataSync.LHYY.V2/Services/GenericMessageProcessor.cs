using System.Globalization;
using Bio.Core.Services;
using Bio.Models;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 通用消息处理器
/// </summary>
public class GenericMessageProcessor
{
    private readonly BioCoreIntegrationService _bioCore;
    private readonly FieldMappingExecutor _mappingExecutor;
    private readonly FilterRuleService _filterRuleService;
    private readonly ConfigService _configService;
    private readonly EventIdentityService _eventIdentityService;
    private readonly ActiveMedicalRecordService _activeMedicalRecordService;
    private readonly DirectTargetWriteService _directTargetWriteService;
    private readonly NestedFormWriteService _nestedFormWriteService;
    private readonly ILogger<GenericMessageProcessor> _logger;

    public GenericMessageProcessor(
        BioCoreIntegrationService bioCore,
        FieldMappingExecutor mappingExecutor,
        FilterRuleService filterRuleService,
        ConfigService configService,
        EventIdentityService eventIdentityService,
        ActiveMedicalRecordService activeMedicalRecordService,
        DirectTargetWriteService directTargetWriteService,
        NestedFormWriteService nestedFormWriteService,
        ILogger<GenericMessageProcessor> logger)
    {
        _bioCore = bioCore;
        _mappingExecutor = mappingExecutor;
        _filterRuleService = filterRuleService;
        _configService = configService;
        _eventIdentityService = eventIdentityService;
        _activeMedicalRecordService = activeMedicalRecordService;
        _directTargetWriteService = directTargetWriteService;
        _nestedFormWriteService = nestedFormWriteService;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessAsync(EsbMessage message, EsbInterfaceConfig config)
    {
        var result = new ProcessResult();
        var integrationProjectCode = message.IntegrationProjectCode ?? config.IntegrationProjectCode;

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
        var combinedVisitIdentity = MessageJsonHelper.ReadString(root, config.CombinedVisitIdentitySourcePath, mainContext);
        var eventStartTime = MessageJsonHelper.ReadDateTime(root, config.EventStartTimeSourcePath, mainContext);

        EsbEventIdentity? combinedIdentity = null;
        if (!string.IsNullOrWhiteSpace(combinedVisitIdentity))
        {
            var lookup = await result.LogStepAsync("按病案号+住院次数组合标识定位患者事件",
                () => _eventIdentityService.FindByCombinedVisitIdentityAsync(
                    integrationProjectCode,
                    combinedVisitIdentity,
                    config.CombinedVisitIdentityFormat,
                    mrn,
                    config.EventTypeName));

            if (lookup.IsAmbiguous)
                return ProcessResult.Fail($"病案号+住院次数组合标识匹配到多条住院事件：{combinedVisitIdentity}");

            combinedIdentity = lookup.Identity;
            if (combinedIdentity != null)
            {
                mrn ??= combinedIdentity.Mrn;
                inpatientNo ??= combinedIdentity.InpatientNo;
                visitNo ??= combinedIdentity.VisitNo;
                message.Mrn ??= combinedIdentity.Mrn;
                message.InpatientNo ??= combinedIdentity.InpatientNo;
                message.VisitNo ??= combinedIdentity.VisitNo;
            }
        }

        if (string.IsNullOrWhiteSpace(mrn))
        {
            return string.IsNullOrWhiteSpace(combinedVisitIdentity)
                ? ProcessResult.Fail("未提取到病案号或病案号+住院次数组合标识")
                : ProcessResult.Fail($"未能按病案号+住院次数组合标识定位患者事件：{combinedVisitIdentity}");
        }

        if (config.MedicalRecordSyncRole == MedicalRecordSyncRole.Supplemental)
        {
            var isActive = await _activeMedicalRecordService.IsActiveAsync(
                integrationProjectCode,
                mrn,
                inpatientNo,
                visitNo,
                eventStartTime);
            if (!isActive)
                return ProcessResult.Filtered("病历不是 Active，补采消息已忽略");
        }

        var hasEventMappings = await _mappingExecutor.HasMappingsAsync(message.TranCode, config.IntegrationProjectCode, MappingTarget.Event);
        var hasQuestionMappings = await _mappingExecutor.HasMappingsAsync(message.TranCode, config.IntegrationProjectCode, MappingTarget.Question);
        var hasSubCardMappings = await _mappingExecutor.HasMappingsAsync(message.TranCode, config.IntegrationProjectCode, MappingTarget.SubCard);
        var requiresEvent = RequiresEvent(config, hasEventMappings, hasQuestionMappings, hasSubCardMappings);

        var licenseCode = string.IsNullOrWhiteSpace(config.LicenseCode)
            ? await _configService.GetDefaultLicenseCodeAsync(config.IntegrationProjectCode)
            : config.LicenseCode;
        if (string.IsNullOrWhiteSpace(licenseCode))
            return ProcessResult.Fail("未配置 LicenseCode");

        var projectContext = await ResolveProjectContextAsync(config, licenseCode, requiresEvent);
        if (projectContext.ErrorMessage != null)
            return ProcessResult.Fail(projectContext.ErrorMessage);

        var patientFields = await result.ExtractWithLogAsync(
            "提取Patient字段",
            () => _mappingExecutor.ExtractPatientFieldsAsync(root, message.TranCode, config.IntegrationProjectCode, config.MainRecordArrayPath));
        patientFields["medical_record_number"] = mrn;

        patient? dbPatient;
        var existingPatient = combinedIdentity == null
            ? null
            : await _bioCore.GetPatientByIdAsync(combinedIdentity.PatientId, projectContext.HospitalId, projectContext.ProjectId);
        existingPatient ??= await _bioCore.GetPatientByMrnAsync(mrn, projectContext.HospitalId, projectContext.ProjectId);
        if (existingPatient != null)
        {
            dbPatient = existingPatient;
            await result.LogStepAsync("更新患者", async () =>
            {
                await _bioCore.UpdatePatientFieldsAsync(existingPatient.id, patientFields);
                return true;
            });
        }
        else
        {
            dbPatient = await result.LogStepAsync("创建患者",
                () => _bioCore.CreatePatientAsync(patientFields, projectContext.HospitalId, projectContext.ProjectId));
        }

        if (dbPatient == null)
            return ProcessResult.Fail("获取或创建患者失败");

        if (!requiresEvent)
        {
            var patientOnlyResult = ProcessResult.Success("患者信息处理成功", dbPatient.id, null);
            patientOnlyResult.Steps = result.Steps;
            return patientOnlyResult;
        }

        if (projectContext.FormSet == null || string.IsNullOrWhiteSpace(config.EventTypeName))
            return ProcessResult.Fail("未找到事件上下文配置，请检查 EventTypeName");

        var eventFields = await result.ExtractWithLogAsync(
            "提取Event字段",
            () => _mappingExecutor.ExtractEventFieldsAsync(root, message.TranCode, config.IntegrationProjectCode, config.MainRecordArrayPath));

        DateTime? eventEndTime = null;
        if (eventFields.TryGetValue("event_end_time", out var endTimeStr) && DateTime.TryParse(endTimeStr, out var parsedEnd))
            eventEndTime = parsedEnd;

        var eventResolve = await ResolveEventAsync(
            config,
            result,
            dbPatient,
            mrn,
            inpatientNo,
            visitNo,
            eventStartTime,
            eventEndTime,
            projectContext.FormSet,
            projectContext.HospitalId,
            projectContext.ProjectId,
            combinedIdentity);

        if (eventResolve.Result != null)
        {
            eventResolve.Result.Steps = result.Steps;
            return eventResolve.Result;
        }

        var dbEvent = eventResolve.Event;
        if (dbEvent == null)
            return ProcessResult.Fail("获取事件失败");

        var resolvedEventStartTime = eventStartTime ?? dbEvent.event_start_time;

        await _eventIdentityService.UpsertAsync(
            integrationProjectCode,
            message.TranCode,
            dbPatient.id,
            dbEvent.id,
            projectContext.HospitalId,
            projectContext.ProjectId,
            mrn,
            config.EventTypeName!,
            inpatientNo,
            visitNo,
            resolvedEventStartTime);

        if (config.MedicalRecordSyncRole == MedicalRecordSyncRole.CaseDriver)
        {
            await _activeMedicalRecordService.UpsertFromCaseDriverAsync(
                integrationProjectCode,
                message.TranCode,
                dbPatient.id,
                dbEvent.id,
                mrn,
                config.EventTypeName!,
                inpatientNo,
                visitNo,
                resolvedEventStartTime,
                eventEndTime);
        }

        if (!hasQuestionMappings && !hasSubCardMappings)
        {
            var patientEventResult = ProcessResult.Success("患者与事件处理成功", dbPatient.id, dbEvent.id);
            patientEventResult.Steps = result.Steps;
            return patientEventResult;
        }

        var formQuestionDict = await _bioCore.GetFormQuestionDictByFormSetAsync(projectContext.FormSet.id);
        var formCardDict = hasSubCardMappings
            ? await _bioCore.GetAllCardListByFormSetAsync(projectContext.FormSet.id)
            : [];
        var hasNestedSubCardMappings = hasSubCardMappings
                                       && await _mappingExecutor.HasNestedSubCardMappingsAsync(
                                           message.TranCode,
                                           config.IntegrationProjectCode,
                                           formCardDict);

        if (hasNestedSubCardMappings)
        {
            try
            {
                var questionValues = hasQuestionMappings
                    ? await result.ExtractWithLogAsync(
                        "提取Question映射",
                        () => _mappingExecutor.ExtractQuestionValuesAsync(
                            root,
                            message.TranCode,
                            config.IntegrationProjectCode,
                            config.MainRecordArrayPath))
                    : [];
                var subCardData = await result.ExtractWithLogAsync(
                    "提取SubCard映射",
                    () => _mappingExecutor.ExtractSubCardDataAsync(
                        root,
                        message.TranCode,
                        filterResult.RowFilterResults,
                        config.IntegrationProjectCode,
                        config.MainRecordArrayPath,
                        formCardDict));
                var writeResult = await _nestedFormWriteService.WriteAsync(
                    dbEvent.id,
                    formQuestionDict,
                    questionValues,
                    subCardData);
                result.AddStep("写入Question", true, $"成功写入 {writeResult.QuestionCount} 个问题答案");
                result.AddStep("写入嵌套SubCard", true, $"成功写入 {writeResult.SubCardCount} 行子卡片");

                var nestedResult = ProcessResult.Success("处理成功", dbPatient.id, dbEvent.id);
                nestedResult.Steps = result.Steps;
                return nestedResult;
            }
            catch (Exception ex)
            {
                return ProcessResult.Fail($"写入嵌套 SubCard 失败: {ex.Message}");
            }
        }

        IFormsetImportService importService;
        try
        {
            importService = await _bioCore.CreateImportServiceAsync(projectContext.FormSet.id);
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
                formCardDict,
                dbPatient,
                dbEvent,
                hasQuestionMappings,
                hasSubCardMappings);
        }
        catch (Exception ex)
        {
            return ProcessResult.Fail($"初始化导入服务失败: {ex.Message}");
        }

        try
        {
            if (hasQuestionMappings)
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

                    if (ShouldSkipQuestionValue(question, questionValue, _logger))
                        continue;

                    var converted = ConvertQuestionValue(question, questionValue.Value?.ToString(), _logger);
                    if (converted == null)
                        continue;

                    try
                    {
                        await importService.SetQuestionValueImportAsync(dbEvent.id, dbPatient.id, questionId, converted);
                        questionCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "设置 Question {QuestionId} 失败", questionId);
                    }
                }

                result.AddStep("写入Question", true, $"成功写入 {questionCount} 个问题答案");
            }

            if (hasSubCardMappings)
            {
                var subCardDataList = await result.ExtractWithLogAsync(
                    "提取SubCard映射",
                    () => _mappingExecutor.ExtractSubCardDataAsync(
                        root,
                        message.TranCode,
                        filterResult.RowFilterResults,
                        config.IntegrationProjectCode,
                        config.MainRecordArrayPath,
                        formCardDict));

                var subCardCount = 0;
                foreach (var subCardData in subCardDataList)
                {
                    foreach (var row in subCardData.Rows)
                    {
                        try
                        {
                            var subCardId = Guid.Empty;
                            var written = false;

                            foreach (var questionValue in row.Values)
                            {
                                var questionId = questionValue.QuestionId;
                                if (!formQuestionDict.TryGetValue(questionId, out var subQuestion))
                                {
                                    _logger.LogWarning("SubCard QuestionId {QuestionId} 不在当前 FormSet 中，已跳过写入", questionId);
                                    continue;
                                }

                                if (ShouldSkipQuestionValue(subQuestion, questionValue, _logger))
                                    continue;

                                var converted = ConvertQuestionValue(subQuestion, questionValue.Value?.ToString(), _logger);
                                if (converted == null)
                                    continue;

                                if (subCardId == Guid.Empty)
                                {
                                    subCardId = await importService.AddSubCardImportAsync(
                                        dbEvent.id, dbPatient.id, subCardData.CardId);

                                    if (subCardId == Guid.Empty)
                                    {
                                        _logger.LogWarning("创建 SubCard 失败: CardId={CardId}", subCardData.CardId);
                                        break;
                                    }
                                }

                                try
                                {
                                    await importService.SetQuestionValueImportAsync(
                                        dbEvent.id, dbPatient.id, questionId, converted, subCardId);
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
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "处理 SubCard 行数据失败: CardId={CardId}", subCardData.CardId);
                        }
                    }
                }

                result.AddStep("写入SubCard", true, $"成功写入 {subCardCount} 行子卡片");
            }

            await result.LogStepAsync("CommitBatch", async () =>
            {
                await importService.CommitBatchAsync();
                return true;
            });

            var successResult = ProcessResult.Success("处理成功", dbPatient.id, dbEvent.id);
            successResult.Steps = result.Steps;
            return successResult;
        }
        catch
        {
            try { await importService.DiscardImportAsync(); } catch { }
            throw;
        }
    }

    private static bool RequiresEvent(EsbInterfaceConfig config, bool hasEventMappings, bool hasQuestionMappings, bool hasSubCardMappings)
    {
        return hasEventMappings ||
               hasQuestionMappings ||
               hasSubCardMappings ||
               !string.IsNullOrWhiteSpace(config.EventTypeName) ||
               !string.IsNullOrWhiteSpace(config.EventStartTimeSourcePath) ||
               !string.IsNullOrWhiteSpace(config.VisitNoSourcePath) ||
               !string.IsNullOrWhiteSpace(config.InpatientNoSourcePath) ||
               !string.IsNullOrWhiteSpace(config.CombinedVisitIdentitySourcePath);
    }

    private async Task<ProcessResult> ProcessWithDirectTargetWriteAsync(
        JToken root,
        EsbMessage message,
        EsbInterfaceConfig config,
        FilterResult filterResult,
        ProcessResult result,
        Dictionary<Guid, form_question> formQuestionDict,
        IReadOnlyDictionary<Guid, CardInfo> formCardDict,
        patient dbPatient,
        patient_event dbEvent,
        bool hasQuestionMappings,
        bool hasSubCardMappings)
    {
        if (hasQuestionMappings)
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

                if (ShouldSkipQuestionValue(question, questionValue, _logger))
                    continue;

                var converted = ConvertQuestionValue(question, questionValue.Value?.ToString(), _logger);
                if (converted == null)
                    continue;

                await _directTargetWriteService.WriteAsync(question, dbPatient.id, dbEvent.id, converted);
                questionCount++;
            }

            result.AddStep("直接写入Question", true, $"成功写入 {questionCount} 个问题答案");
        }

        if (hasSubCardMappings)
        {
            var subCardDataList = await result.ExtractWithLogAsync(
                "提取SubCard映射",
                () => _mappingExecutor.ExtractSubCardDataAsync(
                    root,
                    message.TranCode,
                    filterResult.RowFilterResults,
                    config.IntegrationProjectCode,
                    config.MainRecordArrayPath,
                    formCardDict));

            var subCardCount = 0;
            foreach (var subCardData in subCardDataList)
            {
                foreach (var row in subCardData.Rows)
                {
                    var subCardId = Guid.NewGuid();
                    var written = false;
                    foreach (var questionValue in row.Values)
                    {
                        var questionId = questionValue.QuestionId;
                        if (!formQuestionDict.TryGetValue(questionId, out var question))
                        {
                            _logger.LogWarning("SubCard QuestionId {QuestionId} 不在当前 FormSet 中，已跳过写入", questionId);
                            continue;
                        }

                        if (ShouldSkipQuestionValue(question, questionValue, _logger))
                            continue;

                        var converted = ConvertQuestionValue(question, questionValue.Value?.ToString(), _logger);
                        if (converted == null)
                            continue;

                        await _directTargetWriteService.WriteAsync(question, dbPatient.id, dbEvent.id, converted, subCardId);
                        written = true;
                    }

                    if (written)
                        subCardCount++;
                }
            }

            result.AddStep("直接写入SubCard", true, $"成功写入 {subCardCount} 行子卡片");
        }

        var successResult = ProcessResult.Success("处理成功", dbPatient.id, dbEvent.id);
        successResult.Steps = result.Steps;
        return successResult;
    }

    private static bool IsBioCoreMetadataMismatch(Exception ex)
        => ex.Message.Contains("V2 元数据与表单字段定义不一致", StringComparison.Ordinal);

    private async Task<(form_form_set? FormSet, Guid HospitalId, Guid ProjectId, string? ErrorMessage)> ResolveProjectContextAsync(
        EsbInterfaceConfig config,
        string licenseCode,
        bool requiresEvent)
    {
        if (!string.IsNullOrWhiteSpace(config.EventTypeName))
        {
            var (formSet, hospitalId, projectId) = await _bioCore.FindFormSetAsync(licenseCode, config.EventTypeName);
            if (formSet != null)
                return (formSet, hospitalId, projectId, null);

            if (requiresEvent)
                return (null, Guid.Empty, Guid.Empty, $"未找到 FormSet: LicenseCode={licenseCode}, EventType={config.EventTypeName}");
        }

        var hospitalIdValue = await _configService.GetDefaultHospitalIdAsync(config.IntegrationProjectCode);
        var projectIdValue = await _configService.GetDefaultProjectIdAsync(config.IntegrationProjectCode);
        if (Guid.TryParse(hospitalIdValue, out var defaultHospitalId) && Guid.TryParse(projectIdValue, out var defaultProjectId))
            return (null, defaultHospitalId, defaultProjectId, null);

        return (null, Guid.Empty, Guid.Empty, "未找到 HospitalId/ProjectId，请检查项目或全局配置");
    }

    private async Task<(patient_event? Event, ProcessResult? Result)> ResolveEventAsync(
        EsbInterfaceConfig config,
        ProcessResult result,
        patient dbPatient,
        string mrn,
        string? inpatientNo,
        string? visitNo,
        DateTime? eventStartTime,
        DateTime? eventEndTime,
        form_form_set formSet,
        Guid hospitalId,
        Guid projectId,
        EsbEventIdentity? knownIdentity = null)
    {
        var eventTypeName = config.EventTypeName!;

        if (eventStartTime.HasValue)
        {
            var dbEvent = await result.LogStepAsync("获取/创建事件",
                () => _bioCore.GetOrCreateEventAsync(
                    dbPatient.id,
                    formSet.id,
                    hospitalId,
                    projectId,
                    formSet.name,
                    eventStartTime.Value,
                    eventEndTime,
                    eventTypeName));

            return (dbEvent, null);
        }

        if (!config.AllowMissingEventTime)
        {
            return (null, BuildMissingEventResult(config, dbPatient.id, "未提取到事件开始时间"));
        }

        var identity = knownIdentity ?? await result.LogStepAsync("按住院标识定位住院时间",
            () => _eventIdentityService.FindByVisitIdentityAsync(config.IntegrationProjectCode, mrn, inpatientNo, visitNo));

        if (identity?.EventStartTime != null)
        {
            var dbEvent = await result.LogStepAsync("按住院时间获取/创建事件",
                () => _bioCore.GetOrCreateEventAsync(
                    dbPatient.id,
                    formSet.id,
                    hospitalId,
                    projectId,
                    formSet.name,
                    identity.EventStartTime.Value,
                    eventEndTime,
                    eventTypeName));
            return (dbEvent, null);
        }

        return (null, BuildMissingEventResult(config, dbPatient.id, "缺少事件时间且未能按就诊号/住院号、住院次数或组合标识定位住院时间"));
    }

    private static ProcessResult BuildMissingEventResult(EsbInterfaceConfig config, Guid patientId, string message)
    {
        return config.MissingEventIdentityPolicy switch
        {
            MissingEventIdentityPolicy.DegradeToPatientOnly => ProcessResult.Success($"已降级为仅处理患者：{message}", patientId, null),
            MissingEventIdentityPolicy.Pending => CreateWaitingIdentityResult(patientId, message),
            _ => ProcessResult.Fail(message)
        };
    }

    private static ProcessResult CreateWaitingIdentityResult(Guid patientId, string message)
    {
        var result = ProcessResult.WaitingIdentity(message);
        result.PatientId = patientId;
        return result;
    }

    internal static bool ShouldSkipQuestionValue(form_question question, QuestionValue value, ILogger? logger = null)
    {
        if (!value.IsDictMiss || question.data_type != "选择")
            return false;

        logger?.LogInformation("选择题字典未命中，跳过写入: QuestionId={QuestionId}, Value={Value}", question.id, value.Value);
        return true;
    }

    internal static object? ConvertQuestionValue(form_question question, string? value, ILogger? logger = null)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        switch (question.data_type)
        {
            case "文本":
                return value;

            case "数值":
                if (question.number_is_integer)
                {
                    if (long.TryParse(value, out var longVal))
                        return longVal;

                    logger?.LogWarning("Question {Id} 数值(整数)解析失败: '{Value}'", question.id, value);
                    return null;
                }

                if (decimal.TryParse(value, out var decVal))
                    return Math.Round(decVal, question.number_decimal_places ?? 2);

                logger?.LogWarning("Question {Id} 数值解析失败: '{Value}'", question.id, value);
                return null;

            case "选择":
                var selectedValues = value.Split(['|', ';', ','], StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (question.select_is_multiple_choice)
                {
                    return selectedValues;
                }

                return selectedValues.Count == 0 ? null : new List<string> { selectedValues[0] };

            case "布尔":
                if (bool.TryParse(value, out var boolVal))
                    return boolVal;
                if (value == "1")
                    return true;
                if (value == "0")
                    return false;

                logger?.LogWarning("Question {Id} 布尔解析失败: '{Value}'", question.id, value);
                return null;

            case "日期":
                if (!string.IsNullOrEmpty(question.date_format) &&
                    DateTime.TryParseExact(value, question.date_format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtExact))
                {
                    return NormalizeDateValue(dtExact, question.date_format);
                }

                if (DateTime.TryParse(value, out var dt))
                    return NormalizeDateValue(dt, question.date_format);

                logger?.LogWarning("Question {Id} 日期解析失败: '{Value}'", question.id, value);
                return null;

            default:
                return value;
        }
    }

    private static DateTime NormalizeDateValue(DateTime value, string? dateFormat)
    {
        if (string.IsNullOrWhiteSpace(dateFormat))
            return value;

        var hasHour = dateFormat.Any(c => c is 'H' or 'h');
        var hasMinute = dateFormat.Contains('m');
        var hasSecond = dateFormat.Contains('s');
        var hasFraction = dateFormat.Any(c => c is 'f' or 'F');

        if (!hasHour)
            return value.Date;

        return new DateTime(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            hasMinute ? value.Minute : 0,
            hasSecond ? value.Second : 0,
            hasFraction ? value.Millisecond : 0,
            value.Kind);
    }
}
