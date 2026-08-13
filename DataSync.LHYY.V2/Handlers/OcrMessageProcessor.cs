using DataSync.Common.Ocr;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Handlers;

/// <summary>
/// OCR 后再交给通用处理器的消息处理器。
/// </summary>
public class OcrMessageProcessor : IMessageHandler
{
    private readonly OcrProfileService _profileService;
    private readonly IOcrConversionService _ocrConversionService;
    private readonly OcrAiExtractionService _ocrAiExtractionService;
    private readonly GenericMessageProcessor _genericMessageProcessor;
    private readonly ILogger<OcrMessageProcessor> _logger;

    public OcrMessageProcessor(
        OcrProfileService profileService,
        IOcrConversionService ocrConversionService,
        OcrAiExtractionService ocrAiExtractionService,
        GenericMessageProcessor genericMessageProcessor,
        ILogger<OcrMessageProcessor> logger)
    {
        _profileService = profileService;
        _ocrConversionService = ocrConversionService;
        _ocrAiExtractionService = ocrAiExtractionService;
        _genericMessageProcessor = genericMessageProcessor;
        _logger = logger;
    }

    public async Task<ProcessResult> HandleAsync(EsbMessage message, EsbInterfaceConfig config)
    {
        if (config.ReceiveMode != ReceiveMode.PersistAndAsync)
            return ProcessResult.Fail("OCR 接口仅支持入队异步处理");

        if (!MessageJsonHelper.TryParseToken(message.RawJson, out var token, out var parseError))
            return ProcessResult.Fail(parseError ?? "Raw JSON 解析失败");

        if (token is JArray)
            return ProcessResult.Fail("暂不支持顶层数组 OCR 消息，请使用单条消息或配置主记录数组路径");

        if (token is not JObject root)
            return ProcessResult.Fail("Raw JSON 根节点必须是对象");

        var profile = await _profileService.GetEnabledProfileAsync(config.TranCode, config.IntegrationProjectCode);
        if (profile == null)
            return ProcessResult.Fail($"未找到启用的 OCR 配置：TranCode={config.TranCode}, Project={config.IntegrationProjectCode}");

        List<OcrExtractionRule> extractionRules;
        try
        {
            extractionRules = OcrProfileService.DeserializeExtractionRules(profile.ExtractionRules);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ProcessResult.Fail($"OCR 字段提取规则解析失败：{ex.Message}");
        }

        var profileErrors = OcrProfileService.ValidateProfile(profile, extractionRules);
        if (profileErrors.Count > 0)
            return ProcessResult.Fail($"OCR 配置错误：{string.Join("；", profileErrors)}");

        var mainContext = MessageJsonHelper.ResolveMainRecordContext(root, config.MainRecordArrayPath);
        var sourceValue = MessageJsonHelper.ReadString(root, profile.SourcePath, mainContext);
        if (string.IsNullOrWhiteSpace(sourceValue))
            return ProcessResult.Fail($"未从消息中提取到 PDF 来源：{profile.SourcePath}");

        var options = OcrProfileService.ToConversionOptions(profile);
        options.ExtractionRules = extractionRules;
        options.OutputNameHint = $"{message.TranCode}_{message.MessageId}";
        var source = new OcrSource
        {
            Kind = profile.SourceKind,
            Value = sourceValue
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        OcrDocumentResult ocrResult;
        try
        {
            ocrResult = string.IsNullOrWhiteSpace(options.OutputJsonPath)
                ? await _ocrConversionService.ConvertAsync(source, options)
                : await _ocrConversionService.ConvertToJsonFileAsync(source, options, options.OutputJsonPath);
            await _ocrAiExtractionService.ExtractAsync(ocrResult, extractionRules);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failureType = ex.GetType().Name;
            var failedResult = ProcessResult.Fail($"OCR 识别与字段提取失败：{failureType}");
            failedResult.Steps.Add(new ProcessStepInfo
            {
                Step = "OCR识别与字段提取",
                IsSuccess = false,
                ElapsedMs = (int)stopwatch.ElapsedMilliseconds,
                Detail = failureType
            });
            return failedResult;
        }
        stopwatch.Stop();
        var ocrStep = new ProcessStepInfo
        {
            Step = "OCR识别与字段提取",
            IsSuccess = true,
            ElapsedMs = (int)stopwatch.ElapsedMilliseconds,
            Detail = $"页数 {ocrResult.PageCount}，字段数 {extractionRules.Count}"
        };

        JObject ocrToken;
        try
        {
            ocrToken = OcrMessageJsonHelper.BuildPayload(ocrResult, extractionRules);
        }
        catch (InvalidOperationException ex)
        {
            ocrStep.IsSuccess = false;
            ocrStep.Detail = ex.Message;
            var failedResult = ProcessResult.Fail(ex.Message);
            failedResult.Steps.Add(ocrStep);
            return failedResult;
        }

        if (mainContext is JObject mainRecord && !ReferenceEquals(mainRecord, root))
            mainRecord["Ocr"] = ocrToken;
        else
            root["Ocr"] = ocrToken;

        var ocrMessage = CloneMessageWithRawJson(message, root.ToString(Formatting.None));
        var result = await _genericMessageProcessor.ProcessAsync(ocrMessage, config);
        result.Steps.Insert(0, ocrStep);

        _logger.LogInformation(
            "OCR 消息处理完成：Pages={PageCount}, Fields={FieldCount}, ElapsedMs={ElapsedMs}",
            ocrResult.PageCount,
            extractionRules.Count,
            stopwatch.ElapsedMilliseconds);

        return result;
    }

    private static EsbMessage CloneMessageWithRawJson(EsbMessage message, string rawJson)
    {
        return new EsbMessage
        {
            Id = message.Id,
            MessageId = message.MessageId,
            SourceMessageId = message.SourceMessageId,
            TranCode = message.TranCode,
            IntegrationProjectCode = message.IntegrationProjectCode,
            TranName = message.TranName,
            AppId = message.AppId,
            OrgId = message.OrgId,
            EsbTimestamp = message.EsbTimestamp,
            RawJson = rawJson,
            BodyJson = message.BodyJson,
            IdempotentKey = message.IdempotentKey,
            Mrn = message.Mrn,
            VisitNo = message.VisitNo,
            InpatientNo = message.InpatientNo,
            ResolvedEventTime = message.ResolvedEventTime,
            MatchedRuleGroup = message.MatchedRuleGroup,
            Status = message.Status,
            RetryCount = message.RetryCount,
            ErrorMessage = message.ErrorMessage,
            PatientId = message.PatientId,
            EventId = message.EventId,
            ProcessedAt = message.ProcessedAt,
            ProcessingStartedAt = message.ProcessingStartedAt,
            CreatedAt = message.CreatedAt
        };
    }
}
