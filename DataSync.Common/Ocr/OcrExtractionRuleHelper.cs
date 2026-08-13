namespace DataSync.Common.Ocr;

/// <summary>
/// OCR 字段提取规则校验与结果初始化。
/// </summary>
public static class OcrExtractionRuleHelper
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<OcrExtractionRule> rules)
    {
        var errors = new List<string>();
        var fieldCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rules.Count == 0)
            errors.Add("OCR 接口至少需要配置一个提取字段");

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var prefix = $"第 {index + 1} 个提取字段";

            if (string.IsNullOrWhiteSpace(rule.FieldCode))
                errors.Add($"{prefix}未填写输出字段名称");
            else if (!fieldCodes.Add(rule.FieldCode.Trim()))
                errors.Add($"{prefix}的输出字段名称重复：{rule.FieldCode}");

            if (string.IsNullOrWhiteSpace(rule.SourceFieldName))
                errors.Add($"{prefix}未填写 PDF 字段名称");
        }

        foreach (var group in rules
                     .Where(rule => !string.IsNullOrWhiteSpace(GetSourceFieldName(rule)))
                     .GroupBy(GetSourceFieldName, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var headings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in group)
            {
                if (string.IsNullOrWhiteSpace(rule.SectionHeading))
                {
                    errors.Add($"PDF 字段名称“{group.Key}”配置了多次，必须为每个字段填写所属标题");
                    continue;
                }

                if (!headings.Add(rule.SectionHeading.Trim()))
                    errors.Add($"PDF 字段名称“{group.Key}”的所属标题重复：{rule.SectionHeading}");
            }
        }

        return errors;
    }

    public static string GetSourceFieldName(OcrExtractionRule rule)
        => string.IsNullOrWhiteSpace(rule.SourceFieldName)
            ? rule.FieldCode.Trim()
            : rule.SourceFieldName.Trim();

    public static void Apply(OcrDocumentResult result, IReadOnlyList<OcrExtractionRule> rules)
    {
        if (rules.Count == 0)
            return;

        var errors = Validate(rules);
        if (errors.Count > 0)
            throw new InvalidOperationException($"OCR 字段提取配置错误：{string.Join("；", errors)}");

        foreach (var rule in rules)
            result.ExtractedFields[rule.FieldCode.Trim()] = null;
    }
}
