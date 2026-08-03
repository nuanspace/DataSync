using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataSync.CYYY.Models;

public class DynamicApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("pagination")]
    public DynamicApiPagination? Pagination { get; set; }
}

public class DynamicApiTokenData
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("expireAt")]
    public long ExpireAt { get; set; }
}

public class DynamicApiPagination
{
    [JsonPropertyName("pageNum")]
    public int PageNum { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

public class DynamicApiQueryRequest
{
    [JsonPropertyName("patientId")]
    public string PatientId { get; set; } = "";

    [JsonPropertyName("visitId")]
    public string VisitId { get; set; } = "";

    [JsonPropertyName("startTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndTime { get; set; }

    [JsonPropertyName("pageNum")]
    public int PageNum { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
}

public class DynamicApiTimeRangeQueryRequest
{
    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "";

    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = "";
}
