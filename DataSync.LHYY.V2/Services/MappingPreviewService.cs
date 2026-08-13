using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 映射预览服务：对映射规则执行提取预览（不写库）
/// </summary>
public class MappingPreviewService
{
    private readonly DictService _dictService;
    private readonly FilterRuleService _filterRuleService;

    public MappingPreviewService(DictService dictService, FilterRuleService filterRuleService)
    {
        _dictService = dictService;
        _filterRuleService = filterRuleService;
    }

    public async Task<MappingSamplePreviewResult> PreviewSampleAsync(
        EsbFieldMapping mapping,
        List<EsbFilterRule>? filterRules,
        string? sampleValue,
        IReadOnlyDictionary<string, string?>? filterValues = null)
    {
        var result = new MappingSamplePreviewResult
        {
            RawValue = sampleValue
        };

        result.PassedFilter = EvaluateMappingFilters(result, mapping, filterRules, sampleValue, filterValues);
        if (!result.PassedFilter)
        {
            result.FinalValue = null;
            result.IsMissing = true;
            result.Steps.Add(new MappingPreviewStep
            {
                Name = "最终结果",
                Status = "跳过",
                Message = "过滤条件未通过，正式执行时不会写入该映射。"
            });
            return result;
        }

        var value = sampleValue;
        if (value != null && !string.IsNullOrWhiteSpace(mapping.DictCode))
        {
            var translation = await _dictService.TranslateOrKeepWithResultAsync(mapping.DictCode, value, mapping.DictMatchMode);
            result.IsDictMatched = translation.IsMatched;
            value = translation.Value;
            result.DictTranslatedValue = value;
            result.Steps.Add(new MappingPreviewStep
            {
                Name = "字典转换",
                Status = translation.IsMatched ? "命中" : "未命中",
                InputValue = sampleValue,
                OutputValue = value,
                Message = translation.IsMatched
                    ? $"字典：{mapping.DictCode}"
                    : $"字典未命中，按正式逻辑保留原值。字典：{mapping.DictCode}"
            });

            if (!translation.IsMatched)
            {
                result.Warnings.Add($"字典 {mapping.DictCode} 未命中，最终值会继续使用原值。");
            }
        }
        else if (!string.IsNullOrWhiteSpace(mapping.DictCode))
        {
            result.Steps.Add(new MappingPreviewStep
            {
                Name = "字典转换",
                Status = "跳过",
                Message = "样本值为空，未执行字典转换。"
            });
        }

        if (value == null)
        {
            value = mapping.DefaultValue;
            result.Steps.Add(new MappingPreviewStep
            {
                Name = "默认值",
                Status = value == null ? "跳过" : "已应用",
                OutputValue = value,
                Message = value == null ? "未配置默认值。" : "源值为空，已使用默认值。"
            });
        }
        else if (mapping.DefaultValue != null)
        {
            result.Steps.Add(new MappingPreviewStep
            {
                Name = "默认值",
                Status = "跳过",
                InputValue = value,
                Message = "当前已有值，正式逻辑不会覆盖为默认值。"
            });
        }

        if (value != null && !string.IsNullOrWhiteSpace(mapping.ValueExpression))
        {
            var beforeExpression = value;
            value = FieldMappingExecutor.ApplyExpression(value, mapping.ValueExpression);
            result.Steps.Add(new MappingPreviewStep
            {
                Name = "值表达式",
                Status = value == null ? "结果为空" : "已处理",
                InputValue = beforeExpression,
                OutputValue = value,
                Message = $"表达式：{mapping.ValueExpression}"
            });

            if (value == beforeExpression
                && mapping.ValueExpression.StartsWith("format:", StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add("日期格式表达式未改变样本值，请确认样本值能被 DateTime 解析。");
            }
        }
        else if (!string.IsNullOrWhiteSpace(mapping.ValueExpression))
        {
            result.Steps.Add(new MappingPreviewStep
            {
                Name = "值表达式",
                Status = "跳过",
                Message = "当前值为空，未执行值表达式。"
            });
        }

        result.FinalValue = value;
        result.IsMissing = value == null;
        result.Steps.Add(new MappingPreviewStep
        {
            Name = "最终结果",
            Status = result.IsMissing ? "为空" : "完成",
            InputValue = sampleValue,
            OutputValue = value
        });

        return result;
    }

    /// <summary>
    /// 对单条映射规则执行提取预览
    /// </summary>
    public async Task<MappingPreviewResult> PreviewSingleAsync(
        JToken body,
        EsbFieldMapping mapping,
        string? mainRecordArrayPath = null,
        IReadOnlyList<EsbFieldMapping>? mappings = null,
        IReadOnlyDictionary<Guid, CardInfo>? cards = null,
        List<EsbFilterRule>? filterRules = null)
    {
        var effectiveArrayPath = ResolveEffectiveArrayPath(body, mapping, mappings, cards, mainRecordArrayPath);
        if (SubCardPathHelper.IsParentRecordScopedPath(mapping.SourcePath))
        {
            effectiveArrayPath = ResolveEffectiveParentArrayPath(
                body,
                mapping,
                mappings,
                cards,
                mainRecordArrayPath);
        }
        var result = new MappingPreviewResult
        {
            MappingId = mapping.Id,
            SourcePath = GetPreviewSourcePath(body, mapping, mainRecordArrayPath, effectiveArrayPath) ?? "",
            TargetField = mapping.TargetField,
            MappingTarget = mapping.MappingTarget,
            IsRequired = mapping.IsRequired,
            Description = mapping.Description,
        };

        var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, mainRecordArrayPath);
        var hasArrayItemRules = mapping.MappingTarget == MappingTarget.Question
                                && SubCardPathHelper.HasArrayWildcard(mapping.SourcePath)
                                && filterRules?.Any(rule => rule.IsEnabled && rule.FilterScope == FilterScope.RowFilter) == true;
        var mappingRules = hasArrayItemRules
            ? filterRules?.Where(rule => rule.FilterScope != FilterScope.RowFilter).ToList()
            : filterRules;
        if (!_filterRuleService.CheckMappingRules(body, mainContext, mappingRules))
        {
            result.IsFiltered = true;
            result.FilterSummary = "整条映射判断未通过";
            return result;
        }

        // 提取原始值
        try
        {
            if (hasArrayItemRules)
            {
                var filtered = _filterRuleService.FilterMappingArrayValues(
                    body,
                    mainContext,
                    mapping.SourcePath,
                    filterRules,
                    mainContext);
                result.TotalArrayItemCount = filtered.TotalCount;
                result.MatchedArrayItemCount = filtered.MatchedCount;
                result.FilterSummary = $"数组项 {filtered.MatchedCount}/{filtered.TotalCount}";
                if (filtered.MatchedCount == 0)
                {
                    result.IsFiltered = true;
                    return result;
                }

                result.RawValue = filtered.Values.Count == 0
                    ? null
                    : string.Join("；", filtered.Values);
            }
            else
            {
                result.RawValue = ResolvePreviewValue(body, mapping, mainRecordArrayPath, effectiveArrayPath);
            }
        }
        catch
        {
            // 路径无效时忽略
        }

        var value = result.RawValue;

        // 字典转换
        if (value != null && !string.IsNullOrEmpty(mapping.DictCode))
        {
            value = await _dictService.TranslateOrKeepAsync(mapping.DictCode, value, mapping.DictMatchMode);
            result.DictTranslatedValue = value;
        }

        // 默认值
        value ??= mapping.DefaultValue;

        // 值表达式处理
        if (value != null && !string.IsNullOrEmpty(mapping.ValueExpression))
            value = FieldMappingExecutor.ApplyExpression(value, mapping.ValueExpression);

        result.FinalValue = value;
        result.IsMissing = value == null;

        return result;
    }

