using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Dto;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Data;

public class DataSyncDbContext : DbContext
{
    public DataSyncDbContext(DbContextOptions<DataSyncDbContext> options) : base(options) { }

    public DbSet<EsbMessage> EsbMessages => Set<EsbMessage>();
    public DbSet<EsbInterfaceConfig> EsbInterfaceConfigs => Set<EsbInterfaceConfig>();
    public DbSet<EsbGlobalConfig> EsbGlobalConfigs => Set<EsbGlobalConfig>();
    public DbSet<EsbFieldMapping> EsbFieldMappings => Set<EsbFieldMapping>();
    public DbSet<EsbDict> EsbDicts => Set<EsbDict>();
    public DbSet<EsbDictTemplate> EsbDictTemplates => Set<EsbDictTemplate>();
    public DbSet<EsbDictTemplateItem> EsbDictTemplateItems => Set<EsbDictTemplateItem>();
    public DbSet<EsbProcessLog> EsbProcessLogs => Set<EsbProcessLog>();
    public DbSet<EsbFilterRule> EsbFilterRules => Set<EsbFilterRule>();
    public DbSet<EsbInterfaceMatchRule> EsbInterfaceMatchRules => Set<EsbInterfaceMatchRule>();
    public DbSet<EsbIdempotentKeyPart> EsbIdempotentKeyParts => Set<EsbIdempotentKeyPart>();
    public DbSet<EsbEventIdentity> EsbEventIdentities => Set<EsbEventIdentity>();
    public DbSet<EsbMessageReceipt> EsbMessageReceipts => Set<EsbMessageReceipt>();
    public DbSet<EsbIntegrationProject> EsbIntegrationProjects => Set<EsbIntegrationProject>();
    public DbSet<EsbIntegrationProjectConfig> EsbIntegrationProjectConfigs => Set<EsbIntegrationProjectConfig>();
    public DbSet<EsbIntegrationProjectDocument> EsbIntegrationProjectDocuments => Set<EsbIntegrationProjectDocument>();
    public DbSet<EsbOcrProfile> EsbOcrProfiles => Set<EsbOcrProfile>();
    public DbSet<EsbHtmlProfile> EsbHtmlProfiles => Set<EsbHtmlProfile>();
    public DbSet<EsbMessageListItem> EsbMessageListItems => Set<EsbMessageListItem>();
    public DbSet<ActiveMedicalRecord> ActiveMedicalRecords => Set<ActiveMedicalRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("lhyy");

        modelBuilder.Entity<EsbMessageListItem>(e =>
        {
            e.HasNoKey();
            e.ToView("esb_messages_all", "lhyy");
        });

        // esb_messages 索引
        modelBuilder.Entity<EsbMessage>(e =>
        {
            e.HasIndex(m => m.MessageId).IsUnique();
            e.HasIndex(m => m.TranCode);
            e.HasIndex(m => m.IntegrationProjectCode);
            e.HasIndex(m => m.SourceMessageId);
            e.HasIndex(m => m.IdempotentKey);
            e.HasIndex(m => m.InpatientNo);
            e.HasIndex(m => new { m.Mrn, m.ResolvedEventTime });
            e.HasIndex(m => m.Status);
            e.HasIndex(m => m.CreatedAt);
            e.HasIndex(m => new { m.TranCode, m.Status });
            e.HasIndex(m => new { m.Status, m.RetryCount })
                .HasFilter("status = 3");
        });

        // esb_interface_config 索引
        modelBuilder.Entity<EsbInterfaceConfig>(e =>
        {
            e.HasIndex(c => c.IntegrationProjectCode);
            e.HasIndex(c => c.TranCode)
                .IsUnique()
                .HasFilter("integration_project_code IS NULL");
            e.HasIndex(c => new { c.IntegrationProjectCode, c.TranCode })
                .IsUnique()
                .HasFilter("integration_project_code IS NOT NULL");
            e.HasIndex(c => c.SoapServiceCode)
                .HasDatabaseName("ix_esb_interface_config_soap_service_code");
        });

        // esb_global_config 索引
        modelBuilder.Entity<EsbGlobalConfig>(e =>
        {
            e.HasIndex(c => c.ConfigKey).IsUnique();
        });

        // esb_field_mapping 索引
        modelBuilder.Entity<EsbFieldMapping>(e =>
        {
            e.HasIndex(m => m.TranCode);
            e.HasIndex(m => m.IntegrationProjectCode);
            e.HasIndex(m => m.SyncKey);
            e.HasIndex(m => new { m.TranCode, m.MappingTarget });
        });

