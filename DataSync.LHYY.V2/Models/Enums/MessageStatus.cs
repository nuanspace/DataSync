namespace DataSync.LHYY.V2.Models.Enums;

/// <summary>
/// 消息处理状态
/// </summary>
public enum MessageStatus : short
{
    Pending = 0,
    Processing = 1,
    Success = 2,
    Failed = 3,
    Filtered = 4,
    Unmatched = 5,
    PartialSuccess = 6,
    WaitingIdentity = 7
}

public static class MessageStatusExtensions
{
    public static string ToDisplayText(this MessageStatus status) => status switch
    {
        MessageStatus.Pending => "待处理",
        MessageStatus.Processing => "处理中",
        MessageStatus.Success => "成功",
        MessageStatus.Failed => "失败",
        MessageStatus.Filtered => "已过滤",
        MessageStatus.Unmatched => "未匹配",
        MessageStatus.PartialSuccess => "部分成功",
        MessageStatus.WaitingIdentity => "待身份绑定",
        _ => status.ToString()
    };
}
