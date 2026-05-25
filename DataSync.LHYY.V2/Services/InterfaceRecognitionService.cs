using DataSync.LHYY.V2.Models.Entities;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 接口识别服务
/// </summary>
public class InterfaceRecognitionService
{
    private readonly ConfigService _configService;

    public InterfaceRecognitionService(ConfigService configService)
    {
        _configService = configService;
    }

    public async Task<List<InterfaceRecognitionResult>> ResolveAsync(
        JToken root,
        bool includeGlobalFallback = true)
        => await ResolveAsync(root, null, includeGlobalFallback);

    public async Task<List<InterfaceRecognitionResult>> ResolveAsync(
        JToken root,
        string? integrationProjectCode,
        bool includeGlobalFallback = true)
    {
        var currentProjectCode = string.IsNullOrWhiteSpace(integrationProjectCode)
            ? await _configService.GetCurrentIntegrationProjectCodeAsync()
            : integrationProjectCode!;
        var enabledConfigs = await _configService.GetEnabledInterfaceConfigsAsync(currentProjectCode, includeGlobalFallback);
        var configMap = enabledConfigs.ToDictionary(c => c.TranCode, StringComparer.OrdinalIgnoreCase);

        var legacyTranCode = MessageJsonHelper.TryGetLegacyTranCode(root);
        if (!string.IsNullOrWhiteSpace(legacyTranCode) &&
            configMap.TryGetValue(legacyTranCode, out var legacyConfig))
        {
            return
            [
                new InterfaceRecognitionResult
                {
                    Config = legacyConfig,
                    IsLegacyEsb = true,
                }
            ];
        }

        var fallbackMatch = TryResolveByCommonTranCode(root, configMap);
        if (fallbackMatch != null)
            return [fallbackMatch];

        var allRules = await _configService.GetInterfaceMatchRulesAsync(currentProjectCode, includeGlobalFallback);
        var matches = new List<InterfaceRecognitionResult>();

        foreach (var config in enabledConfigs)
        {
            var rules = allRules
                .Where(r => string.Equals(r.TranCode, config.TranCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.SortOrder)
                .ToList();
            var projectRules = rules
                .Where(r => string.Equals(r.IntegrationProjectCode, config.IntegrationProjectCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (projectRules.Count > 0)
            {
                rules = projectRules;
            }
            else if (includeGlobalFallback)
            {
                rules = rules.Where(r => string.IsNullOrWhiteSpace(r.IntegrationProjectCode)).ToList();
            }
            else
            {
                rules = [];
            }

            if (rules.Count == 0)
                continue;

            var matchedGroup = FindMatchedGroup(root, rules);
            if (matchedGroup == null)
                continue;

            matches.Add(new InterfaceRecognitionResult
            {
                Config = config,
                MatchedGroup = matchedGroup,
            });
        }

        var ordered = matches
            .OrderBy(m => m.Config.SortOrder)
            .ThenBy(m => m.Config.TranCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count <= 1)
            return ordered;

        var primary = ordered[0];
        if (!primary.Config.AllowMultipleMatch)
            return [primary];

        return ordered
            .Where(m => m.Config.AllowMultipleMatch || string.Equals(m.Config.TranCode, primary.Config.TranCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static int? FindMatchedGroup(JToken root, List<EsbInterfaceMatchRule> rules)
    {
        foreach (var group in rules.GroupBy(r => r.MatchGroup).OrderBy(g => g.Key))
        {
            if (group.All(rule => IsRuleMatched(root, rule)))
                return group.Key;
        }

        return null;
    }

    private static bool IsRuleMatched(JToken root, EsbInterfaceMatchRule rule)
    {
        if (rule.SourcePath.Contains("[]", StringComparison.Ordinal))
        {
            var values = FilterRuleService.ResolvePath(root, rule.SourcePath).Select(r => r.value);
            return values.Any(value => FilterRuleService.Evaluate(value, rule.Operator, rule.CompareValue));
        }

        var value = MessageJsonHelper.ReadString(root, rule.SourcePath);
        return FilterRuleService.Evaluate(value, rule.Operator, rule.CompareValue);
    }

    private static InterfaceRecognitionResult? TryResolveByCommonTranCode(
        JToken root,
        Dictionary<string, EsbInterfaceConfig> configMap)
    {
        foreach (var candidate in GetTranCodeCandidates(root))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (configMap.TryGetValue(candidate, out var exactConfig))
            {
                return new InterfaceRecognitionResult
                {
                    Config = exactConfig,
                    IsLegacyEsb = IsLegacyCandidatePath(candidate, root)
                };
            }
        }

        return null;
    }

    private static List<string?> GetTranCodeCandidates(JToken root)
    {
        return
        [
            MessageJsonHelper.TryGetLegacyTranCode(root),
            MessageJsonHelper.ReadString(root, "serverCode"),
            MessageJsonHelper.ReadString(root, "ServerCode"),
            MessageJsonHelper.ReadString(root, "tranCode"),
            MessageJsonHelper.ReadString(root, "TranCode"),
            MessageJsonHelper.ReadString(root, "code"),
            MessageJsonHelper.ReadString(root, "Code")
        ];
    }

    private static bool IsLegacyCandidatePath(string candidate, JToken root)
    {
        var legacyTranCode = MessageJsonHelper.TryGetLegacyTranCode(root);
        return string.Equals(candidate, legacyTranCode, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class InterfaceRecognitionResult
{
    public required EsbInterfaceConfig Config { get; init; }
    public int? MatchedGroup { get; init; }
    public bool IsLegacyEsb { get; init; }
}