    private static string? ResolvePreviewValue(
        JToken body,
        EsbFieldMapping mapping,
        string? mainRecordArrayPath,
        string? effectiveArrayPath)
    {
        if (mapping.MappingTarget == MappingTarget.Question
            && mapping.SourcePath?.Contains("[]", StringComparison.Ordinal) == true)
        {
            var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, mainRecordArrayPath);
            var values = MessageJsonHelper.ResolveScopedTokens(body, mainContext, mapping.SourcePath, mainContext)
                .Select(t => t.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            return values.Count == 0 ? null : string.Join(",", values);
        }

        var token = ResolvePreviewToken(body, mapping, mainRecordArrayPath, effectiveArrayPath);
        return token == null || token.Type is JTokenType.Null or JTokenType.Undefined
            ? null
            : token.ToString();
    }

    private static string? GetPreviewSourcePath(
        JToken body,
        EsbFieldMapping mapping,
        string? mainRecordArrayPath,
        string? effectiveArrayPath)
    {
        if (mapping.MappingTarget != MappingTarget.SubCard)
        {
            return MessageJsonHelper.TryNormalizeMainRecordRelativeSourcePath(mapping.SourcePath, mainRecordArrayPath, out var mainRelativePath)
                ? mainRelativePath
                : mapping.SourcePath;
        }

        var normalizedSourcePath = mapping.SourcePath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalizedSourcePath))
        {
            return normalizedSourcePath;
        }

        if (SubCardPathHelper.IsParentRecordScopedPath(normalizedSourcePath))
        {
            normalizedSourcePath = SubCardPathHelper.TrimParentRecordScopePrefix(normalizedSourcePath);
        }

        if (MessageJsonHelper.TryNormalizeMainRecordSourcePath(normalizedSourcePath, mainRecordArrayPath, out var subCardMainScopedPath)
            && !SubCardPathHelper.HasArrayWildcard(MessageJsonHelper.TrimMainRecordScopePrefix(subCardMainScopedPath)))
        {
            return subCardMainScopedPath;
        }

        if (SubCardPathHelper.IsAbsoluteJsonPath(normalizedSourcePath)
            || SubCardPathHelper.IsRootScopedPath(normalizedSourcePath, effectiveArrayPath)
            || string.IsNullOrWhiteSpace(effectiveArrayPath))
        {
            return normalizedSourcePath;
        }

        return SubCardPathHelper.BuildScopedPath(body, effectiveArrayPath, normalizedSourcePath);
    }

    private static JToken? ResolvePreviewToken(
        JToken body,
        EsbFieldMapping mapping,
        string? mainRecordArrayPath,
        string? effectiveArrayPath)
    {
        if (string.IsNullOrWhiteSpace(mapping.SourcePath))
        {
            return null;
        }

        var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, mainRecordArrayPath);
        if (mapping.MappingTarget != MappingTarget.SubCard)
        {
            return MessageJsonHelper.ResolveFirstScopedToken(body, mainContext, mapping.SourcePath, mainContext);
        }

        var normalizedSourcePath = MessageJsonHelper.TryNormalizeMainRecordSourcePath(mapping.SourcePath, mainRecordArrayPath, out var subCardMainScopedPath)
                                  && !SubCardPathHelper.HasArrayWildcard(MessageJsonHelper.TrimMainRecordScopePrefix(subCardMainScopedPath))
            ? subCardMainScopedPath
            : mapping.SourcePath;

        if (SubCardPathHelper.IsParentRecordScopedPath(normalizedSourcePath))
        {
            normalizedSourcePath = SubCardPathHelper.TrimParentRecordScopePrefix(normalizedSourcePath);
        }

        if (MessageJsonHelper.IsMainRecordScopedPath(normalizedSourcePath))
        {
            return MessageJsonHelper.ResolveFirstScopedToken(body, mainContext, normalizedSourcePath, mainContext);
        }

        if (SubCardPathHelper.IsAbsoluteJsonPath(normalizedSourcePath)
            || (!string.IsNullOrWhiteSpace(effectiveArrayPath)
                && SubCardPathHelper.IsRootScopedPath(normalizedSourcePath, effectiveArrayPath)))
        {
            return MessageJsonHelper.ResolveSampleToken(body, normalizedSourcePath);
        }

        if (string.IsNullOrWhiteSpace(effectiveArrayPath))
        {
            return null;
        }

        return ResolveFirstContainerItem(body, effectiveArrayPath, normalizedSourcePath);
    }

    private static JToken? ResolveFirstContainerItem(JToken body, string arrayPath, string? relativePath)
    {
        var context = SubCardPathHelper.ResolveFirstSubCardContext(body, arrayPath);
        if (context == null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(relativePath)
            ? context
            : SubCardPathHelper.ResolveFirstToken(context, relativePath);
    }

    private static JToken? SafeSelectToken(JToken token, string path) =>
        SubCardPathHelper.SafeSelectToken(token, path);

    /// <summary>
    /// 批量预览所有映射
    /// </summary>
    public async Task<List<MappingPreviewResult>> PreviewAllAsync(
        JToken body,
        List<EsbFieldMapping> mappings,
        string? mainRecordArrayPath = null,
        IReadOnlyDictionary<Guid, CardInfo>? cards = null,
        IReadOnlyDictionary<string, List<EsbFilterRule>>? filterRulesByMappingKey = null)
    {
        var results = new List<MappingPreviewResult>();
        foreach (var mapping in mappings)
        {
            List<EsbFilterRule>? filterRules = null;
            filterRulesByMappingKey?.TryGetValue(BuildMappingKey(mapping), out filterRules);
            results.Add(await PreviewSingleAsync(body, mapping, mainRecordArrayPath, mappings, cards, filterRules));
        }
        return results;
    }

    public static string BuildMappingKey(EsbFieldMapping mapping) =>
        mapping.MappingTarget == MappingTarget.SubCard
            ? $"{mapping.MappingTarget}:{mapping.CardId}:{mapping.TargetField}"
            : $"{mapping.MappingTarget}:{mapping.TargetField}";

    private static string? ResolveEffectiveArrayPath(
        JToken body,
        EsbFieldMapping mapping,
        IReadOnlyList<EsbFieldMapping>? mappings,
        IReadOnlyDictionary<Guid, CardInfo>? cards,
        string? mainRecordArrayPath,
        HashSet<Guid>? visited = null)
    {
        if (mapping.MappingTarget != MappingTarget.SubCard || !mapping.CardId.HasValue)
        {
            return null;
        }

        var effectivePath = SubCardPathHelper.ExpandArrayPathToRoot(body, mapping.ArrayPath, mainRecordArrayPath);
        if (mappings == null || cards == null)
        {
            return effectivePath;
        }

        visited ??= [];
        if (!visited.Add(mapping.CardId.Value))
        {
            return effectivePath;
        }

        var mappedCardIds = mappings
            .Where(item => item.MappingTarget == MappingTarget.SubCard
                           && !EsbFieldMapping.IsSubCardFilterMapping(item)
                           && item.CardId.HasValue)
            .Select(item => item.CardId!.Value)
            .Distinct();
        var parentCardId = SubCardHierarchyHelper
            .BuildMappedParentMap(mappedCardIds, cards)
            .GetValueOrDefault(mapping.CardId.Value);
        if (!parentCardId.HasValue)
        {
            return effectivePath;
        }

        var parentMapping = mappings.FirstOrDefault(item =>
            item.MappingTarget == MappingTarget.SubCard
            && !EsbFieldMapping.IsSubCardFilterMapping(item)
            && item.CardId == parentCardId
            && !string.IsNullOrWhiteSpace(item.ArrayPath));
        if (parentMapping == null)
        {
            return effectivePath;
        }

        var parentPath = ResolveEffectiveArrayPath(
            body,
            parentMapping,
            mappings,
            cards,
            mainRecordArrayPath,
            visited);
        return SubCardPathHelper.ExpandNestedArrayPathToRoot(
            body,
            mapping.ArrayPath,
            parentPath,
            mainRecordArrayPath);
    }

    private static string? ResolveEffectiveParentArrayPath(
        JToken body,
        EsbFieldMapping mapping,
        IReadOnlyList<EsbFieldMapping>? mappings,
        IReadOnlyDictionary<Guid, CardInfo>? cards,
        string? mainRecordArrayPath)
    {
        if (!mapping.CardId.HasValue || mappings == null || cards == null)
        {
            return null;
        }

        var mappedCardIds = mappings
            .Where(item => item.MappingTarget == MappingTarget.SubCard
                           && !EsbFieldMapping.IsSubCardFilterMapping(item)
                           && item.CardId.HasValue)
            .Select(item => item.CardId!.Value)
            .Distinct();
        var parentCardId = SubCardHierarchyHelper
            .BuildMappedParentMap(mappedCardIds, cards)
            .GetValueOrDefault(mapping.CardId.Value);
        if (!parentCardId.HasValue)
        {
            return null;
        }

        var parentMapping = mappings.FirstOrDefault(item =>
            item.MappingTarget == MappingTarget.SubCard
            && !EsbFieldMapping.IsSubCardFilterMapping(item)
            && item.CardId == parentCardId
            && !string.IsNullOrWhiteSpace(item.ArrayPath));
        return parentMapping == null
            ? null
            : ResolveEffectiveArrayPath(body, parentMapping, mappings, cards, mainRecordArrayPath);
    }

    private static bool EvaluateMappingFilters(
        MappingSamplePreviewResult result,
        EsbFieldMapping mapping,
        List<EsbFilterRule>? filterRules,
        string? sampleValue,
        IReadOnlyDictionary<string, string?>? filterValues)
    {
        var enabledRules = filterRules?
            .Where(r => r.IsEnabled)
            .OrderBy(r => NormalizeRuleGroup(r.RuleGroup))
            .ThenBy(r => r.SortOrder)
            .ToList() ?? [];

        if (enabledRules.Count == 0)
        {
            result.Steps.Add(new MappingPreviewStep
            {
                Name = "过滤条件",
                Status = "跳过",
                Message = "未配置启用的过滤条件。"
            });
            return true;
        }

        var mappingRules = enabledRules.Where(rule => rule.FilterScope != FilterScope.RowFilter).ToList();
        var arrayItemRules = enabledRules.Where(rule => rule.FilterScope == FilterScope.RowFilter).ToList();
        return EvaluateRuleSet(mappingRules) && EvaluateRuleSet(arrayItemRules);

        bool EvaluateRuleSet(List<EsbFilterRule> rules)
        {
            if (rules.Count == 0)
                return true;

            var passedGroupCount = 0;
            foreach (var group in rules.GroupBy(r => NormalizeRuleGroup(r.RuleGroup)).OrderBy(g => g.Key))
            {
                var groupPassed = true;
                foreach (var rule in group.OrderBy(r => r.SortOrder))
                {
                    var value = ResolveFilterSampleValue(mapping, rule, sampleValue, filterValues);
                    var matched = FilterRuleService.Evaluate(value, rule.Operator, rule.CompareValue);
                    groupPassed &= matched;
                    result.Steps.Add(new MappingPreviewStep
                    {
                        Name = $"过滤条件 组{NormalizeRuleGroup(rule.RuleGroup)}",
                        Status = matched ? "通过" : "未通过",
                        InputValue = value,
                        OutputValue = matched ? "true" : "false",
                        Message = BuildRuleMessage(rule)
                    });
                }

                if (groupPassed)
                    passedGroupCount++;
            }

            return passedGroupCount > 0;
        }
    }

    private static string? ResolveFilterSampleValue(
        EsbFieldMapping mapping,
        EsbFilterRule rule,
        string? sampleValue,
        IReadOnlyDictionary<string, string?>? filterValues)
    {
        var rulePath = rule.SourcePath?.Trim() ?? "";
        var mappingPath = mapping.SourcePath?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(rulePath)
            && string.Equals(rulePath, mappingPath, StringComparison.Ordinal))
        {
            return sampleValue;
        }

        if (!string.IsNullOrWhiteSpace(rulePath)
            && filterValues?.TryGetValue(rulePath, out var value) == true)
        {
            return value;
        }

        return null;
    }

    private static string BuildRuleMessage(EsbFilterRule rule)
    {
        var compareValue = IsCompareValueDisabled(rule.Operator) ? "" : $" {rule.CompareValue}";
        var description = string.IsNullOrWhiteSpace(rule.Description) ? "" : $"（{rule.Description}）";
        var scope = rule.FilterScope == FilterScope.RowFilter ? "数组项过滤" : "整条映射判断";
        return $"[{scope}] {rule.SourcePath} {rule.Operator}{compareValue}{description}";
    }

    private static bool IsCompareValueDisabled(string? op) =>
        string.Equals(op, "is_empty", StringComparison.OrdinalIgnoreCase)
        || string.Equals(op, "is_not_empty", StringComparison.OrdinalIgnoreCase);

    private static int NormalizeRuleGroup(int ruleGroup) => ruleGroup <= 0 ? 1 : ruleGroup;
}
