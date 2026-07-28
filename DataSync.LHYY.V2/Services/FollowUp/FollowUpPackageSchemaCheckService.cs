using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Tools;
using Npgsql;
using NpgsqlTypes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed partial class FollowUpPackageSchemaCheckService(IConfiguration configuration)
{
    internal const string EmptyFileSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

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
            defaultColumns,
            cancellationToken);
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
        var targets = targetTables.ToDictionary(item => $"{item.SchemaName}.{item.TableName}", StringComparer.OrdinalIgnoreCase);
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
            if (!source.PrimaryKey.SequenceEqual(target.PrimaryKey, StringComparer.OrdinalIgnoreCase))
            {
                breaking = true;
                messages.Add($"主键不一致：{fullName}");
            }
            var sourceColumns = source.Columns.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            var targetColumns = target.Columns.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            scopes.TryGetValue(fullName, out var columnScope);
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
                                            StringComparer.OrdinalIgnoreCase) == true;
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
        IReadOnlyCollection<FollowUpTableColumnScope>? columnScopes = null) =>
        SelectSourceTables(sourceTables, manifest)
            .Select(item =>
            {
                var sourceManifest = manifest.First(manifestItem =>
                    manifestItem.Schema.Equals(item.SchemaName, StringComparison.OrdinalIgnoreCase)
                    && manifestItem.TableName.Equals(item.TableName, StringComparison.OrdinalIgnoreCase));
                if (!IsDynamicFormTable(sourceManifest))
                    return FollowUpSchemaDecisionProcessor.MapSchema(item, decision);
                var scope = FindSourceScope(item.SchemaName, item.TableName, columnScopes)
                    ?? throw new InvalidDataException($"动态表 {item.SchemaName}.{item.TableName} 缺少导入字段范围。");
                return MapAndApplySourceTable(item, decision, scope);
            })
            .ToList();

    internal static FollowUpTableSchema MapAndApplySourceTable(
        FollowUpTableSchema source,
        FollowUpSchemaDecision? decision,
        FollowUpTableColumnScope scope)
    {
        if (!scope.SourceSchema.Equals(source.SchemaName, StringComparison.OrdinalIgnoreCase)
            || !scope.SourceTable.Equals(source.TableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"字段范围与源表不匹配：{source.SchemaName}.{source.TableName}。");

        var allowed = scope.SourceColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        if (!mapped.SchemaName.Equals(scope.TargetSchema, StringComparison.OrdinalIgnoreCase)
            || !mapped.TableName.Equals(scope.TargetTable, StringComparison.OrdinalIgnoreCase)
            || !mapped.Columns.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
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
        item.Schema.Equals("target", StringComparison.OrdinalIgnoreCase)
        && item.DataCategory.Equals("DynamicFormData", StringComparison.OrdinalIgnoreCase);

    internal static void ValidateDynamicTableClassifications(
        IReadOnlyCollection<FollowUpTableManifestItem> manifest)
    {
        var invalid = manifest.FirstOrDefault(item =>
            HasImportPayload(item)
            && (item.Schema.Equals("target", StringComparison.OrdinalIgnoreCase)
                != item.DataCategory.Equals("DynamicFormData", StringComparison.OrdinalIgnoreCase)));
        if (invalid is not null)
            throw SchemaReview(
                $"表 {invalid.Schema}.{invalid.TableName} 的动态表分类与 target 模式不一致。");
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
        {
            using var document = JsonDocument.Parse(row);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("NDJSON 行不是 JSON 对象。");
            foreach (var property in document.RootElement.EnumerateObject())
                if (arrayColumns.Contains(property.Name))
                    ValidateArrayValue(property.Value, property.Name);
        }
    }

    private async Task<DynamicColumnScopeResolution> BuildDynamicColumnScopesAsync(
        FollowUpVerifiedPackage package,
        string? importedFormQuestionContentHash,
        FollowUpSchemaDecision? decision,
        CancellationToken cancellationToken)
    {
        // 共享宽表含其他医院和历史题目列，校验与写入必须复用同一份医院字段范围。
        ValidateDynamicTableClassifications(package.TableManifest);
        var dynamicManifest = package.TableManifest
            .Where(item => HasImportPayload(item) && IsDynamicFormTable(item))
            .ToList();
        if (dynamicManifest.Count == 0)
            return new DynamicColumnScopeResolution([], [], []);

        var questionItems = package.TableManifest.Where(item =>
            item.Schema.Equals("form", StringComparison.OrdinalIgnoreCase)
            && item.TableName.Equals("form_question", StringComparison.OrdinalIgnoreCase)).ToList();
        if (questionItems.Count != 1)
            throw SchemaReview("表清单必须且只能包含一个 form.form_question 项。");
        var questionItem = questionItems[0];
        if (!questionItem.Required || !questionItem.Enabled || questionItem.Skipped)
            throw SchemaReview("form.form_question 必须是已启用、未跳过的必选表。");
        ValidateQuestionSchema(package.SchemaSnapshot);

        var sourceMode = ResolveQuestionScopeSource(
            package.Manifest.PackageType,
            questionItem,
            importedFormQuestionContentHash);
        ValidateQuestionDataFileManifest(package, questionItem, sourceMode);
        var questionReferences = sourceMode switch
        {
            FollowUpQuestionScopeSource.Package => await ReadPackageQuestionReferencesAsync(
                package,
                questionItem,
                cancellationToken),
            FollowUpQuestionScopeSource.Target => await LoadTargetQuestionReferencesAsync(
                package.Manifest.HospitalId,
                cancellationToken),
            _ => []
        };
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
                item.Schema.Equals(scope.SourceSchema, StringComparison.OrdinalIgnoreCase)
                && item.TableName.Equals(scope.SourceTable, StringComparison.OrdinalIgnoreCase));
            var source = package.SchemaSnapshot.Tables.Single(item =>
                item.SchemaName.Equals(scope.SourceSchema, StringComparison.OrdinalIgnoreCase)
                && item.TableName.Equals(scope.SourceTable, StringComparison.OrdinalIgnoreCase));
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
                item.SchemaName.Equals(table.Schema, StringComparison.OrdinalIgnoreCase)
                && item.TableName.Equals(table.TableName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sourceMatches.Count != 1)
            {
                breakingMessages.Add($"动态表 {table.Schema}.{table.TableName} 的源结构快照缺失或重复。");
                continue;
            }
            var source = sourceMatches[0];
            var mapped = FollowUpSchemaDecisionProcessor.MapSchema(source, decision);
            EnsureIdentifier(source.SchemaName);
            EnsureIdentifier(source.TableName);
            EnsureIdentifier(mapped.SchemaName);
            EnsureIdentifier(mapped.TableName);

            var sourceColumns = source.Columns.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            var mappedPairs = source.Columns.Zip(mapped.Columns, (original, target) => (Original: original, Target: target)).ToList();
            var allowedSource = DynamicFixedColumns
                .Concat(source.PrimaryKey)
                .Concat(table.PrimaryKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var fixedColumn in DynamicFixedColumns.Where(item => !sourceColumns.ContainsKey(item)))
                breakingMessages.Add($"源动态表缺少系统固定字段：{source.SchemaName}.{source.TableName}.{fixedColumn}");
            foreach (var primaryKeyColumn in source.PrimaryKey.Concat(table.PrimaryKey)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(item => !sourceColumns.ContainsKey(item)))
                breakingMessages.Add($"源动态表缺少主键字段：{source.SchemaName}.{source.TableName}.{primaryKeyColumn}");
            if (table.PrimaryKey.Count > 0
                && !source.PrimaryKey.SequenceEqual(table.PrimaryKey, StringComparer.OrdinalIgnoreCase))
                breakingMessages.Add($"源结构与表清单主键不一致：{source.SchemaName}.{source.TableName}");

            var arraySourceColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arrayTargetColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sourceMode == FollowUpQuestionScopeSource.Package)
            {
                foreach (var reference in questionReferences.Where(item =>
                             item.TableName.Equals(source.TableName, StringComparison.OrdinalIgnoreCase)))
                {
                    allowedSource.Add(reference.ColumnName);
                    if (!sourceColumns.TryGetValue(reference.ColumnName, out var sourceColumn))
                    {
                        breakingMessages.Add($"医院关联字段在源结构不存在：{source.SchemaName}.{source.TableName}.{reference.ColumnName}");
                        continue;
                    }
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
                }
            }
            else
            {
                foreach (var reference in questionReferences.Where(item =>
                             item.TableName.Equals(mapped.TableName, StringComparison.OrdinalIgnoreCase)))
                {
                    var matches = mappedPairs.Where(item =>
                        item.Target.Name.Equals(reference.ColumnName, StringComparison.OrdinalIgnoreCase)).ToList();
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
                }
            }

            var selectedPairs = mappedPairs.Where(item => allowedSource.Contains(item.Original.Name)).ToList();
            foreach (var collision in selectedPairs
                         .GroupBy(item => item.Target.Name, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
                breakingMessages.Add($"多个源字段映射到同一目标字段：{mapped.SchemaName}.{mapped.TableName}.{collision.Key}");

            var mapping = FollowUpSchemaDecisionProcessor.FindMapping(source.SchemaName, source.TableName, decision);
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
                arrayTargetColumns.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList()));
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
            var expected = scope.TargetColumns
                .Concat(defaultColumns.TryGetValue(fullName, out var defaults) ? defaults : [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using var command = new NpgsqlCommand("""
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = @table
                  AND is_generated = 'NEVER'
                  AND (is_identity = 'NO' OR identity_generation IS DISTINCT FROM 'ALWAYS')
                """, connection);
            command.Parameters.AddWithValue("schema", scope.TargetSchema);
            command.Parameters.AddWithValue("table", scope.TargetTable);
            var writable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                writable.Add(reader.GetString(0));
            foreach (var column in expected.Where(item => !writable.Contains(item)))
                issues.Add($"目标字段不可写或不存在：{fullName}.{column}");
        }
        return issues;
    }

    private static FollowUpTableColumnScope? FindSourceScope(
        string schema,
        string table,
        IReadOnlyCollection<FollowUpTableColumnScope>? scopes) =>
        scopes?.SingleOrDefault(item =>
            item.SourceSchema.Equals(schema, StringComparison.OrdinalIgnoreCase)
            && item.SourceTable.Equals(table, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<FollowUpIgnoredColumnAudit>> AnalyzeDynamicFileAsync(
        FollowUpVerifiedPackage package,
        FollowUpTableManifestItem table,
        FollowUpTableSchema source,
        FollowUpTableColumnScope scope,
        CancellationToken cancellationToken)
    {
        var allowedColumns = scope.SourceColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceColumns = source.Columns.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var arrayColumns = scope.ArrayToTextSourceColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

    private static void ValidateQuestionSchema(FollowUpSchemaSnapshot snapshot)
    {
        var matches = snapshot.Tables.Where(item =>
            item.SchemaName.Equals("form", StringComparison.OrdinalIgnoreCase)
            && item.TableName.Equals("form_question", StringComparison.OrdinalIgnoreCase)).ToList();
        var requiredColumns = new[] { "hospital_id", "table_name", "column_name", "data_type" };
        if (matches.Count != 1
            || requiredColumns.Any(column => !matches[0].Columns.Any(item =>
                item.Name.Equals(column, StringComparison.OrdinalIgnoreCase))))
            throw SchemaReview("form.form_question 结构快照缺失、重复或缺少范围判定字段。");
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

    private static async Task<List<FollowUpQuestionReference>> ReadPackageQuestionReferencesAsync(
        FollowUpVerifiedPackage package,
        FollowUpTableManifestItem item,
        CancellationToken cancellationToken)
    {
        var filePath = SafeStagingPath(package.StagingPath, item.ExportPath!);
        await using (var stream = File.OpenRead(filePath))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!hash.Equals(item.FileHash, StringComparison.OrdinalIgnoreCase))
                throw SchemaReview("form.form_question 实际文件 hash 与表清单不一致。");
        }

        var result = new List<FollowUpQuestionReference>();
        using var reader = new StreamReader(filePath);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw SchemaReview("form.form_question NDJSON 行不是 JSON 对象。");
            var root = document.RootElement;
            var rowHospitalId = ReadRequiredGuid(root, "hospital_id");
            if (rowHospitalId != package.Manifest.HospitalId)
                throw SchemaReview("form.form_question 包含其他医院的表单项。");
            result.Add(CreateQuestionReference(
                ReadRequiredString(root, "table_name"),
                ReadRequiredString(root, "column_name"),
                ReadRequiredString(root, "data_type")));
        }
        if (result.Count != item.RecordCount)
            throw SchemaReview("form.form_question 实际记录数与表清单不一致。");
        return result;
    }

    private async Task<List<FollowUpQuestionReference>> LoadTargetQuestionReferencesAsync(
        Guid hospitalId,
        CancellationToken cancellationToken)
    {
        var result = new List<FollowUpQuestionReference>();
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT BTRIM(question.table_name),
                   BTRIM(question.column_name),
                   question.data_type,
                   question.hospital_id,
                   project.hospital_id
            FROM form.form_question question
            JOIN form.form_project project ON project.id = question.project_id
            WHERE (project.hospital_id = @hospitalId OR question.hospital_id = @hospitalId)
              AND NULLIF(BTRIM(question.table_name), '') IS NOT NULL
              AND NULLIF(BTRIM(question.column_name), '') IS NOT NULL
            ORDER BY BTRIM(question.table_name), BTRIM(question.column_name), question.id
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("hospitalId", NpgsqlDbType.Uuid) { Value = hospitalId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(2))
                throw SchemaReview("医院端 form.form_question 存在缺少 data_type 的表单项。");
            ValidateQuestionHospitalScope(
                hospitalId,
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4));
            result.Add(CreateQuestionReference(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    internal static void ValidateQuestionHospitalScope(
        Guid expectedHospitalId,
        Guid? questionHospitalId,
        Guid? projectHospitalId)
    {
        if (questionHospitalId != expectedHospitalId || projectHospitalId != expectedHospitalId)
            throw SchemaReview("医院端 form.form_question 与所属项目的医院标识不一致。");
    }

    private static FollowUpQuestionReference CreateQuestionReference(string table, string column, string dataType)
    {
        table = table.Trim();
        column = column.Trim();
        dataType = dataType.Trim();
        EnsureIdentifier(table);
        EnsureIdentifier(column);
        if (string.IsNullOrWhiteSpace(dataType))
            throw SchemaReview("form.form_question 存在空 data_type。");
        return new FollowUpQuestionReference(table, column, dataType);
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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}

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