        // esb_dict 唯一约束
        modelBuilder.Entity<EsbDict>(e =>
        {
            e.HasIndex(d => new { d.DictCode, d.SourceValue })
                .IsUnique()
                .HasFilter("integration_project_code IS NULL");
            e.HasIndex(d => new { d.IntegrationProjectCode, d.DictCode, d.SourceValue })
                .IsUnique()
                .HasFilter("integration_project_code IS NOT NULL");
        });

        modelBuilder.Entity<EsbDictTemplate>(e =>
        {
            e.HasIndex(t => t.TemplateCode).IsUnique();
            e.HasIndex(t => t.Category);
            e.HasMany(t => t.Items)
                .WithOne(i => i.Template)
                .HasForeignKey(i => i.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EsbDictTemplateItem>(e =>
        {
            e.HasIndex(i => new { i.TemplateId, i.SortOrder });
        });

        // esb_process_log 索引
        modelBuilder.Entity<EsbProcessLog>(e =>
        {
            e.HasIndex(l => l.MessageId);
            e.HasIndex(l => l.IntegrationProjectCode);
        });

        // esb_filter_rule 索引
        modelBuilder.Entity<EsbFilterRule>(e =>
        {
            e.HasIndex(r => r.TranCode);
            e.HasIndex(r => r.IntegrationProjectCode);
            e.HasIndex(r => r.MappingId);
            e.HasIndex(r => new { r.TranCode, r.RuleGroup });
        });

        modelBuilder.Entity<EsbInterfaceMatchRule>(e =>
        {
            e.HasIndex(r => r.TranCode);
            e.HasIndex(r => r.IntegrationProjectCode);
            e.HasIndex(r => new { r.TranCode, r.MatchGroup });
        });

        modelBuilder.Entity<EsbIdempotentKeyPart>(e =>
        {
            e.HasIndex(p => p.TranCode);
            e.HasIndex(p => p.IntegrationProjectCode);
        });

        modelBuilder.Entity<EsbEventIdentity>(e =>
        {
            e.HasIndex(i => i.EventId);
            e.HasIndex(i => i.IntegrationProjectCode);
            e.HasIndex(i => new { i.Mrn, i.VisitNo });
            e.HasIndex(i => new { i.Mrn, i.InpatientNo });
            e.HasIndex(i => new { i.Mrn, i.InpatientNo, i.VisitNo });
            e.HasIndex(i => new { i.Mrn, i.EventTypeName, i.EventStartTime });
        });

        modelBuilder.Entity<EsbMessageReceipt>(e =>
        {
            e.HasIndex(r => new { r.IntegrationProjectCode, r.TranCode, r.SourceMessageId })
                .IsUnique()
                .HasFilter("source_message_id IS NOT NULL");
            e.HasIndex(r => new { r.IntegrationProjectCode, r.TranCode, r.IdempotentKey })
                .IsUnique()
                .HasFilter("idempotent_key IS NOT NULL");
        });

        modelBuilder.Entity<ActiveMedicalRecord>(e =>
        {
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.IntegrationProjectCode);
            e.HasIndex(r => new { r.IntegrationProjectCode, r.EventId });
            e.HasIndex(r => new { r.IntegrationProjectCode, r.InpatientNo });
            e.HasIndex(r => new { r.IntegrationProjectCode, r.Mrn, r.InpatientNo, r.VisitNo });
        });

        modelBuilder.Entity<EsbIntegrationProject>(e =>
        {
            e.HasIndex(p => p.ProjectCode).IsUnique();
        });

        modelBuilder.Entity<EsbIntegrationProjectConfig>(e =>
        {
            e.HasIndex(c => new { c.IntegrationProjectCode, c.ConfigKey }).IsUnique();
            e.HasIndex(c => c.IntegrationProjectCode);
        });

        modelBuilder.Entity<EsbIntegrationProjectDocument>(e =>
        {
            e.HasIndex(d => d.IntegrationProjectCode);
            e.HasIndex(d => new { d.IntegrationProjectCode, d.Category });
            e.HasIndex(d => new { d.IntegrationProjectCode, d.IsDeleted });
        });

        modelBuilder.Entity<EsbOcrProfile>(e =>
        {
            e.HasIndex(p => p.TranCode);
            e.HasIndex(p => p.IntegrationProjectCode);
            e.HasIndex(p => new { p.IntegrationProjectCode, p.TranCode });
        });

        modelBuilder.Entity<EsbHtmlProfile>(e =>
        {
            e.HasIndex(p => p.TranCode);
            e.HasIndex(p => p.IntegrationProjectCode);
            e.HasIndex(p => new { p.IntegrationProjectCode, p.TranCode });
        });
    }
}
