using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataSync.LHYY.V2.Services.FollowUp;

public static class FollowUpSchemaDecisionProcessor
{
    public static FollowUpTableSchema MapSchema(FollowUpTableSchema source, FollowUpSchemaDecision? decision)
    {
        var mapping = FindMapping(source.SchemaName, source.TableName, decision);
        return new FollowUpTableSchema
        {
            SchemaName = mapping?.TargetSchema ?? source.SchemaName,
            TableName = mapping?.TargetTable ?? source.TableName,
            SchemaHash = source.SchemaHash,
            Columns = source.Columns.Select(column => new FollowUpColumnSchema
            {
                Name = MapColumn(column.Name, mapping),
                DataType = column.DataType,
                IsNullable = column.IsNullable,
                DefaultValue = column.DefaultValue,
                OrdinalPosition = column.OrdinalPosition
            }).ToList(),
            PrimaryKey = source.PrimaryKey.Select(column => MapColumn(column, mapping)).ToList(),
            UniqueConstraints = source.UniqueConstraints.ToList(),
            Indexes = source.Indexes.ToList()
        };
    }

    public static FollowUpTableManifestItem MapManifest(FollowUpTableManifestItem source, FollowUpSchemaDecision? decision)
    {
        var mapping = FindMapping(source.Schema, source.TableName, decision);
        return new FollowUpTableManifestItem
        {
            Schema = mapping?.TargetSchema ?? source.Schema,
            TableName = mapping?.TargetTable ?? source.TableName,
            Enabled = source.Enabled,
            Required = source.Required,
            DataCategory = source.DataCategory,
            ImportPolicy = source.ImportPolicy,
            Dependencies = source.Dependencies,
            Increment = source.Increment,
            PrimaryKey = source.PrimaryKey.Select(column => MapColumn(column, mapping)).ToList(),
            WatermarkColumn = source.WatermarkColumn is null ? null : MapColumn(source.WatermarkColumn, mapping),
            HasIncrementalData = source.HasIncrementalData,
            ExportPath = source.ExportPath,
            SchemaHash = source.SchemaHash,
            RecordCount = source.RecordCount,
            FileHash = source.FileHash,
            ContentHash = source.ContentHash,
            Skipped = source.Skipped,
            SkipReason = source.SkipReason
        };
    }

    public static string MapRow(
        string json,
        string sourceSchema,
        string sourceTable,
        FollowUpSchemaDecision? decision,
        IReadOnlySet<string>? allowedSourceColumns = null)
    {
        var mapping = FindMapping(sourceSchema, sourceTable, decision);
        if (mapping is null && allowedSourceColumns is null) return json;
        var source = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("NDJSON 行不是 JSON 对象。");
        var target = new JsonObject();
        foreach (var property in source)
        {
            if (allowedSourceColumns is not null && !allowedSourceColumns.Contains(property.Key))
                continue;
            var targetColumn = MapColumn(property.Key, mapping);
            if (target.ContainsKey(targetColumn))
                throw new InvalidDataException($"字段映射后出现重复目标字段：{targetColumn}。");
            target[targetColumn] = property.Value?.DeepClone();
        }
        foreach (var defaultValue in mapping?.DefaultValues ?? [])
            if (!target.ContainsKey(defaultValue.Key))
                target[defaultValue.Key] = JsonNode.Parse(defaultValue.Value.GetRawText());
        return target.ToJsonString(FollowUpJson.Options);
    }

    internal static string MapColumn(
        string schema,
        string table,
        string column,
        FollowUpSchemaDecision? decision) =>
        MapColumn(column, FindMapping(schema, table, decision));

    public static IReadOnlyDictionary<string, HashSet<string>> GetDefaultColumns(
        FollowUpSchemaDecision? decision)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (decision is null) return result;
        foreach (var entry in decision.TableMappings)
        {
            var sourceParts = entry.Key.Split('.', 2);
            if (sourceParts.Length != 2) continue;
            var targetName = $"{entry.Value.TargetSchema ?? sourceParts[0]}.{entry.Value.TargetTable ?? sourceParts[1]}";
            result[targetName] = entry.Value.DefaultValues.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    public static FollowUpTableMapping? FindMapping(
        string schema,
        string table,
        FollowUpSchemaDecision? decision)
    {
        if (decision?.DecisionStatus != "ApprovedMapping") return null;
        var key = $"{schema}.{table}";
        return decision.TableMappings.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string MapColumn(string source, FollowUpTableMapping? mapping) =>
        mapping?.ColumnMappings.FirstOrDefault(item =>
            string.Equals(item.Key, source, StringComparison.OrdinalIgnoreCase)).Value ?? source;
}
