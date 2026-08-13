using System.Globalization;
using System.Text.RegularExpressions;
using DataSync.LHYY.V2.Models.Dto;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 为接口映射工作台生成仅用于展示的 JSON 字段中文名称。
/// </summary>
public sealed partial class JsonFieldTranslationService(LlmService llmService)
{
    private const int MaxCandidateCount = 1000;

    private static readonly IReadOnlyDictionary<string, string> BuiltInGlossary =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DATA"] = "数据",
            ["ROWKEY"] = "行记录键",
            ["HIS_KEY"] = "HIS业务键",
            ["HIS_KEY_SRC"] = "HIS键来源",
            ["HIS_PAT_ID"] = "HIS患者编号",
            ["HIS_VIS_ID"] = "HIS就诊编号",
            ["PATIENT_ID"] = "患者编号",
            ["PATIENT_SN"] = "患者序号",
            ["PAT_ROWKEY"] = "患者行记录键",
            ["IN_HOSP_SN"] = "住院序号",
            ["INPATIENT_NO"] = "住院号",
            ["VISIT_NO"] = "就诊号",
            ["MRN"] = "病案号",
            ["CREATED_T"] = "创建时间",
            ["CREATED_AT"] = "创建时间",
            ["UPDATED_T"] = "更新时间",
            ["UPDATED_AT"] = "更新时间",
            ["RECORD_TIME"] = "记录时间",
            ["DELETE_FLAG"] = "删除标志",
            ["ICU_FLAG"] = "ICU标志",
            ["ICU_DEVICE"] = "ICU设备",
            ["SOURCE_MESSAGE_ID"] = "上游消息号",
            ["SERVER_CODE"] = "接口编码",
            ["TRAN_CODE"] = "事件代码",
        };

    private static readonly string[] SensitiveFieldHints =
    [
        "name", "姓名", "patient", "pat_", "mrn", "id", "key", "no", "sn",
        "phone", "mobile", "tel", "card", "certificate", "address", "birth",
        "住院", "就诊", "病案", "患者", "证件", "电话", "地址", "姓名"
    ];

    private static readonly string[] SafeSampleFieldHints =
    [
        "flag", "status", "type", "way", "method", "mode", "device", "unit",
        "take_in", "take_out", "标志", "状态", "类型", "方式", "设备", "单位"
    ];

    /// <summary>
    /// 根据内置公共词典生成当前 JSON 的中文名称。
    /// </summary>
    public static Dictionary<string, string> BuildBuiltInTranslations(JToken root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in BuildCandidates(root))
        {
            if (BuiltInGlossary.TryGetValue(candidate.FieldName, out var chineseName))
            {
                result[candidate.Path] = chineseName;
            }
        }

        return result;
    }

    /// <summary>
    /// 使用本地 LLM 补全当前页面尚未命名的字段，不持久化结果。
    /// </summary>
    public async Task<Dictionary<string, string>> CompleteWithLlmAsync(
        JToken root,
        IReadOnlyDictionary<string, string> currentTranslations,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(currentTranslations, StringComparer.OrdinalIgnoreCase);
        var candidates = BuildCandidates(root)
            .Where(candidate => !result.ContainsKey(candidate.Path))
            .ToList();

        if (candidates.Count == 0)
        {
            return result;
        }

        var suggestions = await llmService.SuggestChineseFieldNamesAsync(candidates, cancellationToken);
        foreach (var (path, chineseName) in suggestions)
        {
            if (!string.IsNullOrWhiteSpace(chineseName))
            {
                result[path] = chineseName.Trim();
            }
        }

        return result;
    }

    /// <summary>
    /// 把数组实际索引统一为 []，保证同一字段在不同样例项中共享展示名称。
    /// </summary>
    public static string NormalizePath(string path) =>
        ArrayIndexRegex().Replace(path, "[]");

    internal static IReadOnlyList<JsonFieldTranslationCandidate> BuildCandidates(JToken root)
    {
        var result = new Dictionary<string, JsonFieldTranslationCandidate>(StringComparer.OrdinalIgnoreCase);
        CollectCandidates(root, "", result);
        return result.Values.ToList();
    }

    private static void CollectCandidates(
        JToken token,
        string parentPath,
        Dictionary<string, JsonFieldTranslationCandidate> result)
    {
        if (result.Count >= MaxCandidateCount)
        {
            return;
        }

        if (token is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                if (result.Count >= MaxCandidateCount)
                {
                    break;
                }

                var rawPath = string.IsNullOrWhiteSpace(parentPath)
                    ? property.Name
                    : $"{parentPath}.{property.Name}";
                var normalizedPath = NormalizePath(rawPath);

                if (!result.TryGetValue(normalizedPath, out var candidate))
                {
                    candidate = new JsonFieldTranslationCandidate
                    {
                        Path = normalizedPath,
                        FieldName = property.Name,
                        NodeType = GetNodeType(property.Value),
                        SampleValue = SanitizeSampleValue(property.Name, rawPath, property.Value)
                    };
                    result[normalizedPath] = candidate;
                }
                else if (string.IsNullOrWhiteSpace(candidate.SampleValue))
                {
                    candidate.SampleValue = SanitizeSampleValue(property.Name, rawPath, property.Value);
                }

                CollectCandidates(property.Value, rawPath, result);
            }
        }
        else if (token is JArray array)
        {
            for (var index = 0; index < array.Count && result.Count < MaxCandidateCount; index++)
            {
                CollectCandidates(array[index], $"{parentPath}[{index}]", result);
            }
        }
    }

    internal static string? SanitizeSampleValue(string fieldName, string path, JToken value)
    {
        if (value.Type == JTokenType.Null)
        {
            return "<空值>";
        }

        if (value is JObject)
        {
            return "<对象>";
        }

        if (value is JArray array)
        {
            return $"<数组，共 {array.Count} 项>";
        }

        if (value.Type is JTokenType.Integer or JTokenType.Float)
        {
            return "<数值>";
        }

        if (value.Type == JTokenType.Boolean)
        {
            return value.Value<bool>() ? "true" : "false";
        }

        var text = value.Value<string>()?.Trim() ?? value.ToString().Trim();
        if (text.Length == 0)
        {
            return "<空字符串>";
        }

        var identity = $"{fieldName} {path}";
        if (SensitiveFieldHints.Any(hint => identity.Contains(hint, StringComparison.OrdinalIgnoreCase)))
        {
            return "<敏感示例已隐藏>";
        }

        if (Guid.TryParse(text, out _))
        {
            return "<UUID>";
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))
        {
            return "<日期时间>";
        }

        if (text.Count(char.IsDigit) >= 4)
        {
            return $"<标识或数值文本，长度 {text.Length}>";
        }

        if (text.Length > 24)
        {
            return $"<文本，长度 {text.Length}>";
        }

        return SafeSampleFieldHints.Any(hint => identity.Contains(hint, StringComparison.OrdinalIgnoreCase))
            ? text
            : $"<文本示例已隐藏，长度 {text.Length}>";
    }

    private static string GetNodeType(JToken token) => token.Type switch
    {
        JTokenType.Object => "Object",
        JTokenType.Array => "Array",
        JTokenType.String => "String",
        JTokenType.Integer or JTokenType.Float => "Number",
        JTokenType.Boolean => "Boolean",
        JTokenType.Null => "Null",
        _ => "Unknown"
    };

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex ArrayIndexRegex();
}
