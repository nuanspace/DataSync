using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 消息 JSON 读取辅助
/// </summary>
public static class MessageJsonHelper
{
    public const string MainRecordScopePrefix = "$main.";
    public const int LargeTextPreviewLength = 1000;

    public static bool TryParseToken(string rawJson, out JToken root, out string? error)
    {
        root = JValue.CreateNull();
        error = null;

        if (string.IsNullOrWhiteSpace(rawJson) || rawJson == "{}")
        {
            error = "Raw JSON 为空";
            return false;
        }

        try
        {
            root = JToken.Parse(rawJson);
            if (root is not JObject && root is not JArray)
            {
                error = "Raw JSON 根节点必须是对象或数组";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Raw JSON 解析失败: {ex.Message}";
            return false;
        }
    }

    public static bool TryParseRoot(string rawJson, out JObject root, out string? error)
    {
        root = new JObject();
        error = null;

        if (!TryParseToken(rawJson, out var token, out error))
            return false;

        if (token is JObject obj)
        {
            root = obj;
            return true;
        }

        error = "Raw JSON 根节点必须是对象";
        return false;
    }

    public static string? ReadString(JToken root, string? path, JToken? context = null)
    {
        return ResolveScopedToken(root, context ?? root, path)?.ToString();
    }

    public static DateTime? ReadDateTime(JToken root, string? path, JToken? context = null)
    {
        var value = ReadString(root, path, context);
        return DateTime.TryParse(value, out var parsed) ? parsed : null;
    }

    public static JToken ResolveMainRecordContext(JToken root, string? mainRecordArrayPath)
    {
        var arrayPath = SubCardPathHelper.NormalizeArrayContainerPath(mainRecordArrayPath);
        if (string.IsNullOrWhiteSpace(arrayPath))
            return root;

        var token = SafeSelectToken(root, arrayPath);
        return token switch
        {
            JArray array when array.Count > 0 => array[0],
            JObject obj => obj,
            _ => root
        };
    }

    public static bool IsMainRecordScopedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Trim().StartsWith(MainRecordScopePrefix, StringComparison.OrdinalIgnoreCase);

    public static string TrimMainRecordScopePrefix(string? path)
    {
        var normalized = path?.Trim() ?? "";
        return IsMainRecordScopedPath(normalized)
            ? normalized[MainRecordScopePrefix.Length..]
            : normalized;
    }

    public static string EnsureMainRecordScopedPath(string? path)
    {
        var normalized = TrimMainRecordScopePrefix(path);
        return string.IsNullOrWhiteSpace(normalized)
            ? ""
            : MainRecordScopePrefix + normalized;
    }

    public static bool TryNormalizeMainRecordSourcePath(string? sourcePath, string? mainRecordArrayPath, out string normalizedPath)
    {
        normalizedPath = sourcePath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(mainRecordArrayPath))
        {
            return false;
        }

        if (IsMainRecordScopedPath(normalizedPath))
        {
            var relativePath = TrimMainRecordScopePrefix(normalizedPath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return false;
            }

            normalizedPath = EnsureMainRecordScopedPath(relativePath);
            return true;
        }

        if (!SubCardPathHelper.TryBuildRelativePath(normalizedPath, mainRecordArrayPath, out var mainRecordRelativePath)
            || string.IsNullOrWhiteSpace(mainRecordRelativePath))
        {
            return false;
        }

        normalizedPath = EnsureMainRecordScopedPath(mainRecordRelativePath);
        return true;
    }

    public static bool TryNormalizeMainRecordRelativeSourcePath(string? sourcePath, string? mainRecordArrayPath, out string normalizedPath)
    {
        normalizedPath = sourcePath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(mainRecordArrayPath))
        {
            return false;
        }

        if (IsMainRecordScopedPath(normalizedPath))
        {
            var relativePath = TrimMainRecordScopePrefix(normalizedPath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return false;
            }

            normalizedPath = relativePath;
            return true;
        }

        if (!SubCardPathHelper.TryBuildRelativePath(normalizedPath, mainRecordArrayPath, out var mainRecordRelativePath)
            || string.IsNullOrWhiteSpace(mainRecordRelativePath))
        {
            return false;
        }

