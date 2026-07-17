using DataSync.Common.FollowUp;
using System.Text;
using System.Text.Json;

namespace DataSync.CYYY.Tests;

public sealed class FollowUpProtocolTests
{
    [Fact]
    public void 中继请求使用驼峰字段且不包含固定身份()
    {
        var request = FollowUpRelayRequest.Create("relay-list", "secret", new { afterSequenceNo = 8, limit = 100 });

        var json = JsonSerializer.Serialize(request, FollowUpJson.Options);

        Assert.Contains("\"protocolVersion\":\"1.0\"", json);
        Assert.Contains("\"operation\":\"relay-list\"", json);
        Assert.Contains("\"afterSequenceNo\":8", json);
        Assert.DoesNotContain("hospitalCode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Envelope字段顺序被改变时拒绝()
    {
        var json = """
            {"packageId":"p1","protocolVersion":"1.0","hospitalCode":"H1","keyId":"k1","keyWrapAlgorithm":"RSA-OAEP-SHA256","payloadCipherAlgorithm":"AES-256-CBC","payloadMacAlgorithm":"HMAC-SHA256","signatureAlgorithm":"RSA-PSS-SHA256","wrappedKeyMaterial":"AA==","iv":"AA==","payloadSha256":"00","payloadHmacSha256":"00","plaintextLength":1,"payloadLength":1,"generatedAt":"2026-07-14T10:00:00+08:00"}
            """;

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpEnvelopeParser.ParseAndValidate(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(FollowUpErrorCodes.PackageIntegrityFailed, exception.ErrorCode);
    }

    [Fact]
    public void Envelope包含额外字段时拒绝()
    {
        var json = CreateEnvelopeJson()[..^1] + ",\"unexpected\":true}";

        Assert.Throws<FollowUpPackageException>(() =>
            FollowUpEnvelopeParser.ParseAndValidate(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Envelope字段与算法正确时通过()
    {
        var envelope = FollowUpEnvelopeParser.ParseAndValidate(Encoding.UTF8.GetBytes(CreateEnvelopeJson()));

        Assert.Equal("p1", envelope.PackageId);
        Assert.Equal("H1", envelope.HospitalCode);
    }

    [Fact]
    public void Incremental只按前驱判断而不要求序号连续()
    {
        var result = FollowUpPackageChain.Evaluate(new FollowUpPackageChainRequest(
            "Incremental", "p20", null, 25, "p20", false, false));

        Assert.True(result.CanImport);
        Assert.True(result.AdvancesMainChain);
    }

    [Fact]
    public void Supplement关联包尚未导入时拒绝()
    {
        var result = FollowUpPackageChain.Evaluate(new FollowUpPackageChainRequest(
            "Supplement", null, "p12", 30, "p20", true, false));

        Assert.False(result.CanImport);
        Assert.False(result.AdvancesMainChain);
    }

    [Fact]
    public void Supplement关联包已导入时允许且不推进主链()
    {
        var result = FollowUpPackageChain.Evaluate(new FollowUpPackageChainRequest(
            "Supplement", null, "p12", 30, "p20", true, true));

        Assert.True(result.CanImport);
        Assert.False(result.AdvancesMainChain);
    }

    [Fact]
    public void Replacement不能替代已经导入的原包()
    {
        var result = FollowUpPackageChain.Evaluate(new FollowUpPackageChainRequest(
            "Replacement", "p20", "p21", 22, "p20", true, true));

        Assert.False(result.CanImport);
        Assert.Equal(FollowUpErrorCodes.PackageNotAvailable, result.ErrorCode);
    }

    private static string CreateEnvelopeJson() =>
        """
        {"protocolVersion":"1.0","packageId":"p1","hospitalCode":"H1","keyId":"k1","keyWrapAlgorithm":"RSA-OAEP-SHA256","payloadCipherAlgorithm":"AES-256-CBC","payloadMacAlgorithm":"HMAC-SHA256","signatureAlgorithm":"RSA-PSS-SHA256","wrappedKeyMaterial":"AA==","iv":"AA==","payloadSha256":"00","payloadHmacSha256":"00","plaintextLength":1,"payloadLength":1,"generatedAt":"2026-07-14T10:00:00+08:00"}
        """;
}
