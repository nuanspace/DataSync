using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataSync.Common.FollowUp;

public static class FollowUpJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public static class FollowUpErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string PackageNotFound = "PACKAGE_NOT_FOUND";
    public const string PackageNotAvailable = "PACKAGE_NOT_AVAILABLE";
    public const string StreamInterrupted = "STREAM_INTERRUPTED";
    public const string PackageIntegrityFailed = "PACKAGE_INTEGRITY_FAILED";
    public const string ContractVersionUnsupported = "CONTRACT_VERSION_UNSUPPORTED";
    public const string SchemaReviewRequired = "SCHEMA_REVIEW_REQUIRED";
    public const string PatientIdentityConflict = "PATIENT_IDENTITY_CONFLICT";
    public const string PatientIdentityBootstrapRequired = "PATIENT_IDENTITY_BOOTSTRAP_REQUIRED";
    public const string InternalError = "INTERNAL_ERROR";
}

public sealed class FollowUpPackageException(string errorCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class FollowUpRelayRequest
{
    public string ProtocolVersion { get; init; } = "1.0";
    public string Operation { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public object Request { get; init; } = new { };

    public static FollowUpRelayRequest Create(
        string operation,
        string token,
        object request,
        TimeProvider? timeProvider = null,
        TimeSpan? requestWindow = null)
    {
        var issuedAt = (timeProvider ?? TimeProvider.System).GetLocalNow();
        return new FollowUpRelayRequest
        {
            Operation = operation,
            RequestId = Guid.NewGuid().ToString("N"),
            Token = token,
            Nonce = Guid.NewGuid().ToString("N"),
            IssuedAt = issuedAt,
            ExpiresAt = issuedAt.Add(requestWindow ?? TimeSpan.FromMinutes(5)),
            Request = request
        };
    }
}

public sealed class FollowUpProtocolResponse<T>
{
    public string ProtocolVersion { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public T? Data { get; set; }
}

public sealed class FollowUpPackageListData
{
    public List<FollowUpPackageSummary> Packages { get; set; } = [];
}

public sealed class FollowUpPackageSummary
{
    public string PackageId { get; set; } = string.Empty;
    public long SequenceNo { get; set; }
    public string PackageType { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? FromWatermark { get; set; }
    public DateTime? ToWatermark { get; set; }
    public string? PreviousPackageId { get; set; }
    public string? RelatedPackageId { get; set; }
    public long SizeBytes { get; set; }
    public string? PackageHash { get; set; }
    public string SchemaDiffLevel { get; set; } = "Compatible";
    public bool RequiresSchemaReview { get; set; }
    public DateTime GeneratedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public sealed class FollowUpPackageAck
{
    public string AckId { get; set; } = string.Empty;
    public string HospitalCode { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string AckStatus { get; set; } = string.Empty;
    public string ImporterVersion { get; set; } = string.Empty;
    public string ReceivedHash { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public JsonElement Detail { get; set; }
}

public sealed class FollowUpEncryptedEnvelope
{
    public string ProtocolVersion { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string HospitalCode { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string KeyWrapAlgorithm { get; set; } = string.Empty;
    public string PayloadCipherAlgorithm { get; set; } = string.Empty;
    public string PayloadMacAlgorithm { get; set; } = string.Empty;
    public string SignatureAlgorithm { get; set; } = string.Empty;
    public string WrappedKeyMaterial { get; set; } = string.Empty;
    public string Iv { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public string PayloadHmacSha256 { get; set; } = string.Empty;
    public long PlaintextLength { get; set; }
    public long PayloadLength { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}

public static class FollowUpEnvelopeParser
{
    private static readonly string[] ExpectedProperties =
    [
        "protocolVersion", "packageId", "hospitalCode", "keyId", "keyWrapAlgorithm",
        "payloadCipherAlgorithm", "payloadMacAlgorithm", "signatureAlgorithm",
        "wrappedKeyMaterial", "iv", "payloadSha256", "payloadHmacSha256",
        "plaintextLength", "payloadLength", "generatedAt"
    ];

    public static FollowUpEncryptedEnvelope ParseAndValidate(ReadOnlySpan<byte> envelopeBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(envelopeBytes.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw IntegrityError("envelope 根节点必须是对象。");

            var actualProperties = document.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
            if (!actualProperties.SequenceEqual(ExpectedProperties, StringComparer.Ordinal))
                throw IntegrityError("envelope 字段集合或顺序不符合协议。");

            var envelope = JsonSerializer.Deserialize<FollowUpEncryptedEnvelope>(envelopeBytes, FollowUpJson.Options)
                ?? throw IntegrityError("envelope 内容为空。");
            if (envelope.ProtocolVersion != "1.0"
                || envelope.KeyWrapAlgorithm != "RSA-OAEP-SHA256"
                || envelope.PayloadCipherAlgorithm != "AES-256-CBC"
                || envelope.PayloadMacAlgorithm != "HMAC-SHA256"
                || envelope.SignatureAlgorithm != "RSA-PSS-SHA256"
                || string.IsNullOrWhiteSpace(envelope.PackageId)
                || string.IsNullOrWhiteSpace(envelope.HospitalCode)
                || string.IsNullOrWhiteSpace(envelope.KeyId)
                || envelope.PlaintextLength < 0
                || envelope.PayloadLength < 0)
            {
                throw IntegrityError("envelope 协议字段无效。");
            }

            return envelope;
        }
        catch (FollowUpPackageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new FollowUpPackageException(
                FollowUpErrorCodes.PackageIntegrityFailed,
                "envelope 解析失败。",
                ex);
        }
    }

    private static FollowUpPackageException IntegrityError(string message) =>
        new(FollowUpErrorCodes.PackageIntegrityFailed, message);
}

public sealed record FollowUpPackageChainRequest(
    string PackageType,
    string? PreviousPackageId,
    string? RelatedPackageId,
    long SequenceNo,
    string? CurrentMainChainHead,
    bool RelatedPackageExists,
    bool RelatedPackageImported);

public sealed record FollowUpPackageChainResult(
    bool CanImport,
    bool AdvancesMainChain,
    string? ErrorCode,
    string? Message);

public static class FollowUpPackageChain
{
    public static FollowUpPackageChainResult Evaluate(FollowUpPackageChainRequest request)
    {
        return request.PackageType switch
        {
            "Baseline" => new(true, true, null, null),
            "Incremental" when string.Equals(request.PreviousPackageId, request.CurrentMainChainHead, StringComparison.Ordinal)
                => new(true, true, null, null),
            "Incremental" => Waiting("增量包前驱尚未成功导入。"),
            "Supplement" when request.RelatedPackageImported => new(true, false, null, null),
            "Supplement" => Waiting("补充包关联的原包尚未成功导入。"),
            "Replacement" when request.RelatedPackageImported
                => new(false, false, FollowUpErrorCodes.PackageNotAvailable, "被替代包已经成功导入。"),
            "Replacement" when request.RelatedPackageExists
                               && string.Equals(request.PreviousPackageId, request.CurrentMainChainHead, StringComparison.Ordinal)
                => new(true, true, null, null),
            "Replacement" => Waiting("替代包前驱尚未成功导入。"),
            _ => new(false, false, FollowUpErrorCodes.InvalidRequest, "未知包类型。")
        };
    }

    private static FollowUpPackageChainResult Waiting(string message) =>
        new(false, false, FollowUpErrorCodes.PackageNotAvailable, message);
}
