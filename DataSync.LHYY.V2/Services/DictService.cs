using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 字典值映射转换服务
/// </summary>
public class DictService
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly IntegrationProjectService _integrationProjectService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DictService> _logger;

    private const string DictCachePrefix = "Dict_";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(10);
    private static readonly char[] SegmentSeparators = ['，', ',', '；', ';', '。', '.', '！', '!', '？', '?', '\r', '\n', '但'];
    private static readonly string[] NegativeTerms = ["否认", "没有", "未发现", "未检出", "未见", "不伴", "不考虑", "排除", "除外", "阴性", "无"];
    private static readonly string[] PositiveTerms = ["诊断为", "存在", "伴有", "合并", "患有", "考虑", "阳性", "伴", "有"];

    public DictService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IntegrationProjectService integrationProjectService,
        IMemoryCache cache,
        ILogger<DictService> logger)
    {
        _contextFactory = contextFactory;
        _integrationProjectService = integrationProjectService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string?> TranslateAsync(string dictCode, string sourceValue, string? dictMatchMode = null)
    {
        var dict = await GetDictAsync(dictCode);
        var source = sourceValue.Trim();
        if (source.Length == 0)
            return null;

        var matchMode = EsbFieldMapping.NormalizeDictMatchMode(dictMatchMode);
        var matches = dict
            .Where(item => IsMatch(source, item.SourceValue, matchMode))
            .Select(item => item.TargetValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches.Count == 0 ? null : string.Join(",", matches);
    }

    public async Task<string> TranslateOrKeepAsync(string dictCode, string sourceValue, string? dictMatchMode = null)
    {
        var translated = await TranslateAsync(dictCode, sourceValue, dictMatchMode);
        if (translated == null)
            _logger.LogInformation("字典未匹配: DictCode={DictCode}, SourceValue={Value}", dictCode, sourceValue);

        return translated ?? sourceValue;
    }

    public async Task<DictTranslationResult> TranslateOrKeepWithResultAsync(string dictCode, string sourceValue, string? dictMatchMode = null)
    {
        var translated = await TranslateAsync(dictCode, sourceValue, dictMatchMode);
        if (translated == null)
            _logger.LogInformation("字典未匹配: DictCode={DictCode}, SourceValue={Value}", dictCode, sourceValue);

        return new DictTranslationResult(translated != null, translated ?? sourceValue);
    }

    public void ClearCache(string dictCode, string currentProjectCode)
    {
        if (string.IsNullOrWhiteSpace(dictCode) || string.IsNullOrWhiteSpace(currentProjectCode))
            return;

        _cache.Remove($"{DictCachePrefix}{currentProjectCode}:{dictCode.Trim()}");
    }

    private async Task<List<DictEntry>> GetDictAsync(string dictCode)
    {
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var cacheKey = $"{DictCachePrefix}{currentProjectCode}:{dictCode}";

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiration;

            await using var db = await _contextFactory.CreateDbContextAsync();
            var items = await db.EsbDicts
                .AsNoTracking()
                .Where(d => d.DictCode == dictCode)
                .WhereInProjectOrGlobal(currentProjectCode)
                .OrderBy(d => d.SortOrder)
                .ToListAsync();

            var dict = items
                .OrderBy(d => string.Equals(d.IntegrationProjectCode, currentProjectCode, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => d.SortOrder)
                .ThenBy(d => d.Id)
                .GroupBy(d => d.SourceValue, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(d => new DictEntry(d.SourceValue, d.TargetValue))
                .ToList();

            _logger.LogDebug("加载字典缓存: {DictCode}, 共 {Count} 条", dictCode, dict.Count);
            return dict;
        }) ?? [];
    }

    private static bool IsMatch(string source, string dictSourceValue, string dictMatchMode)
    {
        var option = dictSourceValue.Trim();
        if (option.Length == 0)
            return false;

        if (DictExpressionHelper.TryMatch(source, option, out var expressionMatched))
            return expressionMatched;

        return dictMatchMode == EsbFieldMapping.DictMatchModeContainsExcludeNegation
            ? IsPositiveMatch(source, option)
            : source.Contains(option, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPositiveMatch(string source, string dictSourceValue)
    {
        var option = dictSourceValue.Trim();
        if (option.Length == 0)
            return false;

        var start = 0;
        while (start < source.Length)
        {
            var index = source.IndexOf(option, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            if (!IsNegated(source, index))
                return true;

            start = index + option.Length;
        }

        return false;
    }

    private static bool IsNegated(string source, int matchIndex)
    {
        var searchStart = matchIndex == 0 ? 0 : matchIndex - 1;
        var segmentStart = source.LastIndexOfAny(SegmentSeparators, searchStart);
        segmentStart = segmentStart < 0 ? 0 : segmentStart + 1;

        var prefix = source[segmentStart..matchIndex];
        var lastNegativeIndex = LastIndexOfAny(prefix, NegativeTerms, false);
        if (lastNegativeIndex < 0)
            return false;

        var lastPositiveIndex = LastIndexOfAny(prefix, PositiveTerms, true);
        return lastPositiveIndex < lastNegativeIndex;
    }

    private static int LastIndexOfAny(string text, string[] terms, bool ignoreSingleYouInNegative)
    {
        var lastIndex = -1;
        foreach (var term in terms)
        {
            var index = text.LastIndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            if (ignoreSingleYouInNegative && IsInNegativeContext(text, index))
            {
                continue;
            }

            if (index > lastIndex)
                lastIndex = index;
        }

        return lastIndex;
    }

    private static bool IsInNegativeContext(string text, int index)
    {
        foreach (var negativeTerm in NegativeTerms)
        {
            var negativeIndex = text.LastIndexOf(negativeTerm, index, StringComparison.OrdinalIgnoreCase);
            if (negativeIndex >= 0 && negativeIndex + negativeTerm.Length >= index)
                return true;
        }

        return false;
    }

    private readonly record struct DictEntry(string SourceValue, string TargetValue);
}

public readonly record struct DictTranslationResult(bool IsMatched, string Value);
