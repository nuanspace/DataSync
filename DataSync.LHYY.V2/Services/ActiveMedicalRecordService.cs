using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// LHYY 侧 Active 病历状态服务。
/// </summary>
public class ActiveMedicalRecordService
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;

    public ActiveMedicalRecordService(IDbContextFactory<DataSyncDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<bool> IsActiveAsync(
        string? integrationProjectCode,
        string? mrn,
        string? inpatientNo,
        string? visitNo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mrn) &&
            string.IsNullOrWhiteSpace(inpatientNo) &&
            string.IsNullOrWhiteSpace(visitNo))
        {
            return false;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = BuildIdentityQuery(db.ActiveMedicalRecords.AsNoTracking(), integrationProjectCode, mrn, inpatientNo, visitNo);
        return await query.AnyAsync(r => r.Status == ActiveMedicalRecordStatuses.Active, cancellationToken);
    }

    public async Task UpsertFromCaseDriverAsync(
        string? integrationProjectCode,
        string tranCode,
        Guid patientId,
        Guid eventId,
        string mrn,
        string eventTypeName,
        string? inpatientNo,
        string? visitNo,
        DateTime? admissionTime,
        DateTime? dischargeTime,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mrn) ||
            (string.IsNullOrWhiteSpace(inpatientNo) && string.IsNullOrWhiteSpace(visitNo)))
        {
            return;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.Now;
        var record = await BuildIdentityQuery(db.ActiveMedicalRecords, integrationProjectCode, mrn, inpatientNo, visitNo)
            .OrderByDescending(r => r.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (record == null)
        {
            record = new ActiveMedicalRecord
            {
                IntegrationProjectCode = integrationProjectCode,
                TranCode = tranCode,
                Mrn = mrn,
                InpatientNo = inpatientNo,
                VisitNo = visitNo,
                PatientId = patientId,
                EventId = eventId,
                EventTypeName = eventTypeName,
                AdmissionTime = admissionTime,
                CreatedAt = now
            };
            db.ActiveMedicalRecords.Add(record);
        }

        record.TranCode = tranCode;
        record.PatientId = patientId;
        record.EventId = eventId;
        record.EventTypeName = string.IsNullOrWhiteSpace(record.EventTypeName) ? eventTypeName : record.EventTypeName;
        record.InpatientNo = string.IsNullOrWhiteSpace(record.InpatientNo) ? inpatientNo : record.InpatientNo;
        record.VisitNo = string.IsNullOrWhiteSpace(record.VisitNo) ? visitNo : record.VisitNo;
        record.AdmissionTime ??= admissionTime;
        record.DischargeTime = dischargeTime ?? record.DischargeTime;
        record.UpdatedAt = now;

        if (record.DischargeTime.HasValue)
        {
            record.Status = ActiveMedicalRecordStatuses.Finished;
            record.FinishedAt ??= now;
        }
        else if (!string.Equals(record.Status, ActiveMedicalRecordStatuses.Finished, StringComparison.OrdinalIgnoreCase))
        {
            record.Status = ActiveMedicalRecordStatuses.Active;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ActiveMedicalRecordListResponse> GetActiveRecordsAsync(
        string? integrationProjectCode,
        int limit,
        long? cursor,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ActiveMedicalRecords
            .AsNoTracking()
            .Where(r => r.Status == ActiveMedicalRecordStatuses.Active);

        if (!string.IsNullOrWhiteSpace(integrationProjectCode))
            query = query.Where(r => r.IntegrationProjectCode == integrationProjectCode);

        if (cursor.HasValue)
            query = query.Where(r => r.Id > cursor.Value);

        var rows = await query
            .OrderBy(r => r.Id)
            .Take(take)
            .Select(r => new ActiveMedicalRecordItem
            {
                Id = r.Id,
                Mrn = r.Mrn,
                InpatientNo = r.InpatientNo,
                VisitNo = r.VisitNo,
                AdmissionTime = r.AdmissionTime,
                PatientId = r.PatientId,
                EventId = r.EventId
            })
            .ToListAsync(cancellationToken);

        return new ActiveMedicalRecordListResponse
        {
            Items = rows,
            NextCursor = rows.Count == take ? rows[^1].Id : null
        };
    }

    private static IQueryable<ActiveMedicalRecord> BuildIdentityQuery(
        IQueryable<ActiveMedicalRecord> query,
        string? integrationProjectCode,
        string? mrn,
        string? inpatientNo,
        string? visitNo)
    {
        query = query.Where(r => r.IntegrationProjectCode == integrationProjectCode);

        if (!string.IsNullOrWhiteSpace(mrn))
            query = query.Where(r => r.Mrn == mrn);

        if (!string.IsNullOrWhiteSpace(inpatientNo) && !string.IsNullOrWhiteSpace(visitNo))
            return query.Where(r => r.InpatientNo == inpatientNo && r.VisitNo == visitNo);

        if (!string.IsNullOrWhiteSpace(inpatientNo))
            return query.Where(r => r.InpatientNo == inpatientNo);

        if (!string.IsNullOrWhiteSpace(visitNo))
            return query.Where(r => r.VisitNo == visitNo);

        return query;
    }
}
