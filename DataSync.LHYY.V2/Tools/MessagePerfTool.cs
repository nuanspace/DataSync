using System.Diagnostics;
using Npgsql;

namespace DataSync.LHYY.V2.Tools;

public static class MessagePerfTool
{
    private const string CommandName = "message-perf";
    private const string DefaultProject = "PERF_TEST";

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
                "seed" => await SeedAsync(connectionString, options),
                "verify" => await VerifyAsync(connectionString, options),
                "cleanup" => await CleanupAsync(connectionString, options.Project),
                _ => PrintUnknownVerb(verb)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"消息性能工具执行失败：{ex.Message}");
            return 1;
        }
    }

    private static async Task<int> SeedAsync(string connectionString, ToolOptions options)
    {
        if (!string.Equals(options.Project, DefaultProject, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("造数命令只允许写入 PERF_TEST 项目。");

        if (!options.ConfirmSeed)
            throw new InvalidOperationException("造数命令需要显式增加 --confirm-perf-seed，避免误向生产库写入测试数据。");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var startDay = DateTime.Today.AddDays(-options.Days + 1);
        var total = 0L;
        for (var dayOffset = 0; dayOffset < options.Days; dayOffset++)
        {
            var day = startDay.AddDays(dayOffset);
            var stopwatch = Stopwatch.StartNew();

            var inserted = await ExecuteNonQueryAsync(connection, """
                INSERT INTO lhyy.esb_messages (
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
                    status,
                    retry_count,
                    error_message,
                    processed_at,
                    created_at
                )
                SELECT
                    'perf-' || to_char(CAST(@day AS timestamp), 'YYYYMMDD') || '-' || g,
                    'src-' || to_char(CAST(@day AS timestamp), 'YYYYMMDD') || '-' || g,
                    'PERF' || lpad((g % 20)::text, 2, '0'),
                    @project,
                    '性能测试接口' || lpad((g % 20)::text, 2, '0'),
                    'PERF_APP',
                    'PERF_ORG',
                    to_char(CAST(@day AS timestamp) + ((g % 86400) * interval '1 second'), 'YYYYMMDDHH24MISS'),
                    jsonb_build_object('project', @project, 'day', to_char(CAST(@day AS timestamp), 'YYYY-MM-DD'), 'seq', g),
                    jsonb_build_object('body', jsonb_build_object('mrn', 'MRN' || lpad((g % 50000)::text, 8, '0'), 'seq', g)),
                    'idem-' || to_char(CAST(@day AS timestamp), 'YYYYMMDD') || '-' || g,
                    'MRN' || lpad((g % 50000)::text, 8, '0'),
                    (g % 10)::text,
                    'INP' || lpad((g % 80000)::text, 8, '0'),
                    CAST(@day AS timestamp) + ((g % 86400) * interval '1 second'),
                    CASE
                        WHEN g % 1000 = 0 THEN 0
                        WHEN g % 1000 = 1 THEN 3
                        WHEN g % 1000 = 2 THEN 4
                        WHEN g % 1000 = 3 THEN 5
                        ELSE 2
                    END,
                    CASE WHEN g % 1000 = 1 THEN 1 ELSE 0 END,
                    CASE WHEN g % 1000 = 1 THEN '性能测试失败样本' ELSE NULL END,
                    CASE WHEN g % 1000 IN (0, 1) THEN NULL ELSE CAST(@day AS timestamp) + ((g % 86400) * interval '1 second') END,
                    CAST(@day AS timestamp) + ((g % 86400) * interval '1 second')
                FROM generate_series(1, @perDay) AS g
                ON CONFLICT (message_id) DO NOTHING;
                """,
                ("day", day),
                ("project", options.Project),
                ("perDay", options.PerDay));

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO lhyy.esb_process_log (
                    message_id,
                    integration_project_code,
                    step,
                    is_success,
                    detail,
                    elapsed_ms,
                    created_at
                )
                SELECT
                    m.id,
                    m.integration_project_code,
                    '性能测试处理汇总',
                    m.status <> 3,
                    '{"processed":1,"failed":0,"items":[],"steps":[]}',
                    12,
                    m.created_at
                FROM lhyy.esb_messages m
                WHERE m.integration_project_code = @project
                  AND m.created_at >= CAST(@day AS timestamp)
                  AND m.created_at < CAST(@day AS timestamp) + interval '1 day'
                  AND m.id % 1000 = 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM lhyy.esb_process_log l
                      WHERE l.message_id = m.id
                        AND l.step = '性能测试处理汇总'
                  );
                """,
                ("project", options.Project),
                ("day", day));

            stopwatch.Stop();
            total += inserted;
            Console.WriteLine($"{day:yyyy-MM-dd}: 插入消息 {inserted} 条，耗时 {stopwatch.Elapsed}。");
        }

        Console.WriteLine($"造数完成：项目 {options.Project}，消息 {total} 条。");
        return 0;
    }

    private static async Task<int> VerifyAsync(string connectionString, ToolOptions options)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var project = options.Project;
        Console.WriteLine($"开始业务抽样验证：项目 {project}");

        await TimedScalarAsync(connection, "热表数量", "SELECT COUNT(*) FROM lhyy.esb_messages WHERE integration_project_code = @project;", ("project", project));
        await TimedScalarAsync(connection, "归档表数量", "SELECT COUNT(*) FROM lhyy.esb_messages_archive WHERE integration_project_code = @project;", ("project", project));
        await TimedScalarAsync(connection, "统一视图数量", "SELECT COUNT(*) FROM lhyy.esb_messages_all WHERE integration_project_code = @project;", ("project", project));
        await TimedRowsAsync(connection, "默认列表", """
            SELECT id, message_id, tran_code, status, created_at
            FROM lhyy.esb_messages_all
            WHERE integration_project_code = @project
              AND created_at >= @startTime
            ORDER BY created_at DESC, id DESC
            LIMIT 20;
            """,
            ("project", project),
            ("startTime", DateTime.Today.AddDays(-30)));
        await TimedRowsAsync(connection, "历史列表", """
            SELECT id, message_id, tran_code, status, created_at
            FROM lhyy.esb_messages_all
            WHERE integration_project_code = @project
              AND created_at >= @startTime
              AND created_at < @endTime
            ORDER BY created_at DESC, id DESC
            LIMIT 20;
            """,
            ("project", project),
            ("startTime", DateTime.Today.AddDays(-90)),
            ("endTime", DateTime.Today.AddDays(-60)));
        await TimedRowsAsync(connection, "按状态筛选", """
            SELECT id, message_id, tran_code, status, created_at
            FROM lhyy.esb_messages_all
            WHERE integration_project_code = @project
              AND status = 3
              AND created_at >= @startTime
            ORDER BY created_at DESC, id DESC
            LIMIT 20;
            """,
            ("project", project),
            ("startTime", DateTime.Today.AddDays(-90)));
        await TimedRowsAsync(connection, "按接口筛选", """
            SELECT id, message_id, tran_code, status, created_at
            FROM lhyy.esb_messages_all
            WHERE integration_project_code = @project
              AND tran_code = 'PERF01'
              AND created_at >= @startTime
            ORDER BY created_at DESC, id DESC
            LIMIT 20;
            """,
            ("project", project),
            ("startTime", DateTime.Today.AddDays(-90)));
        await TimedRowsAsync(connection, "按病案号筛选", """
            SELECT id, message_id, tran_code, mrn, status, created_at
            FROM lhyy.esb_messages_all
            WHERE integration_project_code = @project
              AND mrn = 'MRN00000042'
              AND created_at >= @startTime
            ORDER BY created_at DESC, id DESC
            LIMIT 20;
            """,
            ("project", project),
            ("startTime", DateTime.Today.AddDays(-90)));

        var sampleId = await ExecuteScalarLongAsync(connection, """
            SELECT m.id
            FROM lhyy.esb_messages_all m
            WHERE m.integration_project_code = @project
              AND EXISTS (
                  SELECT 1
                  FROM lhyy.esb_process_log_all l
                  WHERE l.message_id = m.id
              )
            ORDER BY m.created_at
            LIMIT 1;
            """,
            ("project", project));

        if (sampleId == 0)
        {
            sampleId = await ExecuteScalarLongAsync(connection, """
                SELECT id
                FROM lhyy.esb_messages_all
                WHERE integration_project_code = @project
                ORDER BY created_at
                LIMIT 1;
                """,
                ("project", project));
        }

        if (sampleId > 0)
        {
            await TimedRowsAsync(connection, "详情报文", """
                SELECT id, raw_json, body_json
                FROM lhyy.esb_messages_all
                WHERE integration_project_code = @project
                  AND id = @id;
                """,
                ("project", project),
                ("id", sampleId));
            await TimedRowsAsync(connection, "详情日志", """
                SELECT id, step, detail, created_at
                FROM lhyy.esb_process_log_all
                WHERE integration_project_code = @project
                  AND message_id = @id
                ORDER BY created_at, id;
                """,
                ("project", project),
                ("id", sampleId));
        }

        await TimedRowsAsync(connection, "后台抢占候选", """
            SELECT id
            FROM lhyy.esb_messages
            WHERE integration_project_code = @project
              AND (status = 0 OR (status = 3 AND retry_count < 3))
            ORDER BY created_at
            LIMIT 10;
            """,
            ("project", project));

        Console.WriteLine("业务抽样验证完成。");
        return 0;
    }

    private static async Task<int> CleanupAsync(string connectionString, string project)
    {
        if (!string.Equals(project, DefaultProject, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("清理命令只允许删除 PERF_TEST 项目数据。");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var archiveLogs = await ExecuteNonQueryAsync(connection, "DELETE FROM lhyy.esb_process_log_archive WHERE integration_project_code = @project;", ("project", project));
        var hotLogs = await ExecuteNonQueryAsync(connection, "DELETE FROM lhyy.esb_process_log WHERE integration_project_code = @project;", ("project", project));
        var archiveMessages = await ExecuteNonQueryAsync(connection, "DELETE FROM lhyy.esb_messages_archive WHERE integration_project_code = @project;", ("project", project));
        var hotMessages = await ExecuteNonQueryAsync(connection, "DELETE FROM lhyy.esb_messages WHERE integration_project_code = @project;", ("project", project));
        var receipts = await ExecuteNonQueryAsync(connection, "DELETE FROM lhyy.esb_message_receipt WHERE integration_project_code = @project;", ("project", project));

        Console.WriteLine($"清理完成：热表消息 {hotMessages}，归档消息 {archiveMessages}，热表日志 {hotLogs}，归档日志 {archiveLogs}，回执 {receipts}。");
        return 0;
    }

    private static async Task TimedScalarAsync(
        NpgsqlConnection connection,
        string name,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = await ExecuteScalarLongAsync(connection, sql, parameters);
        stopwatch.Stop();
        Console.WriteLine($"{name}: {value}，耗时 {stopwatch.ElapsedMilliseconds}ms");
    }

    private static async Task TimedRowsAsync(
        NpgsqlConnection connection,
        string name,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var stopwatch = Stopwatch.StartNew();
        var count = 0;
        await using var command = BuildCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            count++;
        stopwatch.Stop();
        Console.WriteLine($"{name}: 返回 {count} 行，耗时 {stopwatch.ElapsedMilliseconds}ms");
    }

    private static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarLongAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
    }

    private static NpgsqlCommand BuildCommand(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static int PrintUnknownVerb(string verb)
    {
        Console.WriteLine($"未知 message-perf 子命令：{verb}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  message-perf seed --connection DataSyncDb --project PERF_TEST --days 90 --per-day 300000 --confirm-perf-seed");
        Console.WriteLine("  message-perf verify --connection DataSyncDb --project PERF_TEST");
        Console.WriteLine("  message-perf cleanup --connection DataSyncDb --project PERF_TEST");
    }

    private sealed record ToolOptions(
        string? ConnectionName = null,
        string? ScriptsPath = null,
        string Project = DefaultProject,
        int Days = 90,
        int PerDay = 300000,
        bool ConfirmSeed = false)
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
                    case "--project":
                        options = options with { Project = ReadValue(args, ref i) ?? DefaultProject };
                        break;
                    case "--days":
                        options = options with { Days = ReadPositiveInt(args, ref i, 90) };
                        break;
                    case "--per-day":
                        options = options with { PerDay = ReadPositiveInt(args, ref i, 300000) };
                        break;
                    case "--confirm-perf-seed":
                        options = options with { ConfirmSeed = true };
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
