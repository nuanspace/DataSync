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

    public async Task<List<ActiveMedicalRecordInfo>> GetActiveRecordsAsync(
        ActiveSyncTask task,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.ActiveRecordsUrl))
            throw new InvalidOperationException("Active 病历来源地址不能为空");

        var url = BuildUrl(task);
        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var items = ResolveItemsElement(doc.RootElement);
        if (items.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Active 病历接口返回格式不正确，地址 {Url}", url);
            return [];
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

        return result;
    }

    private static string BuildUrl(ActiveSyncTask task)
    {
        var parameters = new List<string>
        {
            $"limit={Math.Max(1, task.CaseBatchSize)}"
        };

        if (!string.IsNullOrWhiteSpace(task.IntegrationProjectCode))
            parameters.Add($"integrationProjectCode={Uri.EscapeDataString(task.IntegrationProjectCode)}");

        var separator = task.ActiveRecordsUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{task.ActiveRecordsUrl}{separator}{string.Join("&", parameters)}";
    }

    private static JsonElement ResolveItemsElement(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;

        if (TryGetProperty(root, out var data, "data"))
        {
            if (data.ValueKind == JsonValueKind.Array)
                return data;

            if (TryGetProperty(data, out var items, "items", "records"))
                return items;
        }

        return TryGetProperty(root, out var rootItems, "items", "records") ? rootItems : default;
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
