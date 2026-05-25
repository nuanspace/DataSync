using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Data;

public class SyncDbContext : DbContext
{
    public SyncDbContext(DbContextOptions<SyncDbContext> options) : base(options) { }

    public DbSet<SyncTask> SyncTasks => Set<SyncTask>();
    public DbSet<SyncTaskInterface> SyncTaskInterfaces => Set<SyncTaskInterface>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
    public DbSet<IngestionLog> IngestionLogs => Set<IngestionLog>();
    public DbSet<SyncCheckpoint> SyncCheckpoints => Set<SyncCheckpoint>();
    public DbSet<PendingSyncItem> PendingSyncItems => Set<PendingSyncItem>();
    public DbSet<IngestionSource> IngestionSources => Set<IngestionSource>();
    public DbSet<DataLakeInterface> DataLakeInterfaces => Set<DataLakeInterface>();
    public DbSet<DataLakeConfig> DataLakeConfigs => Set<DataLakeConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("cyyy");

        // SyncTask
        modelBuilder.Entity<SyncTask>(e =>
        {
            e.HasIndex(t => t.Code).IsUnique();
            e.HasMany(t => t.Interfaces)
             .WithOne(i => i.Task)
             .HasForeignKey(i => i.TaskId);
        });

        // IngestionSource
        modelBuilder.Entity<IngestionSource>(e =>
        {
            e.HasIndex(s => s.ServerCode).IsUnique();
        });

        // DataLakeInterface
        modelBuilder.Entity<DataLakeInterface>(e =>
        {
            e.HasIndex(d => d.ServerCode).IsUnique();
        });

        // DataLakeConfig（全局唯一配置）
        modelBuilder.Entity<DataLakeConfig>(e =>
        {
            e.ToTable("data_lake_configs");
        });

        // SyncLog 索引
        modelBuilder.Entity<SyncLog>(e =>
        {
            e.HasIndex(l => l.CreatedAt);
            e.HasIndex(l => new { l.HisPatId, l.TaskCode });
            e.HasIndex(l => new { l.TaskCode, l.ServerCode });
            e.HasIndex(l => new { l.TaskCode, l.SourceRecordKey });
        });

        // IngestionLog 索引
        modelBuilder.Entity<IngestionLog>(e =>
        {
            e.HasIndex(l => l.CreatedAt);
            e.HasIndex(l => new { l.ServerCode, l.CreatedAt });
            e.HasIndex(l => new { l.ServerCode, l.Success });
        });

        // PendingSyncItem 索引
        modelBuilder.Entity<PendingSyncItem>(e =>
        {
            e.HasIndex(p => new { p.TaskCode, p.SourceRecordKey }).IsUnique();
            e.HasIndex(p => new { p.TaskCode, p.ObjectKey });
            e.HasIndex(p => new { p.TaskCode, p.Status, p.NextRetryTime });
            e.HasIndex(p => new { p.TaskCode, p.HisPatId, p.PatVisitSn, p.Status });
        });
    }
}
