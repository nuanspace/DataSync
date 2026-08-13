using System.Text.Json;
using System.Text.RegularExpressions;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Html;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// HTML 文本解析配置读取与校验服务。
/// </summary>
public sealed class HtmlProfileService
{
    public const string HandlerName = "HtmlMessageProcessor";
    public const long DefaultMaxInputBytes = 5L * 1024 * 1024;
    public const string DefaultSectionHeadingsText =
        "主诉\n现病史\n既往史\n个人史\n婚育史\n家族史\n体格检查\n专科检查\n辅助检查\n确定诊断\n初步诊断";

    private static readonly JsonSerializerOptions RuleJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;

    public HtmlProfileService(IDbContextFactory<DataSyncDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<EsbHtmlProfile?> GetEnabledProfileAsync(string tranCode, string? integrationProjectCode)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var profiles = await db.EsbHtmlProfiles
            .AsNoTracking()
            .Where(profile => profile.IsEnabled && profile.TranCode == tranCode)
            .OrderBy(profile => profile.Id)
            .ToListAsync();

        return profiles.FirstOrDefault(profile => string.Equals(
                profile.IntegrationProjectCode,
                integrationProjectCode,
                StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault(profile => string.IsNullOrWhiteSpace(profile.IntegrationProjectCode));
    }

    public static bool IsHtmlHandler(EsbInterfaceConfig? config)
        => config?.HandlerType == Models.Enums.HandlerType.Custom
            && string.Equals(config.HandlerName, HandlerName, StringComparison.Ordinal);

    public static List<HtmlExtractionRule> DeserializeExtractionRules(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<List<HtmlExtractionRule>>(value, RuleJsonOptions) ?? [];

    public static string SerializeExtractionRules(IReadOnlyList<HtmlExtractionRule> rules)
        => JsonSerializer.Serialize(rules.OrderBy(rule => rule.SortOrder), RuleJsonOptions);

    public static List<string> ParseSectionHeadings(string? value)
        => (value ?? "")
            .Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<string> ValidateProfile(
        EsbHtmlProfile profile,
        IReadOnlyList<HtmlExtractionRule> rules,
        bool requireEnabled = true,
        bool requireExtractionRules = true)
    {
        var errors = new List<string>();
        if (requireEnabled && !profile.IsEnabled)
            errors.Add("HTML 配置必须启用");
        if (string.IsNullOrWhiteSpace(profile.SourcePath))
            errors.Add("Base64 来源路径不能为空");
        if (profile.MaxInputBytes <= 0)
            errors.Add("最大解码字节数必须大于 0");
        if (requireExtractionRules && rules.Count == 0)
            errors.Add("至少需要配置一个 HTML 提取字段");
        if (profile.PreserveSections && ParseSectionHeadings(profile.SectionHeadings).Count == 0)
            errors.Add("保留章节原文时至少需要配置一个章节标题");

        foreach (var group in rules.GroupBy(rule => rule.FieldCode?.Trim(), StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
                errors.Add("HTML 输出字段名称不能为空");
            else if (group.Count() > 1)
                errors.Add($"HTML 输出字段名称重复：{group.Key}");
        }

        foreach (var rule in rules)
        {
            if (rule.ExtractionType == Models.Enums.HtmlExtractionType.Regex
                && string.IsNullOrWhiteSpace(rule.Pattern))
                errors.Add($"正则字段 {rule.FieldCode} 未配置表达式");
            else if (rule.ExtractionType == Models.Enums.HtmlExtractionType.Regex)
            {
                try
                {
                    _ = new Regex(rule.Pattern!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"正则字段 {rule.FieldCode} 表达式无效：{ex.Message}");
                }
            }
            if (rule.ExtractionType != Models.Enums.HtmlExtractionType.Section
                && rule.ExtractionType != Models.Enums.HtmlExtractionType.Regex
                && string.IsNullOrWhiteSpace(rule.SourceLabel))
                errors.Add($"字段 {rule.FieldCode} 未配置来源标题");
            if (rule.ExtractionType == Models.Enums.HtmlExtractionType.Section
                && string.IsNullOrWhiteSpace(rule.SourceSection))
                errors.Add($"章节字段 {rule.FieldCode} 未配置来源章节");
        }

        return errors.Distinct(StringComparer.Ordinal).ToList();
    }
}
