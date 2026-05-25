namespace DataSync.LHYY.V2.Models.Enums;

/// <summary>
/// JSON 树在对话框中的布局方式
/// </summary>
public enum JsonTreeLayout : byte
{
    /// <summary>折叠按钮（源路径旁展开/收起）</summary>
    Collapse = 0,

    /// <summary>左侧固定面板</summary>
    SidePanel = 1,
}
