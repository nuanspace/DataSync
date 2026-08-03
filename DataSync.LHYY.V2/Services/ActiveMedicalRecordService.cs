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
        DateTime? admissionTime,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mrn) &&
            string.IsNullOrWhiteSpace(inpatientNo) &&
            string.IsNullOrWhiteSpace(visitNo) &&
            !admissionTime.HasValue)
        {
            return false;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = BuildIdentityQuery(
            db.ActiveMedicalRecords.AsNoTracking(),
            integrationProjectCode,
            mrn,
            inpatientNo,
            visitNo,
            admissionTime);
        if (await query.AnyAsync(r => r.Status == ActiveMedicalRecordStatuses.Active, cancellationToken))
            return true;

        var identityQuery = BuildIdentityQuery(
            db.EsbEventIdentities.AsNoTracking(),
            integrationProjectCode,
            mrn,
            inpatientNo,
            visitNo,
            admissionTime);

        var eventIds = identityQuery.Select(r => r.EventId);
        return await db.ActiveMedicalRecords
            .AsNoTracking()
            .AnyAsync(r =>
                r.IntegrationProjectCode == integrationProjectCode &&
                r.Status == ActiveMedicalRecordStatuses.Active &&
                eventIds.Contains(r.EventId),
                cancellationToken);
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
        var record = await db.ActiveMedicalRecords
            .Where(r => r.IntegrationProjectCode == integrationProjectCode && r.EventId == eventId)
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
        record.Mrn = string.IsNullOrWhiteSpace(record.Mrn) ? mrn : record.Mrn;
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
            .ToListAsync(cancellationToken);

        var eventIds = rows.Select(r => r.EventId).Where(id => id != Guid.Empty).Distinct().ToList();
        var inpatientNos = rows
            .Select(r => r.InpatientNo)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .ToList();
        var visitNos = rows
            .Select(r => r.VisitNo)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .ToList();
        var mrns = rows
            .Where(r => r.AdmissionTime.HasValue || !string.IsNullOrWhiteSpace(r.VisitNo))
            .Select(r => r.Mrn)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .ToList();
        var identityQuery = db.EsbEventIdentities
            .AsNoTracking()
            .Where(r => eventIds.Contains(r.EventId) ||
                (r.InpatientNo != null && inpatientNos.Contains(r.InpatientNo)) ||
                (mrns.Contains(r.Mrn) &&
                    ((r.VisitNo != null && visitNos.Contains(r.VisitNo)) || r.EventStartTime.HasValue)));
        if (!string.IsNullOrWhiteSpace(integrationProjectCode))
            identityQuery = identityQuery.Where(r => r.IntegrationProjectCode == integrationProjectCode);

        var identities = await identityQuery
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync(cancellationToken);
        var items = rows
            .Select(row => BuildItem(row, GetHospitalizationIdentities(row, identities)))
            .ToList();

        return new ActiveMedicalRecordListResponse
        {
            Items = items,
            NextCursor = rows.Count == take ? rows[^1].Id : null
        };
    }

    private static ActiveMedicalRecordItem BuildItem(
        ActiveMedicalRecord record,
        List<EsbEventIdentity> identities)
    {
        var mrn = FirstNotEmpty(identities, identity => identity.Mrn);
        var inpatientNo = FirstNotEmpty(identities, identity => identity.InpatientNo);
        var visitNo = FirstNotEmpty(identities, identity => identity.VisitNo);
        var patientId = identities
            .Select(identity => identity.PatientId)
            .FirstOrDefault(value => value != Guid.Empty);
        var admissionTime = identities
            .Select(identity => identity.EventStartTime)
            .FirstOrDefault(value => value.HasValue);

        return new ActiveMedicalRecordItem
        {
            Id = record.Id,
            Mrn = mrn ?? record.Mrn,
            InpatientNo = inpatientNo ?? record.InpatientNo,
            VisitNo = visitNo ?? record.VisitNo,
            AdmissionTime = admissionTime ?? record.AdmissionTime,
            PatientId = patientId == Guid.Empty ? record.PatientId : patientId,
            EventId = record.EventId
        };
    }

    private static string? FirstNotEmpty(
        IEnumerable<EsbEventIdentity> identities,
        Func<EsbEventIdentity, string?> selector)
        => identities.Select(selector).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static List<EsbEventIdentity> GetHospitalizationIdentities(
        ActiveMedicalRecord record,
        List<EsbEventIdentity> identities)
        => identities
            .Select(identity => new { Identity = identity, Rank = GetMatchRank(record, identity) })
            .Where(item => item.Rank < int.MaxValue)
            .OrderBy(item => item.Rank)
            .ThenByDescending(item => item.Identity.UpdatedAt)
            .Select(item => item.Identity)
            .ToList();

    private static int GetMatchRank(ActiveMedicalRecord record, EsbEventIdentity identity)
    {
        if (record.EventId != Guid.Empty && record.EventId == identity.EventId)
            return 0;

        var sameMrn = string.Equals(record.Mrn, identity.Mrn, StringComparison.OrdinalIgnoreCase);
        var sameInpatientNo = !string.IsNullOrWhiteSpace(record.InpatientNo) &&
            string.Equals(record.InpatientNo, identity.InpatientNo, StringComparison.OrdinalIgnoreCase);

        if (sameMrn && sameInpatientNo)
            return 1;

        if (sameInpatientNo)
            return 2;

        if (!sameMrn)
            return int.MaxValue;

        if (!string.IsNullOrWhiteSpace(record.VisitNo) &&
            string.Equals(record.VisitNo, identity.VisitNo, StringComparison.OrdinalIgnoreCase))
            return 3;

        if (record.AdmissionTime.HasValue &&
            identity.EventStartTime.HasValue &&
            record.AdmissionTime.Value.Date == identity.EventStartTime.Value.Date)
        {
            return 4;
        }

        return int.MaxValue;
    }

    private static IQueryable<ActiveMedicalRecord> BuildIdentityQuery(
        IQueryable<ActiveMedicalRecord> query,
        string? integrationProjectCode,
        string? mrn,
        string? inpatientNo,
        string? visitNo,
        DateTime? admissionTime)
    {
        query = query.Where(r => r.IntegrationProjectCode == integrationProjectCode);

        if (!string.IsNullOrWhiteSpace(mrn))
            query = query.Where(r => r.Mrn == mrn);

        if (admissionTime.HasValue)
        {
            var from = admissionTime.Value.Date;
            var to = from.AddDays(1);
            if (!string.IsNullOrWhiteSpace(inpatientNo) && !string.IsNullOrWhiteSpace(visitNo))
                return query.Where(r => r.InpatientNo == inpatientNo || r.VisitNo == visitNo ||
                    (r.AdmissionTime >= from && r.AdmissionTime < to));
            if (!string.IsNullOrWhiteSpace(inpatientNo))
                return query.Where(r => r.InpatientNo == inpatientNo ||
                    (r.AdmissionTime >= from && r.AdmissionTime < to));
            if (!string.IsNullOrWhiteSpace(visitNo))
                return query.Where(r => r.VisitNo == visitNo ||
                    (r.AdmissionTime >= from && r.AdmissionTime < to));
            return query.Where(r => r.AdmissionTime >= from && r.AdmissionTime < to);
        }

        if (!string.IsNullOrWhiteSpace(inpatientNo) && !string.IsNullOrWhiteSpace(visitNo))
            return query.Where(r => r.InpatientNo == inpatientNo || r.VisitNo == visitNo);

        if (!string.IsNullOrWhiteSpace(inpatientNo))
            return query.Where(r => r.InpatientNo == inpatientNo);

        if (!string.IsNullOrWhiteSpace(visitNo))
            return query.Where(r => r.VisitNo == visitNo);

        return query;
    }

    private static IQueryable<EsbEventIdentity> BuildIdentityQuery(
        IQueryable<EsbEventIdentity> query,
        string? integrationProjectCode,
        string? mrn,
        string? inpatientNo,
        string? visitNo,
        DateTime? admissionTime)
    {
        query = query.Where(r => r.IntegrationProjectCode == integrationProjectCode);

        if (!string.IsNullOrWhiteSpace(mrn))
            query = query.Where(r => r.Mrn == mrn);

        if (admissionTime.HasValue)
        {
            var from = admissionTime.Value.Date;
            var to = from.AddDays(1);
            if (!string.IsNullOrWhiteSpace(inpatientNo) && !string.IsNullOrWhiteSpace(visitNo))
                return query.Where(r => r.InpatientNo == inpatientNo || r.VisitNo == visitNo ||
                    (r.EventStartTime >= from && r.EventStartTime < to));
            if (!string.IsNullOrWhiteSpace(inpatientNo))
                return query.Where(r => r.InpatientNo == inpatientNo ||
                    (r.EventStartTime >= from && r.EventStartTime < to));
            if (!string.IsNullOrWhiteSpace(visitNo))
                return query.Where(r => r.VisitNo == visitNo ||
                    (r.EventStartTime >= from && r.EventStartTime < to));
            return query.Where(r => r.EventStartTime >= from && r.EventStartTime < to);
        }

        if (!string.IsNullOrWhiteSpace(inpatientNo) && !string.IsNullOrWhiteSpace(visitNo))
            return query.Where(r => r.InpatientNo == inpatientNo || r.VisitNo == visitNo);
        if (!string.IsNullOrWhiteSpace(inpatientNo))
            return query.Where(r => r.InpatientNo == inpatientNo);
        if (!string.IsNullOrWhiteSpace(visitNo))
            return query.Where(r => r.VisitNo == visitNo);

        return query;
    }
}
