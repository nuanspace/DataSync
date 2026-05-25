using DataSync.LHYY.V2.Models.Entities;

namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// License 基本信息
/// </summary>
public class LicenseInfo
{
    public string Code { get; set; } = "";
    public string ProjectId { get; set; } = "";
}

/// <summary>
/// 医院基本信息
/// </summary>
public class HospitalInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int ProjectCount { get; set; }
    public int EventTypeCount { get; set; }
}

/// <summary>
/// 产品项目基本信息
/// </summary>
public class ProductProjectInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string LicenseCodes { get; set; } = "";
    public int FormSetCount { get; set; }
    public int EventTypeCount { get; set; }
}

/// <summary>
/// 事件类型基本信息
/// </summary>
public class ProductEventTypeInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public Guid FormSetId { get; set; }
    public string FormSetName { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string HospitalName { get; set; } = "";
}

/// <summary>
/// FormSet 概要信息
/// </summary>
public class FormSetInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string HospitalId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string EventTypes { get; set; } = "";
}

/// <summary>
/// 问题信息
/// </summary>
public class QuestionInfo
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string? LabelText { get; set; }
    public string? PromptText { get; set; }
    public string? PrefixText { get; set; }
    public string? SuffixText { get; set; }
    public string? DimensionText { get; set; }
    public string? TableName { get; set; }
    public string? ColumnName { get; set; }
    public string? DataType { get; set; }
    public Guid CardGuid { get; set; }
    public Guid? FormId { get; set; }
    public string? FormName { get; set; }
    public int SortIndex { get; set; }
    public Guid? PreUid { get; set; }
    public string? SelectInfo { get; set; }
    public List<string> Options { get; set; } = [];
}

/// <summary>
/// 表单信息（数据库原始数据）
/// </summary>
public class FormInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int SortIndex { get; set; }
}

/// <summary>
/// 卡片信息（数据库原始数据）
/// </summary>
public class CardInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public string FormName { get; set; } = "";
    public string CardType { get; set; } = "default";
    public Guid? PreUid { get; set; }
}

/// <summary>
/// 卡片树节点
/// </summary>
public class CardNode
{
    public Guid CardId { get; set; }
    public string Name { get; set; } = "";
    public string CardType { get; set; } = "default";
    public Guid? PreUid { get; set; }
    public bool IsExpanded { get; set; } = true;
    public List<CardNode> SubCards { get; set; } = [];
    public List<QuestionInfo> Questions { get; set; } = [];
}

/// <summary>
/// Form/Card 树中的混合子节点（Card / Question）
/// </summary>
public class FormTreeChildNode
{
    public CardNode? Card { get; set; }
    public QuestionInfo? Question { get; set; }
}

/// <summary>
/// Form 树节点
/// </summary>
public class FormNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int SortIndex { get; set; }
    public bool IsExpanded { get; set; } = true;
    public int QuestionCount { get; set; }
    public List<CardNode> Cards { get; set; } = [];
    public List<QuestionInfo> OrphanQuestions { get; set; } = [];
}

/// <summary>
/// Card 级映射对话框中的 Question 编辑行
/// </summary>
public class CardMappingRow
{
    public string QuestionId { get; set; } = "";
    public string QuestionTitle { get; set; } = "";
    public string DataType { get; set; } = "";
    public string? SourcePath { get; set; }
    public string? DictCode { get; set; }
    public string DictMatchMode { get; set; } = EsbFieldMapping.DefaultDictMatchMode;
    public string? DefaultValue { get; set; }
    public string? ValueExpression { get; set; }
    public bool IsRequired { get; set; }
    public List<EsbFilterRule> FilterRules { get; set; } = [];
    public bool IsFilterExpanded { get; set; }
}

/// <summary>
/// 事件类型 Tab 数据
/// </summary>
public class EventTypeTabData
{
    public string EventTypeName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<EsbInterfaceConfig> Interfaces { get; set; } = [];
    public HashSet<string> TranCodes { get; set; } = [];
    public string? EffectiveLicenseCode { get; set; }
    public Guid? FormSetId { get; set; }
    public List<FormNode> FormNodes { get; set; } = [];
    public string? SelectedFormId { get; set; }
    public string? SearchText { get; set; }
}
