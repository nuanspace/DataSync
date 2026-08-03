namespace DataSync.LHYY.V2.Models.Enums;

/// <summary>
/// 病案号与住院次数的组合格式
/// </summary>
public enum CombinedVisitIdentityFormat : short
{
    None = 0,
    MrnUnderscoreVisitNo = 1,
    MrnVisitNo = 2
}
