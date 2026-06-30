namespace DataSync.LHYY.V2.Models.Dto;

public class DictTemplateSummary
{
    public int Id { get; set; }
    public string TemplateCode { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string Category { get; set; } = "";
    public string? DefaultDictCode { get; set; }
    public string DefaultMatchMode { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
}

public sealed class DictTemplateDetail : DictTemplateSummary
{
    public List<DictTemplateItemDto> Items { get; set; } = [];
}

public sealed class DictTemplateItemDto
{
    public string SourceValue { get; set; } = "";
    public string TargetValue { get; set; } = "";
    public int SortOrder { get; set; }
    public string? Description { get; set; }
}

public sealed class DictTemplateApplyResult
{
    public string TemplateCode { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string? DefaultDictCode { get; set; }
    public string DefaultMatchMode { get; set; } = "";
    public string ApplyMode { get; set; } = DictTemplateApplyModes.Replace;
    public List<DictTemplateItemDto> Items { get; set; } = [];
}

public sealed class DictCreateDialogResult
{
    public string DictCode { get; set; } = "";
    public string? RecommendedMatchMode { get; set; }
}

public static class DictTemplateApplyModes
{
    public const string Replace = "replace";
    public const string AppendMissing = "append_missing";
    public const string MergeOverwrite = "merge_overwrite";
}
