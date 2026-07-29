using Xunit;
using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
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
    public void 清理锁竞争失败时释放未持有租约的连接()
    {
        var source = ReadRepositorySource();
        var methodStart = source.IndexOf("TryAcquireStorageCleanupPackageLockAsync", StringComparison.Ordinal);
        var dispose = source.IndexOf("await connection.DisposeAsync();", methodStart, StringComparison.Ordinal);
        var nullReturn = source.IndexOf("return null;", dispose, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && dispose > methodStart && nullReturn > dispose);
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
        Assert.Contains("datasync-cyyy-${safe_version}.tar", releaseScript);
        Assert.Contains("datasync-lhyy-v2-${safe_version}.tar", releaseScript);
        Assert.Contains("datasync-db-${safe_version}.tar", releaseScript);
        Assert.Contains("cube-db-${safe_version}.tar", releaseScript);
        Assert.Contains("manifest/package-manifest.json", releaseScript);
        Assert.Contains("manifest/FILES.csv", releaseScript);
        Assert.Contains("requiredFor,purpose,order", releaseScript);
        Assert.Contains("database/restore-fresh-databases.sh) required_for=\"all\"", releaseScript);
        Assert.Contains("RELEASE_VERSION|DEPLOYMENT_MODE|CYYY_IMAGE", releaseScript);
        Assert.Contains("交付文件路径不能包含逗号、双引号或换行", releaseScript);
    }

    [Fact]
    public void LHYY部署同时提供AspNetCore和BioCore配置文件名()
    {
        foreach (var composeName in new[] { "docker-compose.yml", "docker-compose.fresh-cube.yml" })
        {
            var compose = ReadSource("deploy", "s7-followup-hospital", composeName);
            Assert.Contains("./config/lhyy/appsettings.Production.json:/app/appsettings.Production.json:ro", compose);
            Assert.Contains("./config/lhyy/appsettings.Production.json:/app/appsettings.json:ro", compose);
        }
    }

    [Fact]
    public void ExternalCube模式不定义Cube服务或Secret()
    {
        var externalCompose = ReadSource("deploy", "s7-followup-hospital", "docker-compose.yml");
        var freshCompose = ReadSource("deploy", "s7-followup-hospital", "docker-compose.fresh-cube.yml");
        var externalConfig = ReadSource("deploy", "s7-followup-hospital", "config", "lhyy", "appsettings.Production.json.example");
        var freshConfig = ReadSource("deploy", "s7-followup-hospital", "config", "lhyy", "appsettings.Production.fresh-cube.json.example");

        Assert.DoesNotContain("  cube-db:", externalCompose);
        Assert.DoesNotContain("cube_db_password", externalCompose);
        Assert.Contains("  cube-db:", freshCompose);
        Assert.Contains("cube_db_password", freshCompose);
        Assert.Contains("\"Mode\": \"external-cube\"", externalConfig);
        Assert.Contains("\"Mode\": \"fresh-cube\"", freshConfig);
    }

    [Theory]
    [InlineData(false, "RestoreFailed")]
    [InlineData(true, "Restored")]
    public void 只有数据库和附件尚未恢复完成时才标记恢复失败(bool restoreCompleted, string expected)
    {
        Assert.Equal(expected, FollowUpPackageRestoreService.ResolveTerminalStatus(restoreCompleted));
    }

    [Fact]
    public void 数据库和附件已恢复但快照清理失败时仍按恢复完成处理()
    {
        var exception = new FollowUpRestoreCleanupException(
            [new IOException("临时快照清理失败")]);

        Assert.True(FollowUpPackageRestoreService.IsRestoreCompletedException(exception));
        Assert.Equal("Restored", FollowUpPackageRestoreService.ResolveTerminalStatus(
            FollowUpPackageRestoreService.IsRestoreCompletedException(exception)));
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

    [Fact]
    public async Task 附件校验失败时不得标记目标附件已开始变更()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-mutation-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(target, "hospital-newer-value");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-newer-value"));
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                hash: new string('0', 64),
                sizeBytes: new FileInfo(source).Length);
            var service = AttachmentBackupService(attachmentRoot, root);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.InstallAttachmentsAsync(
                    package,
                    backupRoot,
                    CancellationToken.None));

            Assert.Equal("hospital-newer-value", await File.ReadAllTextAsync(target));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件成功原子替换后上报实际变更路径和包内Hash()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-mutation-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(target, "hospital-old-value");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old-value"));
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot, root);

            var mutations = await service.InstallAttachmentsAsync(
                package,
                backupRoot,
                CancellationToken.None);

            var installed = Assert.Single(mutations);
            Assert.Equal("review.txt", installed.RelativePath);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), installed.InstalledHash);
            Assert.Equal("package-value", await File.ReadAllTextAsync(target));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 部分附件替换失败只补偿实际成功路径且不覆盖未触碰的医院新版本()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-restore-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "files", "uploads"));
            Directory.CreateDirectory(attachmentRoot);
            var firstSource = Path.Combine(staging, "files", "uploads", "first.txt");
            var secondSource = Path.Combine(staging, "files", "uploads", "second.txt");
            var thirdSource = Path.Combine(staging, "files", "uploads", "third.txt");
            await File.WriteAllTextAsync(firstSource, "first-package");
            await File.WriteAllTextAsync(secondSource, "second-package");
            await File.WriteAllTextAsync(thirdSource, "third-package");
            await File.WriteAllTextAsync(Path.Combine(attachmentRoot, "first.txt"), "first-old");
            await File.WriteAllTextAsync(Path.Combine(attachmentRoot, "second.txt"), "second-old");
            await WriteAttachmentBackupAsync(
                backupRoot,
                ("first.txt", "first-old"),
                ("second.txt", "second-old"),
                ("third.txt", null));
            var firstBytes = await File.ReadAllBytesAsync(firstSource);
            var thirdBytes = await File.ReadAllBytesAsync(thirdSource);
            var package = AttachmentPackage(
                staging,
                new FollowUpAttachmentManifest
                {
                    Path = "files/uploads/first.txt",
                    Hash = Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant(),
                    SizeBytes = firstBytes.Length
                },
                new FollowUpAttachmentManifest
                {
                    Path = "files/uploads/second.txt",
                    Hash = new string('0', 64),
                    SizeBytes = new FileInfo(secondSource).Length
                },
                new FollowUpAttachmentManifest
                {
                    Path = "files/uploads/third.txt",
                    Hash = Convert.ToHexString(SHA256.HashData(thirdBytes)).ToLowerInvariant(),
                    SizeBytes = thirdBytes.Length
                });
            var service = AttachmentBackupService(attachmentRoot);
            await File.WriteAllTextAsync(Path.Combine(attachmentRoot, "second.txt"), "second-hospital-newer");
            await File.WriteAllTextAsync(Path.Combine(attachmentRoot, "third.txt"), "third-hospital-new");

            var installException = await Assert.ThrowsAsync<FollowUpAttachmentInstallException>(() =>
                service.InstallAttachmentsAsync(
                    package,
                    backupRoot,
                    CancellationToken.None));
            Assert.IsType<InvalidDataException>(installException.InnerException);
            var mutations = installException.Mutations;
            var mutation = Assert.Single(mutations);
            Assert.Equal("first.txt", mutation.RelativePath);
            Assert.Equal("first-package", await File.ReadAllTextAsync(Path.Combine(attachmentRoot, "first.txt")));

            var skipped = await service.RestoreInstalledAttachmentsAsync(
                backupRoot,
                mutations,
                CancellationToken.None);

            Assert.Empty(skipped);
            Assert.Equal("first-old", await File.ReadAllTextAsync(Path.Combine(attachmentRoot, "first.txt")));
            Assert.Equal("second-hospital-newer", await File.ReadAllTextAsync(Path.Combine(attachmentRoot, "second.txt")));
            Assert.Equal("third-hospital-new", await File.ReadAllTextAsync(Path.Combine(attachmentRoot, "third.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 已触碰附件在补偿前被外部更新时不得覆盖新版本()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-conflict-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "files", "uploads"));
            Directory.CreateDirectory(attachmentRoot);
            var source = Path.Combine(staging, "files", "uploads", "review.txt");
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(Path.Combine(attachmentRoot, "review.txt"), "hospital-old");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var mutations = await service.InstallAttachmentsAsync(package, backupRoot, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(attachmentRoot, "review.txt"), "hospital-newest");

            var skipped = await service.RestoreInstalledAttachmentsAsync(
                backupRoot,
                mutations,
                CancellationToken.None);

            Assert.Contains("review.txt", skipped);
            Assert.Equal("hospital-newest", await File.ReadAllTextAsync(Path.Combine(attachmentRoot, "review.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 完整附件恢复必须在写入前校验全部备份内容()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-full-restore-validation-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var firstTarget = Path.Combine(attachmentRoot, "first.txt");
        var secondTarget = Path.Combine(attachmentRoot, "second.txt");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(firstTarget, "first-current");
            await File.WriteAllTextAsync(secondTarget, "second-current");
            await WriteAttachmentBackupAsync(
                backupRoot,
                ("first.txt", "first-old"),
                ("second.txt", "second-old"));
            await File.WriteAllTextAsync(Path.Combine(backupRoot, "second.txt"), "corrupted");
            var service = AttachmentBackupService(attachmentRoot);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAttachmentsAsync(backupRoot, CancellationToken.None));

            Assert.Equal("first-current", await File.ReadAllTextAsync(firstTarget));
            Assert.Equal("second-current", await File.ReadAllTextAsync(secondTarget));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 完整附件恢复中声明不存在的路径被目录占位时必须失败()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-full-restore-directory-conflict-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(target);
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", null));
            var service = AttachmentBackupService(attachmentRoot);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAttachmentsAsync(backupRoot, CancellationToken.None));

            Assert.Contains("目录项类型", exception.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 完整恢复时附件清单删除条目必须在数据库恢复前阻断(bool hasManifestAnchor)
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-backup-manifest-anchor-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "attachments");
        var databasePath = Path.Combine(root, "database.dump");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await WriteAttachmentBackupAsync(
                backupRoot,
                ("first.txt", "first-old"),
                ("second.txt", "second-old"));
            var internalManifestPath = Path.Combine(backupRoot, "attachment-backup.json");
            var manifestPath = hasManifestAnchor
                ? Path.Combine(root, "attachment-backup.json")
                : internalManifestPath;
            if (hasManifestAnchor) File.Move(internalManifestPath, manifestPath);
            var manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath)))
                .ToLowerInvariant();
            var size = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                root,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                size,
                hasManifestAnchor ? manifestHash : null,
                hasManifestAnchor ? 2 : null);
            await WriteAttachmentBackupAsync(backupRoot, ("first.txt", "first-old"));
            if (hasManifestAnchor) File.Move(internalManifestPath, manifestPath, overwrite: true);
            var service = AttachmentBackupService(attachmentRoot, root);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAsync(artifact, CancellationToken.None));

            Assert.Contains("清单", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 完整恢复对登记hash校验和清单解析必须使用同一份字节()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-manifest-frozen-bytes-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "attachments");
        var databasePath = Path.Combine(root, "database.dump");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await WriteAttachmentBackupAsync(
                backupRoot,
                ("first.txt", "first-old"),
                ("second.txt", "second-old"));
            var internalManifestPath = Path.Combine(backupRoot, "attachment-backup.json");
            var manifestPath = Path.Combine(root, "attachment-backup.json");
            File.Move(internalManifestPath, manifestPath);
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                root,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length),
                Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                2);
            var service = AttachmentBackupService(attachmentRoot, root);

            var paths = await service.ValidateRegisteredAttachmentBackupAsync(
                artifact,
                path => File.WriteAllText(path, File.ReadAllText(path).Replace("first.txt", "ghost.txt")),
                CancellationToken.None);

            Assert.Contains("first.txt", paths);
            Assert.DoesNotContain("ghost.txt", paths);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 数据库恢复必须使用复制后校验hash的临时快照()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-database-restore-snapshot-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "database.dump");
        FollowUpDatabaseRestoreSnapshot? snapshot = null;
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(databasePath, "registered-database");
            var registeredSize = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                root,
                databasePath,
                Path.Combine(root, "attachments"),
                HashText("registered-database"),
                registeredSize);
            var service = AttachmentBackupService(Path.Combine(root, "uploads"), root);

            snapshot = await service.CreateValidatedDatabaseSnapshotAsync(
                artifact,
                CancellationToken.None);
            Assert.False(
                Path.GetFullPath(snapshot.FilePath).StartsWith(
                    Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Assert.Equal(
                registeredSize,
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length));

            await File.WriteAllTextAsync(databasePath, "tampered-database");

            Assert.Equal("registered-database", await File.ReadAllTextAsync(snapshot.FilePath));
            Assert.NotEqual(databasePath, snapshot.FilePath);
        }
        finally
        {
            if (snapshot is not null && Directory.Exists(snapshot.WorkRoot))
                Directory.Delete(snapshot.WorkRoot, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 新登记恢复缺少外置清单时不得回退到旧目录内清单()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-strict-external-manifest-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "attachments");
        var databasePath = Path.Combine(root, "database.dump");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var internalManifestPath = Path.Combine(backupRoot, "attachment-backup.json");
            var manifestBytes = await File.ReadAllBytesAsync(internalManifestPath);
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                root,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length),
                Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                1);
            var service = AttachmentBackupService(attachmentRoot, root);

            var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                service.ValidateRegisteredAttachmentBackupAsync(
                    artifact,
                    afterManifestRead: null,
                    CancellationToken.None));

            Assert.Equal(Path.Combine(root, "attachment-backup.json"), exception.FileName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 登记附件快照必须位于BackupRoot外且源备份变化不影响快照()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-frozen-attachment-snapshot-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "attachments");
        var databasePath = Path.Combine(root, "database.dump");
        object? snapshot = null;
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var internalManifestPath = Path.Combine(backupRoot, "attachment-backup.json");
            var externalManifestPath = Path.Combine(root, "attachment-backup.json");
            File.Move(internalManifestPath, externalManifestPath);
            var manifestBytes = await File.ReadAllBytesAsync(externalManifestPath);
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                root,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length),
                Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                1);
            var service = AttachmentBackupService(attachmentRoot, root);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "CreateValidatedAttachmentSnapshotAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [typeof(FollowUpBackupArtifact), typeof(CancellationToken)],
                modifiers: null);
            Assert.True(method is not null, "完整恢复和导入补偿需要可复用的登记附件冻结快照 API。");

            snapshot = await AwaitTaskResultAsync<object>(method!.Invoke(
                service,
                [artifact, CancellationToken.None]));
            var frozenRoot = Assert.IsType<string>(snapshot.GetType()
                .GetProperty("AttachmentBackupPath", BindingFlags.Instance | BindingFlags.Public)!
                .GetValue(snapshot));
            Assert.False(
                Path.GetFullPath(frozenRoot).StartsWith(
                    Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

            await File.WriteAllTextAsync(Path.Combine(backupRoot, "review.txt"), "tampered-after-freeze");

            Assert.Equal("hospital-old", await File.ReadAllTextAsync(Path.Combine(frozenRoot, "review.txt")));
        }
        finally
        {
            if (snapshot is IAsyncDisposable disposable) await disposable.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BackupRoot覆盖系统临时目录时附件快照必须在创建目录前安全阻断()
    {
        var systemTempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(systemTempRoot, $"followup-invalid-snapshot-root-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "attachments");
        var databasePath = Path.Combine(root, "database.dump");
        var before = Directory.EnumerateDirectories(systemTempRoot, "datasync-followup-attachments-*")
            .Select(Path.GetFullPath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var internalManifestPath = Path.Combine(backupRoot, "attachment-backup.json");
            var externalManifestPath = Path.Combine(root, "attachment-backup.json");
            File.Move(internalManifestPath, externalManifestPath);
            var manifestBytes = await File.ReadAllBytesAsync(externalManifestPath);
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                root,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length),
                Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                1);
            var service = AttachmentBackupService(attachmentRoot, systemTempRoot);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateValidatedAttachmentSnapshotAsync(artifact, CancellationToken.None));

            var after = Directory.EnumerateDirectories(systemTempRoot, "datasync-followup-attachments-*")
                .Select(Path.GetFullPath)
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            Assert.True(before.SetEquals(after), "安全阻断不得留下未登记附件快照目录。");
        }
        finally
        {
            foreach (var path in Directory.EnumerateDirectories(systemTempRoot, "datasync-followup-attachments-*")
                         .Select(Path.GetFullPath)
                         .Where(path => !before.Contains(path)))
                Directory.Delete(path, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 安装和补偿必须复用登记附件冻结快照而不是重新信任可变清单()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-frozen-attachment-install-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var artifactRoot = Path.Combine(root, "artifact");
        var backupRoot = Path.Combine(artifactRoot, "attachments");
        var databasePath = Path.Combine(artifactRoot, "database.dump");
        object? snapshot = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "files", "uploads"));
            Directory.CreateDirectory(attachmentRoot);
            Directory.CreateDirectory(artifactRoot);
            await File.WriteAllTextAsync(Path.Combine(attachmentRoot, "review.txt"), "hospital-old");
            await File.WriteAllTextAsync(Path.Combine(staging, "files", "uploads", "review.txt"), "package-value");
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var internalManifestPath = Path.Combine(backupRoot, "attachment-backup.json");
            var externalManifestPath = Path.Combine(artifactRoot, "attachment-backup.json");
            File.Move(internalManifestPath, externalManifestPath);
            var manifestBytes = await File.ReadAllBytesAsync(externalManifestPath);
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                artifactRoot,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                Directory.EnumerateFiles(artifactRoot, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length),
                Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                1);
            var service = AttachmentBackupService(attachmentRoot, root);
            var snapshotMethod = typeof(FollowUpPackageBackupService).GetMethod(
                "CreateValidatedAttachmentSnapshotAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [typeof(FollowUpBackupArtifact), typeof(CancellationToken)],
                modifiers: null);
            Assert.NotNull(snapshotMethod);
            snapshot = await AwaitTaskResultAsync<object>(snapshotMethod!.Invoke(
                service,
                [artifact, CancellationToken.None]));
            var snapshotType = snapshot.GetType();
            var installMethod = typeof(FollowUpPackageBackupService).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(method => method.Name == "InstallAttachmentsAsync"
                                           && method.GetParameters().Length == 3
                                           && method.GetParameters()[1].ParameterType == snapshotType);
            var restoreMethod = typeof(FollowUpPackageBackupService).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(method => method.Name == "RestoreInstalledAttachmentsAsync"
                                           && method.GetParameters().Length == 3
                                           && method.GetParameters()[0].ParameterType == snapshotType);
            Assert.True(installMethod is not null && restoreMethod is not null,
                "附件安装和补偿必须提供接收同一登记冻结快照的 API。");

            await File.WriteAllTextAsync(Path.Combine(backupRoot, "review.txt"), "tampered-backup");
            await File.WriteAllTextAsync(externalManifestPath, "tampered-manifest");
            var packageBytes = await File.ReadAllBytesAsync(Path.Combine(staging, "files", "uploads", "review.txt"));
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                packageBytes.Length);

            var mutations = await AwaitTaskResultAsync<object>(installMethod!.Invoke(
                service,
                [package, snapshot, CancellationToken.None]));
            Assert.Equal("package-value", await File.ReadAllTextAsync(Path.Combine(attachmentRoot, "review.txt")));

            _ = await AwaitTaskResultAsync<object>(restoreMethod!.Invoke(
                service,
                [snapshot, mutations, CancellationToken.None]));
            Assert.Equal("hospital-old", await File.ReadAllTextAsync(Path.Combine(attachmentRoot, "review.txt")));
        }
        finally
        {
            if (snapshot is IAsyncDisposable disposable) await disposable.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void 完整恢复和历史Sidecar临时清理失败必须聚合而非覆盖主异常()
    {
        var source = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageBackupService.cs");
        AssertMethodContains(source,
            "public async Task<FollowUpBackupArtifact> CreateAsync(",
            "public async Task RestoreAsync(",
            "AggregateException");
        AssertMethodContains(source,
            "private async Task RestoreAttachmentsAsync(",
            "internal async Task<IReadOnlyList<string>> RestoreInstalledAttachmentsAsync(",
            "AggregateException");
        AssertMethodContains(source,
            "private static async Task PublishLegacyArtifactAnchorAsync(",
            "private static int ReadManifestEntryCount(",
            "AggregateException");
        AssertMethodContains(source,
            "private async Task<List<AttachmentBackupEntry>> LoadOrCreateLegacyHashBaselineAsync(",
            "private static void ValidateLegacyBaselineMatches(",
            "AggregateException");
    }

    [Theory]
    [InlineData("files/uploads/attachment-backup.json")]
    [InlineData("files/uploads/attachment-backup.hash-baseline.v2.json")]
    [InlineData("files/uploads/attachment-backup.artifact-anchor.v2.json")]
    public void 备份元数据移出附件目录后同名附件仍可规范化(string path)
    {
        Assert.Equal(
            Path.GetFileName(path),
            FollowUpPackageBackupService.NormalizeAttachmentPath(path));
    }

    [Theory]
    [InlineData("attachment-backup.json")]
    [InlineData("attachment-backup.hash-baseline.v2.json")]
    [InlineData("attachment-backup.artifact-anchor.v2.json")]
    public async Task 外置元数据不得与同名附件备份冲突(string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-external-backup-metadata-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "attachments");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            Directory.CreateDirectory(backupRoot);
            await File.WriteAllTextAsync(Path.Combine(backupRoot, fileName), "hospital-old");
            await File.WriteAllTextAsync(
                Path.Combine(root, "attachment-backup.json"),
                JsonSerializer.Serialize(new
                {
                    version = 2,
                    entries = new[]
                    {
                        new
                        {
                            relativePath = fileName,
                            existed = true,
                            hash = HashText("hospital-old")
                        }
                    }
                }, FollowUpJson.Options));
            var service = AttachmentBackupService(attachmentRoot);

            await service.RestoreAttachmentsAsync(backupRoot, CancellationToken.None);

            Assert.Equal("hospital-old", await File.ReadAllTextAsync(Path.Combine(attachmentRoot, fileName)));
            Assert.Equal(
                Path.Combine(root, "attachment-backup.json"),
                FollowUpPackageBackupService.GetAttachmentBackupManifestPath(backupRoot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 旧记录和旧版无Hash清单首次预检同时生成双锚点且二次可继续()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-legacy-double-anchor-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "attachments");
        var databasePath = Path.Combine(root, "database.dump");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            Directory.CreateDirectory(backupRoot);
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await File.WriteAllTextAsync(Path.Combine(backupRoot, "review.txt"), "hospital-old");
            await File.WriteAllTextAsync(
                Path.Combine(backupRoot, "attachment-backup.json"),
                JsonSerializer.Serialize(
                    new[] { new { relativePath = "review.txt", existed = true } },
                    FollowUpJson.Options));
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                root,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length));
            var service = AttachmentBackupService(attachmentRoot, root);

            var firstAttempt = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ValidateRegisteredAttachmentBackupAsync(
                    artifact,
                    afterManifestRead: null,
                    CancellationToken.None));

            Assert.Contains("两份信任锚点", firstAttempt.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, "attachment-backup.artifact-anchor.v2.json")));
            Assert.True(File.Exists(Path.Combine(root, "attachment-backup.hash-baseline.v2.json")));

            var paths = await service.ValidateRegisteredAttachmentBackupAsync(
                artifact,
                afterManifestRead: null,
                CancellationToken.None);

            Assert.Equal(["review.txt"], paths);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 旧记录前置完整性失败不得提前发布信任锚点()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-legacy-anchor-gate-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "attachments");
        var databasePath = Path.Combine(root, "database.dump");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var registeredSize = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                root,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                registeredSize + 1);
            var service = AttachmentBackupService(attachmentRoot, root);
            var anchorPath = Path.Combine(root, "attachment-backup.artifact-anchor.v2.json");

            var invalidAttempt = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ValidateRegisteredAttachmentBackupAsync(
                    artifact,
                    afterManifestRead: null,
                    CancellationToken.None));

            Assert.Contains("大小", invalidAttempt.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(anchorPath));

            var reviewAttempt = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ValidateRegisteredAttachmentBackupAsync(
                    artifact with { SizeBytes = registeredSize },
                    afterManifestRead: null,
                    CancellationToken.None));

            Assert.Contains("人工核对", reviewAttempt.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(anchorPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Sidecar发布临时残留不得污染登记备份大小()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-sidecar-staging-residue-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "attachments");
        var databasePath = Path.Combine(root, "database.dump");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var internalManifestPath = Path.Combine(backupRoot, "attachment-backup.json");
            var manifestPath = Path.Combine(root, "attachment-backup.json");
            File.Move(internalManifestPath, manifestPath);
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
            var registeredSize = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                root,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                registeredSize,
                Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                1);
            var stagingRoot = Path.Combine(
                root,
                FollowUpPackageBackupService.AttachmentBackupMetadataStagingDirectoryName);
            Directory.CreateDirectory(stagingRoot);
            await File.WriteAllTextAsync(Path.Combine(stagingRoot, "crashed.artifact-anchor.tmp"), "residue");
            var service = AttachmentBackupService(attachmentRoot, root);

            var paths = await service.ValidateRegisteredAttachmentBackupAsync(
                artifact,
                afterManifestRead: null,
                CancellationToken.None);

            Assert.Equal(["review.txt"], paths);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 新登记外置清单丢失时清理不得回退到同名附件()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-cleanup-manifest-path-{Guid.NewGuid():N}");
        var backupRoot = Path.Combine(root, "attachments");
        try
        {
            Directory.CreateDirectory(backupRoot);
            var sameNameAttachment = Path.Combine(backupRoot, "attachment-backup.json");
            await File.WriteAllTextAsync(sameNameAttachment, "business-attachment");

            var registeredManifest = FollowUpPackageBackupService.GetRegisteredAttachmentBackupManifestPath(
                backupRoot,
                requireExternalManifest: true);

            Assert.Equal(Path.Combine(root, "attachment-backup.json"), registeredManifest);
            Assert.False(File.Exists(registeredManifest));
            Assert.NotEqual(sameNameAttachment, registeredManifest);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 登记备份元数据符号链接必须在读取前阻断()
    {
        var managedRoot = Path.Combine(Path.GetTempPath(), $"followup-managed-backup-{Guid.NewGuid():N}");
        var artifactRoot = Path.Combine(managedRoot, "record");
        var attachmentRoot = Path.Combine(managedRoot, "uploads");
        var backupRoot = Path.Combine(artifactRoot, "attachments");
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"followup-outside-metadata-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(backupRoot);
            Directory.CreateDirectory(outsideRoot);
            var databasePath = Path.Combine(artifactRoot, "database.dump");
            await File.WriteAllTextAsync(databasePath, "database-backup");
            var outsideManifest = Path.Combine(outsideRoot, "attachment-backup.json");
            await WriteAttachmentBackupAsync(outsideRoot, ("review.txt", "hospital-old"));
            File.CreateSymbolicLink(Path.Combine(artifactRoot, "attachment-backup.json"), outsideManifest);
            var manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(outsideManifest)))
                .ToLowerInvariant();
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                artifactRoot,
                databasePath,
                backupRoot,
                HashText("database-backup"),
                0,
                manifestHash,
                1);
            var service = AttachmentBackupService(attachmentRoot, managedRoot);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ValidateRegisteredAttachmentBackupAsync(
                    artifact,
                    afterManifestRead: null,
                    CancellationToken.None));

            Assert.Contains("符号链接", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(managedRoot)) Directory.Delete(managedRoot, true);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, true);
        }
    }

    [Fact]
    public async Task 附件路径包含符号链接时不得越过AttachmentRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-link-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var outsideRoot = Path.Combine(root, "outside");
        var backupRoot = Path.Combine(root, "backup");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            Directory.CreateDirectory(outsideRoot);
            Directory.CreateSymbolicLink(Path.Combine(attachmentRoot, "linked"), outsideRoot);
            await File.WriteAllTextAsync(Path.Combine(outsideRoot, "review.txt"), "outside-current");
            await WriteAttachmentBackupAsync(backupRoot, (Path.Combine("linked", "review.txt"), "hospital-old"));
            var service = AttachmentBackupService(attachmentRoot);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAttachmentsAsync(backupRoot, CancellationToken.None));

            Assert.Contains("符号链接", exception.Message, StringComparison.Ordinal);
            Assert.Equal("outside-current", await File.ReadAllTextAsync(Path.Combine(outsideRoot, "review.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 完整附件恢复在复制前必须复核并发替换的父目录链接()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-link-race-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var targetDirectory = Path.Combine(attachmentRoot, "records");
        var outsideRoot = Path.Combine(root, "outside");
        var backupRoot = Path.Combine(root, "backup");
        try
        {
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(outsideRoot);
            await File.WriteAllTextAsync(Path.Combine(outsideRoot, "review.txt"), "outside-current");
            await WriteAttachmentBackupAsync(
                backupRoot,
                (Path.Combine("records", "review.txt"), "hospital-old"));
            var service = AttachmentBackupService(attachmentRoot);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAttachmentsAsync(
                    backupRoot,
                    _ =>
                    {
                        Directory.Delete(targetDirectory, recursive: true);
                        Directory.CreateSymbolicLink(targetDirectory, outsideRoot);
                    },
                    CancellationToken.None));

            Assert.Contains("符号链接", exception.Message, StringComparison.Ordinal);
            Assert.Equal("outside-current", await File.ReadAllTextAsync(Path.Combine(outsideRoot, "review.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件安装在临时副本完成后必须复核并发替换的父目录链接()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-install-link-race-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var targetDirectory = Path.Combine(attachmentRoot, "records");
        var displacedDirectory = Path.Combine(root, "displaced");
        var outsideRoot = Path.Combine(root, "outside");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "records", "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(outsideRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "review.txt"), "hospital-old");
            await File.WriteAllTextAsync(Path.Combine(outsideRoot, "review.txt"), "hospital-old");
            await WriteAttachmentBackupAsync(
                backupRoot,
                (Path.Combine("records", "review.txt"), "hospital-old"));
            var packageBytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/records/review.txt",
                Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                packageBytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "InstallAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(FollowUpVerifiedPackage),
                    typeof(string),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.NotNull(method);
            Action<string> replaceParent = temporary =>
            {
                Directory.Move(targetDirectory, displacedDirectory);
                Directory.CreateSymbolicLink(targetDirectory, outsideRoot);
                File.Copy(
                    Path.Combine(displacedDirectory, Path.GetFileName(temporary)),
                    Path.Combine(outsideRoot, Path.GetFileName(temporary)));
            };

            var exception = await Record.ExceptionAsync(async () =>
                await Assert.IsAssignableFrom<Task>(method!.Invoke(
                    service,
                    [
                        package,
                        backupRoot,
                        null,
                        null,
                        null,
                        replaceParent,
                        CancellationToken.None
                    ])));

            Assert.NotNull(exception);
            Assert.Equal("hospital-old", await File.ReadAllTextAsync(Path.Combine(outsideRoot, "review.txt")));
        }
        finally
        {
            if (Directory.Exists(targetDirectory) && new DirectoryInfo(targetDirectory).LinkTarget is not null)
                Directory.Delete(targetDirectory);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件补偿在临时副本完成后必须复核并发替换的父目录链接()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-restore-link-race-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var targetDirectory = Path.Combine(attachmentRoot, "records");
        var displacedDirectory = Path.Combine(root, "displaced");
        var outsideRoot = Path.Combine(root, "outside");
        var backupRoot = Path.Combine(root, "backup");
        try
        {
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(outsideRoot);
            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "review.txt"), "package-value");
            await File.WriteAllTextAsync(Path.Combine(outsideRoot, "review.txt"), "package-value");
            await WriteAttachmentBackupAsync(
                backupRoot,
                (Path.Combine("records", "review.txt"), "hospital-old"));
            var mutations = new[]
            {
                new FollowUpAttachmentMutation(
                    Path.Combine("records", "review.txt"),
                    HashText("package-value"))
            };
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "RestoreInstalledAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(string),
                    typeof(IReadOnlyCollection<FollowUpAttachmentMutation>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.NotNull(method);
            Action<string> replaceParent = temporary =>
            {
                Directory.Move(targetDirectory, displacedDirectory);
                Directory.CreateSymbolicLink(targetDirectory, outsideRoot);
                File.Copy(
                    Path.Combine(displacedDirectory, Path.GetFileName(temporary)),
                    Path.Combine(outsideRoot, Path.GetFileName(temporary)));
            };

            var exception = await Record.ExceptionAsync(async () =>
                await Assert.IsAssignableFrom<Task>(method!.Invoke(
                    service,
                    [backupRoot, mutations, null, replaceParent, CancellationToken.None])));

            Assert.NotNull(exception);
            Assert.Equal("package-value", await File.ReadAllTextAsync(Path.Combine(outsideRoot, "review.txt")));
        }
        finally
        {
            if (Directory.Exists(targetDirectory) && new DirectoryInfo(targetDirectory).LinkTarget is not null)
                Directory.Delete(targetDirectory);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 旧版无Hash附件清单首次只生成基线且再次确认后可恢复()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-legacy-baseline-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            Directory.CreateDirectory(backupRoot);
            await File.WriteAllTextAsync(target, "hospital-current");
            await File.WriteAllTextAsync(Path.Combine(backupRoot, "review.txt"), "hospital-old");
            await File.WriteAllTextAsync(
                Path.Combine(backupRoot, "attachment-backup.json"),
                JsonSerializer.Serialize(
                    new[] { new { relativePath = "review.txt", existed = true } },
                    FollowUpJson.Options));
            var service = AttachmentBackupService(attachmentRoot);

            var firstAttempt = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAttachmentsAsync(backupRoot, CancellationToken.None));

            Assert.Contains("旧版", firstAttempt.Message, StringComparison.Ordinal);
            Assert.Equal("hospital-current", await File.ReadAllTextAsync(target));
            Assert.True(File.Exists(Path.Combine(root, "attachment-backup.hash-baseline.v2.json")));

            await service.RestoreAttachmentsAsync(backupRoot, CancellationToken.None);

            Assert.Equal("hospital-old", await File.ReadAllTextAsync(target));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 完整附件恢复必须校验实际复制的临时副本后再发布()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-restore-copy-validation-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var firstTarget = Path.Combine(attachmentRoot, "first.txt");
        var secondTarget = Path.Combine(attachmentRoot, "second.txt");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(firstTarget, "first-current");
            await File.WriteAllTextAsync(secondTarget, "second-current");
            await WriteAttachmentBackupAsync(
                backupRoot,
                ("first.txt", "first-old"),
                ("second.txt", "second-old"));
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "RestoreAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(Action<string>), typeof(CancellationToken)],
                modifiers: null);
            Assert.True(method is not null, "完整恢复需要提供批量校验后、单项复制前的内部竞态测试 hook。");
            Action<string> beforeBackupCopy = relativePath =>
            {
                if (relativePath == "second.txt")
                    File.WriteAllText(Path.Combine(backupRoot, relativePath), "corrupted-after-validation");
            };

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await Assert.IsAssignableFrom<Task>(method!.Invoke(
                    service,
                    [backupRoot, beforeBackupCopy, CancellationToken.None])));

            Assert.Equal("second-current", await File.ReadAllTextAsync(secondTarget));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件补偿认领后异常必须保留最后可用包附件并标记状态不确定()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-restore-interrupted-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(target, "package-value");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var mutations = new[] { new FollowUpAttachmentMutation("review.txt", HashText("package-value")) };
            var service = AttachmentBackupService(attachmentRoot);

            await Assert.ThrowsAsync<FollowUpAttachmentStateUncertainException>(() =>
                service.RestoreInstalledAttachmentsAsync(
                    backupRoot,
                    mutations,
                    _ => throw new InvalidOperationException("模拟补偿发布前中断"),
                    CancellationToken.None));

            Assert.False(File.Exists(target));
            var claim = Assert.Single(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
            Assert.Equal("package-value", await File.ReadAllTextAsync(claim));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void 提交已开始但结果未知时禁止自动附件补偿并进入人工完整恢复阻断()
    {
        Assert.False(FollowUpPackageImportService.ShouldRestoreAttachments(
            importCommitted: false,
            attachmentMutationStarted: true,
            commitAttempted: true));
        Assert.Equal(
            "RestoreFailed",
            FollowUpPackageImportService.ResolveFailureStatus(
                new IOException("提交结果未知"),
                attachmentRestoreFailed: false,
                commitOutcomeUnknown: true));
    }

    [Fact]
    public async Task PostgreSQL工具取消后必须确认子进程已经退出()
    {
        var method = typeof(FollowUpPackageBackupService).GetMethod(
            "WaitForPostgreSqlToolExitAsync",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            [typeof(Process), typeof(CancellationToken)],
            modifiers: null);
        Assert.True(method is not null, "PostgreSQL 工具等待逻辑必须拥有取消后的终止并等待语义。");

        using var process = StartLongRunningProcess();
        using var cancellation = new CancellationTokenSource();
        try
        {
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await Assert.IsAssignableFrom<Task>(method!.Invoke(
                    null,
                    [process, cancellation.Token])));

            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task 附件原子移动失败时清理临时文件且不上报成功变更()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-temp-move-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "blocked.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.Combine(attachmentRoot, "blocked.txt"));
            await File.WriteAllTextAsync(source, "package-value");
            await WriteAttachmentBackupAsync(backupRoot, ("blocked.txt", null));
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/blocked.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                service.InstallAttachmentsAsync(package, backupRoot, CancellationToken.None));

            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 备份后安装前被医院流程更新的附件不得被覆盖()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-preinstall-conflict-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(target, "hospital-old");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            // 备份完成后、附件安装前，医院业务流程写入了新版本。
            await File.WriteAllTextAsync(target, "hospital-newer");
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var installMethod = RequireSafeInstallMethod();

            var exception = await Record.ExceptionAsync(() =>
                InvokeSafeInstallAsync(service, installMethod, package, backupRoot));

            Assert.NotNull(exception);
            Assert.Equal("hospital-newer", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件安装在原版本确认后出现医院新版本时不得覆盖()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-install-race-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(target, "hospital-old");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "InstallAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(FollowUpVerifiedPackage),
                    typeof(string),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.NotNull(method);
            var hookCalls = 0;
            Action<string> afterTargetClaimed = relativePath =>
            {
                Assert.Equal("review.txt", relativePath);
                hookCalls++;
                File.WriteAllText(target, "hospital-newest");
            };

            var exception = await Record.ExceptionAsync(async () =>
                await Assert.IsAssignableFrom<Task>(method!.Invoke(
                    service,
                    [package, backupRoot, afterTargetClaimed, CancellationToken.None])));

            Assert.NotNull(exception);
            Assert.Equal(1, hookCalls);
            Assert.Equal("hospital-newest", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件安装认领原版本后发生异常时必须放回原版本()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-install-interrupted-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(target, "hospital-old");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "InstallAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(FollowUpVerifiedPackage),
                    typeof(string),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.NotNull(method);
            Action<string> afterTargetClaimed = _ => throw new InvalidOperationException("模拟发布前中断");

            var exception = await Record.ExceptionAsync(async () =>
                await Assert.IsAssignableFrom<Task>(method!.Invoke(
                    service,
                    [package, backupRoot, afterTargetClaimed, CancellationToken.None])));

            Assert.NotNull(exception);
            Assert.Equal("hospital-old", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件安装成功但原版本认领文件清理失败时必须进入人工阻断()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-install-claim-cleanup-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(target, "hospital-old");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "InstallAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(FollowUpVerifiedPackage),
                    typeof(string),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.True(method is not null, "附件安装需要提供原版本 claim 清理前的内部故障测试 hook。");
            Action<string> beforeClaimCleanup = _ => throw new IOException("模拟 claim 清理失败");

            var exception = await Assert.ThrowsAsync<FollowUpAttachmentInstallException>(() =>
                AwaitTaskResultAsync<IReadOnlyList<FollowUpAttachmentMutation>>(method!.Invoke(
                    service,
                    [package, backupRoot, null, beforeClaimCleanup, CancellationToken.None])));

            Assert.True(exception.RequiresFullRestore);
            Assert.Single(exception.Mutations);
            Assert.Equal("package-value", await File.ReadAllTextAsync(target));
            var claim = Assert.Single(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
            Assert.Equal("hospital-old", await File.ReadAllTextAsync(claim));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 首个附件硬链接探针清理失败时必须保留残留路径并进入人工阻断()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-probe-cleanup-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", null));
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "InstallAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(FollowUpVerifiedPackage),
                    typeof(string),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.True(method is not null, "附件安装需要提供硬链接探针清理前的内部故障测试 hook。");
            string? residuePath = null;
            Action<string> beforeProbeCleanup = path =>
            {
                residuePath = path;
                throw new IOException("模拟 probe 清理失败");
            };

            var exception = await Assert.ThrowsAsync<FollowUpAttachmentInstallException>(() =>
                AwaitTaskResultAsync<IReadOnlyList<FollowUpAttachmentMutation>>(method!.Invoke(
                    service,
                    [package, backupRoot, null, null, null, null, beforeProbeCleanup, CancellationToken.None])));

            Assert.True(exception.RequiresFullRestore);
            Assert.Empty(exception.Mutations);
            Assert.NotNull(residuePath);
            Assert.Contains(residuePath!, exception.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(residuePath));
            Assert.False(File.Exists(Path.Combine(attachmentRoot, "review.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件安装主异常不得被临时文件清理异常覆盖()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-install-double-cleanup-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(target, "hospital-old");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "InstallAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(FollowUpVerifiedPackage),
                    typeof(string),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.True(method is not null, "附件安装需要提供临时文件清理前的内部故障测试 hook。");
            Action<string> beforeClaimCleanup = _ => throw new IOException("模拟 claim 清理失败");
            Action<string> beforeTemporaryCleanup = _ => throw new IOException("模拟 tmp 清理失败");

            var exception = await Assert.ThrowsAsync<FollowUpAttachmentInstallException>(() =>
                AwaitTaskResultAsync<IReadOnlyList<FollowUpAttachmentMutation>>(method!.Invoke(
                    service,
                    [
                        package,
                        backupRoot,
                        null,
                        beforeClaimCleanup,
                        beforeTemporaryCleanup,
                        CancellationToken.None
                    ])));

            Assert.True(exception.RequiresFullRestore);
            var aggregate = Assert.IsType<AggregateException>(exception.InnerException);
            Assert.Contains(aggregate.InnerExceptions, item => item is FollowUpAttachmentStateUncertainException);
            Assert.Single(exception.Mutations);
            Assert.Equal("package-value", await File.ReadAllTextAsync(target));
            Assert.Single(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件安装必须校验实际临时副本后再认领发布()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-install-temp-validation-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(target, "hospital-old");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var bytes = await File.ReadAllBytesAsync(source);
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "InstallAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(FollowUpVerifiedPackage),
                    typeof(string),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.True(method is not null, "附件安装需要提供实际临时副本完成后的内部竞态测试 hook。");
            Action<string> afterTemporaryCopied = temporary => File.WriteAllText(temporary, "tampered");

            var exception = await Record.ExceptionAsync(async () =>
                await Assert.IsAssignableFrom<Task>(method!.Invoke(
                    service,
                    [
                        package,
                        backupRoot,
                        null,
                        null,
                        null,
                        afterTemporaryCopied,
                        CancellationToken.None
                    ])));

            Assert.NotNull(exception);
            Assert.Equal("hospital-old", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件补偿必须在认领前校验实际恢复临时副本()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-restore-temp-validation-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(target, "package-value");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var mutations = new[] { new FollowUpAttachmentMutation("review.txt", HashText("package-value")) };
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "RestoreInstalledAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(string),
                    typeof(IReadOnlyCollection<FollowUpAttachmentMutation>),
                    typeof(Action<string>),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.True(method is not null, "附件补偿需要提供实际恢复临时副本完成后的内部竞态测试 hook。");
            Action<string> afterTemporaryCopied = temporary => File.WriteAllText(temporary, "tampered");

            var exception = await Record.ExceptionAsync(async () =>
                await Assert.IsAssignableFrom<Task>(method!.Invoke(
                    service,
                    [backupRoot, mutations, null, afterTemporaryCopied, CancellationToken.None])));

            Assert.NotNull(exception);
            Assert.Equal("package-value", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件补偿在Hash确认后发生外部写入时必须跳过覆盖()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-restore-race-{Guid.NewGuid():N}");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(target, "package-value");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var packageHash = HashText("package-value");
            var mutations = new[] { new FollowUpAttachmentMutation("review.txt", packageHash) };
            var service = AttachmentBackupService(attachmentRoot);
            var method = typeof(FollowUpPackageBackupService).GetMethod(
                "RestoreInstalledAttachmentsAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(string),
                    typeof(IReadOnlyCollection<FollowUpAttachmentMutation>),
                    typeof(Action<string>),
                    typeof(CancellationToken)
                ],
                modifiers: null);
            Assert.True(
                method is not null,
                "RestoreInstalledAttachmentsAsync 需要提供可在 Hash 确认后注入竞态的内部测试 hook。");
            var hookCalls = 0;
            Action<string> afterHashVerified = relativePath =>
            {
                Assert.Equal("review.txt", relativePath);
                hookCalls++;
                File.WriteAllText(target, "hospital-newest");
            };

            var skipped = await AwaitTaskResultAsync<IReadOnlyList<string>>(
                method!.Invoke(service, [backupRoot, mutations, afterHashVerified, CancellationToken.None]));

            Assert.Equal(1, hookCalls);
            Assert.Contains("review.txt", skipped);
            Assert.Equal("hospital-newest", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.tmp", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(attachmentRoot, "*.claim", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 附件安装API自身返回完整变更集合且不再接受成功回调()
    {
        var installMethods = typeof(FollowUpPackageBackupService)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "InstallAttachmentsAsync")
            .ToList();
        Assert.DoesNotContain(
            installMethods,
            method => method.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(Action<FollowUpAttachmentMutation>)));

        var root = Path.Combine(Path.GetTempPath(), $"followup-attachment-return-mutations-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, "staging");
        var attachmentRoot = Path.Combine(root, "uploads");
        var backupRoot = Path.Combine(root, "backup");
        var source = Path.Combine(staging, "files", "uploads", "review.txt");
        var target = Path.Combine(attachmentRoot, "review.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(attachmentRoot);
            await File.WriteAllTextAsync(source, "package-value");
            await File.WriteAllTextAsync(target, "hospital-old");
            await WriteAttachmentBackupAsync(backupRoot, ("review.txt", "hospital-old"));
            var bytes = await File.ReadAllBytesAsync(source);
            var packageHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var package = AttachmentPackage(
                staging,
                "files/uploads/review.txt",
                packageHash,
                bytes.Length);
            var service = AttachmentBackupService(attachmentRoot);
            var installMethod = RequireSafeInstallMethod();

            var mutations = await InvokeSafeInstallAsync(service, installMethod, package, backupRoot);

            var mutation = Assert.Single(mutations);
            Assert.Equal("review.txt", mutation.RelativePath);
            Assert.Equal(packageHash, mutation.InstalledHash);
            Assert.Equal("package-value", await File.ReadAllTextAsync(target));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void 事务回滚失败时必须同时保留原始导入异常()
    {
        var source = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageImportService.cs");
        var methodStart = source.IndexOf(
            "private async Task<Dictionary<string, int>> ImportDataAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private static async IAsyncEnumerable<string> ReadRowsForImportAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodSource = source[methodStart..methodEnd];
        var originalCatch = methodSource.IndexOf("catch (Exception importException)", StringComparison.Ordinal);
        Assert.True(originalCatch >= 0, "ImportDataAsync 必须显式捕获并保留原始导入异常。");
        var rollback = methodSource.IndexOf("await transaction.RollbackAsync", originalCatch, StringComparison.Ordinal);
        Assert.True(rollback > originalCatch, "原始导入异常的 catch 中必须尝试回滚事务。");
        var rollbackCatch = methodSource.IndexOf("catch (Exception rollbackException)", rollback, StringComparison.Ordinal);
        Assert.True(rollbackCatch > rollback, "RollbackAsync 异常必须单独捕获。");
        var aggregate = methodSource.IndexOf(
            "new AggregateException(importException, rollbackException)",
            rollbackCatch,
            StringComparison.Ordinal);

        Assert.True(
            aggregate > rollbackCatch,
            "RollbackAsync 再次失败时必须聚合原始导入异常与回滚异常。");
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

    private static FollowUpPackageBackupService AttachmentBackupService(
        string attachmentRoot,
        string? backupRoot = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CubeDb"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1"
            })
            .Build();
        return new FollowUpPackageBackupService(
            configuration,
            Options.Create(new FollowUpPackageImportOptions
            {
                AttachmentRoot = attachmentRoot,
                BackupRoot = backupRoot ?? Path.Combine(Path.GetTempPath(), "followup-unused-backups")
            }));
    }

    private static MethodInfo RequireSafeInstallMethod()
    {
        var method = typeof(FollowUpPackageBackupService).GetMethod(
            "InstallAttachmentsAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(FollowUpVerifiedPackage), typeof(string), typeof(CancellationToken)],
            modifiers: null);
        Assert.True(
            method is not null,
            "InstallAttachmentsAsync 应接收附件备份目录并直接返回已安装 mutation 集合。");
        return method!;
    }

    private static Task<IReadOnlyList<FollowUpAttachmentMutation>> InvokeSafeInstallAsync(
        FollowUpPackageBackupService service,
        MethodInfo method,
        FollowUpVerifiedPackage package,
        string backupRoot) =>
        AwaitTaskResultAsync<IReadOnlyList<FollowUpAttachmentMutation>>(
            method.Invoke(service, [package, backupRoot, CancellationToken.None]));

    private static async Task<TResult> AwaitTaskResultAsync<TResult>(object? invocation)
    {
        var task = Assert.IsAssignableFrom<Task>(invocation);
        await task;
        var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(resultProperty);
        return Assert.IsAssignableFrom<TResult>(resultProperty.GetValue(task));
    }

    private static Process StartLongRunningProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("ping 127.0.0.1 -n 31 > nul");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 30");
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("无法启动取消语义测试子进程。");
    }

    private static void AssertMethodContains(
        string source,
        string startMarker,
        string endMarker,
        string expected)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"无法定位方法片段：{startMarker}");
        Assert.Contains(expected, source[start..end], StringComparison.Ordinal);
    }

    private static async Task WriteAttachmentBackupAsync(
        string backupRoot,
        params (string RelativePath, string? Content)[] entries)
    {
        Directory.CreateDirectory(backupRoot);
        foreach (var entry in entries.Where(entry => entry.Content is not null))
        {
            var path = Path.Combine(backupRoot, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, entry.Content!);
        }

        var manifest = new
        {
            version = 2,
            entries = entries.Select(entry => new
            {
                relativePath = entry.RelativePath,
                existed = entry.Content is not null,
                hash = entry.Content is null ? null : HashText(entry.Content)
            })
        };
        await File.WriteAllTextAsync(
            Path.Combine(backupRoot, "attachment-backup.json"),
            JsonSerializer.Serialize(manifest, FollowUpJson.Options));
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static FollowUpVerifiedPackage AttachmentPackage(
        string staging,
        string path,
        string hash,
        long sizeBytes) => AttachmentPackage(
            staging,
            new FollowUpAttachmentManifest
            {
                Path = path,
                Hash = hash,
                SizeBytes = sizeBytes
            });

    private static FollowUpVerifiedPackage AttachmentPackage(
        string staging,
        params FollowUpAttachmentManifest[] attachments) =>
        new(
            "package.fupkg",
            "package-hash",
            staging,
            new FollowUpEncryptedEnvelope(),
            new FollowUpPackageManifest
            {
                AttachmentFiles = attachments.ToList()
            },
            [],
            new FollowUpSchemaSnapshot(),
            new FollowUpSchemaDiff());

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

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FollowUpSnapshotEnvironmentCollection
{
    public const string Name = "FollowUp快照环境变量串行测试";
}

[Collection(FollowUpSnapshotEnvironmentCollection.Name)]
public sealed class FollowUpSnapshotTempBoundaryTests
{
    [Fact]
    public async Task TMP和TEMP指向BackupRoot子目录时数据库及附件快照均在创建前阻断()
    {
        var originalTempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(originalTempRoot, $"followup-snapshot-temp-boundary-{Guid.NewGuid():N}");
        var backupRoot = Path.Combine(root, "managed-backups");
        var artifactRoot = Path.Combine(backupRoot, "LHYY", "package", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(artifactRoot, "database.dump");
        var attachmentBackupPath = Path.Combine(artifactRoot, "attachments");
        var manifestPath = Path.Combine(artifactRoot, "attachment-backup.json");
        var redirectedTempRoot = Path.Combine(backupRoot, "redirected-temp");
        var oldTmp = Environment.GetEnvironmentVariable("TMP");
        var oldTemp = Environment.GetEnvironmentVariable("TEMP");
        var oldTmpDir = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Directory.CreateDirectory(attachmentBackupPath);
            Directory.CreateDirectory(redirectedTempRoot);
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await File.WriteAllTextAsync(manifestPath, "{\"version\":2,\"entries\":[]}");
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                artifactRoot,
                databasePath,
                attachmentBackupPath,
                HashText("database-backup"),
                Directory.EnumerateFiles(artifactRoot, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length),
                HashBytes(await File.ReadAllBytesAsync(manifestPath)),
                0);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:CubeDb"] = "Host=localhost;Database=test;Username=test;Password=test"
                })
                .Build();
            var service = new FollowUpPackageBackupService(
                configuration,
                Options.Create(new FollowUpPackageImportOptions
                {
                    BackupRoot = backupRoot,
                    AttachmentRoot = Path.Combine(root, "uploads")
                }));

            Environment.SetEnvironmentVariable("TMP", redirectedTempRoot);
            Environment.SetEnvironmentVariable("TEMP", redirectedTempRoot);
            Environment.SetEnvironmentVariable("TMPDIR", redirectedTempRoot);
            Assert.Equal(
                Path.GetFullPath(redirectedTempRoot).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateValidatedDatabaseSnapshotAsync(artifact, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateValidatedAttachmentSnapshotAsync(artifact, CancellationToken.None));

            Assert.Empty(Directory.EnumerateDirectories(
                redirectedTempRoot,
                "datasync-followup-*",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            Environment.SetEnvironmentVariable("TMPDIR", oldTmpDir);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TMP符号链接实际指向BackupRoot子目录时数据库及附件快照均阻断()
    {
        var originalTempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(originalTempRoot, $"followup-snapshot-temp-link-{Guid.NewGuid():N}");
        var backupRoot = Path.Combine(root, "managed-backups");
        var artifactRoot = Path.Combine(backupRoot, "LHYY", "package", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(artifactRoot, "database.dump");
        var attachmentBackupPath = Path.Combine(artifactRoot, "attachments");
        var manifestPath = Path.Combine(artifactRoot, "attachment-backup.json");
        var actualTempRoot = Path.Combine(backupRoot, "actual-temp");
        var linkedTempRoot = Path.Combine(root, "outside-looking-temp-link");
        var oldTmp = Environment.GetEnvironmentVariable("TMP");
        var oldTemp = Environment.GetEnvironmentVariable("TEMP");
        var oldTmpDir = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Directory.CreateDirectory(attachmentBackupPath);
            Directory.CreateDirectory(actualTempRoot);
            try
            {
                Directory.CreateSymbolicLink(linkedTempRoot, actualTempRoot);
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException
                                              or UnauthorizedAccessException)
            {
                return;
            }
            await File.WriteAllTextAsync(databasePath, "database-backup");
            await File.WriteAllTextAsync(manifestPath, "{\"version\":2,\"entries\":[]}");
            var artifact = new FollowUpBackupArtifact(
                Guid.NewGuid(),
                artifactRoot,
                databasePath,
                attachmentBackupPath,
                HashText("database-backup"),
                Directory.EnumerateFiles(artifactRoot, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length),
                HashBytes(await File.ReadAllBytesAsync(manifestPath)),
                0);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:CubeDb"] = "Host=localhost;Database=test;Username=test;Password=test"
                })
                .Build();
            var service = new FollowUpPackageBackupService(
                configuration,
                Options.Create(new FollowUpPackageImportOptions
                {
                    BackupRoot = backupRoot,
                    AttachmentRoot = Path.Combine(root, "uploads")
                }));

            Environment.SetEnvironmentVariable("TMP", linkedTempRoot);
            Environment.SetEnvironmentVariable("TEMP", linkedTempRoot);
            Environment.SetEnvironmentVariable("TMPDIR", linkedTempRoot);
            Assert.Equal(
                Path.GetFullPath(linkedTempRoot).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows());

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.CreateValidatedDatabaseSnapshotAsync(artifact, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.CreateValidatedAttachmentSnapshotAsync(artifact, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            Environment.SetEnvironmentVariable("TMPDIR", oldTmpDir);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string HashText(string value) =>
        HashBytes(System.Text.Encoding.UTF8.GetBytes(value));

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
