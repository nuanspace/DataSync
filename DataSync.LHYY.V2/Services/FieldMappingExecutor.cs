using System.Text.RegularExpressions;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 字段映射执行器：根据映射配置从 JSON 提取值
/// </summary>
public class FieldMappingExecutor
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly IntegrationProjectService _integrationProjectService;
    private readonly DictService _dictService;
    private readonly FilterRuleService _filterRuleService;
    private readonly ILogger<FieldMappingExecutor> _logger;

    public FieldMappingExecutor(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IntegrationProjectService integrationProjectService,
        DictService dictService,
        FilterRuleService filterRuleService,
        ILogger<FieldMappingExecutor> logger)
    {
        _contextFactory = contextFactory;
        _integrationProjectService = integrationProjectService;
        _dictService = dictService;
        _filterRuleService = filterRuleService;
        _logger = logger;
    }

    public async Task<List<EsbFieldMapping>> LoadMappingsAsync(string tranCode, MappingTarget? target = null, string? integrationProjectCode = null)
    {
        var currentProjectCode = string.IsNullOrWhiteSpace(integrationProjectCode)
            ? await _integrationProjectService.GetCurrentProjectCodeAsync()
            : integrationProjectCode!;

        await using var db = await _contextFactory.CreateDbContextAsync();
        var query = db.EsbFieldMappings
            .AsNoTracking()
            .Where(m => m.TranCode == tranCode && m.IsEnabled)
            .WhereInProjectOrGlobal(currentProjectCode);

        if (target.HasValue)
            query = query.Where(m => m.MappingTarget == target.Value);

        var mappings = await query.OrderBy(m => m.SortOrder).ToListAsync();
        var projectScoped = mappings
            .Where(m => string.Equals(m.IntegrationProjectCode, currentProjectCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectScoped.Any(m => !EsbFieldMapping.IsSubCardFilterMapping(m)))
        {
            return projectScoped;
        }

        var globalMappings = mappings.Where(m => string.IsNullOrWhiteSpace(m.IntegrationProjectCode)).ToList();
        return projectScoped.Count > 0
            ? projectScoped.Concat(globalMappings).ToList()
            : globalMappings;
    }

    public async Task<bool> HasMappingsAsync(string tranCode, params MappingTarget[] targets)
        => await HasMappingsAsync(tranCode, null, targets);

    public async Task<bool> HasMappingsAsync(string tranCode, string? integrationProjectCode, params MappingTarget[] targets)
    {
        if (targets.Length == 0)
            return false;

        var mappings = await LoadMappingsAsync(tranCode, null, integrationProjectCode);
        return mappings.Any(m => !EsbFieldMapping.IsSubCardFilterMapping(m) && targets.Contains(m.MappingTarget));
    }

    public async Task<Dictionary<string, string?>> ExtractPatientFieldsAsync(
        JToken body,
        string tranCode,
        string? integrationProjectCode = null,
        string? mainRecordArrayPath = null)
    {
        var mappings = await LoadMappingsAsync(tranCode, MappingTarget.Patient, integrationProjectCode);
        var rulesMap = await _filterRuleService.LoadMappingRulesAsync(mappings.Select(m => m.Id));
        var result = new Dictionary<string, string?>();
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, mainRecordArrayPath);

        foreach (var mapping in mappings)
        {
            rulesMap.TryGetValue(mapping.Id, out var rules);
            var value = await ExtractValueAsync(body, mainContext, mapping, rules);
            if (value.Value != null || mapping.IsRequired)
                result[mapping.TargetField] = value.Value;
        }

        return result;
    }

    public async Task<Dictionary<string, string?>> ExtractEventFieldsAsync(
        JToken body,
        string tranCode,
        string? integrationProjectCode = null,
        string? mainRecordArrayPath = null)
    {
        var mappings = await LoadMappingsAsync(tranCode, MappingTarget.Event, integrationProjectCode);
        var rulesMap = await _filterRuleService.LoadMappingRulesAsync(mappings.Select(m => m.Id));
        var result = new Dictionary<string, string?>();
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, mainRecordArrayPath);

        foreach (var mapping in mappings)
        {
            rulesMap.TryGetValue(mapping.Id, out var rules);
            var value = await ExtractValueAsync(body, mainContext, mapping, rules);
            if (value.Value != null)
                result[mapping.TargetField] = value.Value;
        }

        return result;
    }

    public async Task<List<QuestionValue>> ExtractQuestionValuesAsync(
        JToken body,
        string tranCode,
        string? integrationProjectCode = null,
        string? mainRecordArrayPath = null)
    {
        var mappings = await LoadMappingsAsync(tranCode, MappingTarget.Question, integrationProjectCode);
        var rulesMap = await _filterRuleService.LoadMappingRulesAsync(mappings.Select(m => m.Id));
        var result = new List<QuestionValue>();
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, mainRecordArrayPath);

        foreach (var mapping in mappings)
        {
            rulesMap.TryGetValue(mapping.Id, out var rules);
            var value = await ExtractValueAsync(body, mainContext, mapping, rules, true);
            if (value.Value == null)
                continue;

            if (Guid.TryParse(mapping.TargetField, out var questionId))
                result.Add(new QuestionValue(questionId, value.Value, value.IsDictMiss));
            else
                _logger.LogWarning("Question 映射的 TargetField 不是有效的 GUID: {TargetField}", mapping.TargetField);
        }

        return result;
    }

    public async Task<List<SubCardData>> ExtractSubCardDataAsync(
        JToken body,
        string tranCode,
        Dictionary<string, List<int>>? rowFilterResults = null,
        string? integrationProjectCode = null,
        string? mainRecordArrayPath = null)
    {
        var mappings = await LoadMappingsAsync(tranCode, MappingTarget.SubCard, integrationProjectCode);
        if (mappings.Count == 0)
            return [];

        var rulesMap = await _filterRuleService.LoadMappingRulesAsync(mappings.Select(m => m.Id));
        var subCardFilterMappings = mappings
            .Where(EsbFieldMapping.IsSubCardFilterMapping)
            .Where(m => m.CardId.HasValue)
            .GroupBy(m => m.CardId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.SortOrder).First());
        var groups = mappings
            .Where(m => !EsbFieldMapping.IsSubCardFilterMapping(m) && m.CardId.HasValue)
            .GroupBy(m => m.CardId!.Value);
        var result = new List<SubCardData>();
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, mainRecordArrayPath);

        foreach (var group in groups)
        {
            var cardId = group.Key;
            var cardMappings = group.ToList();
            subCardFilterMappings.TryGetValue(cardId, out var subCardFilterMapping);
            var arrayPath = SubCardPathHelper.NormalizeArrayContainerPath(
                cardMappings.FirstOrDefault(m => !string.IsNullOrEmpty(m.ArrayPath))?.ArrayPath
                ?? subCardFilterMapping?.ArrayPath);
            if (string.IsNullOrEmpty(arrayPath))
            {
                _logger.LogWarning("SubCard CardId={CardId} 未配置 ArrayPath", cardId);
                continue;
            }

            var useMainContextRoot = !SubCardPathHelper.IsAbsoluteJsonPath(arrayPath);
            var containerRoot = useMainContextRoot ? mainContext : body;
            var containerToken = SubCardPathHelper.ResolveSubCardContainer(containerRoot, arrayPath)
                ?? (useMainContextRoot ? SubCardPathHelper.ResolveSubCardContainer(body, arrayPath) : null);
            if (!SubCardPathHelper.IsSupportedSubCardContainer(containerToken))
                continue;

            var rowContexts = SubCardPathHelper.ResolveSubCardContexts(containerRoot, arrayPath);
            if (rowContexts.Count == 0 && useMainContextRoot)
                rowContexts = SubCardPathHelper.ResolveSubCardContexts(body, arrayPath);
            if (rowContexts.Count == 0)
                continue;

            if (containerToken is JArray
                && rowFilterResults != null
                && rowFilterResults.TryGetValue(arrayPath, out var filterIndices))
            {
                rowContexts = filterIndices
                    .Where(idx => idx >= 0 && idx < rowContexts.Count)
                    .Select(idx => rowContexts[idx])
                    .ToList();

                if (rowContexts.Count == 0)
                    continue;
            }

            var subCardRules = subCardFilterMapping != null && rulesMap.TryGetValue(subCardFilterMapping.Id, out var filterRules)
                ? filterRules
                : null;
            if (subCardRules?.Count > 0)
            {
                rowContexts = rowContexts
                    .Where(item => _filterRuleService.CheckMappingRules(body, item, subCardRules, arrayPath, mainContext))
                    .ToList();

                if (rowContexts.Count == 0)
                    continue;
            }

            var subCard = new SubCardData { CardId = cardId };

            foreach (var item in rowContexts)
            {
                var rowValues = new List<QuestionValue>();

                foreach (var mapping in cardMappings)
                {
                    JToken? source = null;
                    if (!string.IsNullOrWhiteSpace(mapping.SourcePath))
                    {
                        var useAbsoluteRoot = mapping.SourcePath.StartsWith("$.", StringComparison.Ordinal);
                        var useMainScope = MessageJsonHelper.IsMainRecordScopedPath(mapping.SourcePath);
                        var useLocalRoot = !useAbsoluteRoot
                            && !useMainScope
                            && SubCardPathHelper.IsRootScopedPath(mapping.SourcePath, arrayPath);

                        source = useAbsoluteRoot
                            ? SubCardPathHelper.ResolveFirstToken(body, mapping.SourcePath)
                            : useMainScope
                                ? MessageJsonHelper.ResolveFirstScopedToken(body, item, mapping.SourcePath, mainContext)
                            : useLocalRoot
                                ? MessageJsonHelper.ResolveFirstScopedToken(body, mainContext, mapping.SourcePath)
                                : MessageJsonHelper.ResolveFirstScopedToken(mainContext, item, mapping.SourcePath);
                    }

                    var value = await ApplyMappingValueAsync(source?.ToString(), mapping);

                    if (value.Value == null)
                        continue;

                    rulesMap.TryGetValue(mapping.Id, out var mappingRules);
                    if (!_filterRuleService.CheckMappingRules(body, item, mappingRules, arrayPath, mainContext))
                        continue;

                    if (Guid.TryParse(mapping.TargetField, out var questionId))
                        rowValues.Add(new QuestionValue(questionId, value.Value, value.IsDictMiss));
                }

                if (rowValues.Count > 0)
                    subCard.Rows.Add(rowValues);
            }

            if (subCard.Rows.Count > 0)
                result.Add(subCard);
        }

        return result;
    }

    private async Task<MappedValue> ExtractValueAsync(
        JToken body,
        JToken context,
        EsbFieldMapping mapping,
        List<EsbFilterRule>? rules,
        bool collectArrayValues = false)
    {
        if (!_filterRuleService.CheckMappingRules(body, context, rules))
            return new MappedValue(null, false);

        var sourceValue = ResolveSourceValue(body, context, mapping.SourcePath, collectArrayValues);

        if (sourceValue == null && mapping.IsRequired)
        {
            _logger.LogWarning(
                "必填映射源路径未匹配: TranCode={TranCode}, SourcePath={SourcePath}, TargetField={TargetField}",
                mapping.TranCode,
                mapping.SourcePath,
                mapping.TargetField);
        }

        var value = await ApplyMappingValueAsync(sourceValue, mapping);

        if (value.Value == null && mapping.IsRequired)
            _logger.LogWarning("必填字段为空: TranCode={TranCode}, SourcePath={SourcePath}", mapping.TranCode, mapping.SourcePath);

        return value;
    }

    private static string? ResolveSourceValue(JToken body, JToken context, string? sourcePath, bool collectArrayValues)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        if (!collectArrayValues || !sourcePath.Contains("[]", StringComparison.Ordinal))
            return MessageJsonHelper.ResolveScopedToken(body, context, sourcePath)?.ToString();

        var values = MessageJsonHelper.ResolveScopedTokens(body, context, sourcePath)
            .Select(t => t.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        return values.Count == 0 ? null : string.Join(",", values);
    }

    private async Task<MappedValue> ApplyMappingValueAsync(string? value, EsbFieldMapping mapping)
    {
        var isDictMiss = false;
        if (value != null && !string.IsNullOrEmpty(mapping.DictCode))
        {
            var translation = await _dictService.TranslateOrKeepWithResultAsync(mapping.DictCode, value, mapping.DictMatchMode);
            value = translation.Value;
            isDictMiss = !translation.IsMatched;
        }

        value ??= mapping.DefaultValue;

        if (value != null && !string.IsNullOrEmpty(mapping.ValueExpression))
        {
            var exprResult = ApplyExpression(value, mapping.ValueExpression);
            if (exprResult == value && mapping.ValueExpression.StartsWith("format:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "值表达式日期解析失败: TranCode={TranCode}, Value={Value}, Expression={Expression}",
                    mapping.TranCode,
                    value,
                    mapping.ValueExpression);
            }

            value = exprResult;
        }

        return new MappedValue(value, isDictMiss);
    }

    internal static string? ApplyExpression(string value, string expression)
    {
        if (expression.StartsWith("format:", StringComparison.OrdinalIgnoreCase))
        {
            var format = expression[7..];
            if (DateTime.TryParse(value, out var dt))
                return dt.ToString(format);

            return value;
        }

        if (expression.StartsWith("substring:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = expression[10..].Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out var start) && int.TryParse(parts[1], out var len))
            {
                if (start < value.Length)
                    return value.Substring(start, Math.Min(len, value.Length - start));
            }
        }

        if (expression.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = expression[6..];
            if (string.IsNullOrWhiteSpace(pattern))
                return null;

            try
            {
                var match = Regex.Match(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                if (!match.Success)
                    return null;

                return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        if (expression.StartsWith("replace:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = expression[8..].Split(',', 2);
            if (parts.Length == 2)
                return value.Replace(parts[0], parts[1]);
        }

        return value;
    }

    private readonly record struct MappedValue(string? Value, bool IsDictMiss);
}

public sealed class QuestionValue
{
    public QuestionValue(Guid questionId, object value, bool isDictMiss)
    {
        QuestionId = questionId;
        Value = value;
        IsDictMiss = isDictMiss;
    }

    public Guid QuestionId { get; }
    public object Value { get; }
    public bool IsDictMiss { get; }

    public void Deconstruct(out Guid questionId, out object value)
    {
        questionId = QuestionId;
        value = Value;
    }
}

public class SubCardData
{
    public Guid CardId { get; set; }
    public List<List<QuestionValue>> Rows { get; set; } = [];
}
