using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 接入项目范围过滤扩展
/// </summary>
public static class IntegrationProjectScopeExtensions
{
    public static IQueryable<T> WhereInProjectOrGlobal<T>(this IQueryable<T> query, string? integrationProjectCode)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(integrationProjectCode))
            return query.Where(item => EF.Property<string?>(item, "IntegrationProjectCode") == null);

        return query.Where(item =>
            EF.Property<string?>(item, "IntegrationProjectCode") == integrationProjectCode ||
            EF.Property<string?>(item, "IntegrationProjectCode") == null);
    }

    public static IQueryable<T> WhereInProjectOnly<T>(this IQueryable<T> query, string? integrationProjectCode)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(integrationProjectCode))
            return query.Where(item => EF.Property<string?>(item, "IntegrationProjectCode") == null);

        return query.Where(item => EF.Property<string?>(item, "IntegrationProjectCode") == integrationProjectCode);
    }
}
