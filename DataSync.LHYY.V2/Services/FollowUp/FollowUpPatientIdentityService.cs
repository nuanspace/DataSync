using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataSync.LHYY.V2.Services.FollowUp;

internal static class FollowUpPatientIdentityMatchBasis
{
    public const string Id = "Id";
    public const string SidNumber = "SidNumber";
    public const string Demographics = "Demographics";
}

internal sealed record FollowUpUniquePatientIdentityInput(
    Guid SourceUniquePatientId,
    string? SidNumber,
    string? Name,
    DateOnly? Birthday,
    string? Gender);

internal sealed record FollowUpPatientIdentityInput(
    Guid SourcePatientId,
    Guid? SourceUniquePatientId,
    Guid HospitalId,
    Guid ProjectId,
    string? OriginalSourceType);

internal sealed class FollowUpPatientIdentityScope(string hospitalCode)
{
    public string HospitalCode { get; } = hospitalCode;
    public Dictionary<Guid, FollowUpUniquePatientIdentityInput> UniquePatients { get; } = [];
    public Dictionary<Guid, FollowUpPatientIdentityInput> Patients { get; } = [];
    public HashSet<Guid> ReferencedUniquePatientIds { get; } = [];
    public HashSet<Guid> ReferencedPatientIds { get; } = [];
}

internal sealed record FollowUpUniquePatientIdentityMap(
    Guid SourceUniquePatientId,
    Guid TargetUniquePatientId,
    string MatchBasis,
    bool TargetExisted);

internal sealed record FollowUpPatientIdentityMap(
    Guid SourcePatientId,
    Guid TargetPatientId,
    Guid? SourceUniquePatientId,
    Guid? TargetUniquePatientId,
    string MatchBasis,
    bool PreserveTargetPatient,
    string? OriginalSourceType);

internal sealed record FollowUpIdentityAdaptedRow(
    string Row,
    FollowUpPatientIdentityMap? Patient,
    bool SkipWrite);

