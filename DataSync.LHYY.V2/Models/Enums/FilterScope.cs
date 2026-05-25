namespace DataSync.LHYY.V2.Models.Enums;

/// <summary>
/// 过滤作用范围（仅路径含 [] 时生效）
/// </summary>
public enum FilterScope : short
{
    /// <summary>
    /// 消息判断：数组中存在任一元素满足条件 → 消息通过
    /// </summary>
    MessageCheck = 0,

    /// <summary>
    /// 行过滤：只保留满足条件的数组元素
    /// </summary>
    RowFilter = 1
}
