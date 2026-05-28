using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;
using System.Diagnostics;
using System.Text;

namespace DataSync.LHYY.V2.Services;

public sealed class DatabaseCompareService
{
    public const string SourceConnection = "Connection";
    public const string SourceSqlFile = "SqlFile";
    public const string DataSyncModeClearImport = "ClearImport";
    public const string DataSyncModeUpsert = "Upsert";

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public DatabaseCompareService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public async Task<DatabaseCompareResult> CheckAsync(
        DatabaseCompareRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(request.TargetConnectionName);
        var schemas = ParseSchemas(request.SchemasText);

        if (request.SourceMode == SourceSqlFile)
        {
            var sqlFilePath = ResolveSqlFilePath(request.SourceSqlFilePath);
            var tempDatabaseName = BuildTempDatabaseName();
            await CreateTemporaryDatabaseAsync(target.ConnectionString, tempDatabaseName, cancellationToken);
            try
            {
                var tempConnectionString = BuildDatabaseConnectionString(target.ConnectionString, tempDatabaseName);
                await ExecuteSqlFileByPsqlAsync(tempConnectionString, sqlFilePath, request.ToolPath, cancellationToken);
                await using var sourceConnection = new NpgsqlConnection(tempConnectionString);
                await using var targetConnection = new NpgsqlConnection(target.ConnectionString);
                await sourceConnection.OpenAsync(cancellationToken);
                await targetConnection.OpenAsync(cancellationToken);
                return await BuildCompareResultAsync(sourceConnection, targetConnection, schemas, request.IncludeDrop, cancellationToken);
            }
            finally
            {
                await DropTemporaryDatabaseAsync(target.ConnectionString, tempDatabaseName, cancellationToken);
            }
        }

        var source = GetConnection(request.SourceConnectionName ?? "");
        await using var highConnection = new NpgsqlConnection(source.ConnectionString);
        await using var lowConnection = new NpgsqlConnection(target.ConnectionString);
        await highConnection.OpenAsync(cancellationToken);
        await lowConnection.OpenAsync(cancellationToken);
        return await BuildCompareResultAsync(highConnection, lowConnection, schemas, request.IncludeDrop, cancellationToken);
    }

    public async Task<DatabaseCompareExecuteResult> ExecuteAsync(
        DatabaseCompareRequest request,
        List<DatabaseTableDataSyncRequest> dataSyncTables,
        bool skipBackup,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(request.TargetConnectionName);
        var schemas = ParseSchemas(request.SchemasText);
        var backupFile = skipBackup
            ? "已手工备份，跳过自动备份"
            : await BackupDatabaseAsync(target.ConnectionString, request.ToolPath, cancellationToken);

        if (request.SourceMode == SourceSqlFile)
        {
            var sqlFilePath = ResolveSqlFilePath(request.SourceSqlFilePath);
            var tempDatabaseName = BuildTempDatabaseName();
            await CreateTemporaryDatabaseAsync(target.ConnectionString, tempDatabaseName, cancellationToken);
            try
            {
                var tempConnectionString = BuildDatabaseConnectionString(target.ConnectionString, tempDatabaseName);
                await ExecuteSqlFileByPsqlAsync(tempConnectionString, sqlFilePath, request.ToolPath, cancellationToken);
                await using var sourceConnection = new NpgsqlConnection(tempConnectionString);
                await using var targetConnection = new NpgsqlConnection(target.ConnectionString);
                await sourceConnection.OpenAsync(cancellationToken);
                await targetConnection.OpenAsync(cancellationToken);
                return await ExecuteCompareAsync(sourceConnection, targetConnection, schemas, request.IncludeDrop, dataSyncTables, backupFile, cancellationToken);
            }
            finally
            {
                await DropTemporaryDatabaseAsync(target.ConnectionString, tempDatabaseName, cancellationToken);
            }
        }

        var source = GetConnection(request.SourceConnectionName ?? "");
        await using var highConnection = new NpgsqlConnection(source.ConnectionString);
        await using var lowConnection = new NpgsqlConnection(target.ConnectionString);
        await highConnection.OpenAsync(cancellationToken);
        await lowConnection.OpenAsync(cancellationToken);
        return await ExecuteCompareAsync(highConnection, lowConnection, schemas, request.IncludeDrop, dataSyncTables, backupFile, cancellationToken);
    }

