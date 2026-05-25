using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 直处理模式的轻量幂等回执服务
/// </summary>
public class MessageReceiptService
{
    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;

    public MessageReceiptService(IDbContextFactory<DataSyncDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<bool> ExistsAsync(
        string? integrationProjectCode,
        string tranCode,
        string? sourceMessageId,
        string? idempotentKey,
        long? excludeMessageId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await ExistsAsync(
            db,
            integrationProjectCode,
            tranCode,
            sourceMessageId,
            idempotentKey,
            excludeMessageId,
            cancellationToken);
    }

    public static async Task<bool> ExistsAsync(
        DataSyncDbContext db,
        string? integrationProjectCode,
        string tranCode,
        string? sourceMessageId,
        string? idempotentKey,
        long? excludeMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(sourceMessageId))
        {
            var exists = await db.EsbMessageReceipts.AnyAsync(r =>
                    r.IntegrationProjectCode == integrationProjectCode &&
                    r.TranCode == tranCode &&
                    r.SourceMessageId == sourceMessageId,
                cancellationToken);

            if (exists)
                return true;
        }

        if (!string.IsNullOrWhiteSpace(idempotentKey))
        {
            return await db.EsbMessageReceipts.AnyAsync(r =>
                    r.IntegrationProjectCode == integrationProjectCode &&
                    r.TranCode == tranCode &&
                    r.IdempotentKey == idempotentKey,
                cancellationToken);
        }

        return false;
    }

    public async Task CreateAsync(
        string? integrationProjectCode,
        string tranCode,
        string? sourceMessageId,
        string? idempotentKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceMessageId) && string.IsNullOrWhiteSpace(idempotentKey))
            return;

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        db.EsbMessageReceipts.Add(new EsbMessageReceipt
        {
            IntegrationProjectCode = integrationProjectCode,
            TranCode = tranCode,
            SourceMessageId = sourceMessageId,
            IdempotentKey = idempotentKey,
            CreatedAt = DateTime.Now,
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
