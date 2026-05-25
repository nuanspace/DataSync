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

    public MappingPreviewService(DictService dictService)
    {
        _dictService = dictService;
    }

    /// <summary>
    /// 对单条映射规则执行提取预览
    /// </summary>
    public async Task<MappingPreviewResult> PreviewSingleAsync(JToken body, EsbFieldMapping mapping, string? mainRecordArrayPath = null)
    {
        var result = new MappingPreviewResult
        {
            MappingId = mapping.Id,
            SourcePath = GetPreviewSourcePath(body, mapping, mainRecordArrayPath) ?? "",
            TargetField = mapping.TargetField,
            MappingTarget = mapping.MappingTarget,
            IsRequired = mapping.IsRequired,
            Description = mapping.Description,
        };

        // 提取原始值
        try
        {
            result.RawValue = ResolvePreviewValue(body, mapping, mainRecordArrayPath);
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
        result.IsMissing = string.IsNullOrEmpty(value);

        return result;
    }

    private static string? ResolvePreviewValue(JToken body, EsbFieldMapping mapping, string? mainRecordArrayPath)
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

        return ResolvePreviewToken(body, mapping, mainRecordArrayPath)?.ToString();
    }

    private static string? GetPreviewSourcePath(JToken body, EsbFieldMapping mapping, string? mainRecordArrayPath)
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

        if (MessageJsonHelper.TryNormalizeMainRecordSourcePath(normalizedSourcePath, mainRecordArrayPath, out var subCardMainScopedPath)
            && !SubCardPathHelper.HasArrayWildcard(MessageJsonHelper.TrimMainRecordScopePrefix(subCardMainScopedPath)))
        {
            return subCardMainScopedPath;
        }

        var effectiveArrayPath = SubCardPathHelper.ExpandArrayPathToRoot(body, mapping.ArrayPath, mainRecordArrayPath);
        if (SubCardPathHelper.IsAbsoluteJsonPath(normalizedSourcePath)
            || SubCardPathHelper.IsRootScopedPath(normalizedSourcePath, effectiveArrayPath)
            || string.IsNullOrWhiteSpace(effectiveArrayPath))
        {
            return normalizedSourcePath;
        }

        return SubCardPathHelper.BuildScopedPath(body, effectiveArrayPath, normalizedSourcePath);
    }

    private static JToken? ResolvePreviewToken(JToken body, EsbFieldMapping mapping, string? mainRecordArrayPath)
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

        if (MessageJsonHelper.IsMainRecordScopedPath(normalizedSourcePath))
        {
            return MessageJsonHelper.ResolveFirstScopedToken(body, mainContext, normalizedSourcePath, mainContext);
        }

        var effectiveArrayPath = SubCardPathHelper.ExpandArrayPathToRoot(body, mapping.ArrayPath, mainRecordArrayPath);

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
    public async Task<List<MappingPreviewResult>> PreviewAllAsync(JToken body, List<EsbFieldMapping> mappings, string? mainRecordArrayPath = null)
    {
        var results = new List<MappingPreviewResult>();
        foreach (var mapping in mappings)
        {
            results.Add(await PreviewSingleAsync(body, mapping, mainRecordArrayPath));
        }
        return results;
    }
}
