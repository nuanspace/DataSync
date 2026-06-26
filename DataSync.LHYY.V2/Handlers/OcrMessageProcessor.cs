using DataSync.Common.Ocr;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
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
    private readonly GenericMessageProcessor _genericMessageProcessor;
    private readonly ILogger<OcrMessageProcessor> _logger;

    public OcrMessageProcessor(
        OcrProfileService profileService,
        IOcrConversionService ocrConversionService,
        GenericMessageProcessor genericMessageProcessor,
        ILogger<OcrMessageProcessor> logger)
    {
        _profileService = profileService;
        _ocrConversionService = ocrConversionService;
        _genericMessageProcessor = genericMessageProcessor;
        _logger = logger;
    }

    public async Task<ProcessResult> HandleAsync(EsbMessage message, EsbInterfaceConfig config)
    {
        if (!MessageJsonHelper.TryParseToken(message.RawJson, out var token, out var parseError))
            return ProcessResult.Fail(parseError ?? "Raw JSON 解析失败");

        if (token is JArray)
            return ProcessResult.Fail("暂不支持顶层数组 OCR 消息，请使用单条消息或配置主记录数组路径");

        if (token is not JObject root)
            return ProcessResult.Fail("Raw JSON 根节点必须是对象");

        var profile = await _profileService.GetEnabledProfileAsync(config.TranCode, config.IntegrationProjectCode);
        if (profile == null)
            return ProcessResult.Fail($"未找到启用的 OCR 配置：TranCode={config.TranCode}, Project={config.IntegrationProjectCode}");

        var sourceValue = MessageJsonHelper.ReadString(root, profile.SourcePath);
        if (string.IsNullOrWhiteSpace(sourceValue))
            return ProcessResult.Fail($"未从消息中提取到 PDF 来源：{profile.SourcePath}");

        var options = OcrProfileService.ToConversionOptions(profile);
        options.OutputNameHint = $"{message.TranCode}_{message.MessageId}";
        var source = new OcrSource
        {
            Kind = profile.SourceKind,
            Value = sourceValue
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ocrResult = string.IsNullOrWhiteSpace(options.OutputJsonPath)
            ? await _ocrConversionService.ConvertAsync(source, options)
            : await _ocrConversionService.ConvertToJsonFileAsync(source, options, options.OutputJsonPath);
        stopwatch.Stop();
        var ocrStep = new ProcessStepInfo
        {
            Step = "OCR转换",
            IsSuccess = true,
            ElapsedMs = (int)stopwatch.ElapsedMilliseconds,
            Detail = $"页数 {ocrResult.PageCount}，文本长度 {ocrResult.FullText.Length}"
        };

        root["Ocr"] = JToken.FromObject(ocrResult);

        var ocrMessage = CloneMessageWithRawJson(message, root.ToString(Formatting.None));
        var result = await _genericMessageProcessor.ProcessAsync(ocrMessage, config);
        result.Steps.Insert(0, ocrStep);

        _logger.LogInformation(
            "OCR 消息处理完成：MessageId={MessageId}, TranCode={TranCode}, Pages={PageCount}, TextLength={TextLength}",
            message.MessageId,
            message.TranCode,
            ocrResult.PageCount,
            ocrResult.FullText.Length);

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
