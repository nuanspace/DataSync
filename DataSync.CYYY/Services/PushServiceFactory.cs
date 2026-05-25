namespace DataSync.CYYY.Services;

/// <summary>
/// 根据 PushType 选择推送实现
/// </summary>
public class PushServiceFactory
{
    private readonly ApiPushService _apiPushService;
    private readonly DatabasePushService _databasePushService;

    public PushServiceFactory(ApiPushService apiPushService, DatabasePushService databasePushService)
    {
        _apiPushService = apiPushService;
        _databasePushService = databasePushService;
    }

    public IPushService GetPushService(string pushType)
    {
        return pushType.ToLower() switch
        {
            "api" => _apiPushService,
            "database" => _databasePushService,
            _ => throw new ArgumentException($"不支持的推送类型: {pushType}")
        };
    }
}
