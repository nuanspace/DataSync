using System.Diagnostics;
using System.Text.RegularExpressions;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Models.Html;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 在内存中使用当前草稿配置测试医院响应或单独 Base64，不创建消息、不执行映射和业务写入。
/// </summary>
public sealed class HtmlManualTestService
{
    public const int MaxRecordCount = 100;
    public const int MaxCandidateCount = 200;

    private static readonly Regex LabelCandidateRegex = new(
        @"(?m)(?:^|\n|[^\S\r\n]{2,})(?<label>[\p{L}\p{N}（）()／/_-]{1,20})[^\S\r\n]*[:：][^\S\r\n]*(?<value>.*?)(?=[^\S\r\n]{2,}[\p{L}\p{N}（）()／/_-]{1,20}[^\S\r\n]*[:：]|\n|$)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly HtmlTextExtractionService _extractionService;

    public HtmlManualTestService(HtmlTextExtractionService extractionService)
    {
        _extractionService = extractionService;
    }

    public HtmlManualTestBatchResult Test(
        string input,
        EsbHtmlProfile profile,
        IReadOnlyList<HtmlExtractionRule> rules,
        string? mainRecordArrayPath)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidDataException("请粘贴医院接口响应 JSON 或 FILE_CONTENT Base64 内容");

        var profileErrors = HtmlProfileService.ValidateProfile(
            profile,
            rules,
            requireEnabled: false,
            requireExtractionRules: false);
        if (profileErrors.Count > 0)
            throw new InvalidDataException($"当前 HTML 配置错误：{string.Join("；", profileErrors)}");

        var trimmed = input.Trim();
        if (!TryParseJsonInput(trimmed, out var root, out var parseError))
        {
            if (LooksLikeJson(trimmed))
                throw new InvalidDataException(parseError ?? "医院接口响应 JSON 解析失败");
            return TestBase64(trimmed, profile, rules);
        }

        if (root is JValue { Type: JTokenType.String } stringValue)
            return TestBase64(stringValue.Value<string>() ?? "", profile, rules);
        if (root is not JObject and not JArray)
            throw new InvalidDataException("医院接口响应必须是 JSON 对象、JSON 数组或单独的 Base64 内容");

        return TestJson(root, profile, rules, mainRecordArrayPath);
    }

