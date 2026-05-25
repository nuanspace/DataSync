using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 接入项目上下文服务
/// </summary>
public class IntegrationProjectService
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly IMemoryCache _cache;

    private const string CurrentProjectCodeCacheKey = "CurrentIntegrationProjectCode";
    private const string CurrentProjectCacheKey = "CurrentIntegrationProject";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

    public IntegrationProjectService(IDbContextFactory<DataSyncDbContext> contextFactory, IMemoryCache cache)
    {
        _contextFactory = contextFactory;
        _cache = cache;
    }

    public async Task<string> GetCurrentProjectCodeAsync()
    {
        return await _cache.GetOrCreateAsync(CurrentProjectCodeCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiration;
            await using var db = await _contextFactory.CreateDbContextAsync();
            var code = await db.EsbGlobalConfigs
                .AsNoTracking()
                .Where(c => c.ConfigKey == "DefaultIntegrationProjectCode")
                .Select(c => c.ConfigValue)
                .FirstOrDefaultAsync();

            return string.IsNullOrWhiteSpace(code) ? "DEFAULT" : code!;
        }) ?? "DEFAULT";
    }

    public async Task<EsbIntegrationProject?> GetCurrentProjectAsync()
    {
        return await _cache.GetOrCreateAsync(CurrentProjectCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiration;
            var code = await GetCurrentProjectCodeAsync();
            await using var db = await _contextFactory.CreateDbContextAsync();
            return await db.EsbIntegrationProjects
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProjectCode == code);
        });
    }

    public async Task<List<EsbIntegrationProject>> GetProjectsAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.EsbIntegrationProjects
            .AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.ProjectCode)
            .ToListAsync();
    }

    public async Task SetCurrentProjectCodeAsync(string projectCode)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var config = await db.EsbGlobalConfigs.FirstOrDefaultAsync(c => c.ConfigKey == "DefaultIntegrationProjectCode");
        if (config == null)
        {
            db.EsbGlobalConfigs.Add(new EsbGlobalConfig
            {
                ConfigKey = "DefaultIntegrationProjectCode",
                ConfigValue = projectCode,
                ConfigType = "string",
                Description = "默认接入项目编码"
            });
        }
        else
        {
            config.ConfigValue = projectCode;
        }

        await db.SaveChangesAsync();
        ClearCache();
    }

    public void ClearCache()
    {
        _cache.Remove(CurrentProjectCodeCacheKey);
        _cache.Remove(CurrentProjectCacheKey);
    }
}
