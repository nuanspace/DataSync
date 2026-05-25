namespace DataSync.CYYY.Services;

/// <summary>
/// 推送服务接口
/// </summary>
public interface IPushService
{
    /// <summary>
    /// 推送数据到目标系统
    /// </summary>
    /// <param name="pushTarget">推送目标（API 地址或数据库连接串）</param>
    /// <param name="serverCode">接口编码</param>
    /// <param name="data">要推送的数据</param>
    /// <param name="ct">取消令牌</param>
    Task PushAsync(string pushTarget, string serverCode, List<Dictionary<string, object>> data, CancellationToken ct);
}
