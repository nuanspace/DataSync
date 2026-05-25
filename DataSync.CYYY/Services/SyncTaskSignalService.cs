using System.Collections.Concurrent;

namespace DataSync.CYYY.Services;

/// <summary>
/// 同步任务唤醒信号
/// </summary>
public class SyncTaskSignalService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals =
        new(StringComparer.OrdinalIgnoreCase);

    public void Notify(string taskCode)
    {
        var signal = _signals.GetOrAdd(taskCode, _ => new SemaphoreSlim(0, int.MaxValue));
        signal.Release();
    }

    public void NotifyMany(IEnumerable<string> taskCodes)
    {
        foreach (var taskCode in taskCodes.Distinct(StringComparer.OrdinalIgnoreCase))
            Notify(taskCode);
    }

    public Task<bool> WaitAsync(string taskCode, TimeSpan timeout, CancellationToken ct)
    {
        var signal = _signals.GetOrAdd(taskCode, _ => new SemaphoreSlim(0, int.MaxValue));
        return signal.WaitAsync(timeout, ct);
    }
}
