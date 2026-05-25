using DataSync.CYYY.Components;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using DataSync.CYYY.Services;
using DataSync.CYYY.Workers;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using NLog;
using NLog.Web;
using System.Globalization;

namespace DataSync.CYYY;

public class Program
{
    public static void Main(string[] args)
    {
        // Npgsql 9.x 默认要求 UTC，启用旧版时间戳行为以兼容 DateTime.Now
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        var logger = LogManager.Setup().LoadConfigurationFromFile("nlog.config").GetCurrentClassLogger();
        try
        {
            logger.Debug("启动 DataSync 应用程序...");

            var cultureInfo = new CultureInfo("zh-CN");
            CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
            CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // NLog
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
            builder.Host.UseNLog();

            builder.Services.AddSignalR(options =>
            {
                options.MaximumReceiveMessageSize = 5 * 1024 * 1024;
            });

            // 数据库
            var connectionString = builder.Configuration.GetConnectionString("SyncDb")
                ?? throw new InvalidOperationException("未找到连接字符串 'SyncDb'");
            builder.Services.AddDbContextFactory<SyncDbContext>(options =>
                options.UseNpgsql(connectionString));

            // HttpClient（数据湖 API 可能使用自签名证书，跳过 SSL 验证）
            builder.Services.AddHttpClient("DataLake")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                });

            // 服务注册
            builder.Services.AddSingleton<DataLakeClient>();
            builder.Services.AddSingleton<SyncTaskSignalService>();
            builder.Services.AddScoped<SyncLogService>();
            builder.Services.AddScoped<PendingSyncService>();
            builder.Services.AddScoped<TaskManagementService>();
            builder.Services.AddTransient<SyncOrchestrator>();
            builder.Services.AddTransient<PushServiceFactory>();
            builder.Services.AddTransient<ApiPushService>();
            builder.Services.AddTransient<DatabasePushService>();

            // 采集与本地查询
            builder.Services.AddScoped<IngestionService>();
            builder.Services.AddScoped<LocalQueryService>();

            // 后台任务
            builder.Services.AddHostedService<SyncWorker>();
            builder.Services.AddHostedService<IngestionWorker>();

            // MudBlazor
            builder.Services.AddMudServices(config =>
            {
                config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomLeft;
                config.SnackbarConfiguration.PreventDuplicates = false;
                config.SnackbarConfiguration.NewestOnTop = true;
                config.SnackbarConfiguration.ShowCloseIcon = true;
                config.SnackbarConfiguration.VisibleStateDuration = 8000;
            });

            var app = builder.Build();

            // 自动建表（不存在则创建）+ 种子数据
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SyncDbContext>>().CreateDbContext();
                db.Database.EnsureCreated();

                // EnsureCreated 不会为已存在的数据库创建新表，手动补充
                db.Database.ExecuteSqlRaw("""
                    CREATE TABLE IF NOT EXISTS cyyy.data_lake_configs (
                        "Id"             SERIAL PRIMARY KEY,
                        "BaseUrl"        VARCHAR(500) NOT NULL,
                        "TokenEndpoint"  VARCHAR(200) NOT NULL DEFAULT '/auth/oauth/token',
                        "QueryEndpoint"  VARCHAR(200) NOT NULL DEFAULT '/api/jhids4s/common/server/dataQuery',
                        "ClientId"       VARCHAR(200) NOT NULL DEFAULT '',
                        "ClientSecret"   VARCHAR(500) NOT NULL DEFAULT '',
                        "SysCode"        VARCHAR(100) NOT NULL DEFAULT 'client-app',
                        "PageSize"       INT NOT NULL DEFAULT 100,
                        "MaxResultSize"  INT NOT NULL DEFAULT 10000,
                        "RequestIntervalMilliseconds" INT NOT NULL DEFAULT 200,
                        "UpdatedAt"      TIMESTAMP NOT NULL DEFAULT NOW()
                    )
                    """);