    private async Task<DatabaseCompareExecuteResult> ExecuteCompareAsync(
        NpgsqlConnection sourceConnection,
        NpgsqlConnection targetConnection,
        List<string> schemas,
        bool includeDrop,
        List<DatabaseTableDataSyncRequest> dataSyncTables,
        string backupFile,
        CancellationToken cancellationToken)
    {
        var result = await BuildCompareResultAsync(sourceConnection, targetConnection, schemas, includeDrop, cancellationToken);
        var appliedSqlCount = 0;
        foreach (var item in result.Differences.Where(item => !string.IsNullOrWhiteSpace(item.Sql)))
        {
            await ExecuteNonQueryAsync(targetConnection, item.Sql, cancellationToken);
            appliedSqlCount++;
        }

        var syncedTables = new List<string>();
        foreach (var table in dataSyncTables)
        {
            await SyncTableDataAsync(sourceConnection, targetConnection, table, cancellationToken);
            syncedTables.Add($"{table.Schema}.{table.Table}（{GetDataSyncModeName(table.Mode)}）");
        }

        return new DatabaseCompareExecuteResult(backupFile, appliedSqlCount, syncedTables);
    }

    private async Task<DatabaseCompareResult> BuildCompareResultAsync(
        NpgsqlConnection sourceConnection,
        NpgsqlConnection targetConnection,
        List<string> schemas,
        bool includeDrop,
        CancellationToken cancellationToken)
    {
        var source = await LoadSnapshotAsync(sourceConnection, schemas, cancellationToken);
        var target = await LoadSnapshotAsync(targetConnection, schemas, cancellationToken);
        var differences = BuildDifferences(source, target, includeDrop);
        var tables = source.Tables.Values
            .OrderBy(table => table.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(table => new DatabaseCompareTableOption(table.Schema, table.Name, table.FullName))
            .ToList();

        return new DatabaseCompareResult(schemas, differences, tables);
    }

    private static List<DatabaseCompareDiffItem> BuildDifferences(DatabaseSnapshot source, DatabaseSnapshot target, bool includeDrop)
    {
        var result = new List<DatabaseCompareDiffItem>();

        foreach (var schema in source.Schemas.Except(target.Schemas, StringComparer.OrdinalIgnoreCase))
            Add(result, "Schema", schema, "新增", $"CREATE SCHEMA IF NOT EXISTS {QuoteName(schema)};");

        foreach (var sequence in source.Sequences.Values.Where(item => !target.Sequences.ContainsKey(item.Key)))
            Add(result, "Sequence", sequence.FullName, "新增", sequence.CreateSql);

        foreach (var table in source.Tables.Values.Where(item => !target.Tables.ContainsKey(item.Key)))
        {
            var columns = source.Columns.Values
                .Where(column => column.TableKey == table.Key)
                .OrderBy(column => column.Ordinal)
                .Select(column => "    " + column.BuildDefinition());
            Add(result, "Table", table.FullName, "新增", $"CREATE TABLE {table.FullName} ({Environment.NewLine}{string.Join("," + Environment.NewLine, columns)}{Environment.NewLine});");
        }

        foreach (var column in source.Columns.Values)
        {
            if (!target.Tables.ContainsKey(column.TableKey))
                continue;

            if (!target.Columns.TryGetValue(column.Key, out var targetColumn))
            {
                Add(result, "Column", column.FullName, "新增", $"ALTER TABLE {column.TableFullName} ADD COLUMN {column.BuildDefinition()};");
                continue;
            }

            if (!string.Equals(column.TypeSql, targetColumn.TypeSql, StringComparison.OrdinalIgnoreCase))
            {
                Add(result, "Column", column.FullName, "类型变更",
                    $"ALTER TABLE {column.TableFullName} ALTER COLUMN {QuoteName(column.Name)} TYPE {column.TypeSql} USING {QuoteName(column.Name)}::{column.TypeSql};");
            }

            if (!column.HasGeneratedValue
                && !targetColumn.HasGeneratedValue
                && !NormalizeSql(column.DefaultSql).Equals(NormalizeSql(targetColumn.DefaultSql), StringComparison.OrdinalIgnoreCase))
            {
                var sql = string.IsNullOrWhiteSpace(column.DefaultSql)
                    ? $"ALTER TABLE {column.TableFullName} ALTER COLUMN {QuoteName(column.Name)} DROP DEFAULT;"
                    : $"ALTER TABLE {column.TableFullName} ALTER COLUMN {QuoteName(column.Name)} SET DEFAULT {column.DefaultSql};";
                Add(result, "Column", column.FullName, "默认值变更", sql);
            }

            if (column.IsNullable != targetColumn.IsNullable)
            {
                var sql = column.IsNullable
                    ? $"ALTER TABLE {column.TableFullName} ALTER COLUMN {QuoteName(column.Name)} DROP NOT NULL;"
                    : $"ALTER TABLE {column.TableFullName} ALTER COLUMN {QuoteName(column.Name)} SET NOT NULL;";
                Add(result, "Column", column.FullName, "非空变更", sql);
            }
        }

        foreach (var constraint in source.Constraints.Values)
        {
            if (!target.Tables.ContainsKey(constraint.TableKey))
                continue;

            if (!target.Constraints.TryGetValue(constraint.Key, out var targetConstraint))
            {
                Add(result, "Constraint", constraint.FullName, "新增", constraint.AddSql);
                continue;
            }

            if (!NormalizeSql(constraint.Definition).Equals(NormalizeSql(targetConstraint.Definition), StringComparison.OrdinalIgnoreCase))
            {
                var sql = includeDrop
                    ? $"{targetConstraint.DropSql}{Environment.NewLine}{constraint.AddSql}"
                    : "";
                Add(result, "Constraint", constraint.FullName, "定义变更", sql, true, "需要删除旧约束后重建");
            }
        }

        foreach (var index in source.Indexes.Values)
        {
            if (!target.Tables.ContainsKey(index.TableKey))
                continue;

            if (!target.Indexes.TryGetValue(index.Key, out var targetIndex))
            {
                Add(result, "Index", index.FullName, "新增", index.CreateSql);
                continue;
            }

            if (!NormalizeSql(index.Definition).Equals(NormalizeSql(targetIndex.Definition), StringComparison.OrdinalIgnoreCase))
            {
                var sql = includeDrop
                    ? $"{index.DropSql}{Environment.NewLine}{index.CreateSql}"
                    : "";
                Add(result, "Index", index.FullName, "定义变更", sql, true, "需要删除旧索引后重建");
            }
        }

        foreach (var function in source.Functions.Values)
        {
            if (!target.Functions.TryGetValue(function.Key, out var targetFunction)
                || !NormalizeSql(function.Definition).Equals(NormalizeSql(targetFunction.Definition), StringComparison.OrdinalIgnoreCase))
            {
                Add(result, "Function", function.FullName, target.Functions.ContainsKey(function.Key) ? "定义变更" : "新增", EnsureSemicolon(function.Definition));
            }
        }

        foreach (var view in source.Views.Values)
        {
            if (!target.Views.TryGetValue(view.Key, out var targetView)
                || !NormalizeSql(view.Definition).Equals(NormalizeSql(targetView.Definition), StringComparison.OrdinalIgnoreCase))
            {
                Add(result, "View", view.FullName, target.Views.ContainsKey(view.Key) ? "定义变更" : "新增", view.CreateSql);
            }
        }

        foreach (var trigger in source.Triggers.Values)
        {
            if (!target.Triggers.TryGetValue(trigger.Key, out var targetTrigger))
            {
                Add(result, "Trigger", trigger.FullName, "新增", trigger.CreateSql);
                continue;
            }

            if (!NormalizeSql(trigger.Definition).Equals(NormalizeSql(targetTrigger.Definition), StringComparison.OrdinalIgnoreCase))
            {
                var sql = includeDrop
                    ? $"{trigger.DropSql}{Environment.NewLine}{trigger.CreateSql}"
                    : "";
                Add(result, "Trigger", trigger.FullName, "定义变更", sql, true, "需要删除旧触发器后重建");
            }
        }

        AppendExtraObjects(result, source, target, includeDrop);
        return result;
    }

    private static void AppendExtraObjects(List<DatabaseCompareDiffItem> result, DatabaseSnapshot source, DatabaseSnapshot target, bool includeDrop)
    {
        foreach (var trigger in target.Triggers.Values.Where(item => !source.Triggers.ContainsKey(item.Key)))
            Add(result, "Trigger", trigger.FullName, "低版本多余", includeDrop ? trigger.DropSql : "", true, "删除类操作");

        foreach (var view in target.Views.Values.Where(item => !source.Views.ContainsKey(item.Key)))
            Add(result, "View", view.FullName, "低版本多余", includeDrop ? view.DropSql : "", true, "删除类操作");

        foreach (var function in target.Functions.Values.Where(item => !source.Functions.ContainsKey(item.Key)))
            Add(result, "Function", function.FullName, "低版本多余", includeDrop ? function.DropSql : "", true, "删除类操作");

        foreach (var index in target.Indexes.Values.Where(item => !source.Indexes.ContainsKey(item.Key)))
            Add(result, "Index", index.FullName, "低版本多余", includeDrop ? index.DropSql : "", true, "删除类操作");

        foreach (var constraint in target.Constraints.Values.Where(item => !source.Constraints.ContainsKey(item.Key)))
            Add(result, "Constraint", constraint.FullName, "低版本多余", includeDrop ? constraint.DropSql : "", true, "删除类操作");

        foreach (var column in target.Columns.Values.Where(item => !source.Columns.ContainsKey(item.Key) && source.Tables.ContainsKey(item.TableKey)))
            Add(result, "Column", column.FullName, "低版本多余", includeDrop ? $"ALTER TABLE {column.TableFullName} DROP COLUMN IF EXISTS {QuoteName(column.Name)};" : "", true, "删除类操作");

        foreach (var table in target.Tables.Values.Where(item => !source.Tables.ContainsKey(item.Key)))
            Add(result, "Table", table.FullName, "低版本多余", includeDrop ? $"DROP TABLE IF EXISTS {table.FullName} CASCADE;" : "", true, "删除类操作");

        foreach (var sequence in target.Sequences.Values.Where(item => !source.Sequences.ContainsKey(item.Key)))
            Add(result, "Sequence", sequence.FullName, "低版本多余", includeDrop ? $"DROP SEQUENCE IF EXISTS {sequence.FullName} CASCADE;" : "", true, "删除类操作");
    }

    private async Task SyncTableDataAsync(
        NpgsqlConnection sourceConnection,
        NpgsqlConnection targetConnection,
        DatabaseTableDataSyncRequest request,
        CancellationToken cancellationToken)
    {
        var columns = await LoadTableColumnsAsync(targetConnection, request.Schema, request.Table, cancellationToken);
        if (columns.Count == 0)
            return;

        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (request.Mode == DataSyncModeClearImport)
            {
                await ExecuteNonQueryAsync(targetConnection, $"TRUNCATE TABLE {QuoteTable(request.Schema, request.Table)} RESTART IDENTITY CASCADE;", cancellationToken, transaction);
            }

            var keyColumns = request.Mode == DataSyncModeUpsert
                ? await LoadConflictColumnsAsync(targetConnection, request.Schema, request.Table, columns, cancellationToken)
                : [];
            if (request.Mode == DataSyncModeUpsert && keyColumns.Count == 0)
                throw new InvalidOperationException($"{request.Schema}.{request.Table} 没有可用于更新插入的唯一键。");

            var selectSql = $"SELECT {string.Join(", ", columns.Select(QuoteName))} FROM {QuoteTable(request.Schema, request.Table)};";
            await using var selectCommand = new NpgsqlCommand(selectSql, sourceConnection) { CommandTimeout = 0 };
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new object[columns.Count];
                reader.GetValues(values);
                await UpsertRowAsync(targetConnection, transaction, request, columns, keyColumns, values, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task UpsertRowAsync(
        NpgsqlConnection targetConnection,
        NpgsqlTransaction transaction,
        DatabaseTableDataSyncRequest request,
        List<string> columns,
        List<string> keyColumns,
        object?[] values,
        CancellationToken cancellationToken)
    {
        var tableName = QuoteTable(request.Schema, request.Table);
        var columnSql = string.Join(", ", columns.Select(QuoteName));
        var parameterSql = string.Join(", ", columns.Select((_, index) => $"@p{index}"));

        if (request.Mode == DataSyncModeUpsert)
        {
            var updateColumns = columns
                .Where(column => !keyColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (updateColumns.Count > 0)
            {
                var updateSql = $"""
                    UPDATE {tableName}
                    SET {string.Join(", ", updateColumns.Select(column => $"{QuoteName(column)} = @p{columns.FindIndex(item => string.Equals(item, column, StringComparison.OrdinalIgnoreCase))}"))}
                    WHERE {BuildKeyWhereSql(columns, keyColumns)};
                    """;
                await using var updateCommand = BuildCommand(targetConnection, transaction, updateSql, values);
                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected > 0)
                    return;
            }
            else
            {
                var existsSql = $"SELECT 1 FROM {tableName} WHERE {BuildKeyWhereSql(columns, keyColumns)} LIMIT 1;";
                await using var existsCommand = BuildCommand(targetConnection, transaction, existsSql, values);
                if (await existsCommand.ExecuteScalarAsync(cancellationToken) != null)
                    return;
            }
        }

        var insertSql = $"INSERT INTO {tableName} ({columnSql}) VALUES ({parameterSql});";
        await using var insertCommand = BuildCommand(targetConnection, transaction, insertSql, values);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildKeyWhereSql(List<string> columns, List<string> keyColumns) =>
        string.Join(" AND ", keyColumns.Select(column =>
            $"{QuoteName(column)} IS NOT DISTINCT FROM @p{columns.FindIndex(item => string.Equals(item, column, StringComparison.OrdinalIgnoreCase))}"));

    private static NpgsqlCommand BuildCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        for (var i = 0; i < values.Length; i++)
            command.Parameters.AddWithValue($"p{i}", values[i] is DBNull ? DBNull.Value : values[i] ?? DBNull.Value);

        return command;
    }

    private static async Task<List<string>> LoadTableColumnsAsync(NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.attname
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relname = @table
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND a.attgenerated = ''
              AND a.attidentity = ''
            ORDER BY a.attnum;
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));

        return result;
    }

    private static async Task<List<string>> LoadConflictColumnsAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        List<string> writableColumns,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT string_agg(a.attname, ',' ORDER BY k.ordinality) AS column_names
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN unnest(i.indkey) WITH ORDINALITY AS k(attnum, ordinality) ON true
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
            WHERE n.nspname = @schema
              AND c.relname = @table
              AND i.indisunique
              AND k.attnum > 0
            GROUP BY i.indexrelid, i.indisprimary
            ORDER BY i.indisprimary, count(*) DESC;
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        var writableSet = writableColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var columns = reader.GetString(0)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (columns.All(writableSet.Contains))
                return columns;
        }

