using System.Diagnostics;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Handlers;

/// <summary>
/// 将 Base64 HTML 文本转换为结构化 JSON 后交给通用处理器。
/// </summary>
public sealed class HtmlMessageProcessor : IMessageHandler
{
    private readonly HtmlProfileService _profileService;
    private readonly HtmlTextExtractionService _extractionService;
    private readonly GenericMessageProcessor _genericMessageProcessor;
    private readonly ILogger<HtmlMessageProcessor> _logger;

    public HtmlMessageProcessor(
        HtmlProfileService profileService,
        HtmlTextExtractionService extractionService,
        GenericMessageProcessor genericMessageProcessor,
        ILogger<HtmlMessageProcessor> logger)
    {
        _profileService = profileService;
        _extractionService = extractionService;
        _genericMessageProcessor = genericMessageProcessor;
        _logger = logger;
    }

    public async Task<ProcessResult> HandleAsync(EsbMessage message, EsbInterfaceConfig config)
    {
        if (!MessageJsonHelper.TryParseToken(message.RawJson, out var token, out var parseError))
            return ProcessResult.Fail(parseError ?? "Raw JSON 解析失败");

        var root = token switch
        {
            JObject obj => obj,
            JArray { Count: 1 } array when array[0] is JObject obj => obj,
            JArray => null,
            _ => null
        };
        if (root == null)
            return ProcessResult.Fail("HTML 消息必须是对象或仅含一条记录的数组；批量数组请配置主记录数组路径");

        var profile = await _profileService.GetEnabledProfileAsync(config.TranCode, config.IntegrationProjectCode);
        if (profile == null)
            return ProcessResult.Fail($"未找到启用的 HTML 配置：TranCode={config.TranCode}, Project={config.IntegrationProjectCode}");

        List<Models.Html.HtmlExtractionRule> rules;
        try
        {
            rules = HtmlProfileService.DeserializeExtractionRules(profile.ExtractionRules);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ProcessResult.Fail($"HTML 字段提取规则解析失败：{ex.Message}");
        }

        var errors = HtmlProfileService.ValidateProfile(profile, rules);
        if (errors.Count > 0)
            return ProcessResult.Fail($"HTML 配置错误：{string.Join("；", errors)}");

        var mainContext = MessageJsonHelper.ResolveMainRecordContext(root, config.MainRecordArrayPath);
        var sourceValue = MessageJsonHelper.ReadString(root, profile.SourcePath, mainContext);
        if (string.IsNullOrWhiteSpace(sourceValue))
            return ProcessResult.Fail($"未从消息中提取到 Base64 HTML 内容：{profile.SourcePath}");

        var stopwatch = Stopwatch.StartNew();
        Models.Html.HtmlExtractionResult extractionResult;
        try
        {
            extractionResult = _extractionService.Extract(sourceValue, profile, rules);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            stopwatch.Stop();
            var failed = ProcessResult.Fail($"HTML 文本解析失败：{ex.Message}");
            failed.AddStep("HTML文本解析", false, ex.GetType().Name, (int)stopwatch.ElapsedMilliseconds);
            return failed;
        }
        stopwatch.Stop();

        if (extractionResult.MissingRequiredFields.Count > 0)
        {
            var detail = $"必填字段未提取：{string.Join("、", extractionResult.MissingRequiredFields)}";
            var failed = ProcessResult.Fail(detail);
            failed.AddStep("HTML文本解析", false, detail, (int)stopwatch.ElapsedMilliseconds);
            return failed;
        }

        var htmlPayload = new JObject
        {
            ["Mode"] = "Fields",
            ["Sections"] = JObject.FromObject(extractionResult.Sections),
            ["ExtractedFields"] = JObject.FromObject(extractionResult.ExtractedFields)
        };
        if (mainContext is JObject mainRecord && !ReferenceEquals(mainRecord, root))
            mainRecord["Html"] = htmlPayload;
        else
            root["Html"] = htmlPayload;

        var transformedMessage = CloneMessageWithRawJson(message, root.ToString(Formatting.None));
        var result = await _genericMessageProcessor.ProcessAsync(transformedMessage, config);
        result.Steps.Insert(0, new ProcessStepInfo
        {
            Step = "HTML文本解析",
            IsSuccess = true,
            ElapsedMs = (int)stopwatch.ElapsedMilliseconds,
            Detail = $"章节数 {extractionResult.Sections.Count}，字段数 {rules.Count}，编码 {extractionResult.EncodingName}"
        });

        _logger.LogInformation(
            "HTML 消息处理完成：Sections={SectionCount}, Fields={FieldCount}, TextLength={TextLength}, Encoding={Encoding}, ElapsedMs={ElapsedMs}",
            extractionResult.Sections.Count,
            rules.Count,
            extractionResult.TextLength,
            extractionResult.EncodingName,
            stopwatch.ElapsedMilliseconds);
        return result;
    }

    private static EsbMessage CloneMessageWithRawJson(EsbMessage message, string rawJson) => new()
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
