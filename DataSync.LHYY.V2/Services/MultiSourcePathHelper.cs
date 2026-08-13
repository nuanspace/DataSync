namespace DataSync.LHYY.V2.Services;

public static class MultiSourcePathHelper
{
    public const string ValueSeparator = "；";

    public static List<string> Split(string? sourcePath) =>
        (sourcePath ?? "")
        .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static string JoinPaths(IEnumerable<string?> paths) =>
        string.Join(Environment.NewLine, Normalize(paths));

    public static string? JoinValues(IEnumerable<string?> values)
    {
        var normalized = Normalize(values);
        return normalized.Count == 0 ? null : string.Join(ValueSeparator, normalized);
    }

    public static bool IsMultiple(string? sourcePath) => Split(sourcePath).Count > 1;

    public static bool HasPathSeparator(string? sourcePath) =>
        sourcePath?.Contains('\n', StringComparison.Ordinal) == true;

    public static string GetSubCardScope(string sourcePath)
    {
        if (MessageJsonHelper.IsMainRecordScopedPath(sourcePath))
        {
            return "main";
        }

        if (SubCardPathHelper.IsParentRecordScopedPath(sourcePath))
        {
            return "parent";
        }

        return SubCardPathHelper.IsAbsoluteJsonPath(sourcePath) ? "root" : "row";
    }

    private static List<string> Normalize(IEnumerable<string?> values) =>
        values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
