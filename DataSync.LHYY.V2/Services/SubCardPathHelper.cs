using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 子卡路径辅助：负责展示路径与保存路径之间的转换。
/// </summary>
public static class SubCardPathHelper
{
    public const string RootContainerPath = "$";
    public const string MainRecordContainerPath = "$main";
    public const string ParentRecordContainerPath = "$parent";
    private static readonly Regex ArrayIndexRegex = new(@"\[\d+\]", RegexOptions.Compiled);

    public static bool IsAbsoluteJsonPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Trim().StartsWith("$.", StringComparison.Ordinal);

    public static string TrimJsonRootPrefix(string? path) =>
        IsAbsoluteJsonPath(path) ? path!.Trim()[2..] : path?.Trim() ?? "";

    public static string EnsureAbsoluteJsonPath(string? path)
    {
        var normalized = TrimJsonRootPrefix(path);
        return string.IsNullOrWhiteSpace(normalized) ? "" : "$." + normalized;
    }

    public static string NormalizeArrayPath(string? path)
    {
        var normalized = path?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(normalized)
            ? ""
            : ArrayIndexRegex.Replace(normalized, "[]");
    }

    public static string NormalizeArrayContainerPath(string? path)
    {
        var normalized = NormalizeArrayPath(path);
        return normalized.EndsWith("[]", StringComparison.Ordinal)
            ? normalized[..^2]
            : normalized;
    }

    public static bool HasArrayWildcard(string? path) =>
        NormalizeArrayPath(path).Contains("[]", StringComparison.Ordinal);

    public static bool PathsEqual(string? left, string? right) =>
        TrimJsonRootPrefix(NormalizeArrayContainerPath(left))
            .Equals(TrimJsonRootPrefix(NormalizeArrayContainerPath(right)), StringComparison.OrdinalIgnoreCase);

    public static bool IsRootContainerPath(string? path) =>
        NormalizeArrayContainerPath(path).Equals(RootContainerPath, StringComparison.Ordinal);

