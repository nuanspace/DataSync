using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Models.Html;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 将 Base64 医疗文书确定性地转换为章节和字段 JSON，不调用外部模型。
/// </summary>
public sealed partial class HtmlTextExtractionService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    static HtmlTextExtractionService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public HtmlExtractionResult Extract(
        string base64,
        EsbHtmlProfile profile,
        IReadOnlyList<HtmlExtractionRule> rules)
    {
        var bytes = DecodeBase64(base64, profile.MaxInputBytes);
        var (text, encodingName) = DecodeText(bytes);
        var normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            throw new InvalidDataException("HTML 文本内容为空");

        var headings = HtmlProfileService.ParseSectionHeadings(profile.SectionHeadings);
        var sections = SplitSections(normalizedText, headings);
        var result = new HtmlExtractionResult
        {
            CleanedText = normalizedText,
            TextLength = normalizedText.Length,
            EncodingName = encodingName
        };

        if (profile.PreserveSections)
        {
            foreach (var section in sections)
                result.Sections[section.Key] = section.Value;
        }

        foreach (var rule in rules.OrderBy(rule => rule.SortOrder))
        {
            var value = ExtractRuleValue(normalizedText, sections, rule, rules);
            result.ExtractedFields[rule.FieldCode.Trim()] = value;
            if (rule.IsRequired && string.IsNullOrWhiteSpace(value))
                result.MissingRequiredFields.Add(rule.FieldCode.Trim());
        }

        return result;
    }

    internal static byte[] DecodeBase64(string base64, long maxInputBytes)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidDataException("Base64 内容为空");
        if (maxInputBytes <= 0)
            throw new InvalidDataException("最大解码字节数配置无效");

        var encodedLength = 0L;
        foreach (var character in base64)
        {
            if (!char.IsWhiteSpace(character))
                encodedLength++;
        }

        var estimatedBytes = (encodedLength / 4 + (encodedLength % 4 == 0 ? 0 : 1)) * 3;
        if (estimatedBytes > maxInputBytes + 2)
            throw new InvalidDataException($"Base64 解码内容超过限制（最大 {maxInputBytes} 字节）");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("FILE_CONTENT 不是合法的 Base64 内容");
        }

        if (bytes.LongLength > maxInputBytes)
            throw new InvalidDataException($"Base64 解码内容超过限制（最大 {maxInputBytes} 字节）");
        return bytes;
    }

    internal static (string Text, string EncodingName) DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            return (new UTF8Encoding(false, true).GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length)), "UTF-8");

        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            try
            {
                var gb18030 = Encoding.GetEncoding(
                    "GB18030",
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                return (gb18030.GetString(bytes), "GB18030");
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("文本既不是有效 UTF-8，也不是有效 GB18030");
            }
        }
    }

    internal static string NormalizeText(string value)
    {
        var text = value.Replace("\0", "", StringComparison.Ordinal);
        for (var index = 0; index < 3; index++)
        {
            var decoded = WebUtility.HtmlDecode(text);
            if (string.Equals(decoded, text, StringComparison.Ordinal))
                break;
            text = decoded;
        }

        text = BreakTagRegex().Replace(text, "\n");
        text = BlockEndTagRegex().Replace(text, "\n");
        text = OtherTagRegex().Replace(text, "");
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\t", "    ", StringComparison.Ordinal)
            .Replace('\u00A0', ' ')
            .Replace('\u3000', ' ');
        text = ControlCharacterRegex().Replace(text, "");
        text = NormalizeLineIndentation(text);
        text = ExcessiveBlankLineRegex().Replace(text, "\n\n");
        return text.Trim('\n');
    }

    private static string NormalizeLineIndentation(string text)
    {
        var lines = text.Split('\n')
            .Select(line => line.TrimEnd(' ', '\t'))
            .ToArray();
        var nonEmptyLines = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (nonEmptyLines.Count == 0)
            return string.Empty;

        var commonIndent = nonEmptyLines.Min(CountLeadingSpaces);
        if (commonIndent == 0)
            return string.Join('\n', lines);

        return string.Join('\n', lines.Select(line => string.IsNullOrWhiteSpace(line)
            ? string.Empty
            : line[commonIndent..]));
    }

    private static int CountLeadingSpaces(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == ' ')
            count++;
        return count;
    }

    internal static Dictionary<string, string> SplitSections(string text, IReadOnlyList<string> headings)
    {
        var markers = new List<SectionMarker>();
        foreach (var heading in headings)
        {
            var pattern = BuildFlexibleTextPattern(heading);
            var regex = new Regex(
                $@"(?m)^\s*(?<heading>{pattern})\s*(?:[:：]\s*)?",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            foreach (Match match in regex.Matches(text))
                markers.Add(new SectionMarker(heading, match.Index, match.Index + match.Length));
        }

        markers = markers
            .OrderBy(marker => marker.Start)
            .ThenByDescending(marker => marker.ContentStart - marker.Start)
            .GroupBy(marker => marker.Start)
            .Select(group => group.First())
            .ToList();

        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            var end = index + 1 < markers.Count ? markers[index + 1].Start : text.Length;
            var content = text[marker.ContentStart..end].Trim();
            if (!sections.ContainsKey(marker.Name))
                sections[marker.Name] = content;
        }

        return sections;
    }

    private static string? ExtractRuleValue(
        string fullText,
        IReadOnlyDictionary<string, string> sections,
        HtmlExtractionRule rule,
        IReadOnlyList<HtmlExtractionRule> allRules)
    {
        var source = ResolveSourceText(fullText, sections, rule.SourceSection);
        if (source == null)
            return null;

        return rule.ExtractionType switch
        {
            HtmlExtractionType.Section => NormalizeValue(source),
            HtmlExtractionType.LabelValue => ExtractLabelValue(source, rule.SourceLabel!, allRules),
            HtmlExtractionType.VitalSign => ExtractVitalSign(source, rule.SourceLabel!, allRules),
            HtmlExtractionType.DiagnosisList => ExtractDiagnosisList(source, rule.SourceLabel),
            HtmlExtractionType.WhitespaceTable => ExtractWhitespaceTable(source, rule.SourceLabel!, allRules),
            HtmlExtractionType.Regex => ExtractRegex(source, rule.Pattern!),
            _ => null
        };
    }

    private static string? ResolveSourceText(
        string fullText,
        IReadOnlyDictionary<string, string> sections,
        string? sourceSection)
    {
        if (string.IsNullOrWhiteSpace(sourceSection))
            return fullText;
        return sections.TryGetValue(sourceSection.Trim(), out var value) ? value : null;
    }

    private static string? ExtractLabelValue(
        string source,
        string label,
        IReadOnlyList<HtmlExtractionRule> allRules)
    {
        var labelPattern = BuildFlexibleTextPattern(label);
        var otherLabels = allRules
            .Select(rule => rule.SourceLabel?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, label, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(value => BuildFlexibleTextPattern(value!))
            .ToList();
        var nextLabelPattern = otherLabels.Count == 0
            ? @"[^\s:：]{1,20}[^\S\r\n]*[:：]"
            : $"(?:{string.Join("|", otherLabels)})[^\\S\\r\\n]*[:：]";
        var regex = new Regex(
            $@"(?m)(?:^|\n|[^\S\r\n]{{2,}}){labelPattern}[^\S\r\n]*[:：][^\S\r\n]*(?<value>.*?)(?=[^\S\r\n]{{2,}}{nextLabelPattern}|\n|$)",
            RegexOptions.CultureInvariant,
            RegexTimeout);
        var match = regex.Match(source);
        return match.Success ? NormalizeValue(match.Groups["value"].Value) : null;
    }

    private static string? ExtractDiagnosisList(string source, string? label)
    {
        var text = string.IsNullOrWhiteSpace(label)
            ? source
            : ExtractLabelValue(source, label, []) ?? source;
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => DiagnosisNumberRegex().Replace(line, "").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string? ExtractVitalSign(
        string source,
        string label,
        IReadOnlyList<HtmlExtractionRule> allRules)
    {
        var labelPattern = BuildFlexibleTextPattern(label);
        var nextLabels = allRules
            .Where(rule => rule.ExtractionType == HtmlExtractionType.VitalSign)
            .Select(rule => rule.SourceLabel?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, label, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(value => BuildFlexibleTextPattern(value!))
            .ToList();
        var boundary = nextLabels.Count == 0
            ? @"\n|$"
            : $@"\s+(?:{string.Join("|", nextLabels)})\s*(?:[:：])?|\n|$";
        var regex = new Regex(
            $@"(?m)(?:^|\n|\s+){labelPattern}\s*(?:[:：]\s*)?(?<value>.*?)(?={boundary})",
            RegexOptions.CultureInvariant,
            RegexTimeout);
        var match = regex.Match(source);
        return match.Success ? NormalizeValue(match.Groups["value"].Value) : null;
    }

    private static string? ExtractWhitespaceTable(
        string source,
        string label,
        IReadOnlyList<HtmlExtractionRule> allRules)
    {
        var labeledValue = ExtractLabelValue(source, label, allRules);
        if (labeledValue != null)
            return labeledValue;

        var regex = new Regex(
            $@"(?m)^\s*{BuildFlexibleTextPattern(label)}\s+(?<value>.+?)\s*$",
            RegexOptions.CultureInvariant,
            RegexTimeout);
        var match = regex.Match(source);
        return match.Success ? NormalizeValue(match.Groups["value"].Value) : null;
    }

    private static string? ExtractRegex(string source, string pattern)
    {
        try
        {
            var match = Regex.Match(source, pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, RegexTimeout);
            if (!match.Success)
                return null;
            var value = match.Groups["value"].Success
                ? match.Groups["value"].Value
                : match.Groups.Count > 1
                    ? match.Groups[1].Value
                    : match.Value;
            return NormalizeValue(value);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"HTML 提取正则表达式无效：{ex.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            throw new InvalidDataException("HTML 提取正则表达式执行超时");
        }
    }

    private static string? NormalizeValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string BuildFlexibleTextPattern(string value)
        => string.Join(@"\s*", value.Trim().Where(character => !char.IsWhiteSpace(character)).Select(character => Regex.Escape(character.ToString())));

    private sealed record SectionMarker(string Name, int Start, int ContentStart);

    [GeneratedRegex(@"(?is)<\s*br\s*/?\s*>")]
    private static partial Regex BreakTagRegex();

    [GeneratedRegex(@"(?is)</\s*(?:p|div|li|tr|h[1-6])\s*>")]
    private static partial Regex BlockEndTagRegex();

    [GeneratedRegex(@"(?is)<[^>]+>")]
    private static partial Regex OtherTagRegex();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlCharacterRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveBlankLineRegex();

    [GeneratedRegex(@"^\s*(?:\d+|[一二三四五六七八九十]+)[.、）)]\s*")]
    private static partial Regex DiagnosisNumberRegex();
}
