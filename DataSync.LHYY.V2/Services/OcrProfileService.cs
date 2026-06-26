using DataSync.Common.Ocr;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// OCR 配置读取服务。
/// </summary>
public class OcrProfileService
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;

    public OcrProfileService(IDbContextFactory<DataSyncDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<EsbOcrProfile?> GetEnabledProfileAsync(string tranCode, string? integrationProjectCode)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var list = await db.EsbOcrProfiles
            .AsNoTracking()
            .Where(p => p.IsEnabled && p.TranCode == tranCode)
            .OrderByDescending(p => p.IntegrationProjectCode == integrationProjectCode)
            .ThenBy(p => p.Id)
            .ToListAsync();

        return list.FirstOrDefault(p => string.Equals(p.IntegrationProjectCode, integrationProjectCode, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.IntegrationProjectCode));
    }

    public static OcrConversionOptions ToConversionOptions(EsbOcrProfile profile)
    {
        return new OcrConversionOptions
        {
            Language = string.IsNullOrWhiteSpace(profile.Language) ? "chi_sim" : profile.Language,
            Dpi = profile.Dpi <= 0 ? 300 : profile.Dpi,
            PageSegMode = profile.PageSegMode < 0 ? 11 : profile.PageSegMode,
            MaxPages = profile.MaxPages,
            MaxInputBytes = profile.MaxInputBytes,
            TimeoutSeconds = profile.TimeoutSeconds <= 0 ? 120 : profile.TimeoutSeconds,
            KeepWorkFiles = profile.KeepWorkFiles,
            OutputJsonPath = profile.OutputJsonPath,
            AllowedFileRoots = SplitAllowedRoots(profile.AllowedFileRoots)
        };
    }

    private static IReadOnlyList<string> SplitAllowedRoots(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
