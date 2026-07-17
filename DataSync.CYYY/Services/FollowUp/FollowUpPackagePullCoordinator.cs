using System.Collections.Concurrent;

namespace DataSync.CYYY.Services.FollowUp;

public sealed class FollowUpPackagePullCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sourceLocks = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(string hospitalCode, CancellationToken cancellationToken)
    {
        var gate = _sourceLocks.GetOrAdd(hospitalCode, _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(0, cancellationToken) ? new Lease(gate) : null;
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
