using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Tools;
using Npgsql;
using NpgsqlTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed partial class FollowUpPackageSchemaCheckService(IConfiguration configuration)
{
    internal const string EmptyFileSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    internal const string TargetQuestionScopeSnapshotSql = """
        SELECT projected.row_json,
               question.hospital_id,
               project.hospital_id
        FROM form.form_question question
        LEFT JOIN form.form_project project ON project.id = question.project_id
        CROSS JOIN LATERAL (
            SELECT COALESCE(jsonb_object_agg(property.key, property.value), '{}'::jsonb)::text AS row_json
            FROM jsonb_each(to_jsonb(question)) property
            WHERE property.key = ANY(@sourceColumns)
        ) projected
        WHERE (project.hospital_id = @hospitalId OR question.hospital_id = @hospitalId)
        ORDER BY projected.row_json
        """;
    internal const string TargetQuestionScopeLockSql = """
        SET LOCAL lock_timeout = '30s';
        LOCK TABLE form.form_project, form.form_question IN SHARE MODE;
        SET LOCAL lock_timeout = '0'
        """;
    internal const string PackageQuestionProjectScopeSql = """
        SELECT id, hospital_id
        FROM form.form_project
        WHERE id = ANY(@projectIds)
        """;
    internal const string PackageQuestionHospitalScopeSql = """
        SELECT question.id,
               question.hospital_id,
               project.hospital_id
        FROM form.form_question question
        LEFT JOIN form.form_project project ON project.id = question.project_id
        WHERE question.id = ANY(@questionIds)
        """;
    internal const string PackageQuestionProjectScopeLockSql = """
        SET LOCAL lock_timeout = '30s';
        LOCK TABLE form.form_project, form.form_question IN SHARE MODE;
        SET LOCAL lock_timeout = '0'
        """;

    // 与 NTCare TargetSchemaService 的动态表固定字段保持一致；其余宽表字段必须由医院表单项授权。
    internal static IReadOnlyList<string> DynamicFixedColumns { get; } =
    [
        "id", "parent_table_id", "parent_table_name", "patient_id", "patient_event_id",
        "card_id", "card_sub_id", "parent_card_sub_id", "linked_card_sub_id", "card_name",
        "form_name", "form_set_name", "project_name", "form_id", "form_set_id", "project_id",
        "ward_name", "ward_id", "department_name", "department_id", "region_name", "region_id",
        "hospital_name", "hospital_id", "created_at", "created_by", "created_by_name", "updated_at",
        "updated_by", "updated_by_name", "is_valid"
    ];

    private readonly string _cubeConnectionString = configuration.GetConnectionString("CubeDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");

    public async Task<FollowUpSchemaCheckResult> CheckAsync(
        FollowUpVerifiedPackage package,
        string? importedFormQuestionContentHash,
        FollowUpSchemaDecision? decision = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProtectedQuestionProjectImportContracts(
            package.SchemaSnapshot.Tables,
            package.TableManifest,
            decision);
        var packageProjectScope = await ReadPackageProjectScopeAsync(package, cancellationToken);
        await EnsurePackageProjectScopeAsync(
            package.Manifest.HospitalId,
            packageProjectScope,
            cancellationToken);
        var scopeResolution = await BuildDynamicColumnScopesAsync(
            package,
            importedFormQuestionContentHash,
            decision,
            cancellationToken);
        var mappedTables = SelectAndMapSourceTables(
            package.SchemaSnapshot.Tables,
            package.TableManifest,
            decision,
            scopeResolution.Scopes);
        var mappedManifest = package.TableManifest
            .Select(item => FollowUpSchemaDecisionProcessor.MapManifest(item, decision))
            .ToList();
        var target = await LoadTargetSchemasAsync(SelectSourceTables(mappedTables, mappedManifest), cancellationToken);
        var defaultColumns = FollowUpSchemaDecisionProcessor.GetDefaultColumns(decision);
        var result = Evaluate(
            mappedTables,
            target,
            mappedManifest,
            defaultColumns,
            scopeResolution.Scopes);
        result = MergeScopeResult(result, scopeResolution);
        var writableIssues = await CheckDynamicWritableColumnsAsync(
            scopeResolution.Scopes,
            mappedManifest,
            defaultColumns,
            cancellationToken);
        writableIssues.AddRange(await CheckProtectedQuestionProjectWritableColumnsAsync(
            mappedManifest,
            cancellationToken));
        result = MergeRequiresMapping(result, writableIssues);
        if (!result.Compatible || !DeploymentModePolicy.IsExternalCube(configuration))
            return result;

        var packageTables = mappedManifest
            .Where(HasImportPayload)
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
                result.Messages.Concat(accessIssues).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                result.TableColumnScopes,
                result.IgnoredNonNullColumns);
    }

    public static FollowUpSchemaCheckResult Evaluate(
        IReadOnlyCollection<FollowUpTableSchema> sourceTables,
        IReadOnlyCollection<FollowUpTableSchema> targetTables,
        IReadOnlyCollection<FollowUpTableManifestItem> manifest,
        IReadOnlyDictionary<string, HashSet<string>>? defaultColumns = null,
        IReadOnlyCollection<FollowUpTableColumnScope>? columnScopes = null)
    {
        var sources = SelectSourceTables(sourceTables, manifest);
        var duplicateMessages = sources
            .GroupBy(item => $"{item.SchemaName}.{item.TableName}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"源结构快照存在重复目标表：{group.Key}")
            .Concat(targetTables
                .GroupBy(item => $"{item.SchemaName}.{item.TableName}", StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => $"目标结构快照存在重复表：{group.Key}"))
            .Concat(sources.Concat(targetTables)
                .SelectMany(table => table.Columns
                    .GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => $"结构快照存在重复字段：{table.SchemaName}.{table.TableName}.{group.Key}")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicateMessages.Count > 0)
            return new FollowUpSchemaCheckResult(
                "ReviewRequired",
                "Breaking",
                false,
                duplicateMessages,
                columnScopes?.ToList() ?? [],
                []);

        var targets = targetTables
            .GroupBy(item => $"{item.SchemaName}.{item.TableName}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var scopes = (columnScopes ?? [])
            .GroupBy(item => $"{item.TargetSchema}.{item.TargetTable}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.First(), StringComparer.OrdinalIgnoreCase);
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
            scopes.TryGetValue(fullName, out var columnScope);
            var identifierComparer = columnScope is null ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (!source.PrimaryKey.SequenceEqual(target.PrimaryKey, identifierComparer))
            {
                breaking = true;
                messages.Add($"主键不一致：{fullName}");
            }
            var sourceColumns = source.Columns.ToDictionary(item => item.Name, identifierComparer);
            var targetColumns = target.Columns.ToDictionary(item => item.Name, identifierComparer);
            foreach (var sourceColumn in source.Columns)
            {
                if (!targetColumns.TryGetValue(sourceColumn.Name, out var targetColumn))
                {
                    requiresMapping = true;
                    messages.Add($"目标字段不存在：{fullName}.{sourceColumn.Name}");
                    continue;
                }
                var allowArrayToText = columnScope?.ArrayToTextTargetColumns.Contains(
                                            sourceColumn.Name,
                                            identifierComparer) == true;
                if (!IsCompatibleType(sourceColumn.DataType, targetColumn.DataType, allowArrayToText))
                {
                    breaking = true;
                    messages.Add($"字段类型不兼容：{fullName}.{sourceColumn.Name}（{sourceColumn.DataType} → {targetColumn.DataType}）");
                }
            }
            if (defaultColumns?.TryGetValue(fullName, out var configuredDefaults) == true)
            {
                foreach (var defaultColumn in configuredDefaults.Where(item => !targetColumns.ContainsKey(item)))
                {
                    requiresMapping = true;
                    messages.Add($"默认值目标字段不存在：{fullName}.{defaultColumn}");
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
        return new FollowUpSchemaCheckResult(
            level == "Compatible" ? "Passed" : "ReviewRequired",
            level,
            level == "Compatible",
            messages,
            columnScopes?.ToList() ?? [],
            []);
    }

    internal static IReadOnlyCollection<FollowUpTableSchema> SelectAndMapSourceTables(
        IReadOnlyCollection<FollowUpTableSchema> sourceTables,
        IReadOnlyCollection<FollowUpTableManifestItem> manifest,
        FollowUpSchemaDecision? decision,
        IReadOnlyCollection<FollowUpTableColumnScope>? columnScopes = null)
    {
        var selectedSources = SelectSourceTables(sourceTables, manifest);
        var duplicateSource = selectedSources
            .GroupBy(item => $"{item.SchemaName}.{item.TableName}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSource is not null)
            throw SchemaReview($"源结构快照存在重复目标表：{duplicateSource.Key}。");

        return selectedSources
            .Select(item =>
            {
                var sourceManifest = manifest.First(manifestItem =>
                    manifestItem.Schema.Equals(item.SchemaName, StringComparison.OrdinalIgnoreCase)
                    && manifestItem.TableName.Equals(item.TableName, StringComparison.OrdinalIgnoreCase));
                var mappedManifest = FollowUpSchemaDecisionProcessor.MapManifest(sourceManifest, decision);
                ValidateProtectedQuestionProjectMapping(sourceManifest, mappedManifest);
                ValidateProtectedQuestionProjectColumnMappings(sourceManifest, decision);
                if (!IsMappedDynamicFormTable(sourceManifest, mappedManifest))
                    return FollowUpSchemaDecisionProcessor.MapSchema(item, decision);
                var scope = FindSourceScope(item.SchemaName, item.TableName, columnScopes)
                    ?? throw new InvalidDataException($"动态表 {item.SchemaName}.{item.TableName} 缺少导入字段范围。");
                return MapAndApplySourceTable(item, decision, scope);
            })
            .ToList();
    }

    internal static FollowUpTableSchema MapAndApplySourceTable(
        FollowUpTableSchema source,
        FollowUpSchemaDecision? decision,
        FollowUpTableColumnScope scope)
    {
        if (!scope.SourceSchema.Equals(source.SchemaName, StringComparison.Ordinal)
            || !scope.SourceTable.Equals(source.TableName, StringComparison.Ordinal))
            throw new InvalidDataException($"字段范围与源表不匹配：{source.SchemaName}.{source.TableName}。");

        var allowed = scope.SourceColumns.ToHashSet(StringComparer.Ordinal);
        var filtered = new FollowUpTableSchema
        {
            SchemaName = source.SchemaName,
            TableName = source.TableName,
            SchemaHash = source.SchemaHash,
            Columns = source.Columns.Where(item => allowed.Contains(item.Name)).ToList(),
            PrimaryKey = source.PrimaryKey.ToList(),
            UniqueConstraints = source.UniqueConstraints.ToList(),
            Indexes = source.Indexes.ToList()
        };
        var mapped = FollowUpSchemaDecisionProcessor.MapSchema(filtered, decision);
        if (!mapped.SchemaName.Equals(scope.TargetSchema, StringComparison.Ordinal)
            || !mapped.TableName.Equals(scope.TargetTable, StringComparison.Ordinal)
            || !mapped.Columns.Select(item => item.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(scope.TargetColumns))
            throw new InvalidDataException($"动态表 {source.SchemaName}.{source.TableName} 的字段范围在校验后发生变化。");
        return mapped;
    }

    private static IReadOnlyCollection<FollowUpTableSchema> SelectSourceTables(
        IReadOnlyCollection<FollowUpTableSchema> sourceTables,
        IReadOnlyCollection<FollowUpTableManifestItem> manifest)
    {
        if (manifest.Count == 0) return sourceTables;

        var importTables = manifest.Where(HasImportPayload)
            .Select(item => $"{item.Schema}.{item.TableName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sourceTables
            .Where(item => importTables.Contains($"{item.SchemaName}.{item.TableName}"))
            .ToList();
    }

    // ImportDataAsync 不会访问没有导出文件的空表，结构和权限预检必须使用同一写入集合。
    private static bool HasImportPayload(FollowUpTableManifestItem item) =>
        item.Enabled && !item.Skipped && !string.IsNullOrWhiteSpace(item.ExportPath);

    internal static bool IsDynamicFormTable(FollowUpTableManifestItem item) =>
        item.Schema.Equals("target", StringComparison.Ordinal)
        && item.DataCategory.Equals("DynamicFormData", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMappedDynamicFormTable(
        FollowUpTableManifestItem source,
        FollowUpTableManifestItem mapped)
    {
        var sourceIsTarget = source.Schema.Equals("target", StringComparison.Ordinal);
        var categoryIsDynamic = source.DataCategory.Equals("DynamicFormData", StringComparison.OrdinalIgnoreCase);
        var mappedIsTarget = mapped.Schema.Equals("target", StringComparison.Ordinal);
        if (sourceIsTarget != categoryIsDynamic)
            throw SchemaReview($"表 {source.Schema}.{source.TableName} 的动态表分类与 target 模式不一致。");
        if (sourceIsTarget != mappedIsTarget)
            throw SchemaReview(
                $"表 {source.Schema}.{source.TableName} 不允许通过映射跨越 target 动态表边界。");
        return mappedIsTarget;
    }

    internal static void ValidateProtectedQuestionProjectMapping(
        FollowUpTableManifestItem source,
        FollowUpTableManifestItem mapped)
    {
        var sourceTable = ProtectedQuestionProjectTable(source.Schema, source.TableName);
        var targetTable = ProtectedQuestionProjectTable(mapped.Schema, mapped.TableName);
        if (sourceTable is null && targetTable is null)
            return;
        if (sourceTable is null
            || targetTable is null
            || !sourceTable.Equals(targetTable, StringComparison.Ordinal)
            || !source.Schema.Equals("form", StringComparison.Ordinal)
            || !source.TableName.Equals(sourceTable, StringComparison.Ordinal)
            || !mapped.Schema.Equals("form", StringComparison.Ordinal)
            || !mapped.TableName.Equals(targetTable, StringComparison.Ordinal))
            throw SchemaReview(
                $"安全表 {source.Schema}.{source.TableName} 不允许映射为 {mapped.Schema}.{mapped.TableName}，也不允许其他表映射进入 form.form_project/form.form_question。");
    }

    internal static void ValidateProtectedQuestionProjectColumnMappings(
        FollowUpTableManifestItem source,
        FollowUpSchemaDecision? decision)
    {
        var protectedTable = ProtectedQuestionProjectTable(source.Schema, source.TableName);
        if (protectedTable is null)
            return;
        var mapping = FollowUpSchemaDecisionProcessor.FindMapping(source.Schema, source.TableName, decision);
        if (mapping is null)
            return;
        var protectedColumns = protectedTable.Equals("form_project", StringComparison.OrdinalIgnoreCase)
            ? new HashSet<string>(["id", "hospital_id"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                ["id", "hospital_id", "project_id", "table_name", "column_name", "data_type"],
                StringComparer.OrdinalIgnoreCase);
        var invalid = mapping.ColumnMappings.FirstOrDefault(item =>
        {
            var canonical = protectedColumns.FirstOrDefault(column =>
                column.Equals(item.Key, StringComparison.OrdinalIgnoreCase)
                || column.Equals(item.Value, StringComparison.OrdinalIgnoreCase));
            return canonical is not null
                   && (!item.Key.Equals(canonical, StringComparison.Ordinal)
                       || !item.Value.Equals(canonical, StringComparison.Ordinal));
        });
        if (invalid.Key is not null)
            throw SchemaReview(
                $"安全表 form.{protectedTable} 的归属字段不允许改名映射：{invalid.Key} -> {invalid.Value}。");
    }

    internal static IReadOnlyList<string> GetProtectedQuestionProjectRequiredColumns(
        string schema,
        string table)
    {
        var protectedTable = ProtectedQuestionProjectTable(schema, table);
        if (protectedTable is null)
            return [];
        return protectedTable.Equals("form_project", StringComparison.Ordinal)
            ? ["id", "hospital_id"]
            : ["id", "hospital_id", "project_id", "table_name", "column_name", "data_type"];
    }

    internal static void ValidateProtectedQuestionProjectImportContract(
        FollowUpTableSchema sourceSchema,
        FollowUpTableManifestItem sourceManifest,
        FollowUpTableSchema mappedSchema,
        FollowUpTableManifestItem mappedManifest)
    {
        var protectedTable = ProtectedQuestionProjectTable(sourceManifest.Schema, sourceManifest.TableName);
        if (protectedTable is null)
            return;

        ValidateProtectedQuestionProjectMapping(sourceManifest, mappedManifest);
        var requiredColumns = GetProtectedQuestionProjectRequiredColumns("form", protectedTable);
        var sourceIdentityIsCanonical = sourceSchema.SchemaName.Equals("form", StringComparison.Ordinal)
                                        && sourceSchema.TableName.Equals(protectedTable, StringComparison.Ordinal);
        var mappedIdentityIsCanonical = mappedSchema.SchemaName.Equals("form", StringComparison.Ordinal)
                                        && mappedSchema.TableName.Equals(protectedTable, StringComparison.Ordinal);
        if (!sourceIdentityIsCanonical || !mappedIdentityIsCanonical)
            throw SchemaReview($"安全表 form.{protectedTable} 的源结构或映射后结构名称不规范。");

        var sourceColumns = sourceSchema.Columns.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var mappedColumns = mappedSchema.Columns.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var missingColumns = requiredColumns
            .Where(column => !sourceColumns.Contains(column) || !mappedColumns.Contains(column))
            .ToList();
        if (missingColumns.Count > 0)
            throw SchemaReview(
                $"安全表 form.{protectedTable} 缺少规范安全字段：{string.Join("、", missingColumns)}。");

        if (!sourceSchema.PrimaryKey.SequenceEqual(["id"], StringComparer.Ordinal)
            || !sourceManifest.PrimaryKey.SequenceEqual(["id"], StringComparer.Ordinal)
            || !mappedSchema.PrimaryKey.SequenceEqual(["id"], StringComparer.Ordinal)
            || !mappedManifest.PrimaryKey.SequenceEqual(["id"], StringComparer.Ordinal))
            throw SchemaReview($"安全表 form.{protectedTable} 的源结构、表清单和映射后主键必须精确为单列 id。");

        if (protectedTable.Equals("form_question", StringComparison.Ordinal)
            && !sourceManifest.ImportPolicy.Equals("Upsert", StringComparison.Ordinal))
            throw SchemaReview("Package form.form_question 必须使用精确的 Upsert 导入策略，确保授权快照实际写入目标表。");
    }

    private static void ValidateProtectedQuestionProjectImportContracts(
        IReadOnlyCollection<FollowUpTableSchema> sourceSchemas,
        IReadOnlyCollection<FollowUpTableManifestItem> manifest,
        FollowUpSchemaDecision? decision)
    {
        foreach (var sourceManifest in manifest.Where(item =>
                     HasImportPayload(item)
                     && ProtectedQuestionProjectTable(item.Schema, item.TableName) is not null))
        {
            var matches = sourceSchemas.Where(item =>
                item.SchemaName.Equals(sourceManifest.Schema, StringComparison.OrdinalIgnoreCase)
                && item.TableName.Equals(sourceManifest.TableName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw SchemaReview(
                    $"安全表 {sourceManifest.Schema}.{sourceManifest.TableName} 的源结构快照缺失或重复。");
            var sourceSchema = matches[0];
            var mappedManifest = FollowUpSchemaDecisionProcessor.MapManifest(sourceManifest, decision);
            ValidateProtectedQuestionProjectColumnMappings(sourceManifest, decision);
            ValidateProtectedQuestionProjectImportContract(
                sourceSchema,
                sourceManifest,
                FollowUpSchemaDecisionProcessor.MapSchema(sourceSchema, decision),
                mappedManifest);
        }
    }

    private static string? ProtectedQuestionProjectTable(string schema, string table)
    {
        if (!schema.Equals("form", StringComparison.OrdinalIgnoreCase))
            return null;
        if (table.Equals("form_project", StringComparison.OrdinalIgnoreCase))
            return "form_project";
        return table.Equals("form_question", StringComparison.OrdinalIgnoreCase)
            ? "form_question"
            : null;
    }

    internal static void ValidateDynamicTableClassifications(
        IReadOnlyCollection<FollowUpTableManifestItem> manifest)
    {
        var duplicateManifest = manifest
            .Where(item => item.Schema.Equals("target", StringComparison.Ordinal)
                           || item.DataCategory.Equals("DynamicFormData", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => (item.Schema, item.TableName))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateManifest is not null)
            throw SchemaReview(
                $"动态表清单存在重复项：{duplicateManifest.Key.Schema}.{duplicateManifest.Key.TableName}。");

        var invalid = manifest.FirstOrDefault(item =>
            HasImportPayload(item)
            && (item.Schema.Equals("target", StringComparison.Ordinal)
                != item.DataCategory.Equals("DynamicFormData", StringComparison.OrdinalIgnoreCase)));
        if (invalid is not null)
            throw SchemaReview(
                $"表 {invalid.Schema}.{invalid.TableName} 的动态表分类与 target 模式不一致。");
    }

    private static void ValidateMappedDynamicTableClassifications(
        IReadOnlyCollection<FollowUpTableManifestItem> manifest,
        FollowUpSchemaDecision? decision)
    {
        foreach (var source in manifest.Where(HasImportPayload))
            IsMappedDynamicFormTable(
                source,
                FollowUpSchemaDecisionProcessor.MapManifest(source, decision));
    }

    internal static FollowUpQuestionScopeSource ResolveQuestionScopeSource(
        string packageType,
        FollowUpTableManifestItem item,
        string? importedContentHash)
    {
        if (!string.IsNullOrWhiteSpace(item.ExportPath))
            return FollowUpQuestionScopeSource.Package;
        if (item.RecordCount != 0 || item.HasIncrementalData || !string.IsNullOrWhiteSpace(item.FileHash))
            throw SchemaReview("form.form_question 无导出文件，但清单仍声明了数据或文件 hash。");
        if (string.IsNullOrWhiteSpace(item.ContentHash))
            throw SchemaReview("form.form_question 无导出文件且缺少内容 hash，无法确定医院表单项范围。");
        if (item.ContentHash.Equals(EmptyFileSha256, StringComparison.OrdinalIgnoreCase))
            return FollowUpQuestionScopeSource.Empty;
        if (packageType.Equals("Baseline", StringComparison.Ordinal))
            throw SchemaReview("Baseline 未携带非空的 form.form_question 快照。");
        if (!string.IsNullOrWhiteSpace(importedContentHash)
            && item.ContentHash.Equals(importedContentHash, StringComparison.OrdinalIgnoreCase))
            return FollowUpQuestionScopeSource.Target;
        throw SchemaReview("form.form_question 未携带文件，且内容 hash 无法证明与医院端已导入主链一致。");
    }

    internal static void EnsureTargetQuestionContentHash(
        string expectedHash,
        IReadOnlyCollection<string> actualHashes)
    {
        if (!actualHashes.Contains(expectedHash, StringComparer.OrdinalIgnoreCase))
            throw SchemaReview("医院端 form.form_question 实际内容 hash 与数据包不一致，无法安全回退表单项范围。");
    }

    internal static string ComputeQuestionContentHash(IEnumerable<string> rows) =>
        ComputeQuestionContentHash(rows, "\n");

    internal static IReadOnlyList<string> ComputeQuestionContentHashes(IEnumerable<string> rows)
    {
        var materializedRows = rows.ToList();
        return
        [
            ComputeQuestionContentHash(materializedRows, "\n"),
            ComputeQuestionContentHash(materializedRows, "\r\n")
        ];
    }

    private static string ComputeQuestionContentHash(IEnumerable<string> rows, string newLine)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var newLineBytes = Encoding.UTF8.GetBytes(newLine);
        foreach (var row in rows)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(row));
            hash.AppendData(newLineBytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeSha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    internal static IReadOnlyList<FollowUpIgnoredColumnAudit> CollectIgnoredNonNullColumns(
        string schema,
        string table,
        IEnumerable<string> rows,
        IReadOnlySet<string> allowedColumns,
        IReadOnlySet<string> sourceColumns)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var noArrayColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            AnalyzeDynamicRow(row, schema, table, allowedColumns, sourceColumns, noArrayColumns, counts);
        return counts
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new FollowUpIgnoredColumnAudit(schema, table, item.Key, item.Value))
            .ToList();
    }

    internal static void ValidateArrayToTextValues(
        IEnumerable<string> rows,
        IReadOnlySet<string> arrayColumns)
    {
        foreach (var row in rows)
            ValidateImportRow(row, arrayColumns);
    }

    internal static void ValidateImportRow(
        string row,
        IReadOnlySet<string> arrayToTextColumns)
    {
        using var document = JsonDocument.Parse(row);
        if (arrayToTextColumns.Count == 0)
            return;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("NDJSON 行不是 JSON 对象。");
        foreach (var property in document.RootElement.EnumerateObject())
            if (arrayToTextColumns.Contains(property.Name))
                ValidateArrayValue(property.Value, property.Name);
    }

    private async Task<DynamicColumnScopeResolution> BuildDynamicColumnScopesAsync(
        FollowUpVerifiedPackage package,
        string? importedFormQuestionContentHash,
        FollowUpSchemaDecision? decision,
        CancellationToken cancellationToken)
    {
        // 共享宽表含其他医院和历史题目列，校验与写入必须复用同一份医院字段范围。
        ValidateDynamicTableClassifications(package.TableManifest);
        ValidateMappedDynamicTableClassifications(package.TableManifest, decision);
        var dynamicManifest = package.TableManifest
            .Where(item => HasImportPayload(item) && IsDynamicFormTable(item))
            .ToList();
        var hasPackageQuestionPayload = package.TableManifest.Any(item =>
            HasImportPayload(item)
            && item.Schema.Equals("form", StringComparison.OrdinalIgnoreCase)
            && item.TableName.Equals("form_question", StringComparison.OrdinalIgnoreCase));
        if (dynamicManifest.Count == 0 && !hasPackageQuestionPayload)
            return new DynamicColumnScopeResolution([], [], []);

        var questionItems = package.TableManifest.Where(item =>
            item.Schema.Equals("form", StringComparison.OrdinalIgnoreCase)
            && item.TableName.Equals("form_question", StringComparison.OrdinalIgnoreCase)).ToList();
        if (questionItems.Count != 1)
            throw SchemaReview("表清单必须且只能包含一个 form.form_question 项。");
        var questionItem = questionItems[0];
        if (!questionItem.Required || !questionItem.Enabled || questionItem.Skipped)
            throw SchemaReview("form.form_question 必须是已启用、未跳过的必选表。");
        var questionSchema = ValidateQuestionSchema(package.SchemaSnapshot);
        var questionSourceColumns = questionSchema.Columns.Select(item => item.Name).ToList();

        var sourceMode = ResolveQuestionScopeSource(
            package.Manifest.PackageType,
            questionItem,
            importedFormQuestionContentHash);
        ValidateQuestionDataFileManifest(package, questionItem, sourceMode);
        PackageQuestionScopeSnapshot? packageQuestionScope = null;
        List<FollowUpQuestionReference> questionReferences;
        switch (sourceMode)
        {
            case FollowUpQuestionScopeSource.Package:
                packageQuestionScope = await ReadPackageQuestionScopeAsync(
                    package,
                    questionItem,
                    cancellationToken);
                questionReferences = packageQuestionScope.References;
                break;
            case FollowUpQuestionScopeSource.Target:
            case FollowUpQuestionScopeSource.Empty:
                questionReferences = await LoadTargetQuestionReferencesAsync(
                    package.Manifest.HospitalId,
                    questionItem.ContentHash!,
                    questionSourceColumns,
                    cancellationToken);
                break;
            default:
                throw SchemaReview("无法识别 form.form_question 的范围来源。");
        }
        if (packageQuestionScope is not null)
            await EnsurePackageQuestionProjectScopeAsync(
                package,
                packageQuestionScope,
                cancellationToken);
        var definitions = BuildDynamicColumnScopeDefinitions(
            package.SchemaSnapshot,
            dynamicManifest,
            questionReferences,
            sourceMode,
            decision);
        if (definitions.Scopes.Count != dynamicManifest.Count)
            throw SchemaReview(string.Join("；", definitions.BreakingMessages));
        var audits = new List<FollowUpIgnoredColumnAudit>();
        foreach (var scope in definitions.Scopes)
        {
            var table = dynamicManifest.Single(item =>
                item.Schema.Equals(scope.SourceSchema, StringComparison.Ordinal)
                && item.TableName.Equals(scope.SourceTable, StringComparison.Ordinal));
            var source = package.SchemaSnapshot.Tables.Single(item =>
                item.SchemaName.Equals(scope.SourceSchema, StringComparison.Ordinal)
                && item.TableName.Equals(scope.SourceTable, StringComparison.Ordinal));
            audits.AddRange(await AnalyzeDynamicFileAsync(package, table, source, scope, cancellationToken));
        }
        return new DynamicColumnScopeResolution(
            definitions.Scopes,
            audits,
            definitions.BreakingMessages);
    }

    internal static DynamicColumnScopeBuild BuildDynamicColumnScopeDefinitions(
        FollowUpSchemaSnapshot snapshot,
        IReadOnlyCollection<FollowUpTableManifestItem> dynamicManifest,
        IReadOnlyCollection<FollowUpQuestionReference> questionReferences,
        FollowUpQuestionScopeSource sourceMode,
        FollowUpSchemaDecision? decision)
    {
        ValidateDynamicTableClassifications(dynamicManifest);
        var scopes = new List<FollowUpTableColumnScope>();
        var breakingMessages = new List<string>();
        foreach (var table in dynamicManifest)
        {
            var sourceMatches = snapshot.Tables.Where(item =>
                item.SchemaName.Equals(table.Schema, StringComparison.Ordinal)
                && item.TableName.Equals(table.TableName, StringComparison.Ordinal)).ToList();
            if (sourceMatches.Count != 1)
            {
                breakingMessages.Add($"动态表 {table.Schema}.{table.TableName} 的源结构快照缺失或重复。");
                continue;
            }
            var source = sourceMatches[0];
            var mapped = FollowUpSchemaDecisionProcessor.MapSchema(source, decision);
            EnsureSchemaReviewIdentifier(source.SchemaName, "源动态表 schema");
            EnsureSchemaReviewIdentifier(source.TableName, "源动态表名");
            EnsureSchemaReviewIdentifier(mapped.SchemaName, "目标动态表 schema");
            EnsureSchemaReviewIdentifier(mapped.TableName, "目标动态表名");

            var duplicateSourceColumns = source.Columns
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToList();
            if (duplicateSourceColumns.Count > 0)
            {
                breakingMessages.AddRange(duplicateSourceColumns.Select(group =>
                    $"源动态表结构存在重复字段：{source.SchemaName}.{source.TableName}.{group.Key}"));
                continue;
            }

            var mapping = FollowUpSchemaDecisionProcessor.FindMapping(
                source.SchemaName,
                source.TableName,
                decision);
            foreach (var defaultColumn in mapping?.DefaultValues.Keys ?? Enumerable.Empty<string>())
                EnsureSchemaReviewIdentifier(
                    defaultColumn,
                    $"动态默认值字段 {mapped.SchemaName}.{mapped.TableName}");
            var snapshotName = sourceMode switch
            {
                FollowUpQuestionScopeSource.Package => "包内表单项快照",
                FollowUpQuestionScopeSource.Target => "医院端表单项快照",
                _ => "空表单项快照"
            };
            var expectedMappingKey = $"{source.SchemaName}.{source.TableName}";
            foreach (var inexactKey in decision?.DecisionStatus == "ApprovedMapping"
                         ? decision.TableMappings.Keys.Where(key =>
                             key.Equals(expectedMappingKey, StringComparison.OrdinalIgnoreCase)
                             && !key.Equals(expectedMappingKey, StringComparison.Ordinal))
                         : [])
                breakingMessages.Add(
                    $"{snapshotName}的动态映射源表必须精确匹配源结构：{inexactKey} ≠ {expectedMappingKey}");
            // 题目还携带定义 ID，只重命名引用会让名称与定义记录分裂；动态标识差异必须通过数据库升级解决。
            if (mapping is not null)
            {
                if (!mapped.TableName.Equals(source.TableName, StringComparison.Ordinal))
                    breakingMessages.Add(
                        $"{snapshotName}存在时禁止动态表名映射：{source.SchemaName}.{source.TableName} → {mapped.SchemaName}.{mapped.TableName}");
                foreach (var columnMapping in mapping.ColumnMappings)
                {
                    var sourceColumn = source.Columns.FirstOrDefault(item =>
                        item.Name.Equals(columnMapping.Key, StringComparison.Ordinal));
                    if (sourceColumn is null)
                    {
                        breakingMessages.Add(
                            $"{snapshotName}的动态映射源字段必须精确匹配源结构：{source.SchemaName}.{source.TableName}.{columnMapping.Key}");
                        continue;
                    }
                    if (!columnMapping.Value.Equals(sourceColumn.Name, StringComparison.Ordinal))
                        breakingMessages.Add(
                            $"{snapshotName}存在时禁止动态字段映射：{source.SchemaName}.{source.TableName}.{sourceColumn.Name} → {columnMapping.Value}");
                }
            }

            var sourceColumns = source.Columns.ToDictionary(item => item.Name, StringComparer.Ordinal);
            var mappedPairs = source.Columns.Zip(mapped.Columns, (original, target) => (Original: original, Target: target)).ToList();
            var allowedSource = DynamicFixedColumns
                .Concat(source.PrimaryKey)
                .Concat(table.PrimaryKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var fixedColumn in DynamicFixedColumns.Where(item => !sourceColumns.ContainsKey(item)))
                breakingMessages.Add($"源动态表缺少系统固定字段：{source.SchemaName}.{source.TableName}.{fixedColumn}");
            foreach (var primaryKeyColumn in source.PrimaryKey.Concat(table.PrimaryKey)
                         .Distinct(StringComparer.Ordinal)
                         .Where(item => !sourceColumns.ContainsKey(item)))
                breakingMessages.Add($"源动态表缺少主键字段：{source.SchemaName}.{source.TableName}.{primaryKeyColumn}");
            if (table.PrimaryKey.Count > 0
                && !source.PrimaryKey.SequenceEqual(table.PrimaryKey, StringComparer.Ordinal))
                breakingMessages.Add($"源结构与表清单主键不一致：{source.SchemaName}.{source.TableName}");

            var arraySourceColumns = new HashSet<string>(StringComparer.Ordinal);
            var arrayTargetColumns = new HashSet<string>(StringComparer.Ordinal);
            var fileSourceColumns = new HashSet<string>(StringComparer.Ordinal);
            var fileTargetColumns = new HashSet<string>(StringComparer.Ordinal);
            if (sourceMode == FollowUpQuestionScopeSource.Package)
            {
                foreach (var reference in questionReferences.Where(item =>
                             item.TableName.Equals(source.TableName, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!reference.TableName.Equals(source.TableName, StringComparison.Ordinal))
                    {
                        breakingMessages.Add(
                            $"医院关联动态表名大小写与源结构不一致：{reference.TableName} ≠ {source.TableName}");
                        continue;
                    }
                    if (!sourceColumns.TryGetValue(reference.ColumnName, out var sourceColumn))
                    {
                        breakingMessages.Add($"医院关联字段在源结构不存在：{source.SchemaName}.{source.TableName}.{reference.ColumnName}");
                        continue;
                    }
                    allowedSource.Add(sourceColumn.Name);
                    var mappedColumn = FollowUpSchemaDecisionProcessor.MapColumn(
                        source.SchemaName,
                        source.TableName,
                        sourceColumn.Name,
                        decision);
                    if (AllowsArrayToText(reference.DataType) && IsArrayType(sourceColumn.DataType))
                    {
                        arraySourceColumns.Add(sourceColumn.Name);
                        arrayTargetColumns.Add(mappedColumn);
                    }
                    if (reference.DataType.Equals("文件", StringComparison.Ordinal))
                    {
                        fileSourceColumns.Add(sourceColumn.Name);
                        fileTargetColumns.Add(mappedColumn);
                    }
                }
            }
            else
            {
                foreach (var reference in questionReferences.Where(item =>
                             item.TableName.Equals(mapped.TableName, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!reference.TableName.Equals(mapped.TableName, StringComparison.Ordinal))
                    {
                        breakingMessages.Add(
                            $"医院关联动态表名大小写与目标结构不一致：{reference.TableName} ≠ {mapped.TableName}");
                        continue;
                    }
                    var matches = mappedPairs.Where(item =>
                        item.Target.Name.Equals(reference.ColumnName, StringComparison.Ordinal)).ToList();
                    if (matches.Count != 1)
                    {
                        breakingMessages.Add(matches.Count == 0
                            ? $"医院关联字段在源结构不存在：{mapped.SchemaName}.{mapped.TableName}.{reference.ColumnName}"
                            : $"多个源字段映射到医院关联字段：{mapped.SchemaName}.{mapped.TableName}.{reference.ColumnName}");
                        continue;
                    }
                    allowedSource.Add(matches[0].Original.Name);
                    if (AllowsArrayToText(reference.DataType) && IsArrayType(matches[0].Original.DataType))
                    {
                        arraySourceColumns.Add(matches[0].Original.Name);
                        arrayTargetColumns.Add(matches[0].Target.Name);
                    }
                    if (reference.DataType.Equals("文件", StringComparison.Ordinal))
                    {
                        fileSourceColumns.Add(matches[0].Original.Name);
                        fileTargetColumns.Add(matches[0].Target.Name);
                    }
                }
            }

            var selectedPairs = mappedPairs.Where(item => allowedSource.Contains(item.Original.Name)).ToList();
            // 共享宽表的未授权历史列不会进入 SQL，只校验本轮实际导入的源字段和目标字段。
            foreach (var pair in selectedPairs)
            {
                EnsureSchemaReviewIdentifier(
                    pair.Original.Name,
                    $"源动态字段 {source.SchemaName}.{source.TableName}");
                EnsureSchemaReviewIdentifier(
                    pair.Target.Name,
                    $"目标动态字段 {mapped.SchemaName}.{mapped.TableName}");
            }
            foreach (var collision in selectedPairs
                         .GroupBy(item => item.Target.Name, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
                breakingMessages.Add($"多个源字段映射到同一目标字段：{mapped.SchemaName}.{mapped.TableName}.{collision.Key}");

            foreach (var defaultColumn in mapping?.DefaultValues.Keys ?? Enumerable.Empty<string>())
                if (selectedPairs.Any(item => item.Target.Name.Equals(defaultColumn, StringComparison.OrdinalIgnoreCase)))
                    breakingMessages.Add($"默认值字段与源字段映射冲突：{mapped.SchemaName}.{mapped.TableName}.{defaultColumn}");

            scopes.Add(new FollowUpTableColumnScope(
                source.SchemaName,
                source.TableName,
                mapped.SchemaName,
                mapped.TableName,
                selectedPairs.Select(item => item.Original.Name).ToList(),
                selectedPairs.Select(item => item.Target.Name).ToList(),
                arraySourceColumns.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
                arrayTargetColumns.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
                fileSourceColumns.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
                fileTargetColumns.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        foreach (var collision in scopes
                     .GroupBy(item => $"{item.TargetSchema}.{item.TargetTable}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            breakingMessages.Add($"多个动态源表映射到同一目标表：{collision.Key}");

        return new DynamicColumnScopeBuild(scopes, breakingMessages);
    }

    private static FollowUpSchemaCheckResult MergeScopeResult(
        FollowUpSchemaCheckResult result,
        DynamicColumnScopeResolution scopeResolution)
    {
        var messages = result.Messages
            .Concat(scopeResolution.BreakingMessages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasBreaking = scopeResolution.BreakingMessages.Count > 0 || result.DiffLevel == "Breaking";
        var level = hasBreaking ? "Breaking" : result.DiffLevel;
        return new FollowUpSchemaCheckResult(
            level == "Compatible" ? "Passed" : "ReviewRequired",
            level,
            level == "Compatible",
            messages,
            scopeResolution.Scopes,
            scopeResolution.Audits);
    }

    private static FollowUpSchemaCheckResult MergeRequiresMapping(
        FollowUpSchemaCheckResult result,
        IReadOnlyCollection<string> messages)
    {
        if (messages.Count == 0)
            return result;
        var level = result.DiffLevel == "Breaking" ? "Breaking" : "RequiresMapping";
        return new FollowUpSchemaCheckResult(
            "ReviewRequired",
            level,
            false,
            result.Messages.Concat(messages).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            result.TableColumnScopes,
            result.IgnoredNonNullColumns);
    }

    private async Task<List<string>> CheckDynamicWritableColumnsAsync(
        IReadOnlyCollection<FollowUpTableColumnScope> scopes,
        IReadOnlyCollection<FollowUpTableManifestItem> mappedManifest,
        IReadOnlyDictionary<string, HashSet<string>> defaultColumns,
        CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        if (scopes.Count == 0)
            return issues;
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var scope in scopes)
        {
            var fullName = $"{scope.TargetSchema}.{scope.TargetTable}";
            var manifest = mappedManifest.Single(item =>
                HasImportPayload(item)
                && item.Schema.Equals(scope.TargetSchema, StringComparison.Ordinal)
                && item.TableName.Equals(scope.TargetTable, StringComparison.Ordinal));
            var expected = scope.TargetColumns
                .Concat(defaultColumns.TryGetValue(fullName, out var defaults) ? defaults : [])
                .ToHashSet(StringComparer.Ordinal);
            var privilegePredicate = FollowUpImportPolicyPermissions.BuildColumnPrivilegePredicate(
                manifest.ImportPolicy);
            await using var command = new NpgsqlCommand($"""
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = @table
                  AND is_generated = 'NEVER'
                  AND (is_identity = 'NO' OR identity_generation IS DISTINCT FROM 'ALWAYS')
                  {privilegePredicate}
                """, connection);
            command.Parameters.AddWithValue("schema", scope.TargetSchema);
            command.Parameters.AddWithValue("table", scope.TargetTable);
            var writable = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                writable.Add(reader.GetString(0));
            foreach (var column in expected.Where(item => !writable.Contains(item)))
                issues.Add($"目标字段不可写或不存在：{fullName}.{column}");
        }
        return issues;
    }

    private async Task<List<string>> CheckProtectedQuestionProjectWritableColumnsAsync(
        IReadOnlyCollection<FollowUpTableManifestItem> mappedManifest,
        CancellationToken cancellationToken)
    {
        var protectedTables = mappedManifest
            .Where(HasImportPayload)
            .Select(item => (Manifest: item, Required: GetProtectedQuestionProjectRequiredColumns(item.Schema, item.TableName)))
            .Where(item => item.Required.Count > 0)
            .ToList();
        if (protectedTables.Count == 0)
            return [];

        var issues = new List<string>();
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var item in protectedTables)
        {
            var privilegePredicate = FollowUpImportPolicyPermissions.BuildColumnPrivilegePredicate(
                item.Manifest.ImportPolicy);
            await using var command = new NpgsqlCommand($"""
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = @table
                  AND is_generated = 'NEVER'
                  AND (is_identity = 'NO' OR identity_generation IS DISTINCT FROM 'ALWAYS')
                  {privilegePredicate}
                """, connection);
            command.Parameters.AddWithValue("schema", item.Manifest.Schema);
            command.Parameters.AddWithValue("table", item.Manifest.TableName);
            var writable = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                writable.Add(reader.GetString(0));
            foreach (var column in item.Required.Where(column => !writable.Contains(column)))
                issues.Add($"目标安全字段不可写或不存在：{item.Manifest.Schema}.{item.Manifest.TableName}.{column}");
        }
        return issues;
    }

    private static FollowUpTableColumnScope? FindSourceScope(
        string schema,
        string table,
        IReadOnlyCollection<FollowUpTableColumnScope>? scopes) =>
        scopes?.SingleOrDefault(item =>
            item.SourceSchema.Equals(schema, StringComparison.Ordinal)
            && item.SourceTable.Equals(table, StringComparison.Ordinal));

    private async Task<IReadOnlyList<FollowUpIgnoredColumnAudit>> AnalyzeDynamicFileAsync(
        FollowUpVerifiedPackage package,
        FollowUpTableManifestItem table,
        FollowUpTableSchema source,
        FollowUpTableColumnScope scope,
        CancellationToken cancellationToken)
    {
        var allowedColumns = scope.SourceColumns.ToHashSet(StringComparer.Ordinal);
        var sourceColumns = source.Columns.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var arrayColumns = scope.ArrayToTextSourceColumns.ToHashSet(StringComparer.Ordinal);
        const string attachmentPrefix = "files/uploads/";
        var attachmentPaths = package.Manifest.AttachmentFiles
            .Where(item => item.Path.StartsWith(attachmentPrefix, StringComparison.Ordinal))
            .Select(item => item.Path[attachmentPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var filePath = SafeStagingPath(package.StagingPath, table.ExportPath!);
        var rowCount = 0;
        using var reader = new StreamReader(filePath);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            AnalyzeDynamicRow(
                line,
                source.SchemaName,
                source.TableName,
                allowedColumns,
                sourceColumns,
                arrayColumns,
                counts);
            _ = FollowUpTargetAdaptationService.NormalizeFileQuestionValues(
                line,
                scope.FileQuestionSourceColumns,
                attachmentPaths);
            rowCount++;
        }
        if (rowCount != table.RecordCount)
            throw SchemaReview($"表 {table.Schema}.{table.TableName} 记录数与清单不一致。");
        return counts
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new FollowUpIgnoredColumnAudit(
                source.SchemaName,
                source.TableName,
                item.Key,
                item.Value))
            .ToList();
    }

    private static void AnalyzeDynamicRow(
        string row,
        string schema,
        string table,
        IReadOnlySet<string> allowedColumns,
        IReadOnlySet<string> sourceColumns,
        IReadOnlySet<string> arrayColumns,
        IDictionary<string, int> ignoredCounts)
    {
        using var document = JsonDocument.Parse(row);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"表 {schema}.{table} 的 NDJSON 行不是 JSON 对象。");
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!sourceColumns.Contains(property.Name))
                throw new InvalidDataException($"表 {schema}.{table} 的数据行包含结构快照外字段：{property.Name}。");
            if (arrayColumns.Contains(property.Name))
                ValidateArrayValue(property.Value, property.Name);
            if (!allowedColumns.Contains(property.Name) && property.Value.ValueKind != JsonValueKind.Null)
            {
                ignoredCounts.TryGetValue(property.Name, out var count);
                ignoredCounts[property.Name] = count + 1;
            }
        }
    }

    private static void ValidateArrayValue(JsonElement value, string column)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return;
        if (value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item => item.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            throw new InvalidDataException($"动态字段 {column} 的 ARRAY → text 兼容值必须是仅含字符串或 null 的 JSON 数组。");
    }

    private static bool AllowsArrayToText(string dataType) =>
        dataType.Equals("文件", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("选择", StringComparison.OrdinalIgnoreCase);

    private static bool IsArrayType(string dataType) =>
        dataType.Equals("ARRAY", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("text[]", StringComparison.OrdinalIgnoreCase);

    private static FollowUpTableSchema ValidateQuestionSchema(FollowUpSchemaSnapshot snapshot)
    {
        var matches = snapshot.Tables.Where(item =>
            item.SchemaName.Equals("form", StringComparison.OrdinalIgnoreCase)
            && item.TableName.Equals("form_question", StringComparison.OrdinalIgnoreCase)).ToList();
        var requiredColumns = new[] { "id", "hospital_id", "project_id", "table_name", "column_name", "data_type" };
        if (matches.Count != 1
            || requiredColumns.Any(column => !matches[0].Columns.Any(item =>
                item.Name.Equals(column, StringComparison.Ordinal))))
            throw SchemaReview("form.form_question 结构快照缺失、重复或缺少范围判定字段。");
        return matches[0];
    }

    private static void ValidateQuestionDataFileManifest(
        FollowUpVerifiedPackage package,
        FollowUpTableManifestItem item,
        FollowUpQuestionScopeSource source)
    {
        var dataFiles = package.Manifest.DataFiles.Where(file =>
            file.Table.Equals("form.form_question", StringComparison.OrdinalIgnoreCase)).ToList();
        if (source == FollowUpQuestionScopeSource.Package)
        {
            if (item.RecordCount <= 0 || !item.HasIncrementalData
                || string.IsNullOrWhiteSpace(item.FileHash)
                || string.IsNullOrWhiteSpace(item.ContentHash)
                || !item.FileHash.Equals(item.ContentHash, StringComparison.OrdinalIgnoreCase)
                || dataFiles.Count != 1
                || !dataFiles[0].Path.Equals(item.ExportPath, StringComparison.Ordinal)
                || !dataFiles[0].Hash.Equals(item.FileHash, StringComparison.OrdinalIgnoreCase)
                || dataFiles[0].RecordCount != item.RecordCount)
                throw SchemaReview("form.form_question 文件与数据清单不一致。");
            return;
        }
        if (dataFiles.Count != 0)
            throw SchemaReview("form.form_question 未声明导出文件，但 manifest 仍包含对应数据文件。");
    }

    internal static async Task<PackageQuestionScopeSnapshot> ReadPackageQuestionScopeAsync(
        FollowUpVerifiedPackage package,
        FollowUpTableManifestItem item,
        CancellationToken cancellationToken)
    {
        var filePath = SafeStagingPath(package.StagingPath, item.ExportPath!);
        var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        if (!ComputeSha256(fileBytes).Equals(item.FileHash, StringComparison.OrdinalIgnoreCase))
            throw SchemaReview("form.form_question 实际文件 hash 与表清单不一致。");

        var result = new List<FollowUpQuestionReference>();
        var questionIds = new HashSet<Guid>();
        var projectIds = new HashSet<Guid>();
        var rowCount = 0;
        using var reader = new StreamReader(new MemoryStream(fileBytes));
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw SchemaReview("form.form_question NDJSON 行不是 JSON 对象。");
            var root = document.RootElement;
            var questionId = ReadRequiredGuid(root, "id");
            if (!questionIds.Add(questionId))
                throw SchemaReview($"form.form_question 包含重复题目：{questionId}。");
            var rowHospitalId = ReadRequiredGuid(root, "hospital_id");
            if (rowHospitalId != package.Manifest.HospitalId)
                throw SchemaReview("form.form_question 包含其他医院的表单项。");
            var projectId = ReadRequiredGuid(root, "project_id");
            projectIds.Add(projectId);
            var reference = CreateQuestionReference(
                ReadOptionalString(root, "table_name"),
                ReadOptionalString(root, "column_name"),
                ReadOptionalString(root, "data_type"));
            if (reference is not null)
                result.Add(reference);
            rowCount++;
        }
        if (rowCount != item.RecordCount)
            throw SchemaReview("form.form_question 实际记录数与表清单不一致。");
        _ = GetRequiredProjectManifest(package);
        var packageProjectIds = (await ReadPackageProjectScopeAsync(package, cancellationToken)).ProjectIds;
        var targetProjectIds = projectIds
            .Where(projectId => !packageProjectIds.Contains(projectId))
            .ToHashSet();
        return new PackageQuestionScopeSnapshot(
            result,
            questionIds,
            projectIds,
            packageProjectIds,
            targetProjectIds);
    }

    internal static async Task<PackageProjectScopeSnapshot> ReadPackageProjectScopeAsync(
        FollowUpVerifiedPackage package,
        CancellationToken cancellationToken)
    {
        var projectItems = package.TableManifest.Where(item =>
            item.Schema.Equals("form", StringComparison.OrdinalIgnoreCase)
            && item.TableName.Equals("form_project", StringComparison.OrdinalIgnoreCase)).ToList();
        if (projectItems.Count > 1)
            throw SchemaReview("表清单只能包含一个 form.form_project 项。");
        if (projectItems.Count == 0 || !HasImportPayload(projectItems[0]))
            return new PackageProjectScopeSnapshot([]);
        var projectItem = projectItems[0];
        if (string.IsNullOrWhiteSpace(projectItem.ExportPath))
            return new PackageProjectScopeSnapshot([]);

        var filePath = SafeStagingPath(package.StagingPath, projectItem.ExportPath);
        if (string.IsNullOrWhiteSpace(projectItem.FileHash))
            throw SchemaReview("form.form_project 导出文件缺少 hash。");
        var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        if (!ComputeSha256(fileBytes).Equals(projectItem.FileHash, StringComparison.OrdinalIgnoreCase))
            throw SchemaReview("form.form_project 实际文件 hash 与表清单不一致。");

        var validProjectIds = new HashSet<Guid>();
        var rowCount = 0;
        using var reader = new StreamReader(new MemoryStream(fileBytes));
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw SchemaReview("form.form_project NDJSON 行不是 JSON 对象。");
            var root = document.RootElement;
            var projectId = ReadRequiredProjectGuid(root, "id");
            var hospitalId = ReadRequiredProjectGuid(root, "hospital_id");
            if (hospitalId != package.Manifest.HospitalId)
                throw SchemaReview("包内项目 form.form_project 不属于当前医院。");
            if (!validProjectIds.Add(projectId))
                throw SchemaReview($"form.form_project 包含重复项目：{projectId}。");
            rowCount++;
        }
        if (rowCount != projectItem.RecordCount)
            throw SchemaReview("form.form_project 实际记录数与表清单不一致。");
        return new PackageProjectScopeSnapshot(validProjectIds);
    }

    private async Task EnsurePackageProjectScopeAsync(
        Guid hospitalId,
        PackageProjectScopeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.ProjectIds.Count == 0)
            return;
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureExistingProjectHospitalScopeAsync(
            connection,
            transaction: null,
            hospitalId,
            snapshot.ProjectIds,
            cancellationToken);
    }

    private async Task EnsurePackageQuestionProjectScopeAsync(
        FollowUpVerifiedPackage package,
        PackageQuestionScopeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.QuestionIds.Count == 0
            && snapshot.ProjectIds.Count == 0
            && snapshot.PackageProjectIds.Count == 0)
            return;

        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureExistingProjectHospitalScopeAsync(
            connection,
            transaction: null,
            package.Manifest.HospitalId,
            snapshot.PackageProjectIds,
            cancellationToken);
        await EnsureExistingQuestionHospitalScopeAsync(
            connection,
            transaction: null,
            package.Manifest.HospitalId,
            snapshot.QuestionIds,
            cancellationToken);
        await EnsureQuestionProjectScopeAsync(
            connection,
            transaction: null,
            package.Manifest.HospitalId,
            snapshot.TargetProjectIds,
            cancellationToken);
    }

    private static FollowUpTableManifestItem GetRequiredProjectManifest(FollowUpVerifiedPackage package)
    {
        var projectItems = package.TableManifest.Where(item =>
            item.Schema.Equals("form", StringComparison.OrdinalIgnoreCase)
            && item.TableName.Equals("form_project", StringComparison.OrdinalIgnoreCase)).ToList();
        if (projectItems.Count != 1)
            throw SchemaReview("表清单必须且只能包含一个 form.form_project 项。");
        var projectItem = projectItems[0];
        if (!projectItem.Required || !projectItem.Enabled || projectItem.Skipped)
            throw SchemaReview("form.form_project 必须是已启用、未跳过的必选表。");
        return projectItem;
    }

    private static void EnsureQuestionProjectIds(
        IReadOnlySet<Guid> validProjectIds,
        IReadOnlySet<Guid> referencedProjectIds)
    {
        foreach (var projectId in referencedProjectIds)
            if (!validProjectIds.Contains(projectId))
                throw SchemaReview($"包内 form.form_question 所属项目不在当前医院范围：{projectId}。");
    }

    private async Task<List<FollowUpQuestionReference>> LoadTargetQuestionReferencesAsync(
        Guid hospitalId,
        string expectedContentHash,
        IReadOnlyCollection<string> sourceColumns,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        var snapshot = await ReadTargetQuestionScopeSnapshotAsync(
            connection,
            transaction: null,
            hospitalId,
            sourceColumns,
            cancellationToken);
        EnsureTargetQuestionContentHash(expectedContentHash, snapshot.ContentHashes);
        return snapshot.References;
    }

    internal static async Task LockTargetQuestionScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var lockCommand = new NpgsqlCommand(TargetQuestionScopeLockSql, connection, transaction);
        try
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new FollowUpPackageException(
                FollowUpErrorCodes.InternalError,
                "医院端表单配置正在变更，30 秒内未能取得一致性锁，请稍后重试。",
                exception);
        }
    }

    internal static async Task LockPackageQuestionProjectScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var lockCommand = new NpgsqlCommand(
            PackageQuestionProjectScopeLockSql,
            connection,
            transaction);
        try
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new FollowUpPackageException(
                FollowUpErrorCodes.InternalError,
                "医院端项目或表单项配置正在变更，30 秒内未能取得一致性锁，请稍后重试。",
                exception);
        }
    }

    internal static async Task EnsureExistingProjectHospitalScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid hospitalId,
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0)
            return;
        var existingProjects = await ReadProjectHospitalScopeAsync(
            connection,
            transaction,
            projectIds,
            cancellationToken);
        ValidateExistingProjectHospitalScope(hospitalId, projectIds, existingProjects);
    }

    internal static async Task EnsureQuestionProjectScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid hospitalId,
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0)
            return;
        var existingProjects = await ReadProjectHospitalScopeAsync(
            connection,
            transaction,
            projectIds,
            cancellationToken);
        ValidateExistingProjectHospitalScope(hospitalId, projectIds, existingProjects);
        EnsureQuestionProjectIds(existingProjects.Keys.ToHashSet(), projectIds.ToHashSet());
    }

    internal static async Task EnsureExistingQuestionHospitalScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid hospitalId,
        IReadOnlyCollection<Guid> questionIds,
        CancellationToken cancellationToken)
    {
        if (questionIds.Count == 0)
            return;
        var existingQuestions = await ReadQuestionHospitalScopeAsync(
            connection,
            transaction,
            questionIds,
            cancellationToken);
        ValidateExistingQuestionHospitalScope(hospitalId, questionIds, existingQuestions);
    }

    internal static void ValidateExistingProjectHospitalScope(
        Guid hospitalId,
        IReadOnlyCollection<Guid> projectIds,
        IReadOnlyDictionary<Guid, Guid?> existingProjects)
    {
        foreach (var projectId in projectIds.Distinct())
            if (existingProjects.TryGetValue(projectId, out var existingHospitalId)
                && existingHospitalId != hospitalId)
                throw SchemaReview("包内 form.form_project 的既有同 ID 项目不属于当前医院。");
    }

    internal static void ValidateExistingQuestionHospitalScope(
        Guid hospitalId,
        IReadOnlyCollection<Guid> questionIds,
        IReadOnlyDictionary<Guid, (Guid? QuestionHospitalId, Guid? ProjectHospitalId)> existingQuestions)
    {
        foreach (var questionId in questionIds.Distinct())
            if (existingQuestions.TryGetValue(questionId, out var existingScope)
                && (existingScope.QuestionHospitalId != hospitalId
                    || existingScope.ProjectHospitalId != hospitalId))
                throw SchemaReview("包内 form.form_question 的既有同 ID 题目或所属项目不属于当前医院。");
    }

    private static async Task<Dictionary<Guid, Guid?>> ReadProjectHospitalScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            PackageQuestionProjectScopeSql,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "projectIds",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            projectIds.Distinct().ToArray());
        var existingProjects = new Dictionary<Guid, Guid?>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            existingProjects[reader.GetGuid(0)] = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        return existingProjects;
    }

    private static async Task<Dictionary<Guid, (Guid? QuestionHospitalId, Guid? ProjectHospitalId)>>
        ReadQuestionHospitalScopeAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            IReadOnlyCollection<Guid> questionIds,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            PackageQuestionHospitalScopeSql,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "questionIds",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            questionIds.Distinct().ToArray());
        var existingQuestions =
            new Dictionary<Guid, (Guid? QuestionHospitalId, Guid? ProjectHospitalId)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            existingQuestions[reader.GetGuid(0)] = (
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2));
        return existingQuestions;
    }

    internal async Task EnsureTargetQuestionContentHashAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid hospitalId,
        string expectedContentHash,
        IReadOnlyCollection<string> sourceColumns,
        CancellationToken cancellationToken)
    {
        // 导入事务已在任何读写前持有 SHARE 锁；动态写入前重算，确保授权范围没有漂移。
        var snapshot = await ReadTargetQuestionScopeSnapshotAsync(
            connection,
            transaction,
            hospitalId,
            sourceColumns,
            cancellationToken);
        EnsureTargetQuestionContentHash(expectedContentHash, snapshot.ContentHashes);
    }

    private static async Task<TargetQuestionScopeSnapshot> ReadTargetQuestionScopeSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid hospitalId,
        IReadOnlyCollection<string> sourceColumns,
        CancellationToken cancellationToken)
    {
        if (sourceColumns.Count == 0)
            throw SchemaReview("form.form_question 源结构快照没有可用于内容校验的字段。");
        foreach (var sourceColumn in sourceColumns)
            EnsureSchemaReviewIdentifier(sourceColumn, "form.form_question 源字段");
        var rows = new List<string>();
        var references = new List<FollowUpQuestionReference>();
        await using var command = new NpgsqlCommand(TargetQuestionScopeSnapshotSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("hospitalId", NpgsqlDbType.Uuid) { Value = hospitalId });
        command.Parameters.AddWithValue(
            "sourceColumns",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            sourceColumns.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ValidateQuestionHospitalScope(
                hospitalId,
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2));
            var row = reader.GetString(0);
            rows.Add(row);
            using var document = JsonDocument.Parse(row);
            var root = document.RootElement;
            var reference = CreateQuestionReference(
                ReadOptionalString(root, "table_name"),
                ReadOptionalString(root, "column_name"),
                ReadOptionalString(root, "data_type"));
            if (reference is not null)
                references.Add(reference);
        }
        return new TargetQuestionScopeSnapshot(
            references,
            ComputeQuestionContentHashes(rows));
    }

    internal static void ValidateQuestionHospitalScope(
        Guid expectedHospitalId,
        Guid? questionHospitalId,
        Guid? projectHospitalId)
    {
        if (questionHospitalId != expectedHospitalId || projectHospitalId != expectedHospitalId)
            throw SchemaReview("医院端 form.form_question 与所属项目的医院标识不一致。");
    }

    internal static FollowUpQuestionReference? CreateQuestionReference(
        string? table,
        string? column,
        string? dataType)
    {
        // 未绑定物理表或字段的题目仍属于 form_question 快照，但不构成动态列授权。
        if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(column))
            return null;
        if (!table.Equals(table.Trim(), StringComparison.Ordinal)
            || !column.Equals(column.Trim(), StringComparison.Ordinal))
            throw SchemaReview("form.form_question 的 table_name 和 column_name 不得包含首尾空白。");
        dataType = dataType?.Trim() ?? string.Empty;
        if (!IdentifierRegex().IsMatch(table) || !IdentifierRegex().IsMatch(column))
            throw SchemaReview("form.form_question 的 table_name 或 column_name 是非法数据库标识符。");
        return new FollowUpQuestionReference(table, column, dataType);
    }

    private static string? ReadOptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw SchemaReview($"form.form_question 的 {property} 类型无效。");
        return value.GetString();
    }

    private static string ReadRequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw SchemaReview($"form.form_question 缺少有效的 {property}。");
        return value.GetString()!;
    }

    private static Guid ReadRequiredGuid(JsonElement root, string property)
    {
        var value = ReadRequiredString(root, property);
        if (!Guid.TryParse(value, out var result) || result == Guid.Empty)
            throw SchemaReview($"form.form_question 的 {property} 不是有效 GUID。");
        return result;
    }

    private static Guid ReadRequiredProjectGuid(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var result)
            || result == Guid.Empty)
            throw SchemaReview($"form.form_project 缺少有效的 {property} GUID。");
        return result;
    }

    private static string SafeStagingPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("数据文件路径逃逸 staging 目录。");
        return target;
    }

    private static FollowUpPackageException SchemaReview(string message) =>
        new(FollowUpErrorCodes.SchemaReviewRequired, message);

    private sealed record DynamicColumnScopeResolution(
        List<FollowUpTableColumnScope> Scopes,
        List<FollowUpIgnoredColumnAudit> Audits,
        List<string> BreakingMessages);

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

    private static bool IsCompatibleType(string source, string target, bool allowArrayToText = false)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return true;
        var pair = $"{source.ToLowerInvariant()}->{target.ToLowerInvariant()}";
        return pair is "character varying->text" or "smallint->integer" or "smallint->bigint" or "integer->bigint" or "real->double precision"
               || allowArrayToText && pair is ("array->text" or "text[]->text");
    }

    internal static void EnsureIdentifier(string value)
    {
        if (!IdentifierRegex().IsMatch(value)) throw new InvalidOperationException($"非法数据库标识符：{value}");
    }

    private static void EnsureSchemaReviewIdentifier(string value, string subject)
    {
        if (!IdentifierRegex().IsMatch(value))
            throw SchemaReview($"{subject} 是非法数据库标识符：{value}");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}

internal sealed record TargetQuestionScopeSnapshot(
    List<FollowUpQuestionReference> References,
    IReadOnlyList<string> ContentHashes);

internal sealed record TargetQuestionScopeGuard(
    string ExpectedContentHash,
    IReadOnlyList<string> SourceColumns);

internal sealed record PackageQuestionScopeSnapshot(
    List<FollowUpQuestionReference> References,
    HashSet<Guid> QuestionIds,
    HashSet<Guid> ProjectIds,
    HashSet<Guid> PackageProjectIds,
    HashSet<Guid> TargetProjectIds);

internal sealed record PackageProjectScopeSnapshot(HashSet<Guid> ProjectIds);

internal sealed record PackageQuestionProjectGuard(
    IReadOnlyList<Guid> QuestionIds,
    IReadOnlyList<Guid> ProjectIds,
    IReadOnlyList<Guid> PackageProjectIds);

internal sealed record FollowUpQuestionReference(string TableName, string ColumnName, string DataType);

internal sealed record DynamicColumnScopeBuild(
    List<FollowUpTableColumnScope> Scopes,
    List<string> BreakingMessages);

internal enum FollowUpQuestionScopeSource
{
    Package,
    Target,
    Empty
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
