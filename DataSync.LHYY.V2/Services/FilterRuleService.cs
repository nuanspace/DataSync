using System.Text.RegularExpressions;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 统一过滤规则服务：路径解析、接口级和映射级过滤判断、运算符求值。
/// </summary>
public class FilterRuleService
{
    private static readonly char[] ListValueSeparators = [',', '，', '、', ';', '；', '\r', '\n'];

    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly IntegrationProjectService _integrationProjectService;
    private readonly ILogger<FilterRuleService> _logger;

    public FilterRuleService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IntegrationProjectService integrationProjectService,
        ILogger<FilterRuleService> logger)
    {
        _contextFactory = contextFactory;
        _integrationProjectService = integrationProjectService;
        _logger = logger;
    }

    public async Task<List<EsbFilterRule>> LoadInterfaceRulesAsync(string tranCode, string? integrationProjectCode = null)
    {
        var currentProjectCode = string.IsNullOrWhiteSpace(integrationProjectCode)
            ? await _integrationProjectService.GetCurrentProjectCodeAsync()
            : integrationProjectCode!;

        await using var db = await _contextFactory.CreateDbContextAsync();
        var rules = await db.EsbFilterRules
            .AsNoTracking()
            .Where(r => r.TranCode == tranCode && r.MappingId == null && r.IsEnabled)
            .WhereInProjectOrGlobal(currentProjectCode)
            .OrderBy(r => r.RuleGroup)
            .ThenBy(r => r.SortOrder)
            .ToListAsync();

        var projectScoped = rules
            .Where(r => string.Equals(r.IntegrationProjectCode, currentProjectCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return projectScoped.Count > 0
            ? projectScoped
            : rules.Where(r => string.IsNullOrWhiteSpace(r.IntegrationProjectCode)).ToList();
    }

    public async Task<Dictionary<int, List<EsbFilterRule>>> LoadMappingRulesAsync(IEnumerable<int> mappingIds)
    {
        var ids = mappingIds.ToList();
        if (ids.Count == 0)
            return [];

        await using var db = await _contextFactory.CreateDbContextAsync();
        var rules = await db.EsbFilterRules
            .AsNoTracking()
            .Where(r => r.MappingId != null && ids.Contains(r.MappingId.Value) && r.IsEnabled)
            .OrderBy(r => r.RuleGroup)
            .ThenBy(r => r.SortOrder)
            .ToListAsync();

        return rules.GroupBy(r => r.MappingId!.Value).ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task<FilterResult> ApplyInterfaceFiltersAsync(
        JToken body,
        string tranCode,
        string? integrationProjectCode = null,
        string? mainRecordArrayPath = null)
    {
        var rules = await LoadInterfaceRulesAsync(tranCode, integrationProjectCode);
        if (rules.Count == 0)
            return FilterResult.Passed();

        var mainContext = MessageJsonHelper.ResolveMainRecordContext(body, mainRecordArrayPath);
        var rowFilterByPath = new Dictionary<string, List<int>>();
        var matchedGroupCount = 0;
        var hasUnrestrictedMatchedGroup = false;
        string? firstFailureReason = null;

        foreach (var group in rules.GroupBy(r => NormalizeRuleGroup(r.RuleGroup)).OrderBy(g => g.Key))
        {
            var groupPassed = true;
            var groupRowFilterByPath = new Dictionary<string, List<int>>();

            foreach (var rule in group.OrderBy(r => r.SortOrder))
            {
                if (!EvaluateInterfaceRule(body, mainContext, tranCode, rule, groupRowFilterByPath, ref firstFailureReason))
                {
                    groupPassed = false;
                    break;
                }
            }

            if (!groupPassed)
                continue;

            matchedGroupCount++;
            if (groupRowFilterByPath.Count == 0)
            {
                hasUnrestrictedMatchedGroup = true;
                continue;
            }

            foreach (var (path, indices) in groupRowFilterByPath)
            {
                if (rowFilterByPath.TryGetValue(path, out var existing))
                    rowFilterByPath[path] = existing.Concat(indices).Distinct().OrderBy(i => i).ToList();
                else
                    rowFilterByPath[path] = indices;
            }
        }

        if (matchedGroupCount == 0)
        {
            var reason = firstFailureReason ?? "接口级过滤未通过: 未满足任一规则组";
            _logger.LogInformation("消息被过滤 TranCode={TranCode}, {Reason}", tranCode, reason);
            return FilterResult.Failed(reason);
        }

        return new FilterResult
        {
            IsPassed = true,
            RowFilterResults = hasUnrestrictedMatchedGroup ? [] : rowFilterByPath
        };
    }

    public bool CheckMappingRules(
        JToken body,
        JToken context,
        List<EsbFilterRule>? rules,
        string? arrayPath = null,
        JToken? localRoot = null)
    {
        if (rules == null || rules.Count == 0)
            return true;

        var enabledRules = rules.Where(r => r.IsEnabled).ToList();
        if (enabledRules.Count == 0)
            return true;

        var normalizedArrayPath = SubCardPathHelper.NormalizeArrayContainerPath(arrayPath);
        var effectiveLocalRoot = localRoot ?? context;

        foreach (var group in enabledRules.GroupBy(r => NormalizeRuleGroup(r.RuleGroup)).OrderBy(g => g.Key))
        {
            if (group.OrderBy(r => r.SortOrder).All(IsMappingRuleMatched))
                return true;
        }

        return false;

        bool IsMappingRuleMatched(EsbFilterRule rule)
        {
            var useAbsoluteRoot = rule.SourcePath.StartsWith("$.", StringComparison.Ordinal);
            var useMainScope = MessageJsonHelper.IsMainRecordScopedPath(rule.SourcePath);
            var useLocalRoot = !useAbsoluteRoot
                && !useMainScope
                && !string.IsNullOrWhiteSpace(normalizedArrayPath)
                && SubCardPathHelper.IsRootScopedPath(rule.SourcePath, normalizedArrayPath);

            if (SubCardPathHelper.HasArrayWildcard(rule.SourcePath))
            {
                var resolved = ResolvePath(
                    body,
                    useAbsoluteRoot ? body : useMainScope ? effectiveLocalRoot : useLocalRoot ? effectiveLocalRoot : context,
                    rule.SourcePath,
                    effectiveLocalRoot);
                var values = resolved.Count > 0 ? resolved.Select(r => r.value) : [null];
                return values.Any(value => Evaluate(value, rule.Operator, rule.CompareValue));
            }

            var token = useAbsoluteRoot
                ? SubCardPathHelper.SafeSelectToken(body, rule.SourcePath)
                : useMainScope
                    ? MessageJsonHelper.ResolveScopedToken(body, context, rule.SourcePath, effectiveLocalRoot)
                : MessageJsonHelper.ResolveScopedToken(
                    body,
                    useLocalRoot ? effectiveLocalRoot : context,
                    rule.SourcePath,
                    effectiveLocalRoot);

            return Evaluate(token?.ToString(), rule.Operator, rule.CompareValue);
        }
    }

    public static List<(JToken context, string? value)> ResolvePath(JToken root, string path)
    {
        if (!path.Contains("[]", StringComparison.Ordinal))
        {
            var token = SubCardPathHelper.SafeSelectToken(root, path);
            return [(root, token?.ToString())];
        }

        var bracketIndex = path.IndexOf("[]", StringComparison.Ordinal);
        var arrayPath = path[..bracketIndex];
        var remainder = path[(bracketIndex + 2)..].TrimStart('.');

        var arrayToken = string.IsNullOrWhiteSpace(arrayPath)
            ? root
            : SubCardPathHelper.SafeSelectToken(root, arrayPath);
        if (arrayToken is not JArray array)
            return [];

        var results = new List<(JToken, string?)>();
        foreach (var item in array)
        {
            if (string.IsNullOrEmpty(remainder))
            {
                results.Add((item, item.ToString()));
            }
            else if (remainder.Contains("[]", StringComparison.Ordinal))
            {
                results.AddRange(ResolvePath(item, remainder));
            }
            else
            {
                var valueToken = SubCardPathHelper.SafeSelectToken(item, remainder);
                results.Add((item, valueToken?.ToString()));
            }
        }

        return results;
    }

    public static List<(JToken context, string? value)> ResolvePath(JToken root, JToken context, string path, JToken? mainContext = null)
    {
        if (path.StartsWith("$.", StringComparison.Ordinal))
            return ResolvePath(root, path);

        if (MessageJsonHelper.IsMainRecordScopedPath(path))
        {
            var relativePath = MessageJsonHelper.TrimMainRecordScopePrefix(path);
            return string.IsNullOrWhiteSpace(relativePath)
                ? []
                : ResolvePath(mainContext ?? context, relativePath);
        }

        var resolved = ResolvePath(context, path);
        return HasResolvedResult(path, resolved)
            ? resolved
            : ResolvePath(root, path);
    }

    public static bool Evaluate(string? value, string op, string compareValue)
    {
        return op.ToLowerInvariant() switch
        {
            "eq" => string.Equals(value, compareValue, StringComparison.Ordinal),
            "neq" => !string.Equals(value, compareValue, StringComparison.Ordinal),
            "contains" => value?.Contains(compareValue, StringComparison.Ordinal) == true,
            "not_contains" => value?.Contains(compareValue, StringComparison.Ordinal) != true,
            "starts_with" => value?.StartsWith(compareValue, StringComparison.Ordinal) == true,
            "ends_with" => value?.EndsWith(compareValue, StringComparison.Ordinal) == true,
            "in" => value != null && SplitCompareValues(compareValue).Contains(value.Trim(), StringComparer.Ordinal),
            "not_in" => value == null || !SplitCompareValues(compareValue).Contains(value.Trim(), StringComparer.Ordinal),
            "gt" => TryCompareNumber(value, compareValue) > 0,
            "lt" => TryCompareNumber(value, compareValue) < 0,
            "gte" => TryCompareNumber(value, compareValue) >= 0,
            "lte" => TryCompareNumber(value, compareValue) <= 0,
            "is_empty" => string.IsNullOrEmpty(value),
            "is_not_empty" => !string.IsNullOrEmpty(value),
            "regex" => value != null && Regex.IsMatch(value, compareValue),
            _ => false
        };
    }

    private bool EvaluateInterfaceRule(
        JToken body,
        JToken mainContext,
        string tranCode,
        EsbFilterRule rule,
        Dictionary<string, List<int>> rowFilterByPath,
        ref string? firstFailureReason)
    {
        var hasArray = rule.SourcePath.Contains("[]", StringComparison.Ordinal);
        if (!hasArray)
        {
            var token = MessageJsonHelper.ResolveScopedToken(body, mainContext, rule.SourcePath);
            var value = token?.ToString();
            if (Evaluate(value, rule.Operator, rule.CompareValue))
                return true;

            firstFailureReason ??= $"接口级过滤未通过: {rule.SourcePath} {rule.Operator} {rule.CompareValue}" +
                                   (string.IsNullOrEmpty(rule.Description) ? "" : $" ({rule.Description})");
            return false;
        }

        if (rule.FilterScope == FilterScope.MessageCheck)
        {
            var resolved = ResolvePath(body, mainContext, rule.SourcePath);
            if (resolved.Any(r => Evaluate(r.value, rule.Operator, rule.CompareValue)))
                return true;

            firstFailureReason ??= $"接口级过滤未通过(MessageCheck): {rule.SourcePath} {rule.Operator} {rule.CompareValue}" +
                                   (string.IsNullOrEmpty(rule.Description) ? "" : $" ({rule.Description})");
            return false;
        }

        var arrayPath = GetArrayPath(rule.SourcePath);
        var rowValues = ResolvePath(body, mainContext, rule.SourcePath);
        var passedIndices = new List<int>();

        for (var i = 0; i < rowValues.Count; i++)
        {
            if (Evaluate(rowValues[i].value, rule.Operator, rule.CompareValue))
                passedIndices.Add(i);
        }

        if (rowFilterByPath.TryGetValue(arrayPath, out var existing))
            rowFilterByPath[arrayPath] = existing.Intersect(passedIndices).ToList();
        else
            rowFilterByPath[arrayPath] = passedIndices;

        _logger.LogInformation(
            "行过滤 TranCode={TranCode}, 规则组={RuleGroup}, 路径={Path}, 通过{Count}行",
            tranCode,
            NormalizeRuleGroup(rule.RuleGroup),
            arrayPath,
            rowFilterByPath[arrayPath].Count);

        return true;
    }

    private static string GetArrayPath(string path)
    {
        var idx = path.IndexOf("[]", StringComparison.Ordinal);
        return idx >= 0 ? path[..idx] : path;
    }

    private static bool HasResolvedResult(string path, List<(JToken context, string? value)> resolved)
    {
        if (resolved.Count == 0)
            return false;

        return path.Contains("[]", StringComparison.Ordinal) || resolved[0].value != null;
    }

    private static int NormalizeRuleGroup(int ruleGroup) => ruleGroup <= 0 ? 1 : ruleGroup;

    private static decimal TryCompareNumber(string? value, string compareValue)
    {
        if (!decimal.TryParse(value, out var left) || !decimal.TryParse(compareValue, out var right))
            return decimal.MinValue;

        return left - right;
    }

    private static IEnumerable<string> SplitCompareValues(string compareValue) =>
        compareValue.Split(ListValueSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
