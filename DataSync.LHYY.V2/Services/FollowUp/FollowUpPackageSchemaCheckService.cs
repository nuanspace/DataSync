using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Tools;
using Npgsql;
using System.Text.RegularExpressions;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed partial class FollowUpPackageSchemaCheckService(IConfiguration configuration)
{
    private readonly string _cubeConnectionString = configuration.GetConnectionString("CubeDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");

    public async Task<FollowUpSchemaCheckResult> CheckAsync(
        FollowUpSchemaSnapshot snapshot,
        IReadOnlyCollection<FollowUpTableManifestItem> manifest,
        FollowUpSchemaDecision? decision = null,
        CancellationToken cancellationToken = default)
    {
        var mappedTables = snapshot.Tables.Select(item => FollowUpSchemaDecisionProcessor.MapSchema(item, decision)).ToList();
        var mappedManifest = manifest.Select(item => FollowUpSchemaDecisionProcessor.MapManifest(item, decision)).ToList();
        var target = await LoadTargetSchemasAsync(mappedTables, cancellationToken);
        var result = Evaluate(mappedTables, target, mappedManifest, FollowUpSchemaDecisionProcessor.GetDefaultColumns(decision));
        if (!result.Compatible || !DeploymentModePolicy.IsExternalCube(configuration))
            return result;

        var packageTables = mappedManifest
            .Where(item => item.Enabled && !item.Skipped)
            .Select(item => new ExternalCubePackageTable(item.Schema, item.TableName, item.ImportPolicy))
            .ToArray();
        var accessIssues = await ExternalCubeCompatibilityTool.CheckPackageAccessAsync(
            _cubeConnectionString,
            packageTables,
            cancellationToken);
        return accessIssues.Count == 0
            ? result
            : new FollowUpSchemaCheckResult(
                "ReviewRequired",
                "RequiresMapping",
                false,
                result.Messages.Concat(accessIssues).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public static FollowUpSchemaCheckResult Evaluate(
        IReadOnlyCollection<FollowUpTableSchema> sourceTables,
        IReadOnlyCollection<FollowUpTableSchema> targetTables,
        IReadOnlyCollection<FollowUpTableManifestItem> manifest,
        IReadOnlyDictionary<string, HashSet<string>>? defaultColumns = null)
    {
        var enabled = manifest.Where(item => item.Enabled && !item.Skipped)
            .Select(item => $"{item.Schema}.{item.TableName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = enabled.Count == 0
            ? sourceTables
            : sourceTables.Where(item => enabled.Contains($"{item.SchemaName}.{item.TableName}")).ToList();
        var targets = targetTables.ToDictionary(item => $"{item.SchemaName}.{item.TableName}", StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        var breaking = false;
        var requiresMapping = false;

        foreach (var source in sources)
        {
            var fullName = $"{source.SchemaName}.{source.TableName}";
            if (!targets.TryGetValue(fullName, out var target))
            {
                requiresMapping = true;
                messages.Add($"目标表不存在：{fullName}");
                continue;
            }
            if (!source.PrimaryKey.SequenceEqual(target.PrimaryKey, StringComparer.OrdinalIgnoreCase))
            {
                breaking = true;
                messages.Add($"主键不一致：{fullName}");
            }
            var sourceColumns = source.Columns.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            var targetColumns = target.Columns.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var sourceColumn in source.Columns)
            {
                if (!targetColumns.TryGetValue(sourceColumn.Name, out var targetColumn))
                {
                    requiresMapping = true;
                    messages.Add($"目标字段不存在：{fullName}.{sourceColumn.Name}");
                    continue;
                }
                if (!IsCompatibleType(sourceColumn.DataType, targetColumn.DataType))
                {
                    breaking = true;
                    messages.Add($"字段类型不兼容：{fullName}.{sourceColumn.Name}（{sourceColumn.DataType} → {targetColumn.DataType}）");
                }
            }
            foreach (var targetColumn in target.Columns.Where(item => !item.IsNullable && string.IsNullOrWhiteSpace(item.DefaultValue)))
            {
                var suppliedByDecision = defaultColumns?.TryGetValue(fullName, out var defaults) == true
                    && defaults.Contains(targetColumn.Name);
                if (!sourceColumns.ContainsKey(targetColumn.Name) && !suppliedByDecision)
                {
                    requiresMapping = true;
                    messages.Add($"目标必填字段没有来源或默认值：{fullName}.{targetColumn.Name}");
                }
            }
        }

        var level = breaking ? "Breaking" : requiresMapping ? "RequiresMapping" : "Compatible";
        return new FollowUpSchemaCheckResult(level == "Compatible" ? "Passed" : "ReviewRequired", level, level == "Compatible", messages);
    }

    private async Task<List<FollowUpTableSchema>> LoadTargetSchemasAsync(
        IReadOnlyCollection<FollowUpTableSchema> sourceTables,
        CancellationToken cancellationToken)
    {
        var result = new List<FollowUpTableSchema>();
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var source in sourceTables)
        {
            EnsureIdentifier(source.SchemaName);
            EnsureIdentifier(source.TableName);
            var target = new FollowUpTableSchema { SchemaName = source.SchemaName, TableName = source.TableName };
            await using (var command = new NpgsqlCommand("""
                SELECT column_name, data_type, is_nullable, column_default, ordinal_position
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = @table
                ORDER BY ordinal_position
                """, connection))
            {
                command.Parameters.AddWithValue("schema", source.SchemaName);
                command.Parameters.AddWithValue("table", source.TableName);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    target.Columns.Add(new FollowUpColumnSchema
                    {
                        Name = reader.GetString(0), DataType = reader.GetString(1),
                        IsNullable = reader.GetString(2) == "YES",
                        DefaultValue = reader.IsDBNull(3) ? null : reader.GetString(3),
                        OrdinalPosition = reader.GetInt32(4)
                    });
                }
            }
            if (target.Columns.Count == 0) continue;
            await using (var command = new NpgsqlCommand("""
                SELECT a.attname
                FROM pg_index i
                JOIN pg_class c ON c.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN unnest(i.indkey) WITH ORDINALITY AS key(attnum, ord) ON true
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = key.attnum
                WHERE i.indisprimary AND n.nspname = @schema AND c.relname = @table
                ORDER BY key.ord
                """, connection))
            {
                command.Parameters.AddWithValue("schema", source.SchemaName);
                command.Parameters.AddWithValue("table", source.TableName);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) target.PrimaryKey.Add(reader.GetString(0));
            }
            result.Add(target);
        }
        return result;
    }

    private static bool IsCompatibleType(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return true;
        var pair = $"{source.ToLowerInvariant()}->{target.ToLowerInvariant()}";
        return pair is "character varying->text" or "smallint->integer" or "smallint->bigint" or "integer->bigint" or "real->double precision";
    }

    internal static void EnsureIdentifier(string value)
    {
        if (!IdentifierRegex().IsMatch(value)) throw new InvalidOperationException($"非法数据库标识符：{value}");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}

public static class FollowUpUpsertSqlBuilder
{
    public static string Build(
        string schema,
        string table,
        IReadOnlyCollection<string> columns,
        IReadOnlyCollection<string> primaryKey,
        string importPolicy)
    {
        FollowUpPackageSchemaCheckService.EnsureIdentifier(schema);
        FollowUpPackageSchemaCheckService.EnsureIdentifier(table);
        foreach (var column in columns) FollowUpPackageSchemaCheckService.EnsureIdentifier(column);
        foreach (var column in primaryKey) FollowUpPackageSchemaCheckService.EnsureIdentifier(column);
        if (columns.Count == 0 || primaryKey.Count == 0) throw new InvalidOperationException("通用导入要求目标表存在字段和主键。");

        var quotedTable = $"{Quote(schema)}.{Quote(table)}";
        var columnList = string.Join(", ", columns.Select(Quote));
        var conflictColumns = string.Join(", ", primaryKey.Select(Quote));
        var updateColumns = columns.Where(column => !primaryKey.Contains(column, StringComparer.OrdinalIgnoreCase)).ToList();
        var conflictAction = importPolicy switch
        {
            "InsertIfMissing" => "DO NOTHING",
            "Upsert" when updateColumns.Count > 0 => "DO UPDATE SET " + string.Join(", ", updateColumns.Select(column => $"{Quote(column)} = EXCLUDED.{Quote(column)}")),
            "Upsert" => "DO NOTHING",
            _ => throw new InvalidOperationException($"导入策略 {importPolicy} 不执行写入语句。")
        };
        return $"""
            INSERT INTO {quotedTable} ({columnList})
            SELECT {columnList}
            FROM jsonb_populate_record(NULL::{quotedTable}, @row::jsonb)
            ON CONFLICT ({conflictColumns}) {conflictAction}
            """;
    }

    private static string Quote(string value) => $"\"{value}\"";
}
