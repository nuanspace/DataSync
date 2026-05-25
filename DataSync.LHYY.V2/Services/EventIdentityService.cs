using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 住院关系映射服务
/// </summary>
public class EventIdentityService
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;

    public EventIdentityService(IDbContextFactory<DataSyncDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<EsbEventIdentity?> FindByVisitIdentityAsync(string? integrationProjectCode, string mrn, string? inpatientNo, string? visitNo)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var query = db.EsbEventIdentities
            .AsNoTracking()
            .Where(x => x.IntegrationProjectCode == integrationProjectCode && x.Mrn == mrn);

        if (!string.IsNullOrWhiteSpace(inpatientNo) && !string.IsNullOrWhiteSpace(visitNo))
        {
            return await query
                .Where(x => x.InpatientNo == inpatientNo && x.VisitNo == visitNo)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();
        }

        if (!string.IsNullOrWhiteSpace(visitNo))
        {
            return await query
                .Where(x => x.VisitNo == visitNo)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();
        }

        if (!string.IsNullOrWhiteSpace(inpatientNo))
        {
            return await query
                .Where(x => x.InpatientNo == inpatientNo)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();
        }

        return null;
    }

    public async Task UpsertAsync(
        string? integrationProjectCode,
        string tranCode,
        Guid patientId,
        Guid eventId,
        Guid hospitalId,
        Guid projectId,
        string mrn,
        string eventTypeName,
        string? inpatientNo,
        string? visitNo,
        DateTime? eventStartTime)
    {
        if (eventStartTime == null ||
            (string.IsNullOrWhiteSpace(inpatientNo) && string.IsNullOrWhiteSpace(visitNo)))
        {
            return;
        }

        await using var db = await _contextFactory.CreateDbContextAsync();

        EsbEventIdentity? entity = null;
        if (!string.IsNullOrWhiteSpace(inpatientNo) && !string.IsNullOrWhiteSpace(visitNo))
        {
            entity = await db.EsbEventIdentities.FirstOrDefaultAsync(x =>
                x.IntegrationProjectCode == integrationProjectCode &&
                x.Mrn == mrn &&
                x.InpatientNo == inpatientNo &&
                x.VisitNo == visitNo);
        }

        if (entity == null && !string.IsNullOrWhiteSpace(visitNo))
        {
            entity = await db.EsbEventIdentities.FirstOrDefaultAsync(x =>
                x.IntegrationProjectCode == integrationProjectCode &&
                x.Mrn == mrn &&
                x.VisitNo == visitNo);
        }

        if (entity == null && !string.IsNullOrWhiteSpace(inpatientNo))
        {
            entity = await db.EsbEventIdentities.FirstOrDefaultAsync(x =>
                x.IntegrationProjectCode == integrationProjectCode &&
                x.Mrn == mrn &&
                x.InpatientNo == inpatientNo);
        }

        if (entity == null)
        {
            entity = new EsbEventIdentity
            {
                IntegrationProjectCode = integrationProjectCode,
                TranCode = tranCode,
                PatientId = patientId,
                EventId = eventId,
                HospitalId = hospitalId,
                ProjectId = projectId,
                Mrn = mrn,
                EventTypeName = eventTypeName,
                InpatientNo = inpatientNo,
                VisitNo = visitNo,
                EventStartTime = eventStartTime?.Date,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            };
            db.EsbEventIdentities.Add(entity);
        }
        else
        {
            entity.IntegrationProjectCode = integrationProjectCode;
            entity.TranCode = tranCode;
            entity.PatientId = patientId;
            entity.EventId = eventId;
            entity.HospitalId = hospitalId;
            entity.ProjectId = projectId;
            entity.EventTypeName = string.IsNullOrWhiteSpace(entity.EventTypeName) ? eventTypeName : entity.EventTypeName;
            entity.InpatientNo = string.IsNullOrWhiteSpace(inpatientNo) ? entity.InpatientNo : inpatientNo;
            entity.VisitNo = string.IsNullOrWhiteSpace(visitNo) ? entity.VisitNo : visitNo;
            entity.EventStartTime = eventStartTime?.Date ?? entity.EventStartTime;
            entity.UpdatedAt = DateTime.Now;
        }

        await db.SaveChangesAsync();
    }
}