internal sealed class FollowUpPatientIdentityPlan(
    IReadOnlyDictionary<Guid, FollowUpUniquePatientIdentityMap> uniquePatients,
    IReadOnlyDictionary<Guid, FollowUpPatientIdentityMap> patients)
{
    public IReadOnlyDictionary<Guid, FollowUpUniquePatientIdentityMap> UniquePatients { get; } = uniquePatients;
    public IReadOnlyDictionary<Guid, FollowUpPatientIdentityMap> Patients { get; } = patients;

    public IReadOnlyDictionary<Guid, Guid> PatientIdMap => Patients.ToDictionary(
        item => item.Key,
        item => item.Value.TargetPatientId);

    public FollowUpEdcScopePlan Remap(FollowUpEdcScopePlan plan) => new(
        plan.PatientIds.Select(MapPatientId).Distinct().ToArray(),
        plan.EdcProjectIds,
        plan.MappedPatientIds,
        plan.ShouldApply);

    public FollowUpIdentityAdaptedRow AdaptRow(string schema, string table, string row)
    {
        var kind = ResolveTableKind(schema, table);
        if (kind == IdentityTableKind.None)
            return new FollowUpIdentityAdaptedRow(row, null, false);

        var document = JsonNode.Parse(row) as JsonObject
                       ?? throw new InvalidDataException($"{schema}.{table} 的患者身份数据不是 JSON 对象。");
        FollowUpPatientIdentityMap? patient = null;
        var skipWrite = false;

        switch (kind)
        {
            case IdentityTableKind.UniquePatient:
            {
                var sourceId = ReadRequiredGuid(document, "id", "唯一患者");
                if (UniquePatients.TryGetValue(sourceId, out var mapping))
                {
                    document["id"] = mapping.TargetUniquePatientId;
                    skipWrite = mapping.TargetExisted;
                }
                break;
            }
            case IdentityTableKind.Patient:
            {
                var sourcePatientId = ReadRequiredGuid(document, "id", "患者");
                if (!Patients.TryGetValue(sourcePatientId, out patient))
                    throw IdentityConflict("患者 ID 缺少已验证的院端映射，已阻断整包导入。");
                document["id"] = patient.TargetPatientId;
                if (patient.TargetUniquePatientId.HasValue)
                    document["unique_id"] = patient.TargetUniquePatientId.Value;
                skipWrite = patient.PreserveTargetPatient;
                break;
            }
            case IdentityTableKind.PatientEvent:
                RemapGuid(document, "patient_id", Patients, mapping => mapping.TargetPatientId);
                RemapGuid(document, "unique_patient_id", UniquePatients, mapping => mapping.TargetUniquePatientId);
                break;
            case IdentityTableKind.PatientReference:
            case IdentityTableKind.Dynamic:
                RemapGuid(document, "patient_id", Patients, mapping => mapping.TargetPatientId);
                break;
        }

        return new FollowUpIdentityAdaptedRow(
            document.ToJsonString(FollowUpJson.Options),
            patient,
            skipWrite);
    }

    public bool IsEquivalentTo(FollowUpPatientIdentityPlan other)
    {
        if (UniquePatients.Count != other.UniquePatients.Count || Patients.Count != other.Patients.Count)
            return false;
        return UniquePatients.All(item => other.UniquePatients.TryGetValue(item.Key, out var value) && value == item.Value)
               && Patients.All(item => other.Patients.TryGetValue(item.Key, out var value) && value == item.Value);
    }

    private Guid MapPatientId(Guid sourcePatientId) =>
        Patients.TryGetValue(sourcePatientId, out var mapping)
            ? mapping.TargetPatientId
            : sourcePatientId;

    private static void RemapGuid<T>(
        JsonObject document,
        string propertyName,
        IReadOnlyDictionary<Guid, T> mappings,
        Func<T, Guid> selectTarget)
    {
        if (!TryReadGuid(document, propertyName, out var sourceId))
            return;
        if (!mappings.TryGetValue(sourceId, out var mapping))
            throw IdentityConflict($"字段 {propertyName} 缺少已验证的院端映射，已阻断整包导入。");
        document[propertyName] = selectTarget(mapping);
    }

    private static IdentityTableKind ResolveTableKind(string schema, string table) =>
        (schema.ToLowerInvariant(), table.ToLowerInvariant()) switch
        {
            ("public", "unique_patient") => IdentityTableKind.UniquePatient,
            ("public", "patient") => IdentityTableKind.Patient,
            ("care", "patient_event") => IdentityTableKind.PatientEvent,
            ("care", "patient_hospitalized") or ("care", "patient_outpatient") => IdentityTableKind.PatientReference,
            ("target", _) => IdentityTableKind.Dynamic,
            _ => IdentityTableKind.None
        };

    private enum IdentityTableKind
    {
        None,
        UniquePatient,
        Patient,
        PatientEvent,
        PatientReference,
        Dynamic
    }

    private static Guid ReadRequiredGuid(JsonObject document, string propertyName, string description) =>
        TryReadGuid(document, propertyName, out var value)
            ? value
            : throw new InvalidDataException($"{description}缺少有效的 {propertyName}。");

    private static bool TryReadGuid(JsonObject document, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        return document.TryGetPropertyValue(propertyName, out var node)
               && node is JsonValue jsonValue
               && jsonValue.TryGetValue<string>(out var text)
               && Guid.TryParse(text, out value);
    }

    private static FollowUpPackageException IdentityConflict(string message) =>
        new(FollowUpErrorCodes.PatientIdentityConflict, message);
}

public sealed class FollowUpPatientIdentityService(IConfiguration configuration)
{
    private static readonly string[] RequiredSourceMapColumns =
    [
        "source_patient_id", "target_patient_id", "source_unique_patient_id", "target_unique_patient_id",
        "identity_match_basis", "original_source_type", "hospital_code", "first_package_id",
        "last_package_id", "created_at", "updated_at"
    ];

    private readonly string _cubeConnectionString = configuration.GetConnectionString("CubeDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");
    private readonly string _dataSyncConnectionString = configuration.GetConnectionString("DataSyncDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'DataSyncDb'");

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await using (var dataSyncConnection = new NpgsqlConnection(_dataSyncConnectionString))
        {
            await dataSyncConnection.OpenAsync(cancellationToken);
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var command = new NpgsqlCommand("""
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'lhyy'
                  AND table_name = 'followup_patient_identity_map'
                """, dataSyncConnection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(0));

            var dataSyncMissing = RequiredSourceMapColumns.Where(column => !columns.Contains(column)).ToArray();
            if (dataSyncMissing.Length > 0)
                throw SchemaReview($"DataSyncDb 缺少 FollowUp 患者身份映射表或字段：{string.Join(", ", dataSyncMissing)}。请先执行 DataSyncDb 功能迁移。");
        }

