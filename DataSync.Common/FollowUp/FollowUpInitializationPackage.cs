using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataSync.Common.FollowUp;

public static class FollowUpInitializationPackageTypes
{
    public const string SchemaVersion = "s7sync.initialization.v1";
    public const string HospitalToDmz = "hospital-to-dmz";
    public const string DmzToCloud = "dmz-to-cloud";
    public const string CloudToDmz = "cloud-to-dmz";
    public const string DmzToHospital = "dmz-to-hospital";
}

public sealed class FollowUpInitializationPackage
{
    public string SchemaVersion { get; set; } = FollowUpInitializationPackageTypes.SchemaVersion;
    public string PackageType { get; set; } = string.Empty;
    public string HospitalId { get; set; } = string.Empty;
    public string HospitalCode { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CyyySshPublicKey { get; set; }
    public string? LhyyEncryptionPublicKey { get; set; }
    public string? LhyyEncryptionKeyId { get; set; }
    public string? DmzCloudSshPublicKey { get; set; }
    public string? DmzHostKnownHostsLine { get; set; }
    public string? CloudGatewayKnownHostsLine { get; set; }
    public string? CloudSigningPublicKey { get; set; }
    public string? CloudSigningKeyId { get; set; }
    public string? CloudDeviceToken { get; set; }
    public string? DmzInnerDeviceToken { get; set; }
}

public static class FollowUpInitializationPackageSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(FollowUpInitializationPackage package)
        => JsonSerializer.Serialize(package, Options) + Environment.NewLine;

    public static FollowUpInitializationPackage Deserialize(string json, string expectedType)
    {
        var package = JsonSerializer.Deserialize<FollowUpInitializationPackage>(json, Options)
                      ?? throw new InvalidDataException("初始化包内容为空。");
        if (!string.Equals(package.SchemaVersion, FollowUpInitializationPackageTypes.SchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"不支持的初始化包版本：{package.SchemaVersion}。");
        if (!string.Equals(package.PackageType, expectedType, StringComparison.Ordinal))
            throw new InvalidDataException($"初始化包方向错误，应为 {expectedType}。");
        return package;
    }
}
