using System.Data;
using System.Data.Common;

namespace DataSync.LHYY.V2.Services;

internal enum ArchiveOptimizationState
{
    NotInstalled = 0,
    Partial = 1,
    Ready = 2
}

internal static class ArchiveOptimizationCheck
{
    private const string StateSql = """
        WITH required_indexes(index_name) AS (
            VALUES
                ('ix_esb_messages_project_created_id'),
                ('ix_esb_messages_project_status_created'),
                ('ix_esb_messages_project_tran_created'),
                ('ix_esb_messages_project_mrn_created'),
                ('ix_esb_messages_queue_claim'),
                ('ix_esb_messages_processing_timeout'),
                ('ix_esb_messages_archive_id'),
                ('ux_esb_messages_archive_id_created_at'),
                ('ix_esb_messages_archive_project_created_id'),
                ('ix_esb_messages_archive_project_status_created'),
                ('ix_esb_messages_archive_project_tran_created'),
                ('ix_esb_messages_archive_project_mrn_created'),
                ('ix_esb_messages_archive_mrn_event_time'),
                ('ix_esb_process_log_message_created'),
                ('ux_esb_process_log_archive_id_created_at'),
                ('ix_esb_process_log_archive_message_created'),
                ('ix_esb_process_log_archive_project_created')
        ),
        flags AS (
            SELECT
                to_regclass('lhyy.esb_messages_archive') IS NOT NULL AS has_messages_archive,
                to_regclass('lhyy.esb_process_log_archive') IS NOT NULL AS has_process_log_archive,
                to_regclass('lhyy.esb_messages_all') IS NOT NULL AS has_messages_all,
                to_regclass('lhyy.esb_process_log_all') IS NOT NULL AS has_process_log_all,
                EXISTS (
                    SELECT 1
                    FROM pg_proc p
                    INNER JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname = 'lhyy'
                      AND p.proname = 'ensure_esb_archive_partition'
                      AND oidvectortypes(p.proargtypes) = 'date'
                ) AS has_partition_function,
                NOT EXISTS (
                    SELECT 1
                    FROM required_indexes r
                    WHERE to_regclass('lhyy.' || r.index_name) IS NULL
                ) AS has_required_indexes,
                NOT EXISTS (
                    SELECT 1
                    FROM pg_class c
                    INNER JOIN pg_namespace n ON n.oid = c.relnamespace
                    INNER JOIN pg_index i ON i.indexrelid = c.oid
                    INNER JOIN required_indexes r ON r.index_name = c.relname
                    WHERE n.nspname = 'lhyy'
                      AND i.indisvalid = FALSE
                ) AS has_valid_indexes,
                EXISTS (
                    SELECT 1
                    FROM required_indexes r
                    WHERE to_regclass('lhyy.' || r.index_name) IS NOT NULL
                ) AS has_any_required_index
        )
        SELECT
            CASE
                WHEN has_messages_archive
                    AND has_process_log_archive
                    AND has_messages_all
                    AND has_process_log_all
                    AND has_partition_function
                    AND has_required_indexes
                    AND has_valid_indexes
                    THEN 2
                WHEN has_messages_archive
                    OR has_process_log_archive
                    OR has_messages_all
                    OR has_process_log_all
                    OR has_partition_function
                    OR has_any_required_index
                    THEN 1
                ELSE 0
            END
        FROM flags;
        """;

    private const string ReadableSql = """
        SELECT
            to_regclass('lhyy.esb_messages_archive') IS NOT NULL
            AND to_regclass('lhyy.esb_process_log_archive') IS NOT NULL
            AND to_regclass('lhyy.esb_messages_all') IS NOT NULL
            AND to_regclass('lhyy.esb_process_log_all') IS NOT NULL;
        """;

    public static async Task<bool> IsReadyAsync(DbConnection connection, CancellationToken cancellationToken = default)
        => await GetStateAsync(connection, cancellationToken) == ArchiveOptimizationState.Ready;

    public static async Task<bool> IsReadableAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScalarAsync(connection, ReadableSql, cancellationToken);
        return result is bool readable && readable;
    }

    public static async Task<ArchiveOptimizationState> GetStateAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScalarAsync(connection, StateSql, cancellationToken);
        return Enum.IsDefined(typeof(ArchiveOptimizationState), Convert.ToInt32(result))
            ? (ArchiveOptimizationState)Convert.ToInt32(result)
            : ArchiveOptimizationState.Partial;
    }

    private static async Task<object?> ExecuteScalarAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