        await using var cubeConnection = new NpgsqlConnection(_cubeConnectionString);
        await cubeConnection.OpenAsync(cancellationToken);
        var requirements = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["public.unique_patient"] = ["id", "sid_number", "name", "birthday", "gender"],
            ["public.patient"] = ["id", "unique_id", "hospital_id", "project_id"]
        };
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new NpgsqlCommand("""
            SELECT table_schema, table_name, column_name
            FROM information_schema.columns
            WHERE (table_schema, table_name) IN (
                ('public', 'unique_patient'),
                ('public', 'patient'))
            """, cubeConnection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                available.Add($"{reader.GetString(0)}.{reader.GetString(1)}.{reader.GetString(2)}");
        }

        var missing = requirements
            .SelectMany(item => item.Value.Select(column => $"{item.Key}.{column}"))
            .Where(column => !available.Contains(column))
            .ToArray();
        if (missing.Length > 0)
            throw SchemaReview($"CubeDb 缺少患者身份判定所需的既有业务字段：{string.Join(", ", missing)}。");

        await using var privilegeCommand = new NpgsqlCommand("""
            SELECT bool_and(has_column_privilege(current_user, table_name, column_name, 'SELECT'))
            FROM (VALUES
                ('public.unique_patient', 'id'),
                ('public.unique_patient', 'sid_number'),
                ('public.unique_patient', 'name'),
                ('public.unique_patient', 'birthday'),
                ('public.unique_patient', 'gender'),
                ('public.patient', 'id'),
                ('public.patient', 'unique_id'),
                ('public.patient', 'hospital_id'),
                ('public.patient', 'project_id'))
                AS required(table_name, column_name)
            """, cubeConnection);
        if (await privilegeCommand.ExecuteScalarAsync(cancellationToken) is not true)
            throw SchemaReview("CubeDb 导入账号缺少患者身份合并所需的 SELECT 列权限。");
    }

    internal async Task<FollowUpPatientIdentityScope> ReadScopeAsync(
        FollowUpVerifiedPackage package,
        FollowUpSchemaDecision? schemaDecision,
        CancellationToken cancellationToken)
    {
        var scope = new FollowUpPatientIdentityScope(package.Manifest.HospitalCode);
        foreach (var table in package.TableManifest.Where(item =>
                     item.Enabled && !item.Skipped && !string.IsNullOrWhiteSpace(item.ExportPath)))
        {
            var target = FollowUpSchemaDecisionProcessor.MapManifest(table, schemaDecision);
            var kind = ResolveScopeKind(target.Schema, target.TableName);
            if (kind == ScopeTableKind.None)
                continue;

            var filePath = SafeStagingPath(package.StagingPath, table.ExportPath!);
            if (string.IsNullOrWhiteSpace(table.FileHash))
                throw new InvalidDataException($"表 {table.Schema}.{table.TableName} 缺少导入文件 hash。");
            await using var snapshot = await FollowUpPackageImportService.OpenVerifiedImportSnapshotAsync(
                filePath,
                table.FileHash,
                cancellationToken);
            using var reader = new StreamReader(snapshot, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var mappedLine = FollowUpSchemaDecisionProcessor.MapRow(
                    line,
                    table.Schema,
                    table.TableName,
                    schemaDecision);
                AnalyzeRow(scope, kind, mappedLine);
            }
        }
        return scope;
    }

    internal async Task<FollowUpPatientIdentityPlan> PrepareAsync(
        FollowUpPatientIdentityScope scope,
        CancellationToken cancellationToken)
    {
        var aliases = await ReadAliasesAsync(scope, cancellationToken);
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await ResolveAsync(connection, null, scope, aliases, cancellationToken);
    }

    internal async Task<FollowUpPatientIdentityPlan> VerifyWithLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FollowUpPatientIdentityScope scope,
        FollowUpPatientIdentityPlan expected,
        CancellationToken cancellationToken)
    {
        await using (var lockCommand = new NpgsqlCommand("""
            LOCK TABLE public.unique_patient, public.patient IN SHARE MODE
            """, connection, transaction))
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);

        var aliases = await ReadAliasesAsync(scope, cancellationToken);
        var actual = await ResolveAsync(connection, transaction, scope, aliases, cancellationToken);
        if (!actual.IsEquivalentTo(expected))
            throw IdentityConflict("院端患者身份映射在备份后发生变化，已阻断整包导入；请确认期间是否有患者资料写入后重试。");
        return actual;
    }

    private async Task<FollowUpPatientIdentityPlan> ResolveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        FollowUpPatientIdentityScope scope,
        IReadOnlyCollection<PersistedPatientAlias> aliases,
        CancellationToken cancellationToken)
    {
        var uniqueMaps = await ResolveUniquePatientsAsync(connection, transaction, scope, aliases, cancellationToken);
        var patientMaps = await ResolvePatientsAsync(connection, transaction, scope, aliases, uniqueMaps, cancellationToken);
        return new FollowUpPatientIdentityPlan(uniqueMaps, patientMaps);
    }

    private async Task<List<PersistedPatientAlias>> ReadAliasesAsync(
        FollowUpPatientIdentityScope scope,
        CancellationToken cancellationToken)
    {
        var patientIds = scope.ReferencedPatientIds.Concat(scope.Patients.Keys).Distinct().ToArray();
        var uniquePatientIds = scope.ReferencedUniquePatientIds
            .Concat(scope.UniquePatients.Keys)
            .Concat(scope.Patients.Values.Where(item => item.SourceUniquePatientId.HasValue).Select(item => item.SourceUniquePatientId!.Value))
            .Distinct()
            .ToArray();
        if (patientIds.Length == 0 && uniquePatientIds.Length == 0)
            return [];

        await using var connection = new NpgsqlConnection(_dataSyncConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PersistedPatientAliasSql, connection);
        command.Parameters.AddWithValue("hospitalCode", scope.HospitalCode);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("patientIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = patientIds
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("uniquePatientIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = uniquePatientIds
        });
        var result = new List<PersistedPatientAlias>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PersistedPatientAlias(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return result;
    }

    internal const string PersistedPatientAliasSql = """
            SELECT source_patient_id, target_patient_id, source_unique_patient_id, target_unique_patient_id,
                   identity_match_basis, original_source_type
            FROM lhyy.followup_patient_identity_map
            WHERE hospital_code = @hospitalCode
              AND (
                  source_patient_id = ANY(@patientIds)
                  OR source_unique_patient_id = ANY(@uniquePatientIds))
            """;

    private static async Task<Dictionary<Guid, FollowUpUniquePatientIdentityMap>> ResolveUniquePatientsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        FollowUpPatientIdentityScope scope,
        IReadOnlyCollection<PersistedPatientAlias> aliases,
        CancellationToken cancellationToken)
    {
        var sourceIds = scope.ReferencedUniquePatientIds
            .Concat(scope.UniquePatients.Keys)
            .Concat(scope.Patients.Values.Where(item => item.SourceUniquePatientId.HasValue).Select(item => item.SourceUniquePatientId!.Value))
            .Distinct()
            .ToArray();
        var result = new Dictionary<Guid, FollowUpUniquePatientIdentityMap>();

        foreach (var sourceId in sourceIds)
        {
            var persisted = aliases
                .Where(item => item.SourceUniquePatientId == sourceId && item.TargetUniquePatientId.HasValue)
                .GroupBy(item => item.TargetUniquePatientId!.Value)
                .Select(group => new
                {
                    Target = group.Key,
                    MatchBasis = group
                        .Select(item => item.MatchBasis)
                        .Distinct()
                        .OrderBy(MatchBasisRank)
                        .First()
                })
                .ToArray();
            if (persisted.Length > 1)
                throw IdentityConflict("持久患者映射中同一云端 unique_patient 指向多个院端 ID，已阻断整包导入。");
            if (persisted.Length > 0)
            {
                result[sourceId] = new FollowUpUniquePatientIdentityMap(
                    sourceId,
                    persisted[0].Target,
                    persisted[0].MatchBasis,
                    true);
            }
        }

        var unresolved = scope.UniquePatients.Values
            .Where(item => !result.ContainsKey(item.SourceUniquePatientId))
            .ToArray();
        if (unresolved.Length > 0)
        {
            var json = JsonSerializer.Serialize(unresolved.Select(item => new
            {
                sourceId = item.SourceUniquePatientId,
                sidNumber = item.SidNumber,
                item.Name,
                birthday = item.Birthday,
                item.Gender
            }), FollowUpJson.Options);
            await using var command = new NpgsqlCommand(UniquePatientCandidateSql, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter("sources", NpgsqlDbType.Jsonb) { Value = json });
            var candidates = new List<UniquePatientCandidate>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    candidates.Add(new UniquePatientCandidate(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
            }

            foreach (var source in unresolved)
            {
                var matches = candidates.Where(item => item.SourceId == source.SourceUniquePatientId).ToArray();
                result[source.SourceUniquePatientId] = SelectUniquePatientMapping(
                    source.SourceUniquePatientId,
                    matches);
            }
        }

        foreach (var sourceId in sourceIds.Where(sourceId => !result.ContainsKey(sourceId)))
            result[sourceId] = new FollowUpUniquePatientIdentityMap(
                sourceId,
                sourceId,
                FollowUpPatientIdentityMatchBasis.Id,
                false);

        var requiredTargets = result.Values
            .Where(item => item.TargetExisted)
            .Select(item => item.TargetUniquePatientId)
            .Distinct()
            .ToArray();
        if (requiredTargets.Length > 0)
        {
            await using var targetCommand = new NpgsqlCommand("""
                SELECT id
                FROM public.unique_patient
                WHERE id = ANY(@targetIds)
                """, connection, transaction);
            targetCommand.Parameters.Add(new NpgsqlParameter<Guid[]>("targetIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = requiredTargets
            });
            var existingTargets = new HashSet<Guid>();
            await using var targetReader = await targetCommand.ExecuteReaderAsync(cancellationToken);
            while (await targetReader.ReadAsync(cancellationToken))
                existingTargets.Add(targetReader.GetGuid(0));
            if (requiredTargets.Any(targetId => !existingTargets.Contains(targetId)))
                throw IdentityConflict("持久患者映射指向的院端 unique_patient 已不存在，已阻断整包导入。");
        }

        var duplicatedTargets = result.Values
            .GroupBy(item => item.TargetUniquePatientId)
            .Where(group => group.Select(item => item.SourceUniquePatientId).Distinct().Count() > 1)
            .ToArray();
        if (duplicatedTargets.Length > 0)
            throw IdentityConflict("多个不同云端 unique_patient 被识别为同一院端自然人，已阻断整包导入；请先修复云端唯一患者数据。");
        return result;
    }

    internal static FollowUpUniquePatientIdentityMap SelectUniquePatientMapping(
        Guid sourceId,
        IReadOnlyCollection<UniquePatientCandidate> matches)
    {
        var exact = matches.FirstOrDefault(item => item.MatchBasis == FollowUpPatientIdentityMatchBasis.Id);
        if (exact is not null)
            return new FollowUpUniquePatientIdentityMap(sourceId, exact.TargetId, exact.MatchBasis, true);

        var natural = matches.DistinctBy(item => item.TargetId).ToArray();
        if (natural.Length > 1)
        {
            var rules = natural
                .Select(item => item.MatchBasis == FollowUpPatientIdentityMatchBasis.SidNumber
                    ? "身份证"
                    : "姓名+出生日期+性别")
                .Distinct()
                .Order()
                .ToArray();
            throw IdentityConflict($"唯一患者按{string.Join("、", rules)}规则在院端命中 {natural.Length} 条 unique_patient，已阻断整包导入；请先清理重复数据。");
        }

        return natural.Length == 1
            ? new FollowUpUniquePatientIdentityMap(sourceId, natural[0].TargetId, natural[0].MatchBasis, true)
            : new FollowUpUniquePatientIdentityMap(
                sourceId,
                sourceId,
                FollowUpPatientIdentityMatchBasis.Id,
                false);
    }

    private async Task<Dictionary<Guid, FollowUpPatientIdentityMap>> ResolvePatientsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        FollowUpPatientIdentityScope scope,
        IReadOnlyCollection<PersistedPatientAlias> aliases,
        IReadOnlyDictionary<Guid, FollowUpUniquePatientIdentityMap> uniqueMaps,
        CancellationToken cancellationToken)
    {
        var sourceIds = scope.ReferencedPatientIds.Concat(scope.Patients.Keys).Distinct().ToArray();
        var result = new Dictionary<Guid, FollowUpPatientIdentityMap>();
        var persistedBySource = aliases
            .Where(item => sourceIds.Contains(item.SourcePatientId))
            .GroupBy(item => item.SourcePatientId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (persistedBySource.Any(item => item.Value.Select(value => value.TargetPatientId).Distinct().Count() > 1))
            throw IdentityConflict("持久患者映射中同一云端 patient 指向多个院端 ID，已阻断整包导入。");

        var querySources = sourceIds.Select(sourceId =>
        {
            scope.Patients.TryGetValue(sourceId, out var source);
            var targetUniquePatientId = source?.SourceUniquePatientId is { } uniqueId
                                        && uniqueMaps.TryGetValue(uniqueId, out var uniqueMap)
                ? uniqueMap.TargetUniquePatientId
                : (Guid?)null;
            var persistedTarget = persistedBySource.TryGetValue(sourceId, out var persisted)
                ? persisted[0].TargetPatientId
                : (Guid?)null;
            return new
            {
                sourcePatientId = sourceId,
                persistedTargetPatientId = persistedTarget,
                targetUniquePatientId,
                hospitalId = source?.HospitalId,
                projectId = source?.ProjectId
            };
        }).ToArray();

        var rows = new List<PatientCandidate>();
        if (querySources.Length > 0)
        {
            var json = JsonSerializer.Serialize(querySources, FollowUpJson.Options);
            await using var command = new NpgsqlCommand(PatientCandidateSql, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter("sources", NpgsqlDbType.Jsonb) { Value = json });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PatientCandidate(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.GetGuid(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7)));
            }
        }

        foreach (var sourceId in sourceIds)
        {
            scope.Patients.TryGetValue(sourceId, out var source);
            var targetUniquePatientId = source?.SourceUniquePatientId is { } sourceUniquePatientId
                                        && uniqueMaps.TryGetValue(sourceUniquePatientId, out var uniqueMap)
                ? uniqueMap.TargetUniquePatientId
                : (Guid?)null;
            var candidates = rows.Where(item => item.SourcePatientId == sourceId).ToArray();

            if (persistedBySource.TryGetValue(sourceId, out var persisted))
            {
                var alias = persisted[0];
                var target = candidates.SingleOrDefault(item => item.IsPersistedTarget)
                             ?? throw IdentityConflict("持久患者映射指向的院端 patient 已不存在，已阻断整包导入。");
                if (source is not null
                    && (target.UniquePatientId != targetUniquePatientId
                        || target.HospitalId != source.HospitalId
                        || target.ProjectId != source.ProjectId))
                    throw IdentityConflict("持久患者映射与院端 unique_id+hospital_id+project_id 不一致，已阻断整包导入。");
                result[sourceId] = new FollowUpPatientIdentityMap(
                    sourceId,
                    alias.TargetPatientId,
                    source?.SourceUniquePatientId ?? alias.SourceUniquePatientId,
                    targetUniquePatientId ?? alias.TargetUniquePatientId,
                    alias.MatchBasis,
                    alias.TargetPatientId != sourceId,
                    source?.OriginalSourceType ?? alias.OriginalSourceType);
                continue;
            }

            if (source is null)
                throw BootstrapRequired(
                    "包内患者引用既没有 patient 数据，也没有当前医院的持久来源映射；请先执行旧版本映射迁移，无法迁移时执行 RecoveryBaseline。");

            var matchBasis = source.SourceUniquePatientId.HasValue
                             && uniqueMaps.TryGetValue(source.SourceUniquePatientId.Value, out var patientUniqueMap)
                ? patientUniqueMap.MatchBasis
                : FollowUpPatientIdentityMatchBasis.Id;
            result[sourceId] = SelectPatientMapping(source, targetUniquePatientId, matchBasis, candidates);
        }

        if (result.Values.GroupBy(item => item.TargetPatientId).Any(group => group.Select(item => item.SourcePatientId).Distinct().Count() > 1))
            throw IdentityConflict("多个云端 patient 被映射到同一院端患者明细，已阻断整包导入。");
        await EnsureTargetAliasesAvailableAsync(scope.HospitalCode, result.Values, cancellationToken);
        return result;
    }

    private async Task EnsureTargetAliasesAvailableAsync(
        string hospitalCode,
        IReadOnlyCollection<FollowUpPatientIdentityMap> mappings,
        CancellationToken cancellationToken)
    {
        var targetIds = mappings.Select(item => item.TargetPatientId).Distinct().ToArray();
        if (targetIds.Length == 0)
            return;

        await using var connection = new NpgsqlConnection(_dataSyncConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(TargetPatientAliasSql, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("targetPatientIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = targetIds
        });
        var existing = new List<(Guid TargetPatientId, Guid SourcePatientId)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            existing.Add((reader.GetGuid(0), reader.GetGuid(1)));

        if (mappings.Any(mapping => existing.Any(item =>
                item.TargetPatientId == mapping.TargetPatientId
                && item.SourcePatientId != mapping.SourcePatientId)))
            throw IdentityConflict("院端 patient 已绑定其他云端患者来源，已阻断整包导入。");
    }

    internal const string TargetPatientAliasSql = """
            SELECT target_patient_id, source_patient_id
            FROM lhyy.followup_patient_identity_map
            WHERE hospital_code = @hospitalCode
              AND target_patient_id = ANY(@targetPatientIds)
            """;

    internal static FollowUpPatientIdentityMap SelectPatientMapping(
        FollowUpPatientIdentityInput source,
        Guid? targetUniquePatientId,
        string matchBasis,
        IReadOnlyCollection<PatientCandidate> candidates)
    {
        var scoped = candidates.Where(item => item.IsScopeMatch).DistinctBy(item => item.TargetPatientId).ToArray();
        if (scoped.Length > 1)
            throw IdentityConflict($"患者按 unique_id+hospital_id+project_id 在院端命中 {scoped.Length} 条 patient，已阻断整包导入；请先清理重复数据。");
        var exactMatches = candidates.Where(item => item.IsExactId).DistinctBy(item => item.TargetPatientId).ToArray();
        if (exactMatches.Length > 1)
            throw IdentityConflict("云端 patient.id 在院端命中多条记录，已阻断整包导入。");
        var exact = exactMatches.SingleOrDefault();
        PatientCandidate? selected = null;
        if (scoped.Length == 1)
        {
            selected = scoped[0];
            if (exact is not null && exact.TargetPatientId != selected.TargetPatientId)
                throw IdentityConflict("云端 patient.id 已被院端其他患者范围占用，已阻断整包导入。");
        }
        else if (exact is not null)
        {
            if (exact.UniquePatientId != targetUniquePatientId
                || exact.HospitalId != source.HospitalId
                || exact.ProjectId != source.ProjectId)
                throw IdentityConflict("云端 patient.id 已被院端其他 unique_id、医院或课题占用，已阻断整包导入。");
            selected = exact;
        }

        return new FollowUpPatientIdentityMap(
            source.SourcePatientId,
            selected?.TargetPatientId ?? source.SourcePatientId,
            source.SourceUniquePatientId,
            targetUniquePatientId,
            matchBasis,
            selected is not null && selected.TargetPatientId != source.SourcePatientId,
            source.OriginalSourceType);
    }

    private static void AnalyzeRow(FollowUpPatientIdentityScope scope, ScopeTableKind kind, string row)
    {
        using var document = JsonDocument.Parse(row);
        var root = document.RootElement;
        switch (kind)
        {
            case ScopeTableKind.UniquePatient:
            {
                var id = ReadRequiredGuid(root, "id", "唯一患者");
                scope.UniquePatients[id] = new FollowUpUniquePatientIdentityInput(
                    id,
                    ReadString(root, "sid_number"),
                    ReadString(root, "name"),
                    ReadDate(root, "birthday"),
                    ReadString(root, "gender"));
                scope.ReferencedUniquePatientIds.Add(id);
                break;
            }
            case ScopeTableKind.Patient:
            {
                var id = ReadRequiredGuid(root, "id", "患者");
                var uniqueId = ReadGuid(root, "unique_id");
                scope.Patients[id] = new FollowUpPatientIdentityInput(
                    id,
                    uniqueId,
                    ReadRequiredGuid(root, "hospital_id", "患者"),
                    ReadRequiredGuid(root, "project_id", "患者"),
                    ReadString(root, "source_type"));
                scope.ReferencedPatientIds.Add(id);
                if (uniqueId.HasValue) scope.ReferencedUniquePatientIds.Add(uniqueId.Value);
                break;
            }
            case ScopeTableKind.PatientEvent:
                AddGuid(root, "patient_id", scope.ReferencedPatientIds);
                AddGuid(root, "unique_patient_id", scope.ReferencedUniquePatientIds);
                break;
            case ScopeTableKind.PatientReference:
            case ScopeTableKind.Dynamic:
                AddGuid(root, "patient_id", scope.ReferencedPatientIds);
                break;
        }
    }

    private static ScopeTableKind ResolveScopeKind(string schema, string table) =>
        (schema.ToLowerInvariant(), table.ToLowerInvariant()) switch
        {
            ("public", "unique_patient") => ScopeTableKind.UniquePatient,
            ("public", "patient") => ScopeTableKind.Patient,
            ("care", "patient_event") => ScopeTableKind.PatientEvent,
            ("care", "patient_hospitalized") or ("care", "patient_outpatient") => ScopeTableKind.PatientReference,
            ("target", _) => ScopeTableKind.Dynamic,
            _ => ScopeTableKind.None
        };

    private static void AddGuid(JsonElement row, string propertyName, ISet<Guid> destination)
    {
        if (ReadGuid(row, propertyName) is { } value)
            destination.Add(value);
    }

    private static Guid ReadRequiredGuid(JsonElement row, string propertyName, string description) =>
        ReadGuid(row, propertyName)
        ?? throw new InvalidDataException($"{description}缺少有效的 {propertyName}。");

    private static Guid? ReadGuid(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var result)
            ? result
            : null;

    private static string? ReadString(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateOnly? ReadDate(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateOnly.TryParse(value.GetString(), out var result)
            ? result
            : null;

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

    private static FollowUpPackageException IdentityConflict(string message) =>
        new(FollowUpErrorCodes.PatientIdentityConflict, message);

    private static FollowUpPackageException BootstrapRequired(string message) =>
        new(FollowUpErrorCodes.PatientIdentityBootstrapRequired, message);

    private static int MatchBasisRank(string matchBasis) => matchBasis switch
    {
        FollowUpPatientIdentityMatchBasis.Id => 0,
        FollowUpPatientIdentityMatchBasis.SidNumber => 1,
        FollowUpPatientIdentityMatchBasis.Demographics => 2,
        _ => 3
    };

    internal const string UniquePatientCandidateSql = """
        WITH source AS (
            SELECT input."sourceId" AS source_id,
                   NULLIF(UPPER(BTRIM(input."sidNumber")), '') AS sid_number,
                   NULLIF(BTRIM(input.name), '') AS name,
                   input.birthday,
                   NULLIF(BTRIM(input.gender), '') AS gender
            FROM jsonb_to_recordset(@sources::jsonb) AS input(
                "sourceId" uuid,
                "sidNumber" text,
                name text,
                birthday date,
                gender text)
        ), target AS (
            SELECT patient.*,
                   NULLIF(UPPER(BTRIM(patient.sid_number)), '') AS normalized_sid_number,
                   NULLIF(BTRIM(patient.name), '') AS normalized_name,
                   NULLIF(BTRIM(patient.gender), '') AS normalized_gender
            FROM public.unique_patient patient
        )
        SELECT source.source_id,
               target.id,
               CASE
                   WHEN target.id = source.source_id THEN 'Id'
                   WHEN source.sid_number IS NOT NULL
                        AND target.normalized_sid_number IS NOT NULL
                        AND source.sid_number = target.normalized_sid_number THEN 'SidNumber'
                   ELSE 'Demographics'
               END AS match_basis
        FROM source
        INNER JOIN target ON target.id = source.source_id
          OR (
              target.id <> source.source_id
              AND (
                  (source.sid_number IS NOT NULL
                   AND target.normalized_sid_number IS NOT NULL
                   AND source.sid_number = target.normalized_sid_number)
                  OR (
                      (source.sid_number IS NULL OR target.normalized_sid_number IS NULL)
                      AND source.name IS NOT NULL
                      AND source.birthday IS NOT NULL
                      AND source.gender IS NOT NULL
                      AND target.normalized_name = source.name
                      AND target.birthday = source.birthday
                      AND target.normalized_gender = source.gender
                  )
              )
          )
        """;

    internal const string PatientCandidateSql = """
        WITH source AS (
            SELECT input."sourcePatientId" AS source_patient_id,
                   input."persistedTargetPatientId" AS persisted_target_patient_id,
                   input."targetUniquePatientId" AS target_unique_patient_id,
                   input."hospitalId" AS hospital_id,
                   input."projectId" AS project_id
            FROM jsonb_to_recordset(@sources::jsonb) AS input(
                "sourcePatientId" uuid,
                "persistedTargetPatientId" uuid,
                "targetUniquePatientId" uuid,
                "hospitalId" uuid,
                "projectId" uuid)
        )
        SELECT source.source_patient_id,
               patient.id,
               patient.unique_id,
               patient.hospital_id,
               patient.project_id,
               patient.id = source.source_patient_id AS is_exact_id,
               patient.id = source.persisted_target_patient_id AS is_persisted_target,
               source.target_unique_patient_id IS NOT NULL
                 AND patient.unique_id = source.target_unique_patient_id
                 AND patient.hospital_id = source.hospital_id
                 AND patient.project_id = source.project_id AS is_scope_match
        FROM source
        INNER JOIN public.patient patient
          ON patient.id = source.source_patient_id
          OR patient.id = source.persisted_target_patient_id
          OR (
              source.target_unique_patient_id IS NOT NULL
              AND patient.unique_id = source.target_unique_patient_id
              AND patient.hospital_id = source.hospital_id
              AND patient.project_id = source.project_id)
        """;

    private sealed record PersistedPatientAlias(
        Guid SourcePatientId,
        Guid TargetPatientId,
        Guid? SourceUniquePatientId,
        Guid? TargetUniquePatientId,
        string MatchBasis,
        string? OriginalSourceType);

    internal sealed record UniquePatientCandidate(Guid SourceId, Guid TargetId, string MatchBasis);

    internal sealed record PatientCandidate(
        Guid SourcePatientId,
        Guid TargetPatientId,
        Guid? UniquePatientId,
        Guid HospitalId,
        Guid ProjectId,
        bool IsExactId,
        bool IsPersistedTarget,
        bool IsScopeMatch);

    private enum ScopeTableKind
    {
        None,
        UniquePatient,
        Patient,
        PatientEvent,
        PatientReference,
        Dynamic
    }
}
