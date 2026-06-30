using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Dto;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

public class DictTemplateService
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;

    public DictTemplateService(IDbContextFactory<DataSyncDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<List<DictTemplateSummary>> GetEnabledTemplatesAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.EsbDictTemplates
            .AsNoTracking()
            .Where(t => t.IsEnabled)
            .OrderBy(t => t.Category)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.TemplateName)
            .Select(t => new DictTemplateSummary
            {
                Id = t.Id,
                TemplateCode = t.TemplateCode,
                TemplateName = t.TemplateName,
                Category = t.Category,
                DefaultDictCode = t.DefaultDictCode,
                DefaultMatchMode = t.DefaultMatchMode,
                Description = t.Description,
                SortOrder = t.SortOrder
            })
            .ToListAsync();
    }

    public async Task<DictTemplateDetail?> GetTemplateAsync(int id)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var template = await db.EsbDictTemplates
            .AsNoTracking()
            .Where(t => t.Id == id && t.IsEnabled)
            .Select(t => new DictTemplateDetail
            {
                Id = t.Id,
                TemplateCode = t.TemplateCode,
                TemplateName = t.TemplateName,
                Category = t.Category,
                DefaultDictCode = t.DefaultDictCode,
                DefaultMatchMode = t.DefaultMatchMode,
                Description = t.Description,
                SortOrder = t.SortOrder
            })
            .FirstOrDefaultAsync();

        if (template == null)
        {
            return null;
        }

        template.Items = await db.EsbDictTemplateItems
            .AsNoTracking()
            .Where(i => i.TemplateId == id && i.IsEnabled)
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Id)
            .Select(i => new DictTemplateItemDto
            {
                SourceValue = i.SourceValue,
                TargetValue = i.TargetValue,
                SortOrder = i.SortOrder,
                Description = i.Description
            })
            .ToListAsync();

        return template;
    }
}
