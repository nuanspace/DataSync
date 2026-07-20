using Xunit;
using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpRecoverySafetyTests
{
    [Fact]
    public void 恢复头按实际完成时间而不是包序号选择()
    {
        var source = ReadRepositorySource();
        var restoreSource = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageRestoreService.cs");

        Assert.Contains("ORDER BY COALESCE(finished_at, updated_at) DESC, sequence_no DESC", source);
        Assert.Contains("按实际完成顺序倒序逐包恢复", restoreSource);
        Assert.DoesNotContain("按序号从大到小逐包恢复", restoreSource);
    }

    [Fact]
    public void Worker阻断恢复失败和中断中的危险操作状态()
    {
        var source = ReadRepositorySource();

        Assert.Contains("import_status = ANY(@unsafeStatuses)", source);
        Assert.Contains("\"RestoreFailed\", \"Restoring\", \"Importing\"", source);
    }

    [Fact]
    public void 中断状态可作为恢复头重新执行备份恢复()
    {
        Assert.Contains("Importing", FollowUpPackageImportRepository.RestorableImportStatuses);
        Assert.Contains("Restoring", FollowUpPackageImportRepository.RestorableImportStatuses);
    }

    [Fact]
    public void 进入执行中状态时清除旧的完成时间()
    {
        var source = ReadRepositorySource();

        Assert.Contains("WHEN @status IN ('Validating','BackingUp','Importing','Restoring') THEN NULL", source);
    }

    [Fact]
    public void 手工导入服务执行全局危险状态检查()
    {
        var source = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageImportService.cs");

        Assert.Contains("HasUnsafeOperationAsync", source);
        Assert.Contains("CanStartImport", source);
    }

    [Fact]
    public void 离线发布包使用指定环境并强制Bash脚本为LF()
    {
        var releaseScript = ReadSource("deploy", "s7-followup-hospital", "package-release.sh");
        var attributes = ReadSource(".gitattributes");

        Assert.Contains("printf 'RELEASE_VERSION=%s\\n' \"$RELEASE_VERSION\"", releaseScript);
        Assert.DoesNotContain("cp \"$release_env\" \"$stage/.env.example\"", releaseScript);
        Assert.DoesNotContain("cp -a \"$root/config/.\"", releaseScript);
        Assert.DoesNotContain("cp -a \"$root/secrets/.\"", releaseScript);
        Assert.DoesNotContain("cp -a \"$root/database/.\"", releaseScript);
        Assert.DoesNotContain("cp -a \"$root/postgres-cube/.\"", releaseScript);
        Assert.DoesNotContain("cp -a \"$docs_source/.\"", releaseScript);
        Assert.Contains("appsettings.Production.json.example", releaseScript);
        Assert.Contains("cp \"$root/secrets/README.md\"", releaseScript);
        Assert.Contains("restore-fresh-databases.sh", releaseScript);
        Assert.Contains("verify-fresh-databases.sh", releaseScript);
        Assert.Contains("$root/postgres-cube/Dockerfile", releaseScript);
        Assert.Contains("validate_release_docs", releaseScript);
        Assert.Contains("-type l", releaseScript);
        Assert.Contains("不允许隐藏路径", releaseScript);
        Assert.Contains("不支持的文件类型", releaseScript);
        Assert.Contains("command -v pg_restore", releaseScript);
        Assert.Contains("validate_schema_only_dump \"$datasync_dump\"", releaseScript);
        Assert.Contains("validate_schema_only_dump \"$cube_dump\"", releaseScript);
        Assert.Contains("TABLE DATA|MATERIALIZED VIEW DATA|SEQUENCE SET|BLOB|BLOBS", releaseScript);
        Assert.Contains("*.sh text eol=lf", attributes);
        var outputGuard = releaseScript.IndexOf(
            "ensure_output_outside_docs \"$docs_source\" \"$output_parent\"",
            StringComparison.Ordinal);
        var stageCreation = releaseScript.IndexOf("stage=\"$(mktemp", StringComparison.Ordinal);
        Assert.True(outputGuard >= 0 && stageCreation > outputGuard);
        Assert.Contains(
            "unset RELEASE_VERSION CYYY_IMAGE LHYY_IMAGE DATASYNC_DB_IMAGE CUBE_DB_IMAGE",
            releaseScript);
        Assert.Contains("docs_source=\"$(cd \"$docs_source\" && pwd -P)\"", releaseScript);
        Assert.Contains("output_parent=\"$(cd \"$output_parent\" && pwd -P)\"", releaseScript);
        Assert.Contains("image_index=0", releaseScript);
        Assert.Contains("image-%02d-%s.tar", releaseScript);
    }

    [Theory]
    [InlineData(false, "RestoreFailed")]
    [InlineData(true, "Restored")]
    public void 只有数据库和附件尚未恢复完成时才标记恢复失败(bool restoreCompleted, string expected)
    {
        Assert.Equal(expected, FollowUpPackageRestoreService.ResolveTerminalStatus(restoreCompleted));
    }

    [Fact]
    public void 恢复异常时优先写入阻断状态再补审计记录()
    {
        var source = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageRestoreService.cs");
        var catchStart = source.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var markIndex = source.IndexOf("repository.MarkAsync", catchStart, StringComparison.Ordinal);
        var finishIndex = source.IndexOf("FinishRestoreAsync", catchStart, StringComparison.Ordinal);

        Assert.True(markIndex >= 0 && finishIndex >= 0 && markIndex < finishIndex);
    }

    [Fact]
    public async Task 恢复完成标记刷盘后可由新实例继续读取并删除()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-restore-marker-{Guid.NewGuid():N}");
        try
        {
            var options = Options.Create(new FollowUpPackageImportOptions { BackupRoot = root });
            var marker = new FollowUpRestoreCompletionMarker(
                Guid.NewGuid(), "H001", "P001", Guid.NewGuid(), null, null);
            var firstStore = new FollowUpRestoreCompletionStore(options);

            await firstStore.SaveAsync(marker, CancellationToken.None);
            Assert.Empty(await firstStore.ReadCompletedAsync(CancellationToken.None));
            Assert.Equal(marker, Assert.Single(await firstStore.ReadAllAsync(CancellationToken.None)));

            var completed = marker with { RestoredAt = DateTimeOffset.UtcNow, AuditError = "管理库暂时不可用" };
            await firstStore.SaveAsync(completed, CancellationToken.None);

            var secondStore = new FollowUpRestoreCompletionStore(options);
            var persisted = Assert.Single(await secondStore.ReadCompletedAsync(CancellationToken.None));
            Assert.Equal(completed, persisted);

            await secondStore.DeleteAsync(completed.RestoreId, CancellationToken.None);
            Assert.Empty(await secondStore.ReadCompletedAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 明确恢复失败结果可持久化供后台补齐失败审计()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-restore-failed-marker-{Guid.NewGuid():N}");
        try
        {
            var options = Options.Create(new FollowUpPackageImportOptions { BackupRoot = root });
            var store = new FollowUpRestoreCompletionStore(options);
            var marker = new FollowUpRestoreCompletionMarker(
                Guid.NewGuid(), "H001", "P001", Guid.NewGuid(), null, null, "恢复命令失败");

            await store.SaveAsync(marker, CancellationToken.None);

            Assert.Equal(marker, Assert.Single(await store.ReadAllAsync(CancellationToken.None)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void 历史恢复标记缺少失败字段时仍按未知结果读取()
    {
        var restoreId = Guid.NewGuid();
        var backupRecordId = Guid.NewGuid();
        var json = $$"""
            {
              "restoreId": "{{restoreId}}",
              "hospitalCode": "H001",
              "packageId": "P001",
              "backupRecordId": "{{backupRecordId}}",
              "restoredAt": null,
              "auditError": null
            }
            """;

        var marker = JsonSerializer.Deserialize<FollowUpRestoreCompletionMarker>(json, FollowUpJson.Options);

        Assert.NotNull(marker);
        Assert.Null(marker.RestoreError);
        Assert.Equal(restoreId, marker.RestoreId);
    }

    [Fact]
    public void 恢复服务在业务恢复成功后持久化补写标记且注册后台补偿()
    {
        var restoreSource = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageRestoreService.cs");
        var programSource = ReadSource("DataSync.LHYY.V2", "Program.cs");
        var restoreCall = restoreSource.IndexOf("backupService.RestoreAsync", StringComparison.Ordinal);
        var completedMarker = restoreSource.IndexOf(
            "reconciliationMarker = reconciliationMarker with { RestoredAt", StringComparison.Ordinal);
        var markerCall = restoreSource.IndexOf("completionStore.SaveAsync", completedMarker, StringComparison.Ordinal);
        var statusCall = restoreSource.IndexOf("repository.MarkAsync(state.HospitalCode, state.PackageId, \"Restored\"", StringComparison.Ordinal);

        Assert.True(restoreCall >= 0 && completedMarker > restoreCall && markerCall > completedMarker && statusCall > markerCall);
        Assert.Contains("AddHostedService<FollowUpRestoreReconciliationWorker>", programSource);
    }

    [Fact]
    public void 恢复启动先持久化未完成标记再进入阻断状态()
    {
        var source = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageRestoreService.cs");
        var startRestore = source.IndexOf("restoreId = await repository.StartRestoreAsync", StringComparison.Ordinal);
        var markerCreated = source.IndexOf(
            "reconciliationMarker = new FollowUpRestoreCompletionMarker", startRestore, StringComparison.Ordinal);
        var markerSaved = source.IndexOf(
            "completionStore.SaveAsync(reconciliationMarker, cancellationToken)", markerCreated, StringComparison.Ordinal);
        var restoringMarked = source.IndexOf(
            "repository.MarkAsync(state.HospitalCode, state.PackageId, \"Restoring\"", startRestore, StringComparison.Ordinal);
        var restoreCalled = source.IndexOf("backupService.RestoreAsync", startRestore, StringComparison.Ordinal);

        Assert.True(startRestore >= 0
                    && markerCreated > startRestore
                    && markerSaved > markerCreated
                    && restoringMarked > markerSaved
                    && restoreCalled > restoringMarked);
    }

    [Fact]
    public void 新恢复批次在同一事务内终结旧的运行中审计再登记新批次()
    {
        var source = ReadRepositorySource();
        var methodStart = source.IndexOf("public async Task<Guid> StartRestoreAsync", StringComparison.Ordinal);
        var nextMethod = source.IndexOf("public async Task FinishRestoreAsync", methodStart, StringComparison.Ordinal);
        var methodSource = source[methodStart..nextMethod];
        var transactionStarted = methodSource.IndexOf("BeginTransactionAsync", StringComparison.Ordinal);
        var interruptedUpdated = methodSource.IndexOf(
            "UPDATE lhyy.followup_package_restore_record SET", StringComparison.Ordinal);
        var newRestoreInserted = methodSource.IndexOf(
            "INSERT INTO lhyy.followup_package_restore_record", StringComparison.Ordinal);
        var transactionCommitted = methodSource.IndexOf("CommitAsync", StringComparison.Ordinal);

        Assert.True(transactionStarted >= 0
                    && interruptedUpdated > transactionStarted
                    && newRestoreInserted > interruptedUpdated
                    && transactionCommitted > newRestoreInserted);
        Assert.Contains("restore_status = 'Failed'", methodSource);
        Assert.Contains("error_code = @interruptedErrorCode", methodSource);
        Assert.Contains("error_message = @interruptedErrorMessage", methodSource);
        Assert.Contains("AND restore_status = 'Running'", methodSource);
        Assert.Contains("connection, transaction", methodSource);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void 只有已进入恢复状态或业务恢复完成时才写入包终态(
        bool restoreStateEntered,
        bool restoreCompleted,
        bool expected)
    {
        Assert.Equal(expected, FollowUpPackageRestoreService.ShouldWriteTerminalState(
            restoreStateEntered, restoreCompleted));
    }

    [Fact]
    public void 已完成标记预检优先返回当前恢复成功并阻断冲突标记()
    {
        var completed = FollowUpPackageRestoreService.ResolveCompletedMarkerPreflightResult(
            [FollowUpRestoreReconciliationResult.CompletedAuditOnly,
             FollowUpRestoreReconciliationResult.CompletedCurrent]);
        var conflict = FollowUpPackageRestoreService.ResolveCompletedMarkerPreflightResult(
            [FollowUpRestoreReconciliationResult.Conflict]);
        var historical = FollowUpPackageRestoreService.ResolveCompletedMarkerPreflightResult(
            [FollowUpRestoreReconciliationResult.CompletedAuditOnly,
             FollowUpRestoreReconciliationResult.AlreadyCompleted]);

        Assert.NotNull(completed);
        Assert.True(completed.Success);
        Assert.NotNull(conflict);
        Assert.False(conflict.Success);
        Assert.Null(historical);
    }

    [Fact]
    public void 恢复重试在登记新批次前协调同包已完成标记()
    {
        var source = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageRestoreService.cs");
        var leaseAcquired = source.IndexOf("TryAcquireRecoveryExclusiveAsync", StringComparison.Ordinal);
        var completedMarkerPreflight = source.IndexOf(
            "ReconcileCompletedMarkersAsync(state", StringComparison.Ordinal);
        var newRestoreStarted = source.IndexOf(
            "restoreId = await repository.StartRestoreAsync", StringComparison.Ordinal);
        var preflightMethod = source.IndexOf(
            "private async Task<FollowUpImportOperationResult?> ReconcileCompletedMarkersAsync", StringComparison.Ordinal);

        Assert.True(leaseAcquired >= 0
                    && completedMarkerPreflight > leaseAcquired
                    && newRestoreStarted > completedMarkerPreflight
                    && preflightMethod > newRestoreStarted);
        Assert.Contains("completionStore.ReadCompletedAsync", source[preflightMethod..]);
        Assert.Contains("completionReconciler.ReconcileAsync", source[preflightMethod..]);
    }

    [Fact]
    public void 恢复审计记录不存在时补写必须失败并保留完成标记()
    {
        var source = ReadRepositorySource();
        var methodStart = source.IndexOf("public async Task FinishRestoreAsync", StringComparison.Ordinal);
        var nextMethod = source.IndexOf("public async Task EnqueueAckAsync", methodStart, StringComparison.Ordinal);
        var methodSource = source[methodStart..nextMethod];

        Assert.Contains("var affected = await command.ExecuteNonQueryAsync", methodSource);
        Assert.Contains("if (affected != 1)", methodSource);
    }

    [Fact]
    public void 旧恢复标记不能完成当前新恢复状态()
    {
        var oldRestoreId = Guid.NewGuid();
        var currentRestoreId = Guid.NewGuid();
        var source = ReadRepositorySource();

        Assert.False(FollowUpPackageImportRepository.ShouldUpdateRestoredState(
            oldRestoreId, currentRestoreId, "Restoring"));
        Assert.True(FollowUpPackageImportRepository.ShouldUpdateRestoredState(
            currentRestoreId, currentRestoreId, "Restoring"));
        Assert.False(FollowUpPackageImportRepository.ShouldUpdateRestoredState(
            currentRestoreId, currentRestoreId, "Imported"));
        Assert.Contains("restoreStatus is not (\"Running\" or \"Completed\")", source);
        Assert.DoesNotContain("if (restoreStatus == \"Completed\")", source);
    }

    [Fact]
    public void 未完成旧标记只在新恢复批次出现后标记为中断()
    {
        var currentRestoreId = Guid.NewGuid();
        var oldRestoreId = Guid.NewGuid();

        Assert.Equal(FollowUpRestoreReconciliationResult.PendingCurrent,
            FollowUpPackageImportRepository.ResolvePendingReconciliation(
                currentRestoreId, currentRestoreId, "Running"));
        Assert.Equal(FollowUpRestoreReconciliationResult.FailedFromMarker,
            FollowUpPackageImportRepository.ResolvePendingReconciliation(
                currentRestoreId, currentRestoreId, "Running", true));
        Assert.Equal(FollowUpRestoreReconciliationResult.Conflict,
            FollowUpPackageImportRepository.ResolvePendingReconciliation(
                currentRestoreId, currentRestoreId, "Completed", true));
        Assert.Equal(FollowUpRestoreReconciliationResult.SupersededInterrupted,
            FollowUpPackageImportRepository.ResolvePendingReconciliation(
                oldRestoreId, currentRestoreId, "Running"));
        Assert.Equal(FollowUpRestoreReconciliationResult.AlreadyTerminal,
            FollowUpPackageImportRepository.ResolvePendingReconciliation(
                oldRestoreId, currentRestoreId, "Failed"));
        Assert.Equal(FollowUpRestoreReconciliationResult.CompletedFromAudit,
            FollowUpPackageImportRepository.ResolvePendingReconciliation(
                currentRestoreId, currentRestoreId, "Completed"));
        Assert.True(FollowUpPackageImportRepository.ShouldUpdateRestoreFailedState(
            currentRestoreId, currentRestoreId, "Restoring"));
        Assert.False(FollowUpPackageImportRepository.ShouldUpdateRestoreFailedState(
            oldRestoreId, currentRestoreId, "Restoring"));
    }

    [Fact]
    public void 前台明确恢复失败时先保存失败标记再写管理状态和审计()
    {
        var source = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageRestoreService.cs");
        var catchStart = source.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var failureMarker = source.IndexOf("RestoreError = ex.Message", catchStart, StringComparison.Ordinal);
        var markerSave = source.IndexOf("completionStore.SaveAsync", failureMarker, StringComparison.Ordinal);
        var stateWrite = source.IndexOf("repository.MarkAsync", markerSave, StringComparison.Ordinal);

        Assert.True(failureMarker >= 0 && markerSave > failureMarker && stateWrite > markerSave);
    }

    [Fact]
    public void 恢复完成日志写入后才删除完成标记且后台支持幂等补写()
    {
        var restoreSource = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageRestoreService.cs");
        var repositorySource = ReadRepositorySource();
        var workerSource = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpRestoreReconciliationWorker.cs");
        var logCall = restoreSource.IndexOf("repository.AddRestoreCompletionLogAsync", StringComparison.Ordinal);
        var deleteCall = restoreSource.IndexOf("completionStore.DeleteAsync", StringComparison.Ordinal);

        Assert.True(logCall >= 0 && deleteCall > logCall);
        Assert.Contains("detail_json->>'restoreId' = @restoreId", repositorySource);
        Assert.Contains("SELECT id FROM lhyy.followup_package_restore_record", repositorySource);
        Assert.Contains("FOR UPDATE", repositorySource);
        Assert.Contains("ReadAllAsync", workerSource);
    }

    [Fact]
    public async Task 单个恢复标记失败后仍继续补写后续标记()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-restore-worker-{Guid.NewGuid():N}");
        try
        {
            var options = Options.Create(new FollowUpPackageImportOptions { BackupRoot = root });
            var store = new FollowUpRestoreCompletionStore(options);
            var failedMarker = CompletedMarker("P-FAIL");
            var completedMarker = CompletedMarker("P-OK");
            await store.SaveAsync(failedMarker, CancellationToken.None);
            await store.SaveAsync(completedMarker, CancellationToken.None);
            var reconciler = new FakeRestoreCompletionReconciler(failedMarker.RestoreId);
            var services = new ServiceCollection()
                .AddScoped<IFollowUpRestoreCompletionReconciler>(_ => reconciler)
                .BuildServiceProvider();
            var worker = new FollowUpRestoreReconciliationWorker(
                services.GetRequiredService<IServiceScopeFactory>(),
                store,
                NullLogger<FollowUpRestoreReconciliationWorker>.Instance);

            await worker.ReconcileAsync(CancellationToken.None);

            Assert.Contains(failedMarker.RestoreId, reconciler.Calls);
            Assert.Contains(completedMarker.RestoreId, reconciler.Calls);
            var remaining = Assert.Single(await store.ReadCompletedAsync(CancellationToken.None));
            Assert.Equal(failedMarker.RestoreId, remaining.RestoreId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void 只有状态恢复记录和日志均落库后才清理恢复标记(
        bool stateWritten,
        bool auditWritten,
        bool logWritten,
        bool expected)
    {
        Assert.Equal(expected, FollowUpPackageRestoreService.CanDeleteRestoreMarker(
            stateWritten, auditWritten, logWritten));
    }

    private static FollowUpRestoreCompletionMarker CompletedMarker(string packageId) =>
        new(Guid.NewGuid(), "H001", packageId, Guid.NewGuid(), DateTimeOffset.UtcNow, null);

    private sealed class FakeRestoreCompletionReconciler(Guid failedRestoreId)
        : IFollowUpRestoreCompletionReconciler
    {
        public List<Guid> Calls { get; } = [];

        public Task<FollowUpRestoreReconciliationResult> ReconcileAsync(
            FollowUpRestoreCompletionMarker marker,
            CancellationToken cancellationToken)
        {
            Calls.Add(marker.RestoreId);
            if (marker.RestoreId == failedRestoreId)
                throw new InvalidOperationException("模拟单个标记补写失败");
            return Task.FromResult(FollowUpRestoreReconciliationResult.CompletedCurrent);
        }
    }

    private static string ReadRepositorySource() =>
        ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageImportRepository.cs");

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataSync.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
    }
}
