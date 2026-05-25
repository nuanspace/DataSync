using Bio.Models;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Services;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Handlers;

/// <summary>
/// 患者信息更新处理器（PID0102）
/// 只做患者创建/更新，不涉及事件和问题答案
/// </summary>
public class PatientUpdateHandler : IMessageHandler
{
    private readonly BioCoreIntegrationService _bioCore;
    private readonly FieldMappingExecutor _mappingExecutor;
    private readonly ConfigService _configService;
    private readonly ILogger<PatientUpdateHandler> _logger;

    public PatientUpdateHandler(
        BioCoreIntegrationService bioCore,
        FieldMappingExecutor mappingExecutor,
        ConfigService configService,
        ILogger<PatientUpdateHandler> logger)
    {
        _bioCore = bioCore;
        _mappingExecutor = mappingExecutor;
        _configService = configService;
        _logger = logger;
    }

    public async Task<ProcessResult> HandleAsync(EsbMessage message, EsbInterfaceConfig config)
    {
        var result = new ProcessResult();

        if (string.IsNullOrEmpty(message.RawJson) || message.RawJson == "{}")
            return ProcessResult.Fail("Raw JSON 为空");

        if (!MessageJsonHelper.TryParseToken(message.RawJson, out var body, out var parseError))
            return ProcessResult.Fail(parseError ?? "Raw JSON 解析失败");
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, config.MainRecordArrayPath);

        // 校验病案号路径配置
        if (string.IsNullOrEmpty(config.MrnSourcePath))
            return ProcessResult.Fail($"配置错误：接口 {message.TranCode} 未配置病案号路径（MrnSourcePath）");

        // 从 config 路径直接提取 MRN
        var mrnToken = MessageJsonHelper.ResolveScopedToken(body, mainContext, config.MrnSourcePath);
        var mrn = mrnToken?.ToString();
        if (string.IsNullOrEmpty(mrn))
            return ProcessResult.Fail($"未提取到病案号: 路径 {config.MrnSourcePath} 在消息中无匹配");

        // 确定 LicenseCode（项目级优先，全局兜底）
        var licenseCode = await _configService.GetDefaultLicenseCodeAsync(config.IntegrationProjectCode);

        if (string.IsNullOrEmpty(licenseCode))
            return ProcessResult.Fail("未配置 LicenseCode");

        // 通过 FormSet 获取 hospitalId/projectId
        var eventTypeName = config.EventTypeName ?? "住院";
        var (_, hospitalId, projectId) = await _bioCore.FindFormSetAsync(licenseCode, eventTypeName);

        if (hospitalId == Guid.Empty)
        {
            var hid = await _configService.GetDefaultHospitalIdAsync(config.IntegrationProjectCode);
            var pid = await _configService.GetDefaultProjectIdAsync(config.IntegrationProjectCode);
            if (!Guid.TryParse(hid, out hospitalId) || !Guid.TryParse(pid, out projectId))
                return ProcessResult.Fail("未找到 HospitalId/ProjectId，请检查 LicenseCode 或项目配置");
        }

        // 提取 Patient 字段
        var patientFields = await _mappingExecutor.ExtractPatientFieldsAsync(
            body,
            message.TranCode,
            config.IntegrationProjectCode,
            config.MainRecordArrayPath);
        // 确保 MRN 存在于患者字段中
        patientFields["medical_record_number"] = mrn;

        result.AddStep("提取Patient字段", true, $"提取到 {patientFields.Count} 个字段");

        // 查找已有患者
        var existingPatient = await _bioCore.GetPatientByMrnAsync(mrn, hospitalId, projectId);
        if (existingPatient != null)
        {
            // 复用公共方法更新患者信息
            await _bioCore.UpdatePatientFieldsAsync(existingPatient.id, patientFields);
            result.AddStep("更新患者", true, $"已更新患者: {existingPatient.id}, MRN: {mrn}");

            var successResult = ProcessResult.Success("患者信息已更新", existingPatient.id, null);
            successResult.Steps = result.Steps;
            return successResult;
        }

        // 创建新患者
        var newPatient = await _bioCore.CreatePatientAsync(patientFields, hospitalId, projectId);
        result.AddStep("创建患者", true, $"新建患者: {newPatient.id}, MRN: {mrn}");

        var createResult = ProcessResult.Success("患者创建成功", newPatient.id, null);
        createResult.Steps = result.Steps;
        return createResult;
    }
}
