namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// 过滤结果
/// </summary>
public class FilterResult
{
    /// <summary>
    /// 是否通过过滤
    /// </summary>
    public bool IsPassed { get; set; }

    /// <summary>
    /// 未通过原因
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// 行过滤结果：key=数组路径，value=通过行索引列表
    /// </summary>
    public Dictionary<string, List<int>> RowFilterResults { get; set; } = [];

    public static FilterResult Passed() => new() { IsPassed = true };

    public static FilterResult Failed(string reason) => new() { IsPassed = false, Reason = reason };
}
