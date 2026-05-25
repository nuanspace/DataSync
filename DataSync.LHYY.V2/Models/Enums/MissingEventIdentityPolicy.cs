namespace DataSync.LHYY.V2.Models.Enums;

/// <summary>
/// 缺少事件定位信息时的处理策略
/// </summary>
public enum MissingEventIdentityPolicy : short
{
    Fail = 0,
    DegradeToPatientOnly = 1,
    Pending = 2
}
