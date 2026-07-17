using DataSync.CYYY.Models.FollowUp;
using DataSync.Common.FollowUp;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;

namespace DataSync.CYYY.Services.FollowUp;

public sealed class FollowUpPackageRelayClient(
    IOptions<FollowUpPackageSyncOptions> options,
    FollowUpPackageFileStore fileStore)
{
    private readonly FollowUpPackageSyncOptions _options = options.Value;

    public async Task<string> HealthAsync(FollowUpPackageSourceConfig source, CancellationToken cancellationToken = default)
    {
        using var process = Start(source, "relay-health");
        process.StandardInput.Close();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        EnsureSuccess(process.ExitCode, await stderrTask);
        using var _ = JsonDocument.Parse(stdout);
        return stdout;
    }

    public async Task<List<FollowUpPackageSummary>> ListAsync(
        FollowUpPackageSourceConfig source,
        long? afterSequenceNo,
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest("relay-list", new { afterSequenceNo, limit = Math.Clamp(_options.ListLimit, 1, 1000) });
        var json = await ExecuteJsonAsync(source, "relay-list", request, cancellationToken);
        var response = JsonSerializer.Deserialize<FollowUpProtocolResponse<FollowUpPackageListData>>(json, FollowUpJson.Options)
            ?? throw new FollowUpPackageException(FollowUpErrorCodes.InvalidRequest, "DMZ 包清单响应为空。");
        if (!response.Success
            || response.Data is null
            || response.ProtocolVersion != "1.0"
            || !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            throw new FollowUpPackageException(FollowUpErrorCodes.InvalidRequest, "DMZ 包清单响应无效。");
        return response.Data.Packages;
    }

    public async Task<string> PullAsync(
        FollowUpPackageSourceConfig source,
        FollowUpPackageSummary package,
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest("relay-pull", new { packageId = package.PackageId });
        using var process = Start(source, "relay-pull");
        using var registration = cancellationToken.Register(() => TryKill(process));
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await WriteRequestAsync(process, request, cancellationToken);
        string? savedPath = null;
        try
        {
            savedPath = await fileStore.SaveAsync(
                source.PackageRoot,
                package.PackageId,
                process.StandardOutput.BaseStream,
                package.SizeBytes,
                package.PackageHash,
                _options.MaxPackageBytes,
                cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            EnsureSuccess(process.ExitCode, await stderrTask);
            return savedPath;
        }
        catch
        {
            TryKill(process);
            if (savedPath is not null && File.Exists(savedPath)) File.Delete(savedPath);
            throw;
        }
    }

    public async Task AckAsync(
        FollowUpPackageSourceConfig source,
        string ackPayloadJson,
        CancellationToken cancellationToken = default)
    {
        using var payloadDocument = JsonDocument.Parse(ackPayloadJson);
        var request = CreateRequest("relay-ack", payloadDocument.RootElement.Clone());
        var json = await ExecuteJsonAsync(source, "relay-ack", request, cancellationToken);
        var response = JsonSerializer.Deserialize<FollowUpProtocolResponse<JsonElement>>(json, FollowUpJson.Options)
            ?? throw new FollowUpPackageException(FollowUpErrorCodes.InvalidRequest, "DMZ ACK 响应为空。");
        if (!response.Success
            || response.ProtocolVersion != "1.0"
            || !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            throw new FollowUpPackageException(FollowUpErrorCodes.InvalidRequest, "DMZ ACK 响应无效。");
    }

    private FollowUpRelayRequest CreateRequest(string operation, object request)
    {
        if (!File.Exists(_options.TokenFilePath))
            throw new InvalidOperationException("DMZ token 文件不存在。");
        var token = File.ReadAllText(_options.TokenFilePath).Trim();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("DMZ token 文件为空。");
        return FollowUpRelayRequest.Create(
            operation,
            token,
            request,
            requestWindow: TimeSpan.FromSeconds(Math.Clamp(_options.RequestWindowSeconds, 30, 600)));
    }

    private async Task<string> ExecuteJsonAsync(
        FollowUpPackageSourceConfig source,
        string operation,
        FollowUpRelayRequest request,
        CancellationToken cancellationToken)
    {
        using var process = Start(source, operation);
        using var registration = cancellationToken.Register(() => TryKill(process));
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await WriteRequestAsync(process, request, cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        EnsureSuccess(process.ExitCode, await stderrTask);
        using var _ = JsonDocument.Parse(stdout);
        return stdout;
    }

    private Process Start(FollowUpPackageSourceConfig source, string operation)
    {
        var process = new Process { StartInfo = FollowUpSshCommandBuilder.Create(_options, source, operation) };
        if (!process.Start()) throw new InvalidOperationException("无法启动 SSH 客户端。");
        return process;
    }

    private static async Task WriteRequestAsync(Process process, FollowUpRelayRequest request, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, FollowUpJson.Options, cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        process.StandardInput.Close();
    }

    private static void EnsureSuccess(int exitCode, string stderr)
    {
        if (exitCode == 0) return;
        try
        {
            using var document = JsonDocument.Parse(stderr);
            var root = document.RootElement;
            var code = root.TryGetProperty("errorCode", out var codeValue) ? codeValue.GetString() : null;
            var message = root.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            throw new FollowUpPackageException(code ?? FollowUpErrorCodes.InternalError, message ?? "DMZ 请求失败。");
        }
        catch (JsonException)
        {
            throw new FollowUpPackageException(FollowUpErrorCodes.InternalError, "DMZ SSH 请求失败。");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
