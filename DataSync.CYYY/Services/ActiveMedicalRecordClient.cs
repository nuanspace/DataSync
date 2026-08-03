using System.Text.Json;
using DataSync.CYYY.Models;

namespace DataSync.CYYY.Services;

/// <summary>
/// 从 LHYY 拉取 Active 病历列表。
/// </summary>
public class ActiveMedicalRecordClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ActiveMedicalRecordClient> _logger;

    public ActiveMedicalRecordClient(
        IHttpClientFactory httpClientFactory,
        ILogger<ActiveMedicalRecordClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ActiveMedicalRecordBatch> GetActiveRecordsAsync(
        ActiveSyncTask task,
        long? cursor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.ActiveRecordsUrl))
            throw new InvalidOperationException("Active 病历来源地址不能为空");

        var url = BuildUrl(task, cursor);
        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var container = ResolveDataElement(doc.RootElement);
        var items = ResolveItemsElement(container);
        if (items.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Active 病历接口返回格式不正确，地址 {Url}", url);
            return new ActiveMedicalRecordBatch();
        }

        var result = new List<ActiveMedicalRecordInfo>();
        foreach (var item in items.EnumerateArray())
        {
            var inpatientNo = ReadString(item, "inpatientNo", "inpatient_no", "INP_NO");
            if (string.IsNullOrWhiteSpace(inpatientNo))
                continue;

            result.Add(new ActiveMedicalRecordInfo
            {
                Cursor = ReadLong(item, "cursor", "id"),
                Mrn = ReadString(item, "mrn", "medicalRecordNumber", "medical_record_number"),
                InpatientNo = inpatientNo,
                VisitNo = ReadNullableString(item, "visitNo", "visit_no"),
                AdmissionTime = ReadNullableDateTime(item, "admissionTime", "admission_time"),
                PatientId = ReadNullableGuid(item, "patientId", "patient_id"),
                EventId = ReadNullableGuid(item, "eventId", "event_id")
            });
        }

        return new ActiveMedicalRecordBatch
        {
            Items = result,
            NextCursor = ReadNullableLong(container, "nextCursor", "next_cursor")
        };
    }

    private static string BuildUrl(ActiveSyncTask task, long? cursor)
    {
        var parameters = new List<string>
        {
            $"limit={Math.Max(1, task.CaseBatchSize)}"
        };

        if (!string.IsNullOrWhiteSpace(task.IntegrationProjectCode))
            parameters.Add($"integrationProjectCode={Uri.EscapeDataString(task.IntegrationProjectCode)}");
        if (cursor.HasValue)
            parameters.Add($"cursor={cursor.Value}");

        var separator = task.ActiveRecordsUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{task.ActiveRecordsUrl}{separator}{string.Join("&", parameters)}";
    }

    private static JsonElement ResolveDataElement(JsonElement root)
    {
        if (TryGetProperty(root, out var data, "data"))
            return data;

        return root;
    }

    private static JsonElement ResolveItemsElement(JsonElement container)
    {
        if (container.ValueKind == JsonValueKind.Array)
            return container;

        return TryGetProperty(container, out var items, "items", "records") ? items : default;
    }

    private static string ReadString(JsonElement element, params string[] names)
        => ReadNullableString(element, names) ?? "";

    private static string? ReadNullableString(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
            return null;

        return value.ValueKind == JsonValueKind.Null ? null : value.ToString();
    }

    private static long ReadLong(JsonElement element, params string[] names)
    {
        var text = ReadNullableString(element, names);
        return long.TryParse(text, out var value) ? value : 0;
    }

    private static long? ReadNullableLong(JsonElement element, params string[] names)
    {
        var text = ReadNullableString(element, names);
        return long.TryParse(text, out var value) ? value : null;
    }

    private static DateTime? ReadNullableDateTime(JsonElement element, params string[] names)
    {
        var text = ReadNullableString(element, names);
        return DateTime.TryParse(text, out var value) ? value : null;
    }

    private static Guid? ReadNullableGuid(JsonElement element, params string[] names)
    {
        var text = ReadNullableString(element, names);
        return Guid.TryParse(text, out var value) ? value : null;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
