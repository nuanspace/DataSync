using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Models.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Threading;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 配置查询服务，带内存缓存
/// </summary>
public class ConfigService
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly IntegrationProjectService _integrationProjectService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ConfigService> _logger;

    private const string InterfaceConfigCacheKey = "InterfaceConfigs";
    private const string GlobalConfigCacheKey = "GlobalConfigs";
    private const string ProjectConfigCacheKey = "ProjectConfigs";
    private const string InterfaceMatchRuleCacheKey = "InterfaceMatchRules";
    private const string IdempotentKeyPartCacheKey = "IdempotentKeyParts";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);
    private static long _cacheVersion;

    public ConfigService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IntegrationProjectService integrationProjectService,
        IMemoryCache cache,
        ILogger<ConfigService> logger)
    {
        _contextFactory = contextFactory;
        _integrationProjectService = integrationProjectService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string> GetCurrentIntegrationProjectCodeAsync()
        => await _integrationProjectService.GetCurrentProjectCodeAsync();

    public async Task<EsbInterfaceConfig?> GetInterfaceConfigAsync(
        string tranCode,
        string? integrationProjectCode = null,
        bool includeGlobalFallback = true)
    {
        var configs = await GetAllInterfaceConfigsAsync(integrationProjectCode, includeGlobalFallback);
        return configs.GetValueOrDefault(tranCode);
    }

    public async Task<bool> IsTranCodeEnabledAsync(
        string tranCode,
        string? integrationProjectCode = null,
        bool includeGlobalFallback = true)
    {
        var config = await GetInterfaceConfigAsync(tranCode, integrationProjectCode, includeGlobalFallback);
        return config is { IsEnabled: true };
    }

    public async Task<List<EsbInterfaceConfig>> GetEnabledInterfaceConfigsAsync(
        string? integrationProjectCode = null,
        bool includeGlobalFallback = true)
    {
        var configs = await GetAllInterfaceConfigsAsync(integrationProjectCode, includeGlobalFallback);
        return configs.Values.Where(c => c.IsEnabled).OrderBy(c => c.SortOrder).ToList();
    }

    public async Task<List<EsbInterfaceMatchRule>> GetInterfaceMatchRulesAsync(
        string? integrationProjectCode = null,
        bool includeGlobalFallback = true)
    {
        var currentProjectCode = string.IsNullOrWhiteSpace(integrationProjectCode)
            ? await _integrationProjectService.GetCurrentProjectCodeAsync()
            : integrationProjectCode!;

        return await _cache.GetOrCreateAsync(GetScopedCacheKey(InterfaceMatchRuleCacheKey, currentProjectCode, includeGlobalFallback), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiration;
            await using var db = await _contextFactory.CreateDbContextAsync();
            var query = db.EsbInterfaceMatchRules
                .AsNoTracking()
                .Where(r => r.IsEnabled)
                .AsQueryable();

            query = includeGlobalFallback
                ? query.WhereInProjectOrGlobal(currentProjectCode)
                : query.WhereInProjectOnly(currentProjectCode);

            return await query
                .OrderBy(r => r.TranCode)
                .ThenBy(r => r.MatchGroup)
                .ThenBy(r => r.SortOrder)
                .ToListAsync();
        }) ?? [];
    }

    public async Task<List<EsbIdempotentKeyPart>> GetIdempotentKeyPartsAsync(
        string tranCode,
        string? integrationProjectCode = null,
        bool includeGlobalFallback = true)
    {
        var currentProjectCode = string.IsNullOrWhiteSpace(integrationProjectCode)
            ? await _integrationProjectService.GetCurrentProjectCodeAsync()
            : integrationProjectCode!;

        var all = await _cache.GetOrCreateAsync(GetScopedCacheKey(IdempotentKeyPartCacheKey, currentProjectCode, includeGlobalFallback), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiration;
            await using var db = await _contextFactory.CreateDbContextAsync();
            var query = db.EsbIdempotentKeyParts
                .AsNoTracking()
                .Where(p => p.IsEnabled)
                .AsQueryable();

            query = includeGlobalFallback
                ? query.WhereInProjectOrGlobal(currentProjectCode)
                : query.WhereInProjectOnly(currentProjectCode);

            return await query
                .OrderBy(p => p.TranCode)
                .ThenBy(p => p.SortOrder)
                .ToListAsync();
        }) ?? [];

        var scoped = all
            .Where(p => string.Equals(p.TranCode, tranCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var projectScoped = scoped
            .Where(p => string.Equals(p.IntegrationProjectCode, currentProjectCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!includeGlobalFallback)
            return projectScoped;

        return projectScoped.Count > 0
            ? projectScoped
            : scoped.Where(p => string.IsNullOrWhiteSpace(p.IntegrationProjectCode)).ToList();
    }

    public async Task<string?> GetDefaultLicenseCodeAsync(string? integrationProjectCode = null)
        => await GetEffectiveConfigValueAsync("DefaultLicenseCode", integrationProjectCode);

    public async Task<string?> GetDefaultHospitalIdAsync(string? integrationProjectCode = null)
        => await GetEffectiveConfigValueAsync("DefaultHospitalId", integrationProjectCode);

    public async Task<string?> GetDefaultProjectIdAsync(string? integrationProjectCode = null)
        => await GetEffectiveConfigValueAsync("DefaultProjectId", integrationProjectCode);

    public async Task<string?> GetProjectConfigValueAsync(string key, string? integrationProjectCode = null)
    {
        var configs = await GetAllProjectConfigsAsync(integrationProjectCode);
        return configs.GetValueOrDefault(key);
    }

    public async Task<string?> GetEffectiveConfigValueAsync(string key, string? integrationProjectCode = null)
    {
        var projectValue = await GetProjectConfigValueAsync(key, integrationProjectCode);
        return string.IsNullOrWhiteSpace(projectValue)
            ? await GetGlobalConfigValueAsync(key)
            : projectValue;
    }

    public async Task<string?> GetGlobalConfigValueAsync(string key)
    {
        var configs = await GetAllGlobalConfigsAsync();
        return configs.GetValueOrDefault(key);
    }

    public async Task<string> GetGlobalConfigValueAsync(string key, string defaultValue)
    {
        return await GetGlobalConfigValueAsync(key) ?? defaultValue;
    }

    public async Task<LlmOptions> GetLlmOptionsAsync(LlmOptions fallback)
    {
        var configs = await GetAllGlobalConfigsAsync();
        var activeProvider = GetConfigValue(configs, LlmOptions.ActiveProviderKey, LlmOptions.OnlineProvider);
        if (string.Equals(activeProvider, LlmOptions.LocalProvider, StringComparison.OrdinalIgnoreCase))
        {
            var localFallback = LlmOptions.CreateLocalDefaults();
            var localTimeoutText = GetConfigValue(configs, LlmOptions.LocalTimeoutSecondsKey, localFallback.TimeoutSeconds.ToString());

            return new LlmOptions
            {
                BaseUrl = GetConfigValue(configs, LlmOptions.LocalBaseUrlKey, localFallback.BaseUrl),
                Model = GetConfigValue(configs, LlmOptions.LocalModelKey, localFallback.Model),
                ApiKey = null,
                TimeoutSeconds = int.TryParse(localTimeoutText, out var localTimeoutSeconds) && localTimeoutSeconds > 0
                    ? localTimeoutSeconds
                    : localFallback.TimeoutSeconds
            };
        }

        var timeoutText = GetConfigValue(configs, LlmOptions.TimeoutSecondsKey, fallback.TimeoutSeconds.ToString());
        return new LlmOptions
        {
            BaseUrl = GetConfigValue(configs, LlmOptions.BaseUrlKey, fallback.BaseUrl),
            Model = GetConfigValue(configs, LlmOptions.ModelKey, fallback.Model),
            ApiKey = GetConfigValue(configs, LlmOptions.ApiKeyKey, fallback.ApiKey),
            TimeoutSeconds = int.TryParse(timeoutText, out var timeoutSeconds) && timeoutSeconds > 0
                ? timeoutSeconds
                : fallback.TimeoutSeconds
        };
    }

    public void ClearCache()
    {
        Interlocked.Increment(ref _cacheVersion);
        _cache.Remove(GlobalConfigCacheKey);
        _integrationProjectService.ClearCache();
        _logger.LogInformation("配置缓存已清除");
    }

    private static string GetConfigValue(Dictionary<string, string> configs, string key, string? defaultValue)
    {
        return configs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue ?? "";
    }

    public static List<string> GetAvailableHandlerNames()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMessageHandler).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();
    }

    public List<string> ValidateInterfaceConfig(EsbInterfaceConfig config)
    {
        var errors = new List<string>();

        var hasEventIntent =
            !string.IsNullOrWhiteSpace(config.EventTypeName) ||
            !string.IsNullOrWhiteSpace(config.EventStartTimeSourcePath) ||
            !string.IsNullOrWhiteSpace(config.VisitNoSourcePath) ||
            !string.IsNullOrWhiteSpace(config.InpatientNoSourcePath) ||
            !string.IsNullOrWhiteSpace(config.CombinedVisitIdentitySourcePath);
        var hasMrnPath = !string.IsNullOrWhiteSpace(config.MrnSourcePath);
        var hasCombinedIdentityPath = !string.IsNullOrWhiteSpace(config.CombinedVisitIdentitySourcePath);
        var hasCombinedIdentityFormat = config.CombinedVisitIdentityFormat is
            CombinedVisitIdentityFormat.MrnUnderscoreVisitNo or
            CombinedVisitIdentityFormat.MrnVisitNo;
        var hasCombinedIdentity = hasCombinedIdentityPath && hasCombinedIdentityFormat;

        if (!Enum.IsDefined(config.CombinedVisitIdentityFormat))
            errors.Add("病案号+住院次数组合格式无效");
        var hasVisitIdentityPath =
            !string.IsNullOrWhiteSpace(config.VisitNoSourcePath) ||
            !string.IsNullOrWhiteSpace(config.InpatientNoSourcePath) ||
            hasCombinedIdentity;

        if (hasCombinedIdentityPath && !hasCombinedIdentityFormat)
            errors.Add("已配置病案号+住院次数路径，请选择组合格式");

        if (!hasCombinedIdentityPath && hasCombinedIdentityFormat)
            errors.Add("已选择病案号+住院次数组合格式，请配置组合标识路径");

        if (config.HandlerType == HandlerType.GenericQuestionWriteBack)
        {
            if (!hasMrnPath && !hasVisitIdentityPath)
                errors.Add("未配置病案号路径、就诊号/住院号路径、住院次数路径或病案号+住院次数组合标识");
        }
        else if (config.HandlerType == HandlerType.Generic && !hasMrnPath && !hasCombinedIdentity)
        {
            errors.Add("未配置病案号路径或病案号+住院次数组合标识");
        }
        else if (config.HandlerType != HandlerType.Generic && !hasMrnPath)
        {
            errors.Add("未配置病案号路径（MrnSourcePath）");
        }

        if (RequiresStandardEventIdentity(config) &&
            hasEventIntent &&
            string.IsNullOrWhiteSpace(config.EventStartTimeSourcePath) &&
            string.IsNullOrWhiteSpace(config.VisitNoSourcePath) &&
            string.IsNullOrWhiteSpace(config.InpatientNoSourcePath) &&
            !hasCombinedIdentity)
        {
            errors.Add("已配置事件处理时，至少需要配置事件开始时间路径、就诊号/住院号路径、住院次数路径或病案号+住院次数组合标识之一");
        }

        if (hasEventIntent &&
            !config.AllowMissingEventTime &&
            string.IsNullOrWhiteSpace(config.EventStartTimeSourcePath) &&
            !(config.HandlerType == HandlerType.GenericQuestionWriteBack && hasVisitIdentityPath))
        {
            errors.Add("未允许缺失事件时间时，需配置事件开始时间路径");
        }

        if (config.AllowMissingEventTime &&
            string.IsNullOrWhiteSpace(config.EventStartTimeSourcePath) &&
            string.IsNullOrWhiteSpace(config.VisitNoSourcePath) &&
            string.IsNullOrWhiteSpace(config.InpatientNoSourcePath) &&
            !hasCombinedIdentity)
        {
            errors.Add("允许缺少事件时间时，至少需要配置就诊号/住院号路径、住院次数路径或病案号+住院次数组合标识");
        }

        if (config.ReceiveMode == ReceiveMode.Direct &&
            config.MissingEventIdentityPolicy == MissingEventIdentityPolicy.Pending)
        {
            errors.Add("直处理模式不支持待身份绑定策略，请改为 Fail 或 DegradeToPatientOnly");
        }

        if (OcrProfileService.IsOcrHandler(config) && config.ReceiveMode != ReceiveMode.PersistAndAsync)
            errors.Add("OCR 接口仅支持入队异步处理");

        return errors;
    }

    private static bool RequiresStandardEventIdentity(EsbInterfaceConfig config)
        => config.HandlerType is HandlerType.Generic or HandlerType.GenericQuestionWriteBack
            || OcrProfileService.IsOcrHandler(config);

    private async Task<Dictionary<string, EsbInterfaceConfig>> GetAllInterfaceConfigsAsync(
        string? integrationProjectCode = null,
        bool includeGlobalFallback = true)
    {
        var currentProjectCode = string.IsNullOrWhiteSpace(integrationProjectCode)
            ? await _integrationProjectService.GetCurrentProjectCodeAsync()
            : integrationProjectCode!;

        return await _cache.GetOrCreateAsync(GetScopedCacheKey(InterfaceConfigCacheKey, currentProjectCode, includeGlobalFallback), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiration;
            await using var db = await _contextFactory.CreateDbContextAsync();
            var query = db.EsbInterfaceConfigs
                .AsNoTracking()
                .AsQueryable();

            query = includeGlobalFallback
                ? query.WhereInProjectOrGlobal(currentProjectCode)
                : query.WhereInProjectOnly(currentProjectCode);

            var list = await query
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.TranCode)
                .ToListAsync();

            var merged = list
                .GroupBy(c => c.TranCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => includeGlobalFallback
                        ? g.OrderByDescending(c => string.Equals(c.IntegrationProjectCode, currentProjectCode, StringComparison.OrdinalIgnoreCase))
                           .ThenBy(c => c.SortOrder)
                           .First()
                        : g.OrderBy(c => c.SortOrder).First(),
                    StringComparer.OrdinalIgnoreCase);

            _logger.LogDebug("加载接口配置缓存，共 {Count} 条", merged.Count);
            return merged;
        }) ?? [];
    }

    private async Task<Dictionary<string, string>> GetAllProjectConfigsAsync(string? integrationProjectCode = null)
    {
        var currentProjectCode = string.IsNullOrWhiteSpace(integrationProjectCode)
            ? await _integrationProjectService.GetCurrentProjectCodeAsync()
            : integrationProjectCode!;

        return await _cache.GetOrCreateAsync(GetScopedCacheKey(ProjectConfigCacheKey, currentProjectCode, false), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiration;
            await using var db = await _contextFactory.CreateDbContextAsync();
            var list = await db.EsbIntegrationProjectConfigs
                .AsNoTracking()
                .Where(c => c.IntegrationProjectCode == currentProjectCode)
                .ToListAsync();

            return list
                .Where(c => !string.IsNullOrWhiteSpace(c.ConfigKey))
                .GroupBy(c => c.ConfigKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .OrderByDescending(c => c.UpdatedAt)
                        .ThenByDescending(c => c.Id)
                        .Select(c => c.ConfigValue ?? "")
                        .FirstOrDefault() ?? "",
                    StringComparer.OrdinalIgnoreCase);
        }) ?? [];
    }

    private static string GetScopedCacheKey(string baseKey, string integrationProjectCode, bool includeGlobalFallback)
    {
        var version = Interlocked.Read(ref _cacheVersion);
        return $"{baseKey}:{version}:{integrationProjectCode}:{includeGlobalFallback}";
    }

    private async Task<Dictionary<string, string>> GetAllGlobalConfigsAsync()
    {
        return await _cache.GetOrCreateAsync(GlobalConfigCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiration;
            await using var db = await _contextFactory.CreateDbContextAsync();
            var list = await db.EsbGlobalConfigs.AsNoTracking().ToListAsync();
            _logger.LogDebug("加载全局配置缓存，共 {Count} 条", list.Count);
            return list
                .Where(c => c.ConfigValue != null)
                .ToDictionary(c => c.ConfigKey, c => c.ConfigValue!);
        }) ?? [];
    }
}
