namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// JSON 字段中文名称生成候选。
/// </summary>
public sealed class JsonFieldTranslationCandidate
{
    public string Path { get; set; } = "";

    public string FieldName { get; set; } = "";

    public string NodeType { get; set; } = "Unknown";

    public string? SampleValue { get; set; }
}
