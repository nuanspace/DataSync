using DataSync.CYYY.Components;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using DataSync.CYYY.Models.FollowUp;
using DataSync.CYYY.Services;
using DataSync.CYYY.Services.FollowUp;
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

            // 通用 API 平台，是否忽略证书由平台配置决定。
            builder.Services.AddHttpClient("ApiPlatform");
            builder.Services.AddHttpClient("ApiPlatformInsecure")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                });

            // 服务注册
            builder.Services.AddSingleton<ApiPlatformClient>();
            builder.Services.AddSingleton<SyncTaskSignalService>();
            builder.Services.AddScoped<SyncLogService>();
            builder.Services.AddScoped<PendingSyncService>();
            builder.Services.AddScoped<TaskManagementService>();
            builder.Services.AddScoped<ActiveMedicalRecordClient>();
            builder.Services.AddScoped<ActiveSyncService>();
            builder.Services.AddScoped<PatientContinuousSyncService>();
            builder.Services.AddScoped<PatientContinuousSyncRegistrationService>();
            builder.Services.Configure<FollowUpPackageSyncOptions>(builder.Configuration.GetSection("FollowUpPackageSync"));
            builder.Services.AddScoped<FollowUpPackageRepository>();
            builder.Services.AddScoped<FollowUpPackageSyncService>();
            builder.Services.AddSingleton<FollowUpPackagePullCoordinator>();
            builder.Services.AddSingleton<FollowUpPackageFileStore>();
            builder.Services.AddSingleton<FollowUpPackageRelayClient>();
            builder.Services.AddSingleton<FollowUpPackageKeyService>();
            builder.Services.AddTransient<SyncOrchestrator>();
            builder.Services.AddTransient<PushServiceFactory>();
            builder.Services.AddTransient<ApiPushService>();
            builder.Services.AddTransient<DatabasePushService>();

            // 采集与本地查询
            builder.Services.AddScoped<IngestionService>();
            builder.Services.AddScoped<LocalQueryService>();
            builder.Services.AddScoped<DatabaseQueryService>();

            // 后台任务
            builder.Services.AddHostedService<SyncWorker>();
            builder.Services.AddHostedService<IngestionWorker>();
            builder.Services.AddHostedService<ActiveMedicalRecordSyncWorker>();
            builder.Services.AddHostedService<PatientContinuousSyncWorker>();
            builder.Services.AddHostedService<FollowUpPackagePullWorker>();

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