    /// <summary>
    /// 从成功的测试结果中汇总章节和“标题：值”候选，不包含已经配置的同名字段。
    /// </summary>
    public List<HtmlFieldCandidate> DiscoverCandidates(
        HtmlManualTestBatchResult result,
        IReadOnlyList<HtmlExtractionRule> existingRules)
    {
        var configuredFields = existingRules
            .Select(rule => rule.FieldCode.Trim())
            .Where(fieldCode => !string.IsNullOrWhiteSpace(fieldCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, HtmlFieldCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var extraction in result.Items
                     .Select(item => item.Extraction)
                     .Where(extraction => extraction != null)
                     .Cast<HtmlExtractionResult>())
        {
            foreach (var section in extraction.Sections)
            {
                AddCandidate(candidates, configuredFields, new HtmlFieldCandidate
                {
                    FieldCode = section.Key,
                    SourceSection = section.Key,
                    ExtractionType = HtmlExtractionType.Section,
                    PreviewValue = section.Value
                });
            }

            foreach (var section in extraction.Sections)
                DiscoverLabelCandidates(section.Value, section.Key, candidates, configuredFields);

            DiscoverLabelCandidates(extraction.CleanedText, null, candidates, configuredFields);
            if (candidates.Count >= MaxCandidateCount)
                break;
        }

        return candidates.Values.Take(MaxCandidateCount).ToList();
    }

    /// <summary>
    /// 合并用户选中的候选字段；已有同名字段保持原配置，不被自动覆盖。
    /// </summary>
    public static List<HtmlExtractionRule> MergeCandidateRules(
        IReadOnlyList<HtmlExtractionRule> existingRules,
        IEnumerable<HtmlFieldCandidate> candidates)
    {
        var merged = existingRules.Select(CloneRule).ToList();
        var fieldCodes = merged
            .Select(rule => rule.FieldCode.Trim())
            .Where(fieldCode => !string.IsNullOrWhiteSpace(fieldCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates.Where(candidate => candidate.IsSelected && !candidate.IsConfigured))
        {
            var fieldCode = candidate.FieldCode.Trim();
            if (string.IsNullOrWhiteSpace(fieldCode) || !fieldCodes.Add(fieldCode))
                continue;

            merged.Add(new HtmlExtractionRule
            {
                FieldCode = fieldCode,
                SourceSection = candidate.SourceSection,
                SourceLabel = candidate.SourceLabel,
                ExtractionType = candidate.ExtractionType,
                IsRequired = false
            });
        }

        for (var index = 0; index < merged.Count; index++)
            merged[index].SortOrder = index;
        return merged;
    }

    private HtmlManualTestBatchResult TestBase64(
        string base64,
        EsbHtmlProfile profile,
        IReadOnlyList<HtmlExtractionRule> rules)
    {
        var batch = new HtmlManualTestBatchResult { InputMode = "单独 Base64" };
        batch.Items.Add(TestRecord(1, base64, profile, rules));
        return batch;
    }

    private static void DiscoverLabelCandidates(
        string text,
        string? sourceSection,
        IDictionary<string, HtmlFieldCandidate> candidates,
        IReadOnlySet<string> configuredFields)
    {
        if (string.IsNullOrWhiteSpace(text) || candidates.Count >= MaxCandidateCount)
            return;

        try
        {
            foreach (Match match in LabelCandidateRegex.Matches(text))
            {
                var label = match.Groups["label"].Value.Trim();
                if (string.IsNullOrWhiteSpace(label) || candidates.ContainsKey(label))
                    continue;

                AddCandidate(candidates, configuredFields, new HtmlFieldCandidate
                {
                    FieldCode = label,
                    SourceSection = sourceSection,
                    SourceLabel = label,
                    ExtractionType = HtmlExtractionType.LabelValue,
                    PreviewValue = NormalizeCandidateValue(match.Groups["value"].Value)
                });
                if (candidates.Count >= MaxCandidateCount)
                    break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // 候选发现失败不影响已完成的 HTML 解析和手工配置。
        }
    }

    private static void AddCandidate(
        IDictionary<string, HtmlFieldCandidate> candidates,
        IReadOnlySet<string> configuredFields,
        HtmlFieldCandidate candidate)
    {
        var fieldCode = candidate.FieldCode.Trim();
        if (string.IsNullOrWhiteSpace(fieldCode) || candidates.Count >= MaxCandidateCount)
            return;

        candidate.FieldCode = fieldCode;
        candidate.IsConfigured = configuredFields.Contains(fieldCode);
        candidate.IsSelected = false;
        candidates.TryAdd(fieldCode, candidate);
    }

    private static string? NormalizeCandidateValue(string value)
    {
        var normalized = value.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static HtmlExtractionRule CloneRule(HtmlExtractionRule rule) => new()
    {
        FieldCode = rule.FieldCode,
        SourceSection = rule.SourceSection,
        SourceLabel = rule.SourceLabel,
        ExtractionType = rule.ExtractionType,
        Pattern = rule.Pattern,
        IsRequired = rule.IsRequired,
        SortOrder = rule.SortOrder
    };

    private HtmlManualTestBatchResult TestJson(
        JToken root,
        EsbHtmlProfile profile,
        IReadOnlyList<HtmlExtractionRule> rules,
        string? mainRecordArrayPath)
    {
        var records = ResolveRecords(root, mainRecordArrayPath);
        if (records.Count == 0)
            throw new InvalidDataException("医院接口响应中没有可测试的记录");
        if (records.Count > MaxRecordCount)
            throw new InvalidDataException($"单次最多测试 {MaxRecordCount} 条记录，当前为 {records.Count} 条");

        var batch = new HtmlManualTestBatchResult { InputMode = "医院响应 JSON" };
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var source = ReadManualTestSource(record, profile.SourcePath);
            if (string.IsNullOrWhiteSpace(source))
            {
                batch.Items.Add(new HtmlManualTestItemResult
                {
                    RecordIndex = index + 1,
                    ErrorMessage = $"未从当前记录提取到 Base64 内容：{profile.SourcePath}"
                });
                continue;
            }

            batch.Items.Add(TestRecord(index + 1, source, profile, rules));
        }

        return batch;
    }

    /// <summary>
    /// 手工测试允许直接粘贴 FBC-027 原始记录。正式处理器仍严格使用配置的来源路径，
    /// 这里只在配置路径未命中时回退读取当前记录根级的 FILE_CONTENT。
    /// </summary>
    private static string? ReadManualTestSource(JToken record, string sourcePath)
    {
        var source = MessageJsonHelper.ReadString(record, sourcePath, record);
        return !string.IsNullOrWhiteSpace(source)
            ? source
            : MessageJsonHelper.ReadString(record, "$main.FILE_CONTENT", record);
    }

    private HtmlManualTestItemResult TestRecord(
        int recordIndex,
        string base64,
        EsbHtmlProfile profile,
        IReadOnlyList<HtmlExtractionRule> rules)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var extraction = _extractionService.Extract(base64, profile, rules);
            stopwatch.Stop();
            var missingRequired = extraction.MissingRequiredFields.Count > 0;
            return new HtmlManualTestItemResult
            {
                RecordIndex = recordIndex,
                IsSuccess = !missingRequired,
                ErrorMessage = missingRequired
                    ? $"必填字段未提取：{string.Join("、", extraction.MissingRequiredFields)}"
                    : null,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Extraction = extraction
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            stopwatch.Stop();
            return new HtmlManualTestItemResult
            {
                RecordIndex = recordIndex,
                ErrorMessage = ex.Message,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
    }

    internal static List<JToken> ResolveRecords(JToken root, string? mainRecordArrayPath)
    {
        var normalizedPath = SubCardPathHelper.NormalizeArrayContainerPath(mainRecordArrayPath);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            var selected = MessageJsonHelper.SafeSelectToken(root, normalizedPath);
            if (SubCardPathHelper.IsRootContainerPath(normalizedPath) && selected is JObject rootObject)
                return [rootObject];
            if (selected is JArray configuredArray)
                return configuredArray.Children().ToList();

            // 手工测试可直接粘贴医院 FBC-027 的根数组或单条原始记录。
            // 该回退不进入正式 HtmlMessageProcessor，避免掩盖生产报文路径错误。
            if (root is JArray rawArray
                && rawArray.Children().All(token => token is JObject)
                && rawArray.Children().Any(HasRawFileContentField))
            {
                return rawArray.Children().ToList();
            }
            if (HasRawFileContentField(root))
                return [root];

            throw new InvalidDataException($"主记录数组路径未命中数组：{normalizedPath}");
        }

        return root is JArray array ? array.Children().ToList() : [root];
    }

    private static bool HasRawFileContentField(JToken token)
        => token is JObject obj
            && obj.GetValue("FILE_CONTENT", StringComparison.OrdinalIgnoreCase) != null;

    private static bool TryParseJsonInput(string input, out JToken root, out string? error)
    {
        try
        {
            root = JToken.Parse(input);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            root = JValue.CreateNull();
            error = $"医院接口响应 JSON 解析失败：{ex.Message}";
            return false;
        }
    }

    private static bool LooksLikeJson(string value)
        => value.StartsWith('{') || value.StartsWith('[');
}
