using System.Security.Cryptography;
using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpHospitalInitializationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hospital-init-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task EmptyPlaceholderSecrets_AreNotReportedAsReady()
    {
        Directory.CreateDirectory(_root);
        var options = CreateOptions();
        await File.WriteAllTextAsync(options.CyyyTokenFilePath, string.Empty);
        await File.WriteAllTextAsync(options.CyyyKnownHostsPath, string.Empty);
        var keyService = new FollowUpPackageImportKeyService(Options.Create(options));
        var status = new FollowUpHospitalInitializationService(Options.Create(options), keyService).GetStatus();

        Assert.False(status.DmzTokenReady);
        Assert.False(status.DmzKnownHostsReady);
        Assert.False(status.Complete);
    }

    [Fact]
    public async Task ImportDmzResponse_WritesOnlyHospitalRuntimeTrustFiles()
    {
        Directory.CreateDirectory(_root);
        var options = CreateOptions();
        using var rsa = RSA.Create(3072);
        var keyService = new FollowUpPackageImportKeyService(Options.Create(options));
        var service = new FollowUpHospitalInitializationService(Options.Create(options), keyService);
        var package = new FollowUpInitializationPackage
        {
            PackageType = FollowUpInitializationPackageTypes.DmzToHospital,
            HospitalId = Guid.Parse(options.HospitalId).ToString(),
            HospitalCode = options.HospitalCode,
            DeviceId = options.DeviceId,
            DmzHostKnownHostsLine = "[dmz.example]:2224 ssh-ed25519 AAAATEST",
            DmzInnerDeviceToken = new string('i', 64),
            CloudSigningPublicKey = rsa.ExportSubjectPublicKeyInfoPem()
        };

        await service.ImportDmzResponseAsync(FollowUpInitializationPackageSerializer.Serialize(package));

        Assert.Contains("dmz.example", await File.ReadAllTextAsync(options.CyyyKnownHostsPath));
        Assert.Equal(new string('i', 64), (await File.ReadAllTextAsync(options.CyyyTokenFilePath)).Trim());
        Assert.DoesNotContain("PRIVATE KEY", await File.ReadAllTextAsync(options.CloudSigningPublicKeyPath));
    }

    [Fact]
    public async Task ImportDmzResponse_IsIdempotent_AndRejectsDifferentTrustMaterial()
    {
        Directory.CreateDirectory(_root);
        var options = CreateOptions();
        using var rsa = RSA.Create(3072);
        var service = new FollowUpHospitalInitializationService(
            Options.Create(options), new FollowUpPackageImportKeyService(Options.Create(options)));
        var package = new FollowUpInitializationPackage
        {
            PackageType = FollowUpInitializationPackageTypes.DmzToHospital,
            HospitalId = Guid.Parse(options.HospitalId).ToString(),
            HospitalCode = options.HospitalCode,
            DeviceId = options.DeviceId,
            DmzHostKnownHostsLine = "[dmz.example]:2224 ssh-ed25519 AAAATEST",
            DmzInnerDeviceToken = new string('i', 64),
            CloudSigningPublicKey = rsa.ExportSubjectPublicKeyInfoPem()
        };
        var json = FollowUpInitializationPackageSerializer.Serialize(package);
        await service.ImportDmzResponseAsync(json);
        await service.ImportDmzResponseAsync(json);

        package.DmzInnerDeviceToken = new string('x', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportDmzResponseAsync(
            FollowUpInitializationPackageSerializer.Serialize(package)));
        Assert.Equal(new string('i', 64), (await File.ReadAllTextAsync(options.CyyyTokenFilePath)).Trim());
    }

    private FollowUpPackageImportOptions CreateOptions() => new()
    {
        HospitalId = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        HospitalCode = "hospital-01",
        DeviceId = "device-01",
        CyyyPrivateKeyPath = Path.Combine(_root, "cyyy-key"),
        CyyyKnownHostsPath = Path.Combine(_root, "known-hosts"),
        CyyyTokenFilePath = Path.Combine(_root, "inner-token"),
        DecryptionPrivateKeyPath = Path.Combine(_root, "lhyy-private.pem"),
        CloudSigningPublicKeyPath = Path.Combine(_root, "cloud-signing.pem")
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