        normalizedPath = mainRecordRelativePath;
        return true;
    }

    public static JToken? ResolveScopedToken(JToken root, JToken context, string? path, JToken? mainContext = null)
    {
        var normalized = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (IsMainRecordScopedPath(normalized))
        {
            var relativePath = TrimMainRecordScopePrefix(normalized);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            return SafeSelectToken(mainContext ?? context, relativePath);
        }

        if (SubCardPathHelper.IsAbsoluteJsonPath(normalized))
            return SafeSelectToken(root, normalized);

        return SafeSelectToken(context, normalized) ?? SafeSelectToken(root, normalized);
    }

    public static JToken? ResolveFirstScopedToken(JToken root, JToken context, string? path, JToken? mainContext = null)
    {
        var normalized = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (IsMainRecordScopedPath(normalized))
        {
            var relativePath = TrimMainRecordScopePrefix(normalized);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            return SubCardPathHelper.ResolveFirstToken(mainContext ?? context, relativePath);
        }

        if (SubCardPathHelper.IsAbsoluteJsonPath(normalized))
            return SubCardPathHelper.ResolveFirstToken(root, normalized);

        return SubCardPathHelper.ResolveFirstToken(context, normalized)
            ?? SubCardPathHelper.ResolveFirstToken(root, normalized);
    }

    public static List<JToken> ResolveScopedTokens(JToken root, JToken context, string? path, JToken? mainContext = null)
    {
        var normalized = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        if (IsMainRecordScopedPath(normalized))
        {
            var relativePath = TrimMainRecordScopePrefix(normalized);
            return string.IsNullOrWhiteSpace(relativePath)
                ? []
                : SubCardPathHelper.ResolveTokens(mainContext ?? context, relativePath);
        }

        if (SubCardPathHelper.IsAbsoluteJsonPath(normalized))
            return SubCardPathHelper.ResolveTokens(root, normalized);

        var tokens = SubCardPathHelper.ResolveTokens(context, normalized);
        return tokens.Count > 0 ? tokens : SubCardPathHelper.ResolveTokens(root, normalized);
    }

    public static string? ExtractBodyJson(JToken root)
    {
        var body = SafeSelectToken(root, "$.Request.Body")
            ?? SafeSelectToken(root, "[0].Request.Body");
        if (body == null)
            return null;

        return body.Type == JTokenType.String
            ? body.ToString()
            : body.ToString(Formatting.None);
    }

    public static string? TryGetLegacyTranCode(JToken root)
        => ReadString(root, "$.Request.Head.TranCode")
            ?? ReadString(root, "[0].Request.Head.TranCode");

    public static string? TryGetLegacyMessageId(JToken root)
        => ReadString(root, "$.Request.Head.MessageId")
            ?? ReadString(root, "[0].Request.Head.MessageId");

    public static JToken GetSingleRecordSampleToken(JToken root)
    {
        return root is JArray array && array.Count > 0
            ? array[0]
            : root;
    }

    public static JToken? ResolveSampleToken(JToken root, string? path)
    {
        var normalized = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (root is JArray array
            && array.Count > 0
            && !normalized.StartsWith("[", StringComparison.Ordinal))
        {
            return SubCardPathHelper.ResolveFirstToken(array[0], normalized)
                ?? SubCardPathHelper.ResolveFirstToken(root, normalized);
        }

        return SubCardPathHelper.ResolveFirstToken(root, normalized);
    }

    public static JToken? SelectSampleToken(JToken root, string? path)
    {
        var normalized = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (root is JArray array
            && array.Count > 0
            && !normalized.StartsWith("[", StringComparison.Ordinal))
        {
            return SafeSelectToken(array[0], normalized) ?? SafeSelectToken(root, normalized);
        }

        return SafeSelectToken(root, normalized);
    }

    public static JToken? SafeSelectToken(JToken token, string path)
    {
        try
        {
            return token.SelectToken(path);
        }
        catch
        {
            return null;
        }
    }

    public static bool SanitizeLargeStringValues(
        JToken token,
        int maxLength = LargeTextPreviewLength,
        bool includeLength = false)
    {
        var values = EnumerateJsonValues(token)
            .Where(value => value.Type == JTokenType.String && value.Value<string>()?.Length > maxLength)
            .ToList();

        foreach (var value in values)
        {
            var length = value.Value<string>()?.Length ?? 0;
            value.Value = includeLength
                ? $"[大文本内容已省略，共 {length} 个字符]"
                : "[大文本内容已省略]";
        }

        return values.Count > 0;
    }

    private static IEnumerable<JValue> EnumerateJsonValues(JToken token)
    {
        if (token is JValue value)
        {
            yield return value;
            yield break;
        }

        foreach (var child in token.Children())
        {
            foreach (var descendant in EnumerateJsonValues(child))
                yield return descendant;
        }
    }
}
