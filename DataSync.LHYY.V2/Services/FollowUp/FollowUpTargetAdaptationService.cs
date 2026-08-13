using DataSync.Common.FollowUp;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataSync.LHYY.V2.Services.FollowUp;

internal sealed record FollowUpPatientEventFormMapping(
    Guid EventTypeDefinitionId,
    Guid FormSetId,
    string FormSetName);

/// <summary>
/// 将 FollowUp 数据包中的患者来源适配为 NTCare 现有查询约定，并在 CubeDb 中保留原始来源标识。
/// 患者事件的表单资格由云端导出范围统一判定；已有表单事件保持原样，
/// 无表单住院/门诊基础事件按目标项目的事件类型定义补齐表单链接。
/// 数据包、staging 文件和导入前备份均不经过此层修改。
/// </summary>
public sealed class FollowUpTargetAdaptationService
{
    internal static string AdaptRow(string targetSchema, string targetTable, string row)
    {
        if (IsPatientTable(targetSchema, targetTable))
        {
            var patient = ParseObject(row, "public.patient");
            patient["source_type"] = "care";
            return patient.ToJsonString(FollowUpJson.Options);
        }

        return row;
    }

    internal static string NormalizeFileQuestionValues(
        string row,
        IReadOnlyCollection<string> fileQuestionColumns,
        IReadOnlySet<string> packageAttachmentPaths)
    {
        try
        {
            return NormalizeFileQuestionValuesCore(row, fileQuestionColumns, packageAttachmentPaths);
        }
        catch (FollowUpPackageException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new FollowUpPackageException(
                FollowUpErrorCodes.PackageIntegrityFailed,
                ex.Message,
                ex);
        }
    }

    private static string NormalizeFileQuestionValuesCore(
        string row,
        IReadOnlyCollection<string> fileQuestionColumns,
        IReadOnlySet<string> packageAttachmentPaths)
    {
        if (fileQuestionColumns.Count == 0)
            return row;

        var document = ParseObject(row, "动态表文件题");
        foreach (var column in fileQuestionColumns)
        {
            if (!document.TryGetPropertyValue(column, out var value) || value is null)
                continue;

            if (value is JsonArray array)
            {
                document[column] = NormalizeFileArray(array, packageAttachmentPaths);
                continue;
            }

            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var storedValue))
                throw new InvalidDataException($"文件题字段 {column} 不是字符串或字符串数组。");
            if (string.IsNullOrWhiteSpace(storedValue))
                continue;

