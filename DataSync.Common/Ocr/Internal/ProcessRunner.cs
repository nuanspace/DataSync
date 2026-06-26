using System.Diagnostics;
using System.Text;

namespace DataSync.Common.Ocr.Internal;

internal static class ProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var displayArguments = string.Join(" ", arguments.Select(QuoteForDisplay));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                error.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForKilledProcessAsync(process);
            if (cancellationToken.IsCancellationRequested)
                throw;

            throw new TimeoutException($"命令执行超时：{fileName} {displayArguments}");
        }

        return new ProcessRunResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已退出，忽略清理异常。
        }
    }

    private static async Task WaitForKilledProcessAsync(Process process)
    {
        try
        {
            using var killWaitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(killWaitCts.Token);
        }
        catch
        {
            // 进程可能已退出或无法访问，忽略清理异常。
        }
    }

    private static string QuoteForDisplay(string value)
        => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}

internal sealed record ProcessRunResult(int ExitCode, string Output, string Error);
