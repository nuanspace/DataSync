using DataSync.Common.Ocr;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using System.Text.Json;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// OCR 配置读取服务。
/// </summary>
public class OcrProfileService
{
    public const string HandlerName = "OcrMessageProcessor";
    public const string FixedLanguage = "chi_sim+eng";
    public const int FixedDpi = 300;
    public const int FixedPageSegMode = 3;
    public const int FixedTimeoutSeconds = 180;
    public const int DefaultMaxPages = 20;
    public const long DefaultMaxInputBytes = 20L * 1024 * 1024;

    private static readonly JsonSerializerOptions ExtractionRuleJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;

    public OcrProfileService(IDbContextFactory<DataSyncDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<EsbOcrProfile?> GetEnabledProfileAsync(string tranCode, string? integrationProjectCode)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var list = await db.EsbOcrProfiles
            .AsNoTracking()
            .Where(p => p.IsEnabled && p.TranCode == tranCode)
            .OrderByDescending(p => p.IntegrationProjectCode == integrationProjectCode)
            .ThenBy(p => p.Id)
            .ToListAsync();

        return list.FirstOrDefault(p => string.Equals(p.IntegrationProjectCode, integrationProjectCode, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.IntegrationProjectCode));
    }

    public static OcrConversionOptions ToConversionOptions(EsbOcrProfile profile)
    {
        var extractionRules = DeserializeExtractionRules(profile.ExtractionRules);
        return new OcrConversionOptions
        {
            Language = FixedLanguage,
            Dpi = FixedDpi,
            PageSegMode = FixedPageSegMode,
            MaxPages = profile.MaxPages ?? DefaultMaxPages,
            MaxInputBytes = profile.MaxInputBytes ?? DefaultMaxInputBytes,
            TimeoutSeconds = FixedTimeoutSeconds,
            KeepWorkFiles = false,
            OutputJsonPath = null,
            AllowedFileRoots = SplitAllowedRoots(profile.AllowedFileRoots),
            ExtractionRules = extractionRules,
            IncludeLayoutRecognition = extractionRules.Any(rule => !string.IsNullOrWhiteSpace(rule.SectionHeading))
        };
    }

    public static List<OcrExtractionRule> DeserializeExtractionRules(string? value)
    {
        var rules = string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<List<OcrExtractionRule>>(value, ExtractionRuleJsonOptions) ?? [];
        foreach (var rule in rules.Where(rule => string.IsNullOrWhiteSpace(rule.SourceFieldName)))
            rule.SourceFieldName = rule.FieldCode;
        return rules;
    }

    public static string SerializeExtractionRules(IReadOnlyList<OcrExtractionRule> rules)
        => JsonSerializer.Serialize(rules, ExtractionRuleJsonOptions);

    public static bool IsOcrHandler(EsbInterfaceConfig? config)
        => config?.HandlerType == Models.Enums.HandlerType.Custom
            && string.Equals(config.HandlerName, HandlerName, StringComparison.Ordinal);

    public static IReadOnlyList<string> ValidateProfile(
        EsbOcrProfile profile,
        IReadOnlyList<OcrExtractionRule> rules,
        bool requireEnabled = true)
    {
        var errors = ValidateSourceProfile(profile, requireEnabled).ToList();
        errors.AddRange(OcrExtractionRuleHelper.Validate(rules));
        return errors;
    }

    public static IReadOnlyList<string> ValidateSourceProfile(
        EsbOcrProfile profile,
        bool requireEnabled = true)
    {
        var errors = new List<string>();
        if (requireEnabled && !profile.IsEnabled)
            errors.Add("OCR 配置必须启用");
        if (string.IsNullOrWhiteSpace(profile.SourcePath))
            errors.Add("PDF 来源路径不能为空");
        if (profile.MaxPages is <= 0)
            errors.Add("OCR 最多识别页数必须大于 0");
        if (profile.MaxInputBytes is <= 0)
            errors.Add("OCR 最大 PDF 字节数必须大于 0");
        if (profile.SourceKind == OcrSourceKind.FilePath
            && string.IsNullOrWhiteSpace(profile.AllowedFileRoots))
        {
            errors.Add("文件路径来源必须配置允许读取的文件根目录");
        }

        return errors;
    }

    private static IReadOnlyList<string> SplitAllowedRoots(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class OcrMessageJsonHelper
{
    public static JObject BuildPayload(
        OcrDocumentResult result,
        IReadOnlyList<OcrExtractionRule> rules,
        bool template = false)
    {
        if (rules.Count == 0)
            throw new InvalidOperationException("OCR 接口至少需要配置一个提取字段");

        return BuildFieldsPayload(result, rules, template);
    }

    private static JObject BuildFieldsPayload(
        OcrDocumentResult result,
        IReadOnlyList<OcrExtractionRule> rules,
        bool template)
    {
        if (!template)
        {
            var missingRequiredFields = rules
                .Where(rule => rule.IsRequired)
                .Where(rule => !result.ExtractedFields.TryGetValue(rule.FieldCode.Trim(), out var value)
                    || string.IsNullOrWhiteSpace(value))
                .Select(GetFieldName)
                .ToList();
            if (missingRequiredFields.Count > 0)
                throw new InvalidOperationException($"OCR 必填字段未识别：{string.Join("、", missingRequiredFields)}");
        }

        var extractedFields = new JObject();
        foreach (var rule in rules)
        {
            var fieldCode = rule.FieldCode.Trim();
            extractedFields[fieldCode] = result.ExtractedFields.TryGetValue(fieldCode, out var value) && value != null
                ? new JValue(value)
                : JValue.CreateNull();
        }

        return new JObject
        {
            ["Mode"] = "Fields",
            ["PageCount"] = result.PageCount,
            ["ExtractedFields"] = extractedFields
        };
    }

    private static string GetFieldName(OcrExtractionRule rule)
        => rule.FieldCode.Trim();
}