    public static bool IsMainRecordContainerPath(string? path) =>
        NormalizeArrayContainerPath(path).Equals(MainRecordContainerPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsParentRecordContainerPath(string? path) =>
        NormalizeArrayContainerPath(path).Equals(ParentRecordContainerPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsParentRecordScopedPath(string? path)
    {
        var normalized = NormalizeArrayPath(path);
        return normalized.Equals(ParentRecordContainerPath, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(ParentRecordContainerPath + ".", StringComparison.OrdinalIgnoreCase);
    }

    public static string TrimParentRecordScopePrefix(string? path)
    {
        var normalized = NormalizeArrayPath(path);
        if (normalized.Equals(ParentRecordContainerPath, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return normalized.StartsWith(ParentRecordContainerPath + ".", StringComparison.OrdinalIgnoreCase)
            ? normalized[(ParentRecordContainerPath.Length + 1)..]
            : normalized;
    }

    public static bool IsRootScopedPath(string? sourcePath, string? arrayPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(arrayPath))
        {
            return false;
        }

        var normalizedSource = TrimJsonRootPrefix(NormalizeArrayPath(sourcePath));
        var normalizedArray = TrimJsonRootPrefix(NormalizeArrayContainerPath(arrayPath));
        return normalizedSource.Equals(normalizedArray, StringComparison.OrdinalIgnoreCase)
            || normalizedSource.StartsWith(normalizedArray + ".", StringComparison.OrdinalIgnoreCase)
            || normalizedSource.StartsWith(normalizedArray + "[", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildDisplayPath(string? sourcePath, string? arrayPath)
    {
        var normalizedSource = NormalizeArrayPath(sourcePath);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return "";
        }

        if (IsAbsoluteJsonPath(normalizedSource)
            || IsRootScopedPath(normalizedSource, arrayPath))
        {
            return normalizedSource;
        }

        var normalizedArray = NormalizeArrayContainerPath(arrayPath);
        if (string.IsNullOrWhiteSpace(normalizedArray))
        {
            return normalizedSource;
        }

        if (IsRootContainerPath(normalizedArray) || IsMainRecordContainerPath(normalizedArray))
        {
            return normalizedSource;
        }

        return $"{normalizedArray}[].{normalizedSource}";
    }

    public static string BuildDisplayPath(
        JToken? root,
        string? sourcePath,
        string? arrayPath,
        string? mainRecordArrayPath = null)
    {
        var normalizedSource = NormalizeArrayPath(sourcePath);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return "";
        }

        if (IsMainRecordContainerPath(arrayPath))
        {
            var mainRecordPath = NormalizeArrayContainerPath(mainRecordArrayPath);
            return !string.IsNullOrWhiteSpace(mainRecordPath)
                   && TryBuildRelativePath(normalizedSource, mainRecordPath, out var mainRelativePath)
                   && !string.IsNullOrWhiteSpace(mainRelativePath)
                ? mainRelativePath
                : normalizedSource;
        }

        var effectiveArrayPath = ExpandArrayPathToRoot(root, arrayPath, mainRecordArrayPath);
        if (IsAbsoluteJsonPath(normalizedSource)
            || IsRootScopedPath(normalizedSource, effectiveArrayPath)
            || string.IsNullOrWhiteSpace(effectiveArrayPath))
        {
            return normalizedSource;
        }

        return root == null
            ? BuildDisplayPath(normalizedSource, effectiveArrayPath)
            : BuildScopedPath(root, effectiveArrayPath, normalizedSource);
    }

    public static bool TrySplitWildcardPath(string? path, out string arrayPath, out string relativePath)
    {
        arrayPath = "";
        relativePath = "";

        var normalizedPath = NormalizeArrayPath(path);
        var wildcardIndex = normalizedPath.IndexOf("[]", StringComparison.Ordinal);
        if (wildcardIndex < 0)
        {
            return false;
        }

        arrayPath = NormalizeArrayContainerPath(normalizedPath[..wildcardIndex].TrimEnd('.'));
        relativePath = normalizedPath[(wildcardIndex + 2)..].TrimStart('.');
        return !string.IsNullOrWhiteSpace(arrayPath);
    }

    public static bool TryBuildRelativePath(string? sourcePath, string? arrayPath, out string relativePath)
    {
        relativePath = "";

        var normalizedSource = TrimJsonRootPrefix(NormalizeArrayPath(sourcePath));
        var normalizedArray = TrimJsonRootPrefix(NormalizeArrayContainerPath(arrayPath));
        if (string.IsNullOrWhiteSpace(normalizedSource) || string.IsNullOrWhiteSpace(normalizedArray))
        {
            return false;
        }

        if (normalizedArray == RootContainerPath)
        {
            relativePath = normalizedSource;
            return true;
        }

        if (normalizedArray.Equals(MainRecordContainerPath, StringComparison.OrdinalIgnoreCase))
        {
            relativePath = normalizedSource.StartsWith(MainRecordContainerPath + ".", StringComparison.OrdinalIgnoreCase)
                ? normalizedSource[(MainRecordContainerPath.Length + 1)..]
                : normalizedSource;
            return true;
        }

        if (normalizedSource.Equals(normalizedArray, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedSource.StartsWith(normalizedArray + "[].", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = normalizedSource[(normalizedArray.Length + 3)..];
            return true;
        }

        if (normalizedSource.StartsWith(normalizedArray + "[]", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = normalizedSource[(normalizedArray.Length + 2)..].TrimStart('.');
            return true;
        }

        if (normalizedSource.StartsWith(normalizedArray + ".", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = normalizedSource[(normalizedArray.Length + 1)..];
            return true;
        }

        return false;
    }

    public static string BuildScopedPath(JToken? root, string arrayPath, string sourcePath)
    {
        if (IsRootContainerPath(arrayPath))
        {
            return sourcePath;
        }

        if (IsMainRecordContainerPath(arrayPath))
        {
            return $"{MainRecordContainerPath}.{sourcePath}";
        }

        return root != null && ResolveSubCardContainer(root, arrayPath) is JObject
            ? $"{arrayPath}.{sourcePath}"
            : $"{arrayPath}[].{sourcePath}";
    }

    public static string? ExpandArrayPathToRoot(JToken? root, string? arrayPath, string? mainRecordArrayPath)
    {
        var normalized = NormalizeArrayContainerPath(arrayPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (IsAbsoluteJsonPath(normalized))
        {
            return normalized;
        }

        var mainRecordPath = NormalizeArrayContainerPath(mainRecordArrayPath);
        if (IsMainRecordContainerPath(normalized))
        {
            return string.IsNullOrWhiteSpace(mainRecordPath) ? null : mainRecordPath;
        }

        if (IsRootContainerPath(normalized))
        {
            return RootContainerPath;
        }

        if (!string.IsNullOrWhiteSpace(mainRecordPath))
        {
            if (TryBuildRelativePath(normalized, mainRecordPath, out var mainRelativePath))
            {
                normalized = string.IsNullOrWhiteSpace(mainRelativePath) ? MainRecordContainerPath : mainRelativePath;
            }

            return IsMainRecordContainerPath(normalized)
                ? mainRecordPath
                : BuildScopedPath(root, mainRecordPath, normalized);
        }

        return normalized;
    }

    public static string? ExpandNestedArrayPathToRoot(
        JToken? root,
        string? arrayPath,
        string? parentArrayPath,
        string? mainRecordArrayPath)
    {
        var normalized = NormalizeArrayContainerPath(arrayPath);
        var normalizedParent = NormalizeArrayContainerPath(parentArrayPath);
        if (string.IsNullOrWhiteSpace(normalizedParent))
        {
            return ExpandArrayPathToRoot(root, normalized, mainRecordArrayPath);
        }

        if (IsParentRecordContainerPath(normalized))
        {
            return normalizedParent;
        }

        if (IsAbsoluteJsonPath(normalized) || IsMainRecordContainerPath(normalized))
        {
            return ExpandArrayPathToRoot(root, normalized, mainRecordArrayPath);
        }

        if (PathsEqual(normalized, normalizedParent)
            || TryBuildRelativePath(normalized, normalizedParent, out _))
        {
            return normalized;
        }

        if (TryBuildRelativePathFromParentSuffix(normalized, normalizedParent, out var suffixRelativePath))
        {
            return BuildScopedPath(root, normalizedParent, suffixRelativePath);
        }

        return BuildScopedPath(root, normalizedParent, normalized);
    }

    private static bool TryBuildRelativePathFromParentSuffix(
        string sourcePath,
        string parentPath,
        out string relativePath)
    {
        relativePath = "";
        var parentParts = TrimJsonRootPrefix(parentPath)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 1; index < parentParts.Length; index++)
        {
            var parentSuffix = string.Join('.', parentParts.Skip(index));
            if (TryBuildRelativePath(sourcePath, parentSuffix, out relativePath)
                && !string.IsNullOrWhiteSpace(relativePath))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryInferObjectContainerPath(
        JToken? root,
        string? sourcePath,
        out string containerPath,
        out string relativePath)
    {
        containerPath = "";
        relativePath = "";

        var normalized = NormalizeArrayPath(sourcePath);
        if (root == null
            || string.IsNullOrWhiteSpace(normalized)
            || HasArrayWildcard(normalized)
            || IsAbsoluteJsonPath(normalized))
        {
            return false;
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        for (var i = parts.Length - 1; i > 0; i--)
        {
            var candidatePath = string.Join('.', parts.Take(i));
            var candidateRelativePath = string.Join('.', parts.Skip(i));
            if (ResolveSubCardContainer(root, candidatePath) is JObject)
            {
                containerPath = candidatePath;
                relativePath = candidateRelativePath;
                return true;
            }
        }

        return false;
    }

    public static JToken? ResolveSubCardContainer(JToken root, string? path)
    {
        var normalizedPath = NormalizeArrayContainerPath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        if (normalizedPath == RootContainerPath || normalizedPath.Equals(MainRecordContainerPath, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        if (root is JArray array
            && array.Count > 0
            && !normalizedPath.StartsWith("[", StringComparison.Ordinal)
            && !IsAbsoluteJsonPath(normalizedPath))
        {
            return HasArrayWildcard(normalizedPath)
                ? ResolveFirstToken(array[0], normalizedPath) ?? ResolveFirstToken(root, normalizedPath)
                : SafeSelectToken(array[0], normalizedPath) ?? SafeSelectToken(root, normalizedPath);
        }

        if (HasArrayWildcard(normalizedPath))
        {
            return ResolveFirstToken(root, normalizedPath);
        }

        return SafeSelectToken(root, normalizedPath);
    }

    public static bool IsSupportedSubCardContainer(JToken? token) =>
        token is JArray or JObject;

    public static List<JToken> ResolveSubCardContexts(JToken root, string? path)
    {
        var container = ResolveSubCardContainer(root, path);
        return container switch
        {
            JArray array when array.Count > 0 => array.Children().ToList(),
            JObject obj => [obj],
            _ => []
        };
    }

    public static JToken? ResolveFirstSubCardContext(JToken root, string? path)
    {
        var container = ResolveSubCardContainer(root, path);
        return container switch
        {
            JArray array when array.Count > 0 => array[0],
            JObject obj => obj,
            _ => null
        };
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

    public static List<JToken> ResolveTokens(JToken root, string? path)
    {
        var normalizedPath = NormalizeArrayPath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return [];
        }

        return ResolveTokensCore(root, normalizedPath);
    }

    public static JToken? ResolveFirstToken(JToken root, string? path) =>
        ResolveTokens(root, path).FirstOrDefault();

    private static List<JToken> ResolveTokensCore(JToken current, string path)
    {
        var wildcardIndex = path.IndexOf("[]", StringComparison.Ordinal);
        if (wildcardIndex < 0)
        {
            var token = SafeSelectToken(current, path);
            return token == null ? [] : [token];
        }

        var arrayPath = path[..wildcardIndex].TrimEnd('.');
        var remainder = path[(wildcardIndex + 2)..].TrimStart('.');
        var arrayToken = string.IsNullOrWhiteSpace(arrayPath)
            ? current
            : SafeSelectToken(current, arrayPath);
        if (arrayToken is not JArray array)
        {
            return [];
        }

        var results = new List<JToken>();
        foreach (var item in array)
        {
            if (string.IsNullOrWhiteSpace(remainder))
            {
                results.Add(item);
                continue;
            }

            results.AddRange(ResolveTokensCore(item, remainder));
        }

        return results;
    }
}
