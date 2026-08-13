using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
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

    public async Task<EsbEventIdentity?> FindByVisitIdentityAsync(
        string? integrationProjectCode,
        string? mrn,
        string? inpatientNo,
        string? visitNo,
        string? eventTypeName = null)
    {
        if (string.IsNullOrWhiteSpace(inpatientNo) && string.IsNullOrWhiteSpace(visitNo))
            return null;

        await using var db = await _contextFactory.CreateDbContextAsync();
        var query = db.EsbEventIdentities
            .AsNoTracking()
            .Where(x => x.IntegrationProjectCode == integrationProjectCode);

        if (!string.IsNullOrWhiteSpace(mrn))
            query = query.Where(x => x.Mrn == mrn);

        if (!string.IsNullOrWhiteSpace(eventTypeName))
            query = query.Where(x => x.EventTypeName == eventTypeName);

        if (!string.IsNullOrWhiteSpace(inpatientNo) && !string.IsNullOrWhiteSpace(visitNo))
        {
            var identity = await query
                .Where(x => x.InpatientNo == inpatientNo && x.VisitNo == visitNo)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();

            if (identity != null)
                return identity;
        }

        if (!string.IsNullOrWhiteSpace(inpatientNo))
        {
            var identity = await query
                .Where(x => x.InpatientNo == inpatientNo)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();

            if (identity != null)
                return identity;
        }

        if (!string.IsNullOrWhiteSpace(visitNo))
        {
            return await query
                .Where(x => x.VisitNo == visitNo)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();
        }

        return null;
    }

    public async Task<(EsbEventIdentity? Identity, bool IsAmbiguous)> FindByCombinedVisitIdentityAsync(
        string? integrationProjectCode,
        string? combinedValue,
        CombinedVisitIdentityFormat format,
        string? mrn = null,
        string? eventTypeName = null)
    {
        if (string.IsNullOrWhiteSpace(combinedValue) || format == CombinedVisitIdentityFormat.None)
            return (null, false);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var query = db.EsbEventIdentities
            .AsNoTracking()
            .Where(x =>
                x.IntegrationProjectCode == integrationProjectCode &&
                x.VisitNo != null);

        if (!string.IsNullOrWhiteSpace(mrn))
            query = query.Where(x => x.Mrn == mrn);

        if (!string.IsNullOrWhiteSpace(eventTypeName))
            query = query.Where(x => x.EventTypeName == eventTypeName);

        query = format switch
        {
            CombinedVisitIdentityFormat.MrnUnderscoreVisitNo =>
                query.Where(x => x.Mrn + "_" + x.VisitNo == combinedValue),
            CombinedVisitIdentityFormat.MrnVisitNo =>
                query.Where(x => x.Mrn + x.VisitNo == combinedValue),
            _ => query.Where(_ => false)
        };

        var matches = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Take(2)
            .ToListAsync();

        return (matches.FirstOrDefault(), matches.Count > 1);
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
        if (eventStartTime == null)
        {
            return;
        }

        var eventDate = eventStartTime.Value.Date;
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

        if (entity == null && !string.IsNullOrWhiteSpace(inpatientNo))
        {
            entity = await db.EsbEventIdentities.FirstOrDefaultAsync(x =>
                x.IntegrationProjectCode == integrationProjectCode &&
                x.Mrn == mrn &&
                x.InpatientNo == inpatientNo);
        }

        if (entity == null && !string.IsNullOrWhiteSpace(visitNo))
        {
            entity = await db.EsbEventIdentities.FirstOrDefaultAsync(x =>
                x.IntegrationProjectCode == integrationProjectCode &&
                x.Mrn == mrn &&
                x.VisitNo == visitNo);
        }

        if (entity == null)
        {
            entity = await db.EsbEventIdentities.FirstOrDefaultAsync(x =>
                x.IntegrationProjectCode == integrationProjectCode &&
                x.Mrn == mrn &&
                x.EventTypeName == eventTypeName &&
                x.EventStartTime == eventDate);
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
                EventStartTime = eventDate,
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
            entity.EventStartTime = eventDate;
            entity.UpdatedAt = DateTime.Now;
        }

        await db.SaveChangesAsync();
        await RequeueWaitingIdentityMessagesAsync(
            db,
            integrationProjectCode,
            mrn,
            inpatientNo,
            visitNo,
            eventDate);
    }

    private static async Task RequeueWaitingIdentityMessagesAsync(
        DataSyncDbContext db,
        string? integrationProjectCode,
        string mrn,
        string? inpatientNo,
        string? visitNo,
        DateTime eventDate)
    {
        var baseQuery = db.EsbMessages
            .Where(m => m.IntegrationProjectCode == integrationProjectCode &&
                m.Status == MessageStatus.WaitingIdentity);

        if (!string.IsNullOrWhiteSpace(inpatientNo))
            await RequeueAsync(baseQuery.Where(m => m.InpatientNo == inpatientNo));

        if (!string.IsNullOrWhiteSpace(visitNo))
            await RequeueAsync(baseQuery.Where(m => m.Mrn == mrn && m.VisitNo == visitNo));

        var nextDate = eventDate.AddDays(1);
        await RequeueAsync(baseQuery.Where(m =>
            m.Mrn == mrn &&
            m.ResolvedEventTime >= eventDate &&
            m.ResolvedEventTime < nextDate));
    }

    private static Task<int> RequeueAsync(IQueryable<EsbMessage> query)
    {
        return query.ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, MessageStatus.Pending)
            .SetProperty(m => m.ErrorMessage, (string?)null)
            .SetProperty(m => m.ProcessedAt, (DateTime?)null)
            .SetProperty(m => m.ProcessingStartedAt, (DateTime?)null));
    }
}
