using System.Diagnostics;
using Npgsql;

namespace DataSync.LHYY.V2.Tools;

public static class MessageArchiveTool
{
    private const string CommandName = "message-archive";
    private const int DefaultHotDays = 30;
    private const int DefaultBatchSize = 50000;
    private static readonly TimeSpan BackupReuseWindow = TimeSpan.FromHours(24);
    private const long ArchiveLockKey = 2026052601;
    private const string UpgradeScriptRelativePath = "DatabaseUpgrades/EsbMessagesPerformanceOptimization/upgrade_esb_messages_archive_optimization.sql";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            var verb = args[1].ToLowerInvariant();
            var options = ToolOptions.Parse(args.Skip(2).ToArray());
            var connectionString = ToolConnectionHelper.ResolveConnectionString(options.ConnectionName, options.ScriptsPath);
            Console.WriteLine($"已连接目标：{ToolConnectionHelper.DescribeConnection(connectionString)}");

            return verb switch
            {
                "upgrade" => await UpgradeAsync(connectionString, options),
                "migrate" => await MigrateAsync(connectionString, options),
                "verify" => await VerifyAsync(connectionString),
                _ => PrintUnknownVerb(verb)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"消息归档工具执行失败：{ex.Message}");
            return 1;
        }
    }

    private static async Task<int> UpgradeAsync(string connectionString, ToolOptions options)
    {
        var rootPath = ToolConnectionHelper.ResolveRootPath(options.ScriptsPath);
        var scriptPath = Path.Combine(rootPath, UpgradeScriptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("未找到本次 ESB 消息性能优化升级脚本。", scriptPath);

        var sql = await File.ReadAllTextAsync(scriptPath);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        if (!await TryAcquireArchiveLockAsync(connection))
            throw new InvalidOperationException("已有归档任务正在执行，请等待后台归档或其他迁移工具结束后再重试。");

        try
        {
            Console.WriteLine("开始升级前自检：检查本次优化依赖的基础表结构。");
            await ValidateOptimizationPrerequisitesAsync(connection);

            if (await IsArchiveOptimizationReadyAsync(connection))
            {
                Console.WriteLine("检测到本次 ESB 消息性能优化结构已安装且校验通过，跳过升级脚本和重复备份。");
                return 0;
            }

            if (options.SkipBackup)
            {
                Console.WriteLine("已由外部升级包完成数据库备份，跳过工具内备份。");
            }
            else
            {
                Console.WriteLine("开始备份数据库。");
                var backupFile = await BackupDatabaseAsync(connectionString, rootPath, options.PgDumpPath);
                WriteBackupStamp(options.BackupStampPath, backupFile);
                Console.WriteLine($"备份完成：{backupFile}");
            }

            Console.WriteLine($"执行本次优化升级脚本：{Path.GetRelativePath(rootPath, scriptPath)}");
            await SqlScriptExecutionHelper.ExecuteAsync(connection, sql);

            Console.WriteLine("开始升级后自检：检查归档表、统一视图和关键索引。");
            await ValidateArchiveOptimizationAsync(connection);

            Console.WriteLine("升级完成：ESB 消息冷热归档与查询优化已就绪。");
            return 0;
        }
        finally
        {
            await ReleaseArchiveLockAsync(connection);
        }
    }

    private static async Task<int> MigrateAsync(string connectionString, ToolOptions options)
    {
        var rootPath = ToolConnectionHelper.ResolveRootPath(options.ScriptsPath);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        if (!await TryAcquireArchiveLockAsync(connection))
            throw new InvalidOperationException("已有归档任务正在执行，请等待后台归档或其他迁移工具结束后再重试。");

        try
        {
            await ValidateArchiveOptimizationAsync(connection);

            var hotDays = await ResolveHotRetentionDaysAsync(connection, options);
            var threshold = DateTime.Now.AddDays(-hotDays);
            Console.WriteLine($"本次迁移使用热表保留天数：{hotDays}，阈值：{threshold:yyyy-MM-dd HH:mm:ss}。");

            if (!options.DryRun && !options.SkipBackup && await HasEligibleMessagesAsync(connection, threshold))
            {
                var backupFile = await EnsureRecentBackupAsync(connectionString, rootPath, options.PgDumpPath, options.BackupStampPath);
                Console.WriteLine($"迁移前备份确认完成：{backupFile}");
            }
            else if (!options.DryRun && options.SkipBackup)
            {
                Console.WriteLine("已由外部升级包完成数据库备份，跳过迁移前工具内备份。");
            }

            var totalMessages = 0L;
            var totalLogs = 0L;
            var batchIndex = 0;

            while (true)
            {
                batchIndex++;
                await using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    await ExecuteNonQueryAsync(connection, transaction, """
                        CREATE TEMP TABLE tmp_esb_archive_batch (
                            id BIGINT PRIMARY KEY,
                            created_at TIMESTAMP NOT NULL
                        ) ON COMMIT DROP;
                        """);

                    await ExecuteNonQueryAsync(connection, transaction, """
                        INSERT INTO tmp_esb_archive_batch (id, created_at)
                        SELECT id, created_at
                        FROM lhyy.esb_messages
                        WHERE created_at < @threshold
                          AND status IN (2, 4, 5, 6)
                        ORDER BY created_at, id
                        LIMIT @batchSize
                        FOR UPDATE SKIP LOCKED;
                        """,
                        ("threshold", threshold),
                        ("batchSize", options.BatchSize));

                    var batchCount = await ExecuteScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM tmp_esb_archive_batch;");
                    if (batchCount == 0)
                    {
                        await transaction.RollbackAsync();
                        break;
                    }

                    var months = await LoadArchiveMonthsAsync(connection, transaction);
                    foreach (var month in months)
                    {
                        await ExecuteNonQueryAsync(
                            connection,
                            transaction,
                            "SELECT lhyy.ensure_esb_archive_partition(CAST(@month AS date));",
                            ("month", month));
                    }

                    if (options.DryRun)
                    {
                        Console.WriteLine($"批次 {batchIndex}: 将迁移 {batchCount} 条消息，涉及 {months.Count} 个月份分区。");
                        await transaction.RollbackAsync();
                        break;
                    }

                    var insertedMessages = await ExecuteNonQueryAsync(connection, transaction, """
                        INSERT INTO lhyy.esb_messages_archive (
                            id,
                            message_id,
                            source_message_id,
                            tran_code,
                            integration_project_code,
                            tran_name,
                            app_id,
                            org_id,
                            esb_timestamp,
                            raw_json,
                            body_json,
                            idempotent_key,
                            mrn,
                            visit_no,
                            inpatient_no,
                            resolved_event_time,
                            matched_rule_group,
                            status,
                            retry_count,
                            error_message,
                            patient_id,
                            event_id,
                            processed_at,
                            processing_started_at,
                            created_at,
                            archived_at
                        )
                        SELECT
                            m.id,
                            m.message_id,
                            m.source_message_id,
                            m.tran_code,
                            m.integration_project_code,
                            m.tran_name,
                            m.app_id,
                            m.org_id,
                            m.esb_timestamp,
                            m.raw_json,
                            m.body_json,
                            m.idempotent_key,
                            m.mrn,
                            m.visit_no,
                            m.inpatient_no,
                            m.resolved_event_time,
                            m.matched_rule_group,
                            m.status,
                            m.retry_count,
                            m.error_message,
                            m.patient_id,
                            m.event_id,
                            m.processed_at,
                            m.processing_started_at,
                            m.created_at,
                            NOW()
                        FROM lhyy.esb_messages m
                        INNER JOIN tmp_esb_archive_batch b ON b.id = m.id
                        WHERE TRUE
                        ON CONFLICT DO NOTHING;
                        """);

                    var insertedLogs = await ExecuteNonQueryAsync(connection, transaction, """
                        INSERT INTO lhyy.esb_process_log_archive (
                            id,
                            message_id,
                            integration_project_code,
                            step,
                            is_success,
                            detail,
                            elapsed_ms,
                            created_at,
                            archived_at
                        )
                        SELECT
                            l.id,
                            l.message_id,
                            l.integration_project_code,
                            l.step,
                            l.is_success,
                            l.detail,
                            l.elapsed_ms,
                            l.created_at,
                            NOW()
                        FROM lhyy.esb_process_log l
                        INNER JOIN tmp_esb_archive_batch b ON b.id = l.message_id
                        WHERE TRUE
                        ON CONFLICT DO NOTHING;
                        """);

                    await ExecuteNonQueryAsync(connection, transaction, """
                        DELETE FROM lhyy.esb_process_log l
                        USING tmp_esb_archive_batch b
                        WHERE b.id = l.message_id
                          AND EXISTS (
                              SELECT 1
                              FROM lhyy.esb_messages_archive a
                              WHERE a.id = b.id
                                AND a.created_at = b.created_at
                          );
                        """);

                    await ExecuteNonQueryAsync(connection, transaction, """
                        DELETE FROM lhyy.esb_messages m
                        USING tmp_esb_archive_batch b
                        WHERE b.id = m.id
                          AND EXISTS (
                              SELECT 1
                              FROM lhyy.esb_messages_archive a
                              WHERE a.id = b.id
                                AND a.created_at = b.created_at
                          );
                        """);

                    await transaction.CommitAsync();
                    totalMessages += insertedMessages;
                    totalLogs += insertedLogs;
                    Console.WriteLine($"批次 {batchIndex}: 处理消息 {batchCount} 条，迁移消息 {insertedMessages} 条，处理日志 {insertedLogs} 条。");
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }

            Console.WriteLine($"迁移完成：消息 {totalMessages} 条，处理日志 {totalLogs} 条，阈值 {threshold:yyyy-MM-dd HH:mm:ss}。");
            return 0;
        }
        finally
        {
            await ReleaseArchiveLockAsync(connection);
        }
    }

    private static async Task<int> VerifyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ValidateArchiveOptimizationAsync(connection);

        var hotMessages = await ExecuteScalarLongAsync(connection, null, "SELECT COUNT(*) FROM lhyy.esb_messages;");
        var archiveMessages = await ExecuteScalarLongAsync(connection, null, "SELECT COUNT(*) FROM lhyy.esb_messages_archive;");
        var allMessages = await ExecuteScalarLongAsync(connection, null, "SELECT COUNT(*) FROM lhyy.esb_messages_all;");
        var hotLogs = await ExecuteScalarLongAsync(connection, null, "SELECT COUNT(*) FROM lhyy.esb_process_log;");
        var archiveLogs = await ExecuteScalarLongAsync(connection, null, "SELECT COUNT(*) FROM lhyy.esb_process_log_archive;");
        var allLogs = await ExecuteScalarLongAsync(connection, null, "SELECT COUNT(*) FROM lhyy.esb_process_log_all;");

        Console.WriteLine($"消息：热表={hotMessages}, 归档={archiveMessages}, 统一视图={allMessages}");
        Console.WriteLine($"处理日志：热表={hotLogs}, 归档={archiveLogs}, 统一视图={allLogs}");

        if (hotMessages + archiveMessages != allMessages)
            throw new InvalidOperationException("消息统一视图数量不等于热表与归档表之和。");

        if (hotLogs + archiveLogs != allLogs)
            throw new InvalidOperationException("处理日志统一视图数量不等于热表与归档表之和。");

        var duplicateMessages = await ExecuteScalarLongAsync(connection, null, """
            SELECT COUNT(*)
            FROM (
                SELECT id, created_at
                FROM lhyy.esb_messages_archive
                GROUP BY id, created_at
                HAVING COUNT(*) > 1
            ) d;
            """);
        if (duplicateMessages > 0)
            throw new InvalidOperationException($"归档消息存在重复数据：{duplicateMessages} 组。");

        var duplicateLogs = await ExecuteScalarLongAsync(connection, null, """
            SELECT COUNT(*)
            FROM (
                SELECT id, created_at
                FROM lhyy.esb_process_log_archive
                GROUP BY id, created_at
                HAVING COUNT(*) > 1
            ) d;
            """);
        if (duplicateLogs > 0)
            throw new InvalidOperationException($"归档处理日志存在重复数据：{duplicateLogs} 组。");

        Console.WriteLine("校验通过。");
        return 0;
    }

    private static async Task ValidateOptimizationPrerequisitesAsync(NpgsqlConnection connection)
    {
        var missingColumns = await ExecuteScalarLongAsync(connection, null, """
            SELECT COUNT(*)
            FROM (
                VALUES
                    ('esb_messages', 'id'),
                    ('esb_messages', 'message_id'),
                    ('esb_messages', 'source_message_id'),
                    ('esb_messages', 'tran_code'),
                    ('esb_messages', 'integration_project_code'),
                    ('esb_messages', 'tran_name'),
                    ('esb_messages', 'app_id'),
                    ('esb_messages', 'org_id'),
                    ('esb_messages', 'esb_timestamp'),
                    ('esb_messages', 'raw_json'),
                    ('esb_messages', 'body_json'),
                    ('esb_messages', 'idempotent_key'),
                    ('esb_messages', 'mrn'),
                    ('esb_messages', 'visit_no'),
                    ('esb_messages', 'inpatient_no'),
                    ('esb_messages', 'resolved_event_time'),
                    ('esb_messages', 'matched_rule_group'),
                    ('esb_messages', 'status'),
                    ('esb_messages', 'retry_count'),
                    ('esb_messages', 'error_message'),
                    ('esb_messages', 'patient_id'),
                    ('esb_messages', 'event_id'),
                    ('esb_messages', 'processed_at'),
                    ('esb_messages', 'processing_started_at'),
                    ('esb_messages', 'created_at'),
                    ('esb_process_log', 'id'),
                    ('esb_process_log', 'message_id'),
                    ('esb_process_log', 'integration_project_code'),
                    ('esb_process_log', 'step'),
                    ('esb_process_log', 'is_success'),
                    ('esb_process_log', 'detail'),
                    ('esb_process_log', 'elapsed_ms'),
                    ('esb_process_log', 'created_at'),
                    ('esb_global_config', 'config_key'),
                    ('esb_global_config', 'config_value'),
                    ('esb_global_config', 'config_type'),
                    ('esb_global_config', 'description')
            ) AS required(table_name, column_name)
            WHERE NOT EXISTS (
                SELECT 1
                FROM information_schema.columns c
                WHERE c.table_schema = 'lhyy'
                  AND c.table_name = required.table_name
                  AND c.column_name = required.column_name
            );
            """);

        if (missingColumns > 0)
            throw new InvalidOperationException("当前数据库缺少本次优化依赖的基础表结构。请先完成客户当前版本应执行的基础升级，再执行本次 ESB 消息性能优化升级。");
    }

    private static async Task ValidateArchiveOptimizationAsync(NpgsqlConnection connection)
    {
        var missingObjects = await ExecuteScalarLongAsync(connection, null, """
            SELECT COUNT(*)
            FROM (
                VALUES
                    ('lhyy.esb_messages_archive'),
                    ('lhyy.esb_process_log_archive'),
                    ('lhyy.esb_messages_all'),
                    ('lhyy.esb_process_log_all')
            ) AS required(object_name)
            WHERE to_regclass(required.object_name) IS NULL;
            """);
        if (missingObjects > 0)
            throw new InvalidOperationException("归档表或统一视图未创建完整。");

        var missingIndexes = await ExecuteScalarLongAsync(connection, null, """
            SELECT COUNT(*)
            FROM (
                VALUES
                    ('ix_esb_messages_project_created_id'),
                    ('ix_esb_messages_project_status_created'),
                    ('ix_esb_messages_project_tran_created'),
                    ('ix_esb_messages_project_mrn_created'),
                    ('ix_esb_messages_queue_claim'),
                    ('ix_esb_messages_processing_timeout'),
                    ('ix_esb_messages_archive_id'),
                    ('ux_esb_messages_archive_id_created_at'),
                    ('ix_esb_messages_archive_project_created_id'),
                    ('ix_esb_messages_archive_project_status_created'),
                    ('ix_esb_messages_archive_project_tran_created'),
                    ('ix_esb_messages_archive_project_mrn_created'),
                    ('ix_esb_messages_archive_mrn_event_time'),
                    ('ix_esb_process_log_message_created'),
                    ('ux_esb_process_log_archive_id_created_at'),
                    ('ix_esb_process_log_archive_message_created'),
                    ('ix_esb_process_log_archive_project_created')
            ) AS required(index_name)
            WHERE to_regclass('lhyy.' || required.index_name) IS NULL;
            """);
        if (missingIndexes > 0)
            throw new InvalidOperationException($"缺少 {missingIndexes} 个本次优化相关索引。");

        var invalidIndexes = await ExecuteScalarLongAsync(connection, null, """
            SELECT COUNT(*)
            FROM pg_class c
            INNER JOIN pg_namespace n ON n.oid = c.relnamespace
            INNER JOIN pg_index i ON i.indexrelid = c.oid
            WHERE n.nspname = 'lhyy'
              AND c.relname IN (
                  'ix_esb_messages_project_created_id',
                  'ix_esb_messages_project_status_created',
                  'ix_esb_messages_project_tran_created',
                  'ix_esb_messages_project_mrn_created',
                  'ix_esb_messages_queue_claim',
                  'ix_esb_messages_processing_timeout',
                  'ux_esb_messages_archive_id_created_at',
                  'ix_esb_process_log_message_created',
                  'ux_esb_process_log_archive_id_created_at'
              )
              AND i.indisvalid = FALSE;
            """);
        if (invalidIndexes > 0)
            throw new InvalidOperationException($"存在 {invalidIndexes} 个本次优化相关的无效索引。");

        var unexpectedIndexDefinitions = await ExecuteScalarLongAsync(connection, null, """
            WITH required(index_name, patterns) AS (
                VALUES
                    ('ix_esb_messages_project_created_id', ARRAY['lhyy.esb_messages', 'integration_project_code', 'created_at DESC', 'id DESC']),
                    ('ix_esb_messages_project_status_created', ARRAY['lhyy.esb_messages', 'integration_project_code', 'status', 'created_at DESC']),
                    ('ix_esb_messages_project_tran_created', ARRAY['lhyy.esb_messages', 'integration_project_code', 'tran_code', 'created_at DESC']),
                    ('ix_esb_messages_project_mrn_created', ARRAY['lhyy.esb_messages', 'integration_project_code', 'mrn', 'created_at DESC']),
                    ('ix_esb_messages_queue_claim', ARRAY['lhyy.esb_messages', 'status', 'retry_count', 'created_at', 'id']),
                    ('ix_esb_messages_processing_timeout', ARRAY['lhyy.esb_messages', 'processing_started_at']),
                    ('ix_esb_messages_archive_id', ARRAY['lhyy.esb_messages_archive', 'id']),
                    ('ux_esb_messages_archive_id_created_at', ARRAY['UNIQUE INDEX', 'lhyy.esb_messages_archive', 'id', 'created_at']),
                    ('ix_esb_messages_archive_project_created_id', ARRAY['lhyy.esb_messages_archive', 'integration_project_code', 'created_at DESC', 'id DESC']),
                    ('ix_esb_messages_archive_project_status_created', ARRAY['lhyy.esb_messages_archive', 'integration_project_code', 'status', 'created_at DESC']),
                    ('ix_esb_messages_archive_project_tran_created', ARRAY['lhyy.esb_messages_archive', 'integration_project_code', 'tran_code', 'created_at DESC']),
                    ('ix_esb_messages_archive_project_mrn_created', ARRAY['lhyy.esb_messages_archive', 'integration_project_code', 'mrn', 'created_at DESC']),
                    ('ix_esb_messages_archive_mrn_event_time', ARRAY['lhyy.esb_messages_archive', 'mrn', 'resolved_event_time']),
                    ('ix_esb_process_log_message_created', ARRAY['lhyy.esb_process_log', 'message_id', 'created_at']),
                    ('ux_esb_process_log_archive_id_created_at', ARRAY['UNIQUE INDEX', 'lhyy.esb_process_log_archive', 'id', 'created_at']),
                    ('ix_esb_process_log_archive_message_created', ARRAY['lhyy.esb_process_log_archive', 'message_id', 'created_at']),
                    ('ix_esb_process_log_archive_project_created', ARRAY['lhyy.esb_process_log_archive', 'integration_project_code', 'created_at'])
            )
            SELECT COUNT(*)
            FROM required r
            INNER JOIN pg_class c ON c.relname = r.index_name
            INNER JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'lhyy'
              AND NOT (
                  SELECT bool_and(lower(pg_get_indexdef(c.oid)) LIKE '%' || lower(pattern) || '%')
                  FROM unnest(r.patterns) AS pattern
              );
            """);
        if (unexpectedIndexDefinitions > 0)
            throw new InvalidOperationException($"存在 {unexpectedIndexDefinitions} 个本次优化相关索引定义不符合预期。");
    }

    private static async Task<bool> IsArchiveOptimizationReadyAsync(NpgsqlConnection connection)
    {
        try
        {
            await ValidateArchiveOptimizationAsync(connection);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> ResolveHotRetentionDaysAsync(NpgsqlConnection connection, ToolOptions options)
    {
        if (options.HotDays.HasValue)
            return options.HotDays.Value;

        var configValue = await ExecuteScalarStringAsync(connection, null, """
            SELECT config_value
            FROM lhyy.esb_global_config
            WHERE config_key = 'MessageHotRetentionDays'
            LIMIT 1;
            """);

        return int.TryParse(configValue, out var days) && days > 0
            ? days
            : DefaultHotDays;
    }

    private static async Task<bool> HasEligibleMessagesAsync(NpgsqlConnection connection, DateTime threshold)
    {
        return await ExecuteScalarBoolAsync(connection, null, """
            SELECT EXISTS (
                SELECT 1
                FROM lhyy.esb_messages
                WHERE created_at < @threshold
                  AND status IN (2, 4, 5, 6)
                LIMIT 1
            );
            """, ("threshold", threshold));
    }

    private static async Task<string> EnsureRecentBackupAsync(
        string connectionString,
        string rootPath,
        string? configuredPgDumpPath,
        string? backupStampPath)
    {
        var stampedBackup = ReadStampedBackupFile(connectionString, backupStampPath, BackupReuseWindow);
        if (stampedBackup is not null)
        {
            Console.WriteLine($"使用本次升级步骤生成的备份：{stampedBackup}");
            return stampedBackup;
        }

        var recentBackup = FindRecentBackupFile(connectionString, rootPath, BackupReuseWindow);
        if (recentBackup is not null)
        {
            Console.WriteLine($"检测到 24 小时内已有同一目标库的本次优化备份，跳过重复备份：{recentBackup}");
            return recentBackup;
        }

        Console.WriteLine("迁移将删除热表中已归档数据，开始迁移前备份数据库。");
        var backupFile = await BackupDatabaseAsync(connectionString, rootPath, configuredPgDumpPath);
        WriteBackupStamp(backupStampPath, backupFile);
        return backupFile;
    }

    private static async Task<string> BackupDatabaseAsync(string connectionString, string rootPath, string? configuredPgDumpPath)
    {
        var pgDumpPath = ResolvePgDumpPath(configuredPgDumpPath)
            ?? throw new InvalidOperationException("未找到 pg_dump.exe，无法备份，升级已停止。");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var (host, port) = ResolveHostAndPort(builder);
        var username = !string.IsNullOrWhiteSpace(builder.Username)
            ? builder.Username
            : throw new InvalidOperationException("连接字符串缺少 Username，无法执行备份。");
        var database = !string.IsNullOrWhiteSpace(builder.Database)
            ? builder.Database
            : throw new InvalidOperationException("连接字符串缺少 Database，无法执行备份。");
        var backupDirectory = Path.Combine(rootPath, "DatabaseBackups");
        Directory.CreateDirectory(backupDirectory);

        var backupFile = Path.Combine(
            backupDirectory,
            $"{BuildBackupFilePrefix(host, port, database)}{DateTime.Now:yyyyMMdd_HHmmss}.backup");

        var process = new Process();
        process.StartInfo.FileName = pgDumpPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.Environment["PGPASSWORD"] = builder.Password ?? "";
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("--username");
        process.StartInfo.ArgumentList.Add(username);
        process.StartInfo.ArgumentList.Add("--dbname");
        process.StartInfo.ArgumentList.Add(database);
        process.StartInfo.ArgumentList.Add("--format");
        process.StartInfo.ArgumentList.Add("c");
        process.StartInfo.ArgumentList.Add("--file");
        process.StartInfo.ArgumentList.Add(backupFile);
        process.StartInfo.ArgumentList.Add("--no-password");

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump 备份失败：{error}{output}");

        return backupFile;
    }

    private static string? FindRecentBackupFile(string connectionString, string rootPath, TimeSpan maxAge)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database))
            return null;
        var (host, port) = ResolveHostAndPort(builder);

        var backupDirectory = Path.Combine(rootPath, "DatabaseBackups");
        if (!Directory.Exists(backupDirectory))
            return null;

        var prefix = BuildBackupFilePrefix(host, port, builder.Database);
        var cutoff = DateTime.Now.Subtract(maxAge);
        return Directory.GetFiles(backupDirectory, $"{prefix}*.backup", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => file.LastWriteTime >= cutoff)
            .OrderByDescending(file => file.LastWriteTime)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static string? ReadStampedBackupFile(string connectionString, string? backupStampPath, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(backupStampPath) || !File.Exists(backupStampPath))
            return null;

        var stamp = ReadBackupStamp(backupStampPath);
        var backupFile = stamp.GetValueOrDefault("backup_file");
        if (string.IsNullOrWhiteSpace(backupFile) || !File.Exists(backupFile))
            return null;

        if (!DateTime.TryParse(stamp.GetValueOrDefault("created_at"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var createdAt)
            || DateTime.Now - createdAt.ToLocalTime() > maxAge)
        {
            return null;
        }

        var targetKey = BuildConnectionBackupKey(connectionString);
        if (!string.Equals(stamp.GetValueOrDefault("target"), targetKey, StringComparison.OrdinalIgnoreCase))
            return null;

        return IsBackupFileForConnection(connectionString, backupFile, maxAge) ? backupFile : null;
    }

    private static void WriteBackupStamp(string? backupStampPath, string backupFile)
    {
        if (string.IsNullOrWhiteSpace(backupStampPath))
            return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(backupStampPath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var lines = new[]
        {
            $"target={BuildConnectionBackupKeyFromBackupFile(backupFile)}",
            $"backup_file={backupFile}",
            $"created_at={DateTime.Now:O}"
        };
        File.WriteAllLines(backupStampPath, lines);
    }

    private static Dictionary<string, string> ReadBackupStamp(string backupStampPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(backupStampPath))
        {
            var index = line.IndexOf('=');
            if (index <= 0)
                continue;

            result[line[..index].Trim()] = line[(index + 1)..].Trim();
        }

        return result;
    }

    private static bool IsBackupFileForConnection(string connectionString, string backupFile, TimeSpan? maxAge = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database))
            return false;

        var (host, port) = ResolveHostAndPort(builder);
        var prefix = BuildBackupFilePrefix(host, port, builder.Database);
        if (!Path.GetFileName(backupFile).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return maxAge is null || DateTime.Now - File.GetLastWriteTime(backupFile) <= maxAge.Value;
    }

    private static string BuildConnectionBackupKey(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database))
            return "";

        var (host, port) = ResolveHostAndPort(builder);
        return BuildConnectionBackupKey(host, port, builder.Database);
    }

    private static string BuildConnectionBackupKeyFromBackupFile(string backupFile)
    {
        var fileName = Path.GetFileNameWithoutExtension(backupFile);
        var marker = "_esb_messages_opt_";
        var markerIndex = fileName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return markerIndex > 0 ? fileName[..markerIndex] : "";
    }

    private static string BuildConnectionBackupKey(string host, int port, string database) =>
        $"{SanitizeBackupFilePart(host)}_{port}_{SanitizeBackupFilePart(database)}";

    private static string BuildBackupFilePrefix(string host, int port, string database) =>
        $"{BuildConnectionBackupKey(host, port, database)}_esb_messages_opt_";

    private static string SanitizeBackupFilePart(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalidChars.Contains(ch) || ch is ':' or '\\' or '/' ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static string? ResolvePgDumpPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath)
                && string.Equals(Path.GetFileName(configuredPath), OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump", StringComparison.OrdinalIgnoreCase))
            {
                return configuredPath;
            }

            var directory = File.Exists(configuredPath)
                ? Path.GetDirectoryName(configuredPath)
                : configuredPath;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var executableName = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var postgresRoot = Path.Combine(programFiles, "PostgreSQL");
        if (!Directory.Exists(postgresRoot))
            return null;

        return Directory.GetFiles(postgresRoot, executableName, SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static (string Host, int Port) ResolveHostAndPort(NpgsqlConnectionStringBuilder builder)
    {
        var host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;
        var port = builder.Port > 0 ? builder.Port : 5432;
        var parts = host.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort))
            return (parts[0], parsedPort);

        return (host, port);
    }

    private static async Task<List<DateTime>> LoadArchiveMonthsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var result = new List<DateTime>();
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT date_trunc('month', month_value)::date
            FROM (
                SELECT m.created_at AS month_value
                FROM tmp_esb_archive_batch m
                UNION
                SELECT l.created_at AS month_value
                FROM lhyy.esb_process_log l
                INNER JOIN tmp_esb_archive_batch b ON b.id = l.message_id
            ) s
            ORDER BY 1;
            """, connection, transaction);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetDateTime(0));

        return result;
    }

    private static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarLongAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private static async Task<bool> ExecuteScalarBoolAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        return value is bool result && result;
    }

    private static async Task<string?> ExecuteScalarStringAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        return value?.ToString();
    }

    private static NpgsqlCommand BuildCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task<bool> TryAcquireArchiveLockAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lockKey);", connection);
        command.Parameters.AddWithValue("lockKey", ArchiveLockKey);
        var value = await command.ExecuteScalarAsync();
        return value is bool acquired && acquired;
    }

    private static async Task ReleaseArchiveLockAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@lockKey);", connection);
        command.Parameters.AddWithValue("lockKey", ArchiveLockKey);
        await command.ExecuteScalarAsync();
    }

    private static int PrintUnknownVerb(string verb)
    {
        Console.WriteLine($"未知 message-archive 子命令：{verb}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  message-archive upgrade --connection DataSyncDb --pg-dump \"C:\\Program Files\\PostgreSQL\\17\\bin\\pg_dump.exe\" --backup-stamp \"%TEMP%\\esb_messages_opt.stamp\"");
        Console.WriteLine("  message-archive migrate --connection DataSyncDb --batch-size 50000 --backup-stamp \"%TEMP%\\esb_messages_opt.stamp\"");
        Console.WriteLine("  message-archive migrate --connection DataSyncDb --hot-days 60 --batch-size 50000");
        Console.WriteLine("  message-archive upgrade --connection DataSyncDb --skip-backup");
        Console.WriteLine("  message-archive verify --connection DataSyncDb");
    }

    private sealed record ToolOptions(
        string? ConnectionName = null,
        string? ScriptsPath = null,
        string? PgDumpPath = null,
        string? BackupStampPath = null,
        int? HotDays = null,
        int BatchSize = DefaultBatchSize,
        bool DryRun = false,
        bool SkipBackup = false)
    {
        public static ToolOptions Parse(string[] args)
        {
            var options = new ToolOptions();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--connection":
                    case "-c":
                        options = options with { ConnectionName = ReadValue(args, ref i) };
                        break;
                    case "--scripts":
                        options = options with { ScriptsPath = ReadValue(args, ref i) };
                        break;
                    case "--pg-dump":
                        options = options with { PgDumpPath = ReadValue(args, ref i) };
                        break;
                    case "--backup-stamp":
                        options = options with { BackupStampPath = ReadValue(args, ref i) };
                        break;
                    case "--hot-days":
                        options = options with { HotDays = ReadPositiveInt(args, ref i, DefaultHotDays) };
                        break;
                    case "--batch-size":
                        options = options with { BatchSize = ReadPositiveInt(args, ref i, DefaultBatchSize) };
                        break;
                    case "--dry-run":
                        options = options with { DryRun = true };
                        break;
                    case "--skip-backup":
                        options = options with { SkipBackup = true };
                        break;
                }
            }

            return options;
        }

        private static string? ReadValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
                return null;

            index++;
            return args[index];
        }

        private static int ReadPositiveInt(string[] args, ref int index, int fallback)
        {
            var value = ReadValue(args, ref index);
            return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
        }
    }
}
