using System.Text.RegularExpressions;

namespace DataSync.LHYY.V2.Services;

public static class DictExpressionHelper
{
    public const string MatchModeAll = "all";
    public const string MatchModeAny = "any";
    private static readonly Regex AndRegex = new(@"\s+and\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OrRegex = new(@"\s+or\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string NormalizeMatchMode(string? mode) =>
        string.Equals(mode, MatchModeAny, StringComparison.OrdinalIgnoreCase)
            ? MatchModeAny
            : MatchModeAll;

    public static List<string> SplitKeywordsText(string? text) =>
        (text ?? "")
        .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static string? BuildSourceValue(string? keywordsText, string? matchMode)
    {
        return BuildSourceValue(SplitKeywordsText(keywordsText), matchMode);
    }

    public static string? BuildSourceValue(IEnumerable<string?> keywords, string? matchMode)
    {
        var normalizedKeywords = NormalizeKeywords(keywords);
        if (normalizedKeywords.Count == 0)
        {
            return null;
        }

        if (normalizedKeywords.Count == 1)
        {
            return normalizedKeywords[0];
        }

        var separator = NormalizeMatchMode(matchMode) == MatchModeAny ? " || " : " && ";
        return string.Join(separator, normalizedKeywords);
    }

    public static List<string> NormalizeKeywords(IEnumerable<string?> keywords) =>
        keywords
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static List<string?> ParseSourceValueKeywords(string? sourceValue)
    {
        var (_, keywordsText) = ParseSourceValue(sourceValue);
        var keywords = SplitKeywordsText(keywordsText);
        if (keywords.Count == 0)
        {
            return [null];
        }

        return keywords.Select(v => (string?)v).ToList();
    }

    public static (string MatchMode, string KeywordsText) ParseSourceValue(string? sourceValue)
    {
        var value = sourceValue?.Trim() ?? "";
        if (value.Length == 0)
        {
            return (MatchModeAll, "");
        }

        var expression = ParseExpressionParts(value);
        if (expression == null)
        {
            return (MatchModeAll, value);
        }

        return (expression.Value.MatchMode, string.Join(Environment.NewLine, expression.Value.Keywords));
    }

    public static bool TryMatch(string source, string expression, out bool matched)
    {
        matched = false;
        var expressionParts = ParseExpressionParts(expression);
        if (expressionParts == null)
        {
            return false;
        }

        matched = expressionParts.Value.MatchMode == MatchModeAny
            ? expressionParts.Value.Keywords.Any(v => source.Contains(v, StringComparison.OrdinalIgnoreCase))
            : expressionParts.Value.Keywords.All(v => source.Contains(v, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private static ExpressionParts? ParseExpressionParts(string expression)
    {
        var hasAnd = expression.Contains("&&", StringComparison.Ordinal) || AndRegex.IsMatch(expression);
        var hasOr = expression.Contains("||", StringComparison.Ordinal) || OrRegex.IsMatch(expression);
        if (hasAnd == hasOr)
        {
            return null;
        }

        var matchMode = hasOr ? MatchModeAny : MatchModeAll;
        var keywords = SplitExpressionParts(expression, hasOr ? "||" : "&&", hasOr ? OrRegex : AndRegex);
        return keywords.Count < 2 ? null : new ExpressionParts(matchMode, keywords);
    }

    private static List<string> SplitExpressionParts(string expression, string symbolOperator, Regex wordOperator)
    {
        var normalized = wordOperator.Replace(expression.Replace(symbolOperator, "\n"), "\n");
        return normalized
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
    }

    private readonly record struct ExpressionParts(string MatchMode, List<string> Keywords);
}