        return [];
    }

    private static async Task<DatabaseSnapshot> LoadSnapshotAsync(NpgsqlConnection connection, List<string> schemas, CancellationToken cancellationToken)
    {
        var snapshot = new DatabaseSnapshot();
        await LoadSchemasAsync(connection, schemas, snapshot, cancellationToken);
        await LoadSequencesAsync(connection, schemas, snapshot, cancellationToken);
        await LoadTablesAsync(connection, schemas, snapshot, cancellationToken);
        await LoadColumnsAsync(connection, schemas, snapshot, cancellationToken);
        await LoadConstraintsAsync(connection, schemas, snapshot, cancellationToken);
        await LoadIndexesAsync(connection, schemas, snapshot, cancellationToken);
        await LoadViewsAsync(connection, schemas, snapshot, cancellationToken);
        await LoadFunctionsAsync(connection, schemas, snapshot, cancellationToken);
        await LoadTriggersAsync(connection, schemas, snapshot, cancellationToken);
        return snapshot;
    }

    private static async Task LoadSchemasAsync(NpgsqlConnection connection, List<string> schemas, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT nspname FROM pg_namespace WHERE nspname = ANY(@schemas);", connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            snapshot.Schemas.Add(reader.GetString(0));
    }

    private static async Task LoadSequencesAsync(NpgsqlConnection connection, List<string> schemas, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT schemaname, sequencename, data_type::text, start_value, min_value, max_value, increment_by, cycle, cache_size
            FROM pg_sequences
            WHERE schemaname = ANY(@schemas);
            """, connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new SequenceInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetValue(3).ToString() ?? "1",
                reader.GetValue(4).ToString() ?? "1",
                reader.GetValue(5).ToString() ?? "1",
                reader.GetValue(6).ToString() ?? "1",
                reader.GetBoolean(7),
                reader.GetInt64(8));
            snapshot.Sequences[item.Key] = item;
        }
    }

    private static async Task LoadTablesAsync(NpgsqlConnection connection, List<string> schemas, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = ANY(@schemas)
              AND c.relkind IN ('r', 'p');
            """, connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new TableInfo(reader.GetString(0), reader.GetString(1));
            snapshot.Tables[item.Key] = item;
        }
    }

    private static async Task LoadColumnsAsync(NpgsqlConnection connection, List<string> schemas, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, a.attname, a.attnum, format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull AS is_nullable,
                   pg_get_expr(ad.adbin, ad.adrelid) AS default_sql,
                   a.attidentity::text,
                   a.attgenerated::text
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
            WHERE n.nspname = ANY(@schemas)
              AND c.relkind IN ('r', 'p')
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY n.nspname, c.relname, a.attnum;
            """, connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new ColumnInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt16(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8));
            snapshot.Columns[item.Key] = item;
        }
    }

    private static async Task LoadConstraintsAsync(NpgsqlConnection connection, List<string> schemas, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, con.conname, con.contype::text, pg_get_constraintdef(con.oid, true)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = ANY(@schemas)
              AND con.contype IN ('p', 'u', 'f', 'c', 'x');
            """, connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new ConstraintInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            snapshot.Constraints[item.Key] = item;
        }
    }

    private static async Task LoadIndexesAsync(NpgsqlConnection connection, List<string> schemas, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname, t.relname, i.relname, pg_get_indexdef(ix.indexrelid)
            FROM pg_index ix
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            LEFT JOIN pg_constraint con ON con.conindid = ix.indexrelid
            WHERE n.nspname = ANY(@schemas)
              AND con.oid IS NULL;
            """, connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new IndexInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            snapshot.Indexes[item.Key] = item;
        }
    }

    private static async Task LoadViewsAsync(NpgsqlConnection connection, List<string> schemas, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, c.relkind::text, pg_get_viewdef(c.oid, true)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = ANY(@schemas)
              AND c.relkind IN ('v', 'm');
            """, connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new ViewInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            snapshot.Views[item.Key] = item;
        }
    }

    private static async Task LoadFunctionsAsync(NpgsqlConnection connection, List<string> schemas, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname, p.proname, pg_get_function_identity_arguments(p.oid), pg_get_functiondef(p.oid)
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = ANY(@schemas);
            """, connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new FunctionInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            snapshot.Functions[item.Key] = item;
        }
    }

    private static async Task LoadTriggersAsync(NpgsqlConnection connection, List<string> schemas, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, t.tgname, pg_get_triggerdef(t.oid, true)
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = ANY(@schemas)
              AND NOT t.tgisinternal;
            """, connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new TriggerInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            snapshot.Triggers[item.Key] = item;
        }
    }

    private DatabaseConnectionOption GetConnection(string connectionName)
    {
        var target = _configuration.GetSection("ConnectionStrings")
            .GetChildren()
            .Select(section => new DatabaseConnectionOption(section.Key, section.Value ?? "", section.Key))
            .FirstOrDefault(item => string.Equals(item.Name, connectionName, StringComparison.OrdinalIgnoreCase));
        return target ?? throw new InvalidOperationException($"未找到连接字符串：{connectionName}");
    }

    private async Task<string> BackupDatabaseAsync(string connectionString, string? configuredPgDumpPath, CancellationToken cancellationToken)
    {
        var pgDumpPath = ResolveToolPath(configuredPgDumpPath, "pg_dump")
            ?? throw new InvalidOperationException("未找到 pg_dump.exe，无法备份。");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var (host, port) = ResolveHostAndPort(builder);
        var username = RequireValue(builder.Username, "连接字符串缺少 Username。");
        var database = RequireValue(builder.Database, "连接字符串缺少 Database。");
        var backupDirectory = Path.Combine(_environment.ContentRootPath, "DatabaseBackups");
        Directory.CreateDirectory(backupDirectory);
        var backupFile = Path.Combine(backupDirectory, $"{database}_{DateTime.Now:yyyyMMdd_HHmmss}.backup");

        await RunPostgresToolAsync(pgDumpPath, builder.Password, [
            "--host", host,
            "--port", port.ToString(),
            "--username", username,
            "--dbname", database,
            "--format", "c",
            "--file", backupFile,
            "--no-password"
        ], cancellationToken);

        return backupFile;
    }

    private async Task ExecuteSqlFileByPsqlAsync(string connectionString, string sqlFilePath, string? configuredToolPath, CancellationToken cancellationToken)
    {
        var psqlPath = ResolveToolPath(configuredToolPath, "psql")
            ?? throw new InvalidOperationException("未找到 psql.exe，无法导入高版本 SQL 文件。");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var (host, port) = ResolveHostAndPort(builder);
        var username = RequireValue(builder.Username, "连接字符串缺少 Username。");
        var database = RequireValue(builder.Database, "连接字符串缺少 Database。");

        await RunPostgresToolAsync(psqlPath, builder.Password, [
            "--host", host,
            "--port", port.ToString(),
            "--username", username,
            "--dbname", database,
            "--no-password",
            "--set", "ON_ERROR_STOP=on",
            "--file", sqlFilePath
        ], cancellationToken);
    }

    private static async Task RunPostgresToolAsync(string fileName, string? password, string[] args, CancellationToken cancellationToken)
    {
        var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.Environment["PGPASSWORD"] = password ?? "";
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} 执行失败：{error}{output}");
    }

    private async Task CreateTemporaryDatabaseAsync(string targetConnectionString, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(BuildServerConnectionString(targetConnectionString));
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, $"CREATE DATABASE {QuoteName(databaseName)};", cancellationToken);
    }

    private async Task DropTemporaryDatabaseAsync(string targetConnectionString, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(BuildServerConnectionString(targetConnectionString));
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, $"""
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = '{databaseName.Replace("'", "''")}'
              AND pid <> pg_backend_pid();
            DROP DATABASE IF EXISTS {QuoteName(databaseName)};
            """, cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string ResolveSqlFilePath(string? sqlFilePath)
    {
        if (string.IsNullOrWhiteSpace(sqlFilePath))
            throw new InvalidOperationException("请先选择高版本 SQL 文件。");

        var path = Path.IsPathRooted(sqlFilePath)
            ? sqlFilePath
            : Path.Combine(_environment.ContentRootPath, sqlFilePath);
        path = Path.GetFullPath(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("SQL 文件不存在。", path);
        if (!path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许使用 .sql 文件。");

        return path;
    }

    private static string BuildServerConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres"
        };
        return builder.ConnectionString;
    }

    private static string BuildDatabaseConnectionString(string connectionString, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName
        };
        return builder.ConnectionString;
    }

    private static string BuildTempDatabaseName() =>
        $"datasync_upgrade_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..48].ToLowerInvariant();

    private static string? ResolveToolPath(string? configuredPath, string toolName)
    {
        var executableName = OperatingSystem.IsWindows() ? $"{toolName}.exe" : toolName;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath)
                && string.Equals(Path.GetFileName(configuredPath), executableName, StringComparison.OrdinalIgnoreCase))
            {
                return configuredPath;
            }

            var directory = File.Exists(configuredPath) ? Path.GetDirectoryName(configuredPath) : configuredPath;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        var postgresRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        if (!Directory.Exists(postgresRoot))
            return null;

        return Directory.GetFiles(postgresRoot, executableName, SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static List<string> ParseSchemas(string? schemasText)
    {
        var schemas = (schemasText ?? "lhyy")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return schemas.Count == 0 ? ["lhyy"] : schemas;
    }

    private static void Add(
        List<DatabaseCompareDiffItem> result,
        string category,
        string objectName,
        string changeType,
        string sql,
        bool isDangerous = false,
        string? note = null)
    {
        result.Add(new DatabaseCompareDiffItem(category, objectName, changeType, sql, isDangerous, note));
    }

    private static string QuoteTable(string schema, string table) => $"{QuoteName(schema)}.{QuoteName(table)}";
    private static string QuoteName(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string RequireValue(string? value, string message) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;

    private static (string Host, int Port) ResolveHostAndPort(NpgsqlConnectionStringBuilder builder)
    {
        var host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;
        var port = builder.Port > 0 ? builder.Port : 5432;
        var parts = host.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[1], out var parsedPort) ? (parts[0], parsedPort) : (host, port);
    }

    private static string NormalizeSql(string? sql) =>
        string.Join(" ", (sql ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd(';');

    private static string EnsureSemicolon(string sql) => sql.TrimEnd().EndsWith(';') ? sql : sql.TrimEnd() + ";";

    private static string GetDataSyncModeName(string mode) =>
        mode == DataSyncModeClearImport ? "清空导入" : "更新插入";

    private static string CreateIndexSql(string definition)
    {
        var sql = EnsureSemicolon(definition);
        if (sql.StartsWith("CREATE UNIQUE INDEX ", StringComparison.OrdinalIgnoreCase))
            return "CREATE UNIQUE INDEX IF NOT EXISTS " + sql["CREATE UNIQUE INDEX ".Length..];
        if (sql.StartsWith("CREATE INDEX ", StringComparison.OrdinalIgnoreCase))
            return "CREATE INDEX IF NOT EXISTS " + sql["CREATE INDEX ".Length..];
        return sql;
    }

    private sealed class DatabaseSnapshot
    {
        public HashSet<string> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SequenceInfo> Sequences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TableInfo> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ColumnInfo> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ConstraintInfo> Constraints { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IndexInfo> Indexes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ViewInfo> Views { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FunctionInfo> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TriggerInfo> Triggers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record TableInfo(string Schema, string Name)
    {
        public string Key => $"{Schema}.{Name}";
        public string FullName => QuoteTable(Schema, Name);
    }

    private sealed record SequenceInfo(string Schema, string Name, string DataType, string StartValue, string MinValue, string MaxValue, string IncrementBy, bool Cycle, long CacheSize)
    {
        public string Key => $"{Schema}.{Name}";
        public string FullName => QuoteTable(Schema, Name);
        public string CreateSql => $"CREATE SEQUENCE IF NOT EXISTS {FullName} AS {DataType} START WITH {StartValue} INCREMENT BY {IncrementBy} MINVALUE {MinValue} MAXVALUE {MaxValue} CACHE {CacheSize}{(Cycle ? " CYCLE" : "")};";
    }

    private sealed record ColumnInfo(string Schema, string Table, string Name, short Ordinal, string TypeSql, bool IsNullable, string? DefaultSql, string IdentityMode, string GeneratedMode)
    {
        public string TableKey => $"{Schema}.{Table}";
        public string Key => $"{Schema}.{Table}.{Name}";
        public string TableFullName => QuoteTable(Schema, Table);
        public string FullName => $"{TableFullName}.{QuoteName(Name)}";
        public bool HasGeneratedValue => !string.IsNullOrWhiteSpace(IdentityMode) || !string.IsNullOrWhiteSpace(GeneratedMode);

        public string BuildDefinition()
        {
            var parts = new List<string> { QuoteName(Name), TypeSql };
            if (!string.IsNullOrWhiteSpace(IdentityMode))
                parts.Add(IdentityMode == "a" ? "GENERATED ALWAYS AS IDENTITY" : "GENERATED BY DEFAULT AS IDENTITY");
            else if (!string.IsNullOrWhiteSpace(GeneratedMode) && !string.IsNullOrWhiteSpace(DefaultSql))
                parts.Add($"GENERATED ALWAYS AS ({DefaultSql}) STORED");
            else if (!string.IsNullOrWhiteSpace(DefaultSql))
                parts.Add($"DEFAULT {DefaultSql}");
            if (!IsNullable)
                parts.Add("NOT NULL");
            return string.Join(" ", parts);
        }
    }

    private sealed record ConstraintInfo(string Schema, string Table, string Name, string Type, string Definition)
    {
        public string TableKey => $"{Schema}.{Table}";
        public string Key => Type == "p" ? $"{Schema}.{Table}.primary_key" : $"{Schema}.{Table}.{Name}";
        public string FullName => $"{QuoteTable(Schema, Table)}.{QuoteName(Name)}";
        public string AddSql => $"ALTER TABLE {QuoteTable(Schema, Table)} ADD CONSTRAINT {QuoteName(Name)} {Definition};";
        public string DropSql => $"ALTER TABLE {QuoteTable(Schema, Table)} DROP CONSTRAINT IF EXISTS {QuoteName(Name)} CASCADE;";
    }

    private sealed record IndexInfo(string Schema, string Table, string Name, string Definition)
    {
        public string TableKey => $"{Schema}.{Table}";
        public string Key => $"{Schema}.{Table}.{Name}";
        public string FullName => $"{QuoteTable(Schema, Table)}.{QuoteName(Name)}";
        public string CreateSql => CreateIndexSql(Definition);
        public string DropSql => $"DROP INDEX IF EXISTS {QuoteTable(Schema, Name)} CASCADE;";
    }

    private sealed record ViewInfo(string Schema, string Name, string RelKind, string Definition)
    {
        public string Key => $"{Schema}.{Name}";
        public string FullName => QuoteTable(Schema, Name);
        public string CreateSql => RelKind == "m"
            ? $"CREATE MATERIALIZED VIEW IF NOT EXISTS {FullName} AS {Definition};"
            : $"CREATE OR REPLACE VIEW {FullName} AS {Definition};";
        public string DropSql => RelKind == "m"
            ? $"DROP MATERIALIZED VIEW IF EXISTS {FullName} CASCADE;"
            : $"DROP VIEW IF EXISTS {FullName} CASCADE;";
    }

    private sealed record FunctionInfo(string Schema, string Name, string IdentityArgs, string Definition)
    {
        public string Key => $"{Schema}.{Name}({IdentityArgs})";
        public string FullName => $"{QuoteName(Schema)}.{QuoteName(Name)}({IdentityArgs})";
        public string DropSql => $"DROP FUNCTION IF EXISTS {FullName} CASCADE;";
    }

    private sealed record TriggerInfo(string Schema, string Table, string Name, string Definition)
    {
        public string Key => $"{Schema}.{Table}.{Name}";
        public string FullName => $"{QuoteTable(Schema, Table)}.{QuoteName(Name)}";
        public string CreateSql => EnsureSemicolon(Definition);
        public string DropSql => $"DROP TRIGGER IF EXISTS {QuoteName(Name)} ON {QuoteTable(Schema, Table)} CASCADE;";
    }
}

public sealed record DatabaseCompareRequest(
    string TargetConnectionName,
    string SourceMode,
    string? SourceConnectionName,
    string? SourceSqlFilePath,
    string SchemasText,
    string? ToolPath,
    bool IncludeDrop);

public sealed record DatabaseCompareResult(
    List<string> Schemas,
    List<DatabaseCompareDiffItem> Differences,
    List<DatabaseCompareTableOption> Tables);

public sealed record DatabaseCompareDiffItem(
    string Category,
    string ObjectName,
    string ChangeType,
    string Sql,
    bool IsDangerous,
    string? Note);

public sealed record DatabaseCompareTableOption(string Schema, string Table, string FullName);

public sealed record DatabaseTableDataSyncRequest(string Schema, string Table, string Mode);

public sealed record DatabaseCompareExecuteResult(string BackupFile, int AppliedSqlCount, List<string> SyncedTables)
{
    public List<string> SummaryItems =>
    [
        $"执行结构 SQL：{AppliedSqlCount} 条",
        ..SyncedTables.Select(table => $"同步数据：{table}")
    ];
}
