using Npgsql;

namespace DataSync.LHYY.V2.Services.FollowUp;

internal interface IFollowUpCubeAdvisoryLockProvider
{
    ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken);
    ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken);
    ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken);
}

internal interface IFollowUpCubePersistentStateGate
{
    ValueTask<bool> IsBlockedAsync(CancellationToken cancellationToken);
    void Invalidate() { }
}

public sealed class FollowUpCubeOperationCoordinator
{
    private readonly IFollowUpCubeAdvisoryLockProvider _advisoryLockProvider;
    private readonly IFollowUpCubePersistentStateGate _persistentStateGate;
    private readonly SemaphoreSlim _exclusiveGate = new(1, 1);
    private readonly SemaphoreSlim _sharedStateGate = new(1, 1);
    private IAsyncDisposable? _sharedAdvisoryLease;
    private int _sharedLeaseCount;
    private int _maintenanceActive;

    internal FollowUpCubeOperationCoordinator(IFollowUpCubeAdvisoryLockProvider advisoryLockProvider)
        : this(advisoryLockProvider, new AllowAllPersistentStateGate())
    {
    }

    internal FollowUpCubeOperationCoordinator(
        IFollowUpCubeAdvisoryLockProvider advisoryLockProvider,
        IFollowUpCubePersistentStateGate persistentStateGate)
    {
        _advisoryLockProvider = advisoryLockProvider;
        _persistentStateGate = persistentStateGate;
    }

    public static FollowUpCubeOperationCoordinator Create(
        IConfiguration configuration,
        ILoggerFactory loggerFactory) =>
        new(
            new PostgreSqlFollowUpCubeAdvisoryLockProvider(
                configuration,
                loggerFactory.CreateLogger<PostgreSqlFollowUpCubeAdvisoryLockProvider>()),
            new PostgreSqlFollowUpCubePersistentStateGate(configuration));

    public bool IsMaintenanceActive => Volatile.Read(ref _maintenanceActive) != 0;

    internal void InvalidatePersistentStateGate() => _persistentStateGate.Invalidate();

    public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken)
        => await TryAcquireExclusiveCoreAsync(false, cancellationToken);

    public async ValueTask<IAsyncDisposable?> TryAcquireRecoveryExclusiveAsync(CancellationToken cancellationToken)
        => await TryAcquireExclusiveCoreAsync(true, cancellationToken);

    private async ValueTask<IAsyncDisposable?> TryAcquireExclusiveCoreAsync(
        bool allowPersistentBlock,
        CancellationToken cancellationToken)
    {
        if (!await _exclusiveGate.WaitAsync(0, cancellationToken)) return null;
        var releaseGateOnFailure = true;
        IAsyncDisposable? advisoryLease = null;
        try
        {
            await _sharedStateGate.WaitAsync(cancellationToken);
            try { Volatile.Write(ref _maintenanceActive, 1); }
            finally { _sharedStateGate.Release(); }

            advisoryLease = await _advisoryLockProvider.TryAcquireExclusiveAsync(cancellationToken);
            if (advisoryLease is null)
            {
                releaseGateOnFailure = false;
                ReleaseExclusiveGate();
                return null;
            }
            if (!allowPersistentBlock && await _persistentStateGate.IsBlockedAsync(cancellationToken))
            {
                await advisoryLease.DisposeAsync();
                advisoryLease = null;
                releaseGateOnFailure = false;
                ReleaseExclusiveGate();
                return null;
            }
            var acquiredLease = advisoryLease;
            advisoryLease = null;
            releaseGateOnFailure = false;
            return new CallbackLease(async () =>
            {
                try { await acquiredLease.DisposeAsync(); }
                finally { ReleaseExclusiveGate(); }
            });
        }
        catch
        {
            try
            {
                if (advisoryLease is not null) await advisoryLease.DisposeAsync();
            }
            finally
            {
                if (releaseGateOnFailure) ReleaseExclusiveGate();
            }
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
            if (await _persistentStateGate.IsBlockedAsync(cancellationToken)) return null;
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

    private sealed class AllowAllPersistentStateGate : IFollowUpCubePersistentStateGate
    {
        public ValueTask<bool> IsBlockedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}

internal sealed class FollowUpCubePersistentStateCache(
    Func<CancellationToken, ValueTask<bool>> loader,
    TimeProvider timeProvider,
    TimeSpan cacheDuration) : IFollowUpCubePersistentStateGate
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateLock = new();
    private bool _hasValue;
    private bool _cachedValue;
    private DateTimeOffset _expiresAt;
    private long _generation;

    public async ValueTask<bool> IsBlockedAsync(CancellationToken cancellationToken)
    {
        if (TryGetCached(out var cached)) return cached;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCached(out cached)) return cached;
            long generation;
            lock (_stateLock) generation = _generation;
            var loaded = await loader(cancellationToken);
            lock (_stateLock)
            {
                if (generation == _generation)
                {
                    _cachedValue = loaded;
                    _expiresAt = timeProvider.GetUtcNow() + cacheDuration;
                    _hasValue = true;
                }
            }
            return loaded;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Invalidate()
    {
        lock (_stateLock)
        {
            _hasValue = false;
            _generation++;
        }
    }

    private bool TryGetCached(out bool value)
    {
        lock (_stateLock)
        {
            value = _cachedValue;
            return _hasValue && timeProvider.GetUtcNow() < _expiresAt;
        }
    }
}

internal sealed class PostgreSqlFollowUpCubePersistentStateGate : IFollowUpCubePersistentStateGate
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);
    private readonly string _connectionString;
    private readonly FollowUpCubePersistentStateCache _cache;

    public PostgreSqlFollowUpCubePersistentStateGate(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DataSyncDb")
            ?? throw new InvalidOperationException("未找到连接字符串 'DataSyncDb'");
        _cache = new FollowUpCubePersistentStateCache(LoadAsync, TimeProvider.System, CacheDuration);
    }

    internal static bool IsMissingStateTable(string sqlState) => sqlState == "42P01";

    public ValueTask<bool> IsBlockedAsync(CancellationToken cancellationToken) =>
        _cache.IsBlockedAsync(cancellationToken);

    public void Invalidate() => _cache.Invalidate();

    private async ValueTask<bool> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM lhyy.followup_package_import_state
                WHERE import_status IN ('RestoreFailed', 'Restoring', 'Importing'))
            """, connection);
        try
        {
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        catch (PostgresException ex) when (IsMissingStateTable(ex.SqlState))
        {
            return false;
        }
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
