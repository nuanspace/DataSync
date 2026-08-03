using System.Text.RegularExpressions;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Dto;
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
        string? mainRecordArrayPath = null,
        IReadOnlyDictionary<Guid, CardInfo>? cards = null)
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
            .GroupBy(m => m.CardId!.Value)
            .Select(group =>
            {
                var cardId = group.Key;
                var cardMappings = group.ToList();
                subCardFilterMappings.TryGetValue(cardId, out var filterMapping);
                var arrayPath = SubCardPathHelper.NormalizeArrayContainerPath(
                    cardMappings.FirstOrDefault(mapping => !string.IsNullOrEmpty(mapping.ArrayPath))?.ArrayPath
                    ?? filterMapping?.ArrayPath);
                return new SubCardMappingGroup(cardId, cardMappings, filterMapping, arrayPath);
            })
            .Where(group =>
            {
                if (!string.IsNullOrWhiteSpace(group.ArrayPath))
                {
                    return true;
                }

                _logger.LogWarning("SubCard CardId={CardId} 未配置 ArrayPath", group.CardId);
                return false;
            })
            .ToDictionary(group => group.CardId);

        if (groups.Count == 0)
            return [];

        var parentMap = SubCardHierarchyHelper.BuildMappedParentMap(groups.Keys, cards);
        var childMap = parentMap
            .Where(pair => pair.Value.HasValue)
            .GroupBy(pair => pair.Value!.Value)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToList());
        var result = new List<SubCardData>();
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, mainRecordArrayPath);

        foreach (var rootGroup in groups.Values.Where(group => !parentMap.GetValueOrDefault(group.CardId).HasValue))
        {
            var subCard = await ExtractSubCardNodeAsync(
                body,
                mainContext,
                rootGroup,
                groups,
                childMap,
                rulesMap,
                rowFilterResults,
                mainRecordArrayPath,
                null,
                null);
            if (subCard != null)
                result.Add(subCard);
        }

        return result;
    }

    public async Task<bool> HasNestedSubCardMappingsAsync(
        string tranCode,
        string? integrationProjectCode,
        IReadOnlyDictionary<Guid, CardInfo>? cards)
    {
        var mappings = await LoadMappingsAsync(tranCode, MappingTarget.SubCard, integrationProjectCode);
        var cardIds = mappings
            .Where(mapping => !EsbFieldMapping.IsSubCardFilterMapping(mapping) && mapping.CardId.HasValue)
            .Select(mapping => mapping.CardId!.Value)
            .Distinct()
            .ToList();
        return SubCardHierarchyHelper.BuildMappedParentMap(cardIds, cards).Values.Any(parentId => parentId.HasValue);
    }

    private async Task<SubCardData?> ExtractSubCardNodeAsync(
        JToken body,
        JToken mainContext,
        SubCardMappingGroup group,
        IReadOnlyDictionary<Guid, SubCardMappingGroup> groups,
        IReadOnlyDictionary<Guid, List<Guid>> childMap,
        IReadOnlyDictionary<int, List<EsbFilterRule>> rulesMap,
        Dictionary<string, List<int>>? rowFilterResults,
        string? mainRecordArrayPath,
        JToken? parentContext,
        string? parentArrayPath)
    {
        var (rowContexts, isArray, effectiveArrayPath) = ResolveSubCardRowContexts(
            body,
            mainContext,
            group.ArrayPath,
            mainRecordArrayPath,
            parentContext,
            parentArrayPath);
        if (rowContexts.Count == 0)
            return null;

        if (isArray && rowFilterResults != null)
        {
            var filterIndices = rowFilterResults.GetValueOrDefault(group.ArrayPath)
                                ?? rowFilterResults.GetValueOrDefault(effectiveArrayPath ?? "");
            if (filterIndices != null)
            {
                rowContexts = filterIndices
                    .Where(index => index >= 0 && index < rowContexts.Count)
                    .Select(index => rowContexts[index])
                    .ToList();
            }
        }

        var subCardRules = group.FilterMapping != null
                           && rulesMap.TryGetValue(group.FilterMapping.Id, out var filterRules)
            ? filterRules
            : null;
        if (subCardRules?.Count > 0)
        {
            rowContexts = rowContexts
                .Where(item => _filterRuleService.CheckMappingRules(
                    body,
                    item,
                    subCardRules,
                    effectiveArrayPath ?? group.ArrayPath,
                    mainContext))
                .ToList();
        }

        var subCard = new SubCardData { CardId = group.CardId };
        foreach (var item in rowContexts)
        {
            var row = new SubCardRowData();
            foreach (var mapping in group.Mappings)
            {
                var source = ResolveSubCardSource(
                    body,
                    mainContext,
                    item,
                    parentContext,
                    mapping.SourcePath,
                    group.ArrayPath,
                    effectiveArrayPath);
                var value = await ApplyMappingValueAsync(source?.ToString(), mapping);
                if (value.Value == null)
                    continue;

                rulesMap.TryGetValue(mapping.Id, out var mappingRules);
                if (!_filterRuleService.CheckMappingRules(
                        body,
                        item,
                        mappingRules,
                        effectiveArrayPath ?? group.ArrayPath,
                        mainContext))
                {
                    continue;
                }

                if (Guid.TryParse(mapping.TargetField, out var questionId))
                {
                    row.Values.Add(new QuestionValue(questionId, value.Value, value.IsDictMiss));
                }
            }

            if (childMap.TryGetValue(group.CardId, out var childCardIds))
            {
                foreach (var childCardId in childCardIds)
                {
                    if (!groups.TryGetValue(childCardId, out var childGroup))
                        continue;

                    var child = await ExtractSubCardNodeAsync(
                        body,
                        mainContext,
                        childGroup,
                        groups,
                        childMap,
                        rulesMap,
                        rowFilterResults,
                        mainRecordArrayPath,
                        item,
                        effectiveArrayPath ?? group.ArrayPath);
                    if (child != null)
                    {
                        row.Children.Add(child);
                    }
                }
            }

            if (row.Values.Count > 0 || row.Children.Count > 0)
            {
                subCard.Rows.Add(row);
            }
        }

        return subCard.Rows.Count == 0 ? null : subCard;
    }

    private static (List<JToken> Contexts, bool IsArray, string? EffectiveArrayPath) ResolveSubCardRowContexts(
        JToken body,
        JToken mainContext,
        string arrayPath,
        string? mainRecordArrayPath,
        JToken? parentContext,
        string? parentArrayPath)
    {
        var effectiveArrayPath = parentContext == null
            ? SubCardPathHelper.ExpandArrayPathToRoot(body, arrayPath, mainRecordArrayPath)
            : SubCardPathHelper.ExpandNestedArrayPathToRoot(body, arrayPath, parentArrayPath, mainRecordArrayPath);

        if (parentContext != null
            && (SubCardPathHelper.IsParentRecordContainerPath(arrayPath)
                || SubCardPathHelper.PathsEqual(effectiveArrayPath, parentArrayPath)))
        {
            return ([parentContext], false, effectiveArrayPath ?? parentArrayPath);
        }

        if (parentContext != null
            && !SubCardPathHelper.IsAbsoluteJsonPath(arrayPath)
            && !SubCardPathHelper.IsMainRecordContainerPath(arrayPath))
        {
            var relativePath = arrayPath;
            if (!string.IsNullOrWhiteSpace(effectiveArrayPath)
                && !string.IsNullOrWhiteSpace(parentArrayPath)
                && SubCardPathHelper.TryBuildRelativePath(effectiveArrayPath, parentArrayPath, out var relative))
            {
                relativePath = relative;
            }

            if (string.IsNullOrWhiteSpace(relativePath)
                || SubCardPathHelper.IsRootContainerPath(relativePath))
            {
                return ([parentContext], false, effectiveArrayPath ?? parentArrayPath);
            }

            var nestedContainer = SubCardPathHelper.ResolveSubCardContainer(parentContext, relativePath);
            if (SubCardPathHelper.IsSupportedSubCardContainer(nestedContainer))
            {
                return (ToContexts(nestedContainer), nestedContainer is JArray, effectiveArrayPath);
            }
        }

        var useMainContextRoot = !SubCardPathHelper.IsAbsoluteJsonPath(arrayPath);
        var containerRoot = useMainContextRoot ? mainContext : body;
        var containerPath = SubCardPathHelper.IsMainRecordContainerPath(arrayPath)
            ? SubCardPathHelper.RootContainerPath
            : arrayPath;
        var containerToken = SubCardPathHelper.ResolveSubCardContainer(containerRoot, containerPath)
                             ?? (useMainContextRoot
                                 ? SubCardPathHelper.ResolveSubCardContainer(body, effectiveArrayPath ?? arrayPath)
                                 : null);
        return SubCardPathHelper.IsSupportedSubCardContainer(containerToken)
            ? (ToContexts(containerToken), containerToken is JArray, effectiveArrayPath)
            : ([], false, effectiveArrayPath);
    }

    private static List<JToken> ToContexts(JToken? container) => container switch
    {
        JArray array when array.Count > 0 => array.Children().ToList(),
        JObject obj => [obj],
        _ => [],
    };

    private static JToken? ResolveSubCardSource(
        JToken body,
        JToken mainContext,
        JToken rowContext,
        JToken? parentContext,
        string? sourcePath,
        string arrayPath,
        string? effectiveArrayPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        if (SubCardPathHelper.IsParentRecordScopedPath(sourcePath))
        {
            var parentRelativePath = SubCardPathHelper.TrimParentRecordScopePrefix(sourcePath);
            return parentContext == null
                ? null
                : string.IsNullOrWhiteSpace(parentRelativePath)
                    ? parentContext
                    : SubCardPathHelper.ResolveFirstToken(parentContext, parentRelativePath);
        }

        if (SubCardPathHelper.IsAbsoluteJsonPath(sourcePath))
            return SubCardPathHelper.ResolveFirstToken(body, sourcePath);

        if (MessageJsonHelper.IsMainRecordScopedPath(sourcePath))
            return MessageJsonHelper.ResolveFirstScopedToken(body, rowContext, sourcePath, mainContext);

        if (SubCardPathHelper.IsRootScopedPath(sourcePath, effectiveArrayPath ?? arrayPath)
            && SubCardPathHelper.TryBuildRelativePath(sourcePath, effectiveArrayPath ?? arrayPath, out var relativePath))
        {
            return string.IsNullOrWhiteSpace(relativePath)
                ? rowContext
                : SubCardPathHelper.ResolveFirstToken(rowContext, relativePath);
        }

        return MessageJsonHelper.ResolveFirstScopedToken(mainContext, rowContext, sourcePath);
    }

    private async Task<MappedValue> ExtractValueAsync(
        JToken body,
        JToken context,
        EsbFieldMapping mapping,
        List<EsbFilterRule>? rules,
        bool collectArrayValues = false)
    {
        var hasArraySource = collectArrayValues
                             && SubCardPathHelper.HasArrayWildcard(mapping.SourcePath);
        var hasArrayItemRules = hasArraySource
                                && rules?.Any(rule => rule.IsEnabled && rule.FilterScope == FilterScope.RowFilter) == true;
        var mappingRules = hasArrayItemRules
            ? rules?.Where(rule => rule.FilterScope != FilterScope.RowFilter).ToList()
            : rules;

        if (!_filterRuleService.CheckMappingRules(body, context, mappingRules))
            return new MappedValue(null, false);

        string? sourceValue;
        if (hasArrayItemRules)
        {
            var filtered = _filterRuleService.FilterMappingArrayValues(
                body,
                context,
                mapping.SourcePath,
                rules,
                context);
            if (filtered.MatchedCount == 0)
                return new MappedValue(null, false);

            sourceValue = filtered.Values.Count == 0 ? null : string.Join("；", filtered.Values);
        }
        else
        {
            sourceValue = ResolveSourceValue(body, context, mapping.SourcePath, collectArrayValues);
        }

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

    private sealed record SubCardMappingGroup(
        Guid CardId,
        List<EsbFieldMapping> Mappings,
        EsbFieldMapping? FilterMapping,
        string ArrayPath);
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
    public List<SubCardRowData> Rows { get; set; } = [];
}

public class SubCardRowData
{
    public List<QuestionValue> Values { get; set; } = [];
    public List<SubCardData> Children { get; set; } = [];
}