            if (storedValue.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                JsonNode? nested;
                try
                {
                    nested = JsonNode.Parse(storedValue);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"文件题字段 {column} 的 JSON 数组无效。", ex);
                }
                if (nested is not JsonArray nestedArray)
                    throw new InvalidDataException($"文件题字段 {column} 的 JSON 值不是数组。");
                document[column] = NormalizeFileArray(nestedArray, packageAttachmentPaths)
                    .ToJsonString(FollowUpJson.Options);
                continue;
            }

            document[column] = NormalizeFileReference(storedValue, packageAttachmentPaths);
        }
        return document.ToJsonString(FollowUpJson.Options);
    }

    private static JsonArray NormalizeFileArray(
        JsonArray array,
        IReadOnlySet<string> packageAttachmentPaths)
    {
        var normalized = new JsonArray();
        foreach (var item in array)
        {
            if (item is null)
                continue;
            if (item is not JsonValue value || !value.TryGetValue<string>(out var storedValue))
                throw new InvalidDataException("文件题数组只能包含字符串或 null。");
            if (string.IsNullOrWhiteSpace(storedValue))
                continue;
            normalized.Add(NormalizeFileReference(storedValue, packageAttachmentPaths));
        }
        return normalized;
    }

    private static string NormalizeFileReference(
        string storedValue,
        IReadOnlySet<string> packageAttachmentPaths)
    {
        var value = storedValue.Trim();
        if (value.Contains('\\'))
            throw new InvalidDataException("文件题附件路径不安全。");

        var hasUploadPrefix = value.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)
                              || value.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase);
        if (!hasUploadPrefix && value.StartsWith('/'))
            throw new InvalidDataException("文件题附件路径不属于上传目录。");
        if (!hasUploadPrefix && Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.Scheme is not ("http" or "https")
                || !absoluteUri.AbsolutePath.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("文件题附件路径不属于上传目录。");
            value = absoluteUri.AbsolutePath;
        }
        else
        {
            value = value.Split(['?', '#'], 2)[0];
        }

        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidDataException("文件题附件路径编码无效。", ex);
        }
        if (value.Contains('\\'))
            throw new InvalidDataException("文件题附件路径不安全。");

        var relative = value.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)
            ? value["/uploads/".Length..]
            : value.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase)
                ? value["uploads/".Length..]
                : value;
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
            throw new InvalidDataException("文件题附件路径不安全。");
        var normalized = string.Join('/', segments);
        if (!packageAttachmentPaths.Contains(normalized))
            throw new InvalidDataException($"文件题附件未包含在数据包中：{normalized}");
        return normalized;
    }

    internal async Task<string> AdaptRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string targetSchema,
        string targetTable,
        string row,
        IReadOnlyDictionary<Guid, string> basePatientEventTypes,
        IDictionary<(Guid ProjectId, string EventType), IReadOnlyList<FollowUpPatientEventFormMapping>> mappingCache,
        CancellationToken cancellationToken)
    {
        var adapted = AdaptRow(targetSchema, targetTable, row);
        if (!IsPatientEventTable(targetSchema, targetTable))
            return adapted;

        var patientEvent = ParseObject(row, "care.patient_event");
        var formSetId = ReadOptionalGuid(patientEvent, "form_set_id", "患者事件");
        if (formSetId.HasValue)
        {
            if (formSetId.Value == Guid.Empty)
                throw new FollowUpPackageException(
                    FollowUpErrorCodes.SchemaReviewRequired,
                    "患者事件 form_set_id 不能是空 UUID。");
            return row;
        }

        var eventId = ReadRequiredGuid(patientEvent, "id", "无表单患者事件");
        var projectId = ReadRequiredGuid(patientEvent, "project_id", "无表单患者事件");
        if (projectId == Guid.Empty)
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                "无表单患者事件缺少有效的 project_id，无法匹配目标表单。");

        var eventType = ReadString(patientEvent, "event_type");
        if (string.IsNullOrWhiteSpace(eventType))
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                "无表单患者事件缺少 event_type，无法匹配目标表单。");

        EnsureSupportedBasePatientEvent(eventId, eventType, basePatientEventTypes);
        var mappings = await GetPatientEventFormMappingsAsync(
            mappingCache,
            projectId,
            eventType,
            () => LoadPatientEventFormMappingsAsync(
                connection,
                transaction,
                projectId,
                eventType,
                cancellationToken));
        return ApplyPatientEventFormMapping(
            row,
            SelectPatientEventFormMapping(projectId, eventType, mappings));
    }

    internal static void EnsureSupportedBasePatientEvent(
        Guid eventId,
        string eventType,
        IReadOnlyDictionary<Guid, string> basePatientEventTypes)
    {
        if (eventType is not ("住院" or "门诊"))
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                $"无表单患者事件 {eventId} 的类型为“{eventType}”，仅支持住院或门诊基础事件。已阻止导入。");
        if (!basePatientEventTypes.TryGetValue(eventId, out var detailType))
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                $"无表单患者事件 {eventId} 在数据包内缺少对应的住院/门诊明细。已阻止导入。");
        if (!detailType.Equals(eventType, StringComparison.Ordinal))
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                $"无表单患者事件 {eventId} 的事件类型“{eventType}”与“{detailType}”明细关联类型不一致。已阻止导入。");
    }

    internal static async Task<IReadOnlyList<FollowUpPatientEventFormMapping>> GetPatientEventFormMappingsAsync(
        IDictionary<(Guid ProjectId, string EventType), IReadOnlyList<FollowUpPatientEventFormMapping>> cache,
        Guid projectId,
        string eventType,
        Func<Task<IReadOnlyList<FollowUpPatientEventFormMapping>>> loadAsync)
    {
        var key = (projectId, eventType);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var mappings = await loadAsync();
        cache[key] = mappings;
        return mappings;
    }

    internal static string ApplyPatientEventFormMapping(
        string row,
        FollowUpPatientEventFormMapping mapping)
    {
        var patientEvent = ParseObject(row, "care.patient_event");
        patientEvent["event_type_definition_id"] = mapping.EventTypeDefinitionId;
        patientEvent["form_set_id"] = mapping.FormSetId;
        patientEvent["form_set_name"] = mapping.FormSetName;
        return patientEvent.ToJsonString(FollowUpJson.Options);
    }

    internal static FollowUpPatientEventFormMapping SelectPatientEventFormMapping(
        Guid projectId,
        string eventType,
        IReadOnlyList<FollowUpPatientEventFormMapping> mappings)
    {
        if (mappings.Count == 1)
            return mappings[0];

        var reason = mappings.Count == 0 ? "未找到" : "找到多个";
        throw new FollowUpPackageException(
            FollowUpErrorCodes.SchemaReviewRequired,
            $"目标 CubeDb 项目 {projectId} 中{reason}事件类型“{eventType}”的有效表单映射，已阻止写入无表单患者事件。");
    }

    private static bool IsPatientTable(string schema, string table) =>
        schema.Equals("public", StringComparison.OrdinalIgnoreCase)
        && table.Equals("patient", StringComparison.OrdinalIgnoreCase);

    private static bool IsPatientEventTable(string schema, string table) =>
        schema.Equals("care", StringComparison.OrdinalIgnoreCase)
        && table.Equals("patient_event", StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<FollowUpPatientEventFormMapping>> LoadPatientEventFormMappingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid projectId,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT event_type.id,
                   event_type.form_set_id,
                   COALESCE(NULLIF(event_type.form_set_name, ''), form_set.name)
            FROM care.event_type_definition event_type
            JOIN form.form_form_set form_set ON form_set.id = event_type.form_set_id
            WHERE event_type.project_id = @project_id
              AND event_type.name = @event_type
              AND COALESCE(event_type.is_valid, TRUE) = TRUE
              AND event_type.form_set_id IS NOT NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("project_id", projectId);
        command.Parameters.AddWithValue("event_type", eventType);

        var mappings = new List<FollowUpPatientEventFormMapping>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            mappings.Add(new FollowUpPatientEventFormMapping(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2)));
        return mappings;
    }

    internal static Guid? ReadOptionalGuid(string row, string propertyName, string context)
        => ReadOptionalGuid(ParseObject(row, context), propertyName, context);

    private static Guid? ReadOptionalGuid(JsonObject row, string propertyName, string context)
    {
        if (!row.TryGetPropertyValue(propertyName, out var node) || node is null)
            return null;
        if (node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out var value))
            return value;

        throw new FollowUpPackageException(
            FollowUpErrorCodes.SchemaReviewRequired,
            $"{context}字段 {propertyName} 不是有效 UUID。");
    }

    private static Guid ReadRequiredGuid(JsonObject row, string propertyName, string context)
        => ReadOptionalGuid(row, propertyName, context)
           ?? throw new FollowUpPackageException(
               FollowUpErrorCodes.SchemaReviewRequired,
               $"{context}缺少字段 {propertyName}。");

    private static string ReadString(JsonObject row, string propertyName)
        => row.TryGetPropertyValue(propertyName, out var node)
           && node is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var value)
            ? value
            : string.Empty;

    private static JsonObject ParseObject(string row, string table) =>
        JsonNode.Parse(row)?.AsObject()
        ?? throw new InvalidDataException($"{table} 数据行不是 JSON 对象。");
}
