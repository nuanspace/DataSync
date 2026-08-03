namespace DataSync.CYYY.Models;

/// <summary>
/// 动态接口平台连接配置（数据库存储，全局唯一一条记录）。
/// </summary>
public class DynamicApiConfig
{
    public int Id { get; set; }
    public string BaseUrl { get; set; } = "";
    public string TokenEndpoint { get; set; } = "/api/v1/auth/token";
    public string QueryEndpointPrefix { get; set; } = "/api/dynamic/api/inpatient/query";
    public string AppKey { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public int PageSize { get; set; } = 100;
    public int RequestIntervalMilliseconds { get; set; } = 200;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
