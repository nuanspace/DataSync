namespace DataSync.CYYY.Models;

/// <summary>
/// 数据湖连接配置（数据库存储，全局唯一一条记录）
/// </summary>
public class DataLakeConfig
{
    public int Id { get; set; }
    public string BaseUrl { get; set; } = "";
    public string TokenEndpoint { get; set; } = "/auth/oauth/token";
    public string QueryEndpoint { get; set; } = "/api/jhids4s/common/server/dataQuery";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string SysCode { get; set; } = "client-app";
    public int PageSize { get; set; } = 100;
    public int MaxResultSize { get; set; } = 10000;
    public int RequestIntervalMilliseconds { get; set; } = 200;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool DebugLogEnabled { get; set; } = true;
}
