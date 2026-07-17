using Bio.Core;
using Bio.Core.FormSetDC;
using Bio.Core.FormSetDC.V2.Internal;
using Bio.Core.Services;
using Bio.Services;
using DataSync.Common.Ocr;
using DataSync.LHYY.V2.Components;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Options;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services;
using DataSync.LHYY.V2.Services.FollowUp;
using DataSync.LHYY.V2.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using MudBlazor;
using MudBlazor.Services;
using NLog;
using NLog.Web;
using Npgsql;
using System.Globalization;

namespace DataSync.LHYY.V2;

public class Program
{
    public static async Task Main(string[] args)
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        var logger = LogManager.Setup().LoadConfigurationFromFile("nlog.config").GetCurrentClassLogger();
        try
        {
            logger.Debug("启动 DataSync.LHYY.V2 应用程序...");

            var cultureInfo = new CultureInfo("zh-CN");
            CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
            CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

            if (DatabaseUpgradeTool.IsCommand(args))
            {
                Environment.ExitCode = await DatabaseUpgradeTool.RunAsync(args);
                return;
            }

            if (MessageArchiveTool.IsCommand(args))
            {
                Environment.ExitCode = await MessageArchiveTool.RunAsync(args);
                return;
            }

            if (MessagePerfTool.IsCommand(args))
            {
                Environment.ExitCode = await MessagePerfTool.RunAsync(args);
                return;
            }

            var builder = WebApplication.CreateBuilder(args);

            // Blazor
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // API 控制器
            builder.Services.AddControllers();

            // 内存缓存
            builder.Services.AddMemoryCache();

            // NLog
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
            builder.Host.UseNLog();

            builder.Services.AddSignalR(options =>
            {
                options.MaximumReceiveMessageSize = 5 * 1024 * 1024;
            });

            // 平台库（DataSync）
            var dataSyncConnStr = builder.Configuration.GetConnectionString("DataSyncDb")
                ?? throw new InvalidOperationException("未找到连接字符串 'DataSyncDb'");
            builder.Services.AddDbContextFactory<DataSyncDbContext>(options =>
                options.UseNpgsql(dataSyncConnStr));

            // 产品库（Bio.Core CubeDb）
            var cubeDbConnStr = builder.Configuration.GetConnectionString("CubeDb")
                ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");
            cubeDbConnStr = EnsureCubeDbSearchPath(cubeDbConnStr);
            if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("DefaultConnection")))
            {
                builder.Configuration["ConnectionStrings:DefaultConnection"] = cubeDbConnStr;
            }
            builder.Services.AddDbContextFactory<Bio.Models.CubeDbContext>(options =>
                options.UseNpgsql(cubeDbConnStr));

            // Bio.Core 服务注册
            builder.Services.AddFormsetServices();
            builder.Services.AddSingleton<ITargetMetadataService, TargetMetadataService>();
            builder.Services.AddScoped<ITargetDataService, TargetDataService>();
            builder.Services.AddScoped<ITargetSchemaService, TargetSchemaService>();

            // 平台服务注册
            builder.Services.Configure<OcrRuntimeOptions>(builder.Configuration.GetSection("Ocr"));
            builder.Services.Configure<FollowUpPackageImportOptions>(builder.Configuration.GetSection("FollowUpPackageImport"));
            builder.Services.AddDataSyncOcr();
            builder.Services.AddScoped<EsbReceiverService>();
            builder.Services.AddScoped<SoapWebServiceService>();
            builder.Services.AddScoped<IntegrationProjectService>();
            builder.Services.AddScoped<ConfigService>();
            builder.Services.AddScoped<ProjectDocumentService>();
            builder.Services.AddScoped<InterfaceRecognitionService>();
            builder.Services.AddScoped<IdempotentKeyService>();
            builder.Services.AddScoped<EventIdentityService>();
            builder.Services.AddScoped<ActiveMedicalRecordService>();
            builder.Services.AddScoped<MessageReceiptService>();
            builder.Services.AddScoped<MessageQueryService>();
            builder.Services.AddScoped<BioCoreIntegrationService>();
            builder.Services.AddScoped<DictService>();
            builder.Services.AddScoped<DictTemplateService>();
            builder.Services.AddScoped<FieldMappingExecutor>();
            builder.Services.AddScoped<DirectTargetWriteService>();
            builder.Services.AddScoped<GenericMessageProcessor>();
            builder.Services.AddScoped<GenericQuestionWriteBackProcessor>();
            builder.Services.AddScoped<OcrProfileService>();
            builder.Services.AddScoped<MessageExecutionService>();
            builder.Services.AddScoped<FilterRuleService>();
            builder.Services.AddScoped<MappingPreviewService>();
            builder.Services.AddScoped<MessageMappingPreviewService>();
            builder.Services.AddScoped<ConfigSyncService>();
            builder.Services.AddScoped<FollowUpPackageImportRepository>();
            builder.Services.AddSingleton<IFollowUpCubeAdvisoryLockProvider, PostgreSqlFollowUpCubeAdvisoryLockProvider>();
            builder.Services.AddSingleton(sp => new FollowUpCubeOperationCoordinator(
                sp.GetRequiredService<IFollowUpCubeAdvisoryLockProvider>()));
            builder.Services.AddScoped<FollowUpPackageVerifyService>();
            builder.Services.AddScoped<FollowUpPackageSchemaCheckService>();
            builder.Services.AddScoped<FollowUpPackageBackupService>();
            builder.Services.AddScoped<FollowUpPackageImportService>();
            builder.Services.AddScoped<FollowUpPackageRestoreService>();
            builder.Services.AddSingleton<FollowUpPackageImportKeyService>();
            builder.Services.AddSingleton<DatabaseUpgradeService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseUpgradeService>());
            builder.Services.AddSingleton<DatabaseCompareService>();
            builder.Services.AddSingleton<MessageProcessingNotifier>();

            // LLM 服务
            builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("LlmOptions"));
            builder.Services.AddHttpClient<LlmService>();

            // 后台处理引擎
            builder.Services.AddHostedService<MessageProcessingService>();

            // 日志清理服务
            builder.Services.AddHostedService<ProcessLogCleanupService>();
            builder.Services.AddHostedService<FollowUpPackageImportWorker>();

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

            // 自动建表（平台库）
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DataSyncDbContext>>().CreateDbContext();
                db.Database.EnsureCreated();
                EnsurePlatformSchemaPatched(db);
                logger.Info("平台库表结构已就绪");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "平台库初始化失败，请检查 DataSyncDb 连接字符串");
            }

            // Bio.Core 静态数据预加载（失败不影响应用启动）
            try
            {
                var staticData = app.Services.GetRequiredService<IStaticDataService>();
                await staticData.PreloadAllDataAsync();

                var formsetStaticData = app.Services.GetRequiredService<IFormsetStaticDataService>();
                await formsetStaticData.PreloadAllDataAsync(staticData);


                logger.Info("Bio.Core 静态数据预加载完成");
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Bio.Core 预加载失败，消息处理功能暂不可用。请检查 CubeDb 连接字符串");
            }

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseStaticFiles();
            app.UseStaticFiles(BuildProjectDocumentStaticFileOptions(app.Environment.ContentRootPath));

            var mudBlazorStaticAssetsPath = FindMudBlazorStaticAssetsPath();
            if (!string.IsNullOrWhiteSpace(mudBlazorStaticAssetsPath))
            {
                var mudBlazorJsPath = Path.Combine(mudBlazorStaticAssetsPath, "MudBlazor.min.js");
                var mudBlazorCssPath = Path.Combine(mudBlazorStaticAssetsPath, "MudBlazor.min.css");

                app.Use(async (context, next) =>
                {
                    if (string.Equals(context.Request.Path, "/_content/MudBlazor/MudBlazor.min.js", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.ContentType = "text/javascript";
                        await context.Response.SendFileAsync(mudBlazorJsPath);
                        return;
                    }

                    if (string.Equals(context.Request.Path, "/_content/MudBlazor/MudBlazor.min.css", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.ContentType = "text/css";
                        await context.Response.SendFileAsync(mudBlazorCssPath);
                        return;
                    }

                    await next();
                });
            }
            else
            {
                logger.Warn("未找到 MudBlazor 静态资源目录，/_content/MudBlazor/* 可能无法访问");
            }

            app.UseAntiforgery();
            app.MapStaticAssets();

            // API 控制器路由
            app.MapControllers();

            // Blazor 组件路由
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            logger.Info("DataSync.LHYY.V2 应用程序已启动");
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

    private static string? FindMudBlazorStaticAssetsPath()
    {
        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packagesRoot))
        {
            packagesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        var mudBlazorRoot = Path.Combine(packagesRoot, "mudblazor");
        if (!Directory.Exists(mudBlazorRoot))
        {
            return null;
        }

        var versionDirectories = Directory.GetDirectories(mudBlazorRoot)
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                return new
                {
                    Path = path,
                    Version = Version.TryParse(name, out var parsedVersion) ? parsedVersion : new Version(0, 0)
                };
            })
            .OrderByDescending(item => item.Version)
            .Select(item => item.Path);

        foreach (var versionDirectory in versionDirectories)
        {
            var staticAssetsDirectory = Path.Combine(versionDirectory, "staticwebassets");
            if (File.Exists(Path.Combine(staticAssetsDirectory, "MudBlazor.min.js"))
                && File.Exists(Path.Combine(staticAssetsDirectory, "MudBlazor.min.css")))
            {
                return staticAssetsDirectory;
            }
        }

        return null;
    }

    private static void EnsurePlatformSchemaPatched(DataSyncDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            ALTER TABLE IF EXISTS lhyy.esb_interface_config
                ADD COLUMN IF NOT EXISTS main_record_array_path VARCHAR(500);
            """);

        db.Database.ExecuteSqlRaw("""
            ALTER TABLE IF EXISTS lhyy.esb_interface_config
                ADD COLUMN IF NOT EXISTS medical_record_sync_role INTEGER NOT NULL DEFAULT 0;
            """);

        db.Database.ExecuteSqlRaw("""
            ALTER TABLE IF EXISTS lhyy.esb_interface_config
                ADD COLUMN IF NOT EXISTS soap_enabled BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE IF EXISTS lhyy.esb_interface_config
                ADD COLUMN IF NOT EXISTS soap_service_code VARCHAR(100);
            ALTER TABLE IF EXISTS lhyy.esb_interface_config
                ADD COLUMN IF NOT EXISTS soap_operation VARCHAR(100);
            ALTER TABLE IF EXISTS lhyy.esb_interface_config
                ADD COLUMN IF NOT EXISTS soap_action VARCHAR(500);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS ux_esb_interface_config_soap_operation
                ON lhyy.esb_interface_config (LOWER(soap_service_code), LOWER(soap_operation))
                WHERE soap_enabled = TRUE;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_esb_interface_config_soap_action
                ON lhyy.esb_interface_config (LOWER(soap_service_code), LOWER(soap_action))
                WHERE soap_enabled = TRUE;
            """);

        db.Database.ExecuteSqlRaw("""
            ALTER TABLE IF EXISTS lhyy.esb_field_mapping
                ADD COLUMN IF NOT EXISTS sync_key VARCHAR(64);
            """);

        db.Database.ExecuteSqlRaw("""
            ALTER TABLE IF EXISTS lhyy.esb_field_mapping
                ADD COLUMN IF NOT EXISTS last_sync_hash VARCHAR(64);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS ix_esb_field_mapping_sync_key
                ON lhyy.esb_field_mapping (sync_key);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS lhyy.active_medical_records (
                id                       BIGSERIAL PRIMARY KEY,
                integration_project_code VARCHAR(50),
                tran_code                VARCHAR(20),
                mrn                      VARCHAR(100) NOT NULL,
                inpatient_no             VARCHAR(100),
                visit_no                 VARCHAR(100),
                patient_id               UUID NOT NULL,
                event_id                 UUID NOT NULL,
                event_type_name          VARCHAR(100) NOT NULL DEFAULT '',
                admission_time           TIMESTAMP,
                discharge_time           TIMESTAMP,
                status                   VARCHAR(20) NOT NULL DEFAULT 'Active',
                created_at               TIMESTAMP NOT NULL DEFAULT NOW(),
                updated_at               TIMESTAMP NOT NULL DEFAULT NOW(),
                finished_at              TIMESTAMP
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS ix_active_medical_records_status
                ON lhyy.active_medical_records (status);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS ix_active_medical_records_project
                ON lhyy.active_medical_records (integration_project_code);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS ix_active_medical_records_project_inpatient
                ON lhyy.active_medical_records (integration_project_code, inpatient_no);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS lhyy.esb_dict_template (
                id                 SERIAL PRIMARY KEY,
                template_code      VARCHAR(100) NOT NULL,
                template_name      VARCHAR(100) NOT NULL,
                category           VARCHAR(50) NOT NULL DEFAULT '',
                default_dict_code  VARCHAR(100),
                default_match_mode VARCHAR(50) NOT NULL DEFAULT 'contains',
                description        VARCHAR(500),
                is_enabled         BOOLEAN NOT NULL DEFAULT TRUE,
                sort_order         INTEGER NOT NULL DEFAULT 0,
                created_at         TIMESTAMP NOT NULL DEFAULT NOW(),
                updated_at         TIMESTAMP NOT NULL DEFAULT NOW()
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS ux_esb_dict_template_code
                ON lhyy.esb_dict_template (template_code);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS ix_esb_dict_template_category
                ON lhyy.esb_dict_template (category);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS lhyy.esb_dict_template_item (
                id           SERIAL PRIMARY KEY,
                template_id  INTEGER NOT NULL REFERENCES lhyy.esb_dict_template(id) ON DELETE CASCADE,
                source_value VARCHAR(200) NOT NULL,
                target_value VARCHAR(200) NOT NULL,
                sort_order   INTEGER NOT NULL DEFAULT 0,
                description  VARCHAR(500),
                is_enabled   BOOLEAN NOT NULL DEFAULT TRUE
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS ix_esb_dict_template_item_template_sort
                ON lhyy.esb_dict_template_item (template_id, sort_order);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS lhyy.esb_ocr_profile (
                id                       SERIAL PRIMARY KEY,
                tran_code                VARCHAR(20) NOT NULL,
                integration_project_code VARCHAR(50),
                profile_name             VARCHAR(100),
                is_enabled               BOOLEAN NOT NULL DEFAULT TRUE,
                source_kind              INTEGER NOT NULL DEFAULT 0,
                source_path              VARCHAR(500) NOT NULL DEFAULT '$.pdfPath',
                language                 VARCHAR(50) NOT NULL DEFAULT 'chi_sim',
                dpi                      INTEGER NOT NULL DEFAULT 300,
                page_seg_mode            INTEGER NOT NULL DEFAULT 11,
                max_pages                INTEGER,
                max_input_bytes          BIGINT,
                timeout_seconds          INTEGER NOT NULL DEFAULT 120,
                keep_work_files          BOOLEAN NOT NULL DEFAULT FALSE,
                allowed_file_roots       TEXT,
                output_json_path         VARCHAR(1000),
                description              VARCHAR(500),
                created_at               TIMESTAMP NOT NULL DEFAULT NOW(),
                updated_at               TIMESTAMP NOT NULL DEFAULT NOW()
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS ix_esb_ocr_profile_tran_code
                ON lhyy.esb_ocr_profile (tran_code);
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS ix_esb_ocr_profile_project_tran
                ON lhyy.esb_ocr_profile (integration_project_code, tran_code);
            """);
    }

    private static string EnsureCubeDbSearchPath(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var schemas = string.IsNullOrWhiteSpace(builder.SearchPath)
            ? []
            : builder.SearchPath.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        foreach (var schema in new[] { "public", "care", "form", "target" })
        {
            if (!schemas.Contains(schema, StringComparer.OrdinalIgnoreCase))
                schemas.Add(schema);
        }

        builder.SearchPath = string.Join(",", schemas);
        return builder.ConnectionString;
    }

    private static StaticFileOptions BuildProjectDocumentStaticFileOptions(string contentRootPath)
    {
        var rootPath = Path.Combine(contentRootPath, "ProjectDocuments");
        Directory.CreateDirectory(rootPath);

        return new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(rootPath),
            RequestPath = ProjectDocumentService.RequestPathPrefix
        };
    }
}