                // 补充 DebugLogEnabled 列
                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.data_lake_configs
                    ADD COLUMN IF NOT EXISTS "DebugLogEnabled" BOOLEAN NOT NULL DEFAULT TRUE
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.data_lake_configs
                    ADD COLUMN IF NOT EXISTS "RequestIntervalMilliseconds" INT NOT NULL DEFAULT 200
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_tasks
                    ADD COLUMN IF NOT EXISTS enable_trigger_record_push BOOLEAN NOT NULL DEFAULT FALSE
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_tasks
                    ADD COLUMN IF NOT EXISTS trigger_push_target TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_tasks
                    ADD COLUMN IF NOT EXISTS trigger_push_params TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_task_interfaces
                    ADD COLUMN IF NOT EXISTS interface_key TEXT NOT NULL DEFAULT ''
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_task_interfaces
                    ADD COLUMN IF NOT EXISTS parent_interface_key TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_task_interfaces
                    ADD COLUMN IF NOT EXISTS parent_result_field TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_task_interfaces
                    ADD COLUMN IF NOT EXISTS mount_field TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_task_interfaces
                    ADD COLUMN IF NOT EXISTS route_field TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_task_interfaces
                    ADD COLUMN IF NOT EXISTS route_operator TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_task_interfaces
                    ADD COLUMN IF NOT EXISTS route_value TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_task_interfaces
                    ADD COLUMN IF NOT EXISTS output_fields TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    UPDATE cyyy.sync_task_interfaces
                    SET interface_key = 'iface_' || id::text
                    WHERE interface_key = ''
                    """);

                db.Database.ExecuteSqlRaw("""
                    UPDATE cyyy.sync_task_interfaces
                    SET route_operator = 'eq'
                    WHERE COALESCE(route_operator, '') = ''
                    """);

                db.Database.ExecuteSqlRaw("""
                    CREATE UNIQUE INDEX IF NOT EXISTS ix_sync_task_interfaces_task_interface_key
                    ON cyyy.sync_task_interfaces (task_id, interface_key)
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.sync_logs
                    ADD COLUMN IF NOT EXISTS source_record_key TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    CREATE TABLE IF NOT EXISTS cyyy.pending_sync_items (
                        id                 BIGSERIAL PRIMARY KEY,
                        task_code          VARCHAR(50)   NOT NULL,
                        source_server_code VARCHAR(100)  NOT NULL,
                        source_record_key  TEXT          NOT NULL,
                        object_key         TEXT          NOT NULL DEFAULT '',
                        his_pat_id         TEXT          NOT NULL DEFAULT '',
                        pat_visit_sn       TEXT          NOT NULL DEFAULT '',
                        pat_name           TEXT          NOT NULL DEFAULT '',
                        trigger_record_json TEXT         NOT NULL DEFAULT '{{}}',
                        trigger_push_done  BOOLEAN       NOT NULL DEFAULT FALSE,
                        trigger_push_done_at TIMESTAMP,
                        trigger_push_error TEXT,
                        status             VARCHAR(20)   NOT NULL DEFAULT 'Pending',
                        retry_count        INT           NOT NULL DEFAULT 0,
                        last_error         TEXT,
                        next_retry_time    TIMESTAMP,
                        last_started_at    TIMESTAMP,
                        last_completed_at  TIMESTAMP,
                        created_at         TIMESTAMP     NOT NULL DEFAULT NOW(),
                        updated_at         TIMESTAMP     NOT NULL DEFAULT NOW(),
                        CONSTRAINT uq_pending_sync_items_source UNIQUE (task_code, source_record_key)
                    )
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.pending_sync_items
                    ADD COLUMN IF NOT EXISTS object_key TEXT NOT NULL DEFAULT ''
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.pending_sync_items
                    ADD COLUMN IF NOT EXISTS his_pat_id TEXT NOT NULL DEFAULT ''
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.pending_sync_items
                    ADD COLUMN IF NOT EXISTS pat_visit_sn TEXT NOT NULL DEFAULT ''
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.pending_sync_items
                    ADD COLUMN IF NOT EXISTS pat_name TEXT NOT NULL DEFAULT ''
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.pending_sync_items
                    ADD COLUMN IF NOT EXISTS trigger_push_done BOOLEAN NOT NULL DEFAULT FALSE
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.pending_sync_items
                    ADD COLUMN IF NOT EXISTS trigger_push_done_at TIMESTAMP
                    """);

                db.Database.ExecuteSqlRaw("""
                    ALTER TABLE cyyy.pending_sync_items
                    ADD COLUMN IF NOT EXISTS trigger_push_error TEXT
                    """);

                db.Database.ExecuteSqlRaw("""
                    UPDATE cyyy.pending_sync_items AS p
                    SET his_pat_id = COALESCE(NULLIF(p.trigger_record_json::jsonb ->> st.patient_id_field, ''), p.his_pat_id, ''),
                        pat_visit_sn = CASE
                            WHEN COALESCE(st.visit_sn_field, '') = '' THEN ''
                            ELSE COALESCE(p.trigger_record_json::jsonb ->> st.visit_sn_field, p.pat_visit_sn, '')
                        END,
                        pat_name = COALESCE(NULLIF(p.trigger_record_json::jsonb ->> 'PAT_NAME', ''), p.pat_name, ''),
                        object_key = CASE
                            WHEN COALESCE(NULLIF(p.trigger_record_json::jsonb ->> st.patient_id_field, ''), p.his_pat_id, '') = '' THEN p.object_key
                            WHEN COALESCE(st.visit_sn_field, '') = '' THEN
                                'PAT:' || replace(replace(replace(COALESCE(NULLIF(p.trigger_record_json::jsonb ->> st.patient_id_field, ''), p.his_pat_id, ''), '\', '\\'), '|', '\|'), '=', '\=')
                            ELSE
                                'PAT:' || replace(replace(replace(COALESCE(NULLIF(p.trigger_record_json::jsonb ->> st.patient_id_field, ''), p.his_pat_id, ''), '\', '\\'), '|', '\|'), '=', '\=')
                                || '|VISIT:' ||
                                replace(replace(replace(COALESCE(p.trigger_record_json::jsonb ->> st.visit_sn_field, p.pat_visit_sn, ''), '\', '\\'), '|', '\|'), '=', '\=')
                        END
                    FROM cyyy.sync_tasks AS st
                    WHERE st.code = p.task_code
                      AND (
                          p.object_key = ''
                          OR p.his_pat_id = ''
                          OR p.pat_name = ''
                          OR (COALESCE(st.visit_sn_field, '') <> '' AND p.pat_visit_sn = '')
                      )
                    """);

                db.Database.ExecuteSqlRaw("""
                    CREATE INDEX IF NOT EXISTS ix_pending_sync_items_task_status_retry
                    ON cyyy.pending_sync_items (task_code, status, next_retry_time)
                    """);

                db.Database.ExecuteSqlRaw("""
                    CREATE INDEX IF NOT EXISTS ix_pending_sync_items_task_object
                    ON cyyy.pending_sync_items (task_code, object_key)
                    """);

                db.Database.ExecuteSqlRaw("""
                    CREATE INDEX IF NOT EXISTS ix_sync_logs_task_source_record
                    ON cyyy.sync_logs (task_code, source_record_key)
                    """);

                db.Database.ExecuteSqlRaw("""
                    CREATE INDEX IF NOT EXISTS ix_pending_sync_items_task_patient_status
                    ON cyyy.pending_sync_items (task_code, his_pat_id, pat_visit_sn, status)
                    """);

                db.Database.ExecuteSqlRaw("""
                    DO $$
                    BEGIN
                        IF EXISTS (
                            SELECT 1
                            FROM pg_constraint
                            WHERE conname = 'uq_pending_sync_items_object'
                        ) THEN
                            ALTER TABLE cyyy.pending_sync_items
                            DROP CONSTRAINT uq_pending_sync_items_object;
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM pg_constraint
                            WHERE conname = 'uq_pending_sync_items_source'
                        ) THEN
                            BEGIN
                                ALTER TABLE cyyy.pending_sync_items
                                ADD CONSTRAINT uq_pending_sync_items_source UNIQUE (task_code, source_record_key);
                            EXCEPTION
                                WHEN duplicate_table OR duplicate_object OR unique_violation THEN
                                    NULL;
                            END;
                        END IF;
                    END $$;
                    """);

            }

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            logger.Info("DataSync 应用程序已启动");
            app.Run();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "应用程序启动时发生错误");
            throw;
        }
        finally
        {
            LogManager.Shutdown();
        }
    }
}
