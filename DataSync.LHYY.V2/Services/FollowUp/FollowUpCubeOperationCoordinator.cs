using Npgsql;

namespace DataSync.LHYY.V2.Services.FollowUp;

internal interface IFollowUpCubeAdvisoryLockProvider
{
    ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken);
    ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken);
    ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken);
}

public sealed class FollowUpCubeOperationCoordinator
{
    private readonly IFollowUpCubeAdvisoryLockProvider _advisoryLockProvider;
    private readonly SemaphoreSlim _exclusiveGate = new(1, 1);
    private readonly SemaphoreSlim _sharedStateGate = new(1, 1);
    private IAsyncDisposable? _sharedAdvisoryLease;
    private int _sharedLeaseCount;
    private int _maintenanceActive;

    internal FollowUpCubeOperationCoordinator(IFollowUpCubeAdvisoryLockProvider advisoryLockProvider)
    {
        _advisoryLockProvider = advisoryLockProvider;
    }

    public bool IsMaintenanceActive => Volatile.Read(ref _maintenanceActive) != 0;

    public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        if (!await _exclusiveGate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            await _sharedStateGate.WaitAsync(cancellationToken);
            try { Volatile.Write(ref _maintenanceActive, 1); }
            finally { _sharedStateGate.Release(); }

            var advisoryLease = await _advisoryLockProvider.TryAcquireExclusiveAsync(cancellationToken);
            if (advisoryLease is null)
            {
                ReleaseExclusiveGate();
                return null;
            }
            return new CallbackLease(async () =>
            {
                try { await advisoryLease.DisposeAsync(); }
                finally { ReleaseExclusiveGate(); }
            });
        }
        catch
        {
            ReleaseExclusiveGate();
            throw;
        }
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken)
    {
        if (IsMaintenanceActive) return null;
        await _sharedStateGate.WaitAsync(cancellationToken);
        try
        {
            if (IsMaintenanceActive) return null;
            await using var admissionLease = await _advisoryLockProvider.TryAcquireSharedAdmissionAsync(cancellationToken);
            if (admissionLease is null) return null;
            if (_sharedLeaseCount == 0)
            {
                _sharedAdvisoryLease = await _advisoryLockProvider.TryAcquireSharedAsync(cancellationToken);
                if (_sharedAdvisoryLease is null) return null;
            }
            _sharedLeaseCount++;
            return new CallbackLease(ReleaseSharedLeaseAsync);
        }
        finally
        {
            _sharedStateGate.Release();
        }
    }

    private async ValueTask ReleaseSharedLeaseAsync()
    {
        IAsyncDisposable? advisoryLease = null;
        await _sharedStateGate.WaitAsync();
        try
        {
            _sharedLeaseCount--;
            if (_sharedLeaseCount == 0)
            {
                advisoryLease = _sharedAdvisoryLease;
                _sharedAdvisoryLease = null;
            }
        }
        finally
        {
            _sharedStateGate.Release();
        }
        if (advisoryLease is not null) await advisoryLease.DisposeAsync();
    }

    private void ReleaseExclusiveGate()
    {
        Volatile.Write(ref _maintenanceActive, 0);
        _exclusiveGate.Release();
    }

    private sealed class CallbackLease(Func<ValueTask> onDispose) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? onDispose() : ValueTask.CompletedTask;
    }
}

internal sealed class PostgreSqlFollowUpCubeAdvisoryLockProvider(
    IConfiguration configuration,
    ILogger<PostgreSqlFollowUpCubeAdvisoryLockProvider> logger) : IFollowUpCubeAdvisoryLockProvider
{
    private const long OperationLockKey = 739944761221001;
    private const long MaintenanceLockKey = 739944761221002;
    private readonly string _connectionString = BuildLockConnectionString(configuration);

    public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        try
        {
            if (!await TryLockAsync(connection, "pg_try_advisory_lock", OperationLockKey, cancellationToken))
            {
                await connection.DisposeAsync();
                return null;
            }
            try
            {
                await LockAsync(connection, "pg_advisory_lock", MaintenanceLockKey, cancellationToken);
                return new PostgreSqlLease(connection, logger,
                    ("pg_advisory_unlock", MaintenanceLockKey),
                    ("pg_advisory_unlock", OperationLockKey));
            }
            catch
            {
                await TryUnlockAsync(connection, "pg_advisory_unlock", OperationLockKey);
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        try
        {
            if (!await TryLockAsync(connection, "pg_try_advisory_lock_shared", MaintenanceLockKey, cancellationToken))
            {
                await connection.DisposeAsync();
                return null;
            }
            return new PostgreSqlLease(connection, logger, ("pg_advisory_unlock_shared", MaintenanceLockKey));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        try
        {
            if (!await TryLockAsync(connection, "pg_try_advisory_lock_shared", OperationLockKey, cancellationToken))
            {
                await connection.DisposeAsync();
                return null;
            }
            return new PostgreSqlLease(connection, logger, ("pg_advisory_unlock_shared", OperationLockKey));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string BuildLockConnectionString(IConfiguration configuration)
    {
        var source = configuration.GetConnectionString("CubeDb")
            ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");
        var builder = new NpgsqlConnectionStringBuilder(source)
        {
            ApplicationName = "FollowUpCubeOperationLock",
            MinPoolSize = 0,
            MaxPoolSize = 4
        };
        return builder.ConnectionString;
    }

    private static async Task<bool> TryLockAsync(
        NpgsqlConnection connection,
        string function,
        long key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {function}(@key)", connection);
        command.Parameters.AddWithValue("key", key);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task LockAsync(
        NpgsqlConnection connection,
        string function,
        long key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {function}(@key)", connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TryUnlockAsync(NpgsqlConnection connection, string function, long key)
    {
        try
        {
            await using var command = new NpgsqlCommand($"SELECT {function}(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            NpgsqlConnection.ClearPool(connection);
        }
    }

    private sealed class PostgreSqlLease(
        NpgsqlConnection connection,
        ILogger logger,
        params (string Function, long Key)[] unlocks) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                foreach (var (function, key) in unlocks)
                    await TryUnlockAsync(connection, function, key);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "释放 FollowUp CubeDb advisory lock 失败，清理连接池。");
                NpgsqlConnection.ClearPool(connection);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
