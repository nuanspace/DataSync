using System.Data;
using System.Text.RegularExpressions;
using Bio.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

public class DirectTargetWriteService
{
    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.Compiled);

    private readonly IDbContextFactory<CubeDbContext> _cubeDbContextFactory;
    private readonly ILogger<DirectTargetWriteService> _logger;

    public DirectTargetWriteService(
        IDbContextFactory<CubeDbContext> cubeDbContextFactory,
        ILogger<DirectTargetWriteService> logger)
    {
        _cubeDbContextFactory = cubeDbContextFactory;
        _logger = logger;
    }

    public async Task<bool> WriteAsync(
        form_question question,
        Guid patientId,
        Guid patientEventId,
        object value,
        Guid? cardSubId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeIdentifier(question.table_name) || !IsSafeIdentifier(question.column_name))
        {
            _logger.LogWarning("目标表或列名非法，跳过直接写入: Table={Table}, Column={Column}", question.table_name, question.column_name);
            return false;
        }

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync(cancellationToken);
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(cancellationToken);

        var rowId = await FindRowIdAsync(conn, question, patientEventId, cardSubId, cancellationToken);
        if (rowId.HasValue)
        {
            await UpdateValueAsync(conn, question, rowId.Value, value, cancellationToken);
            return true;
        }

        await InsertValueAsync(conn, question, patientId, patientEventId, value, cardSubId, cancellationToken);
        return true;
    }

    private static async Task<Guid?> FindRowIdAsync(
        IDbConnection conn,
        form_question question,
        Guid patientEventId,
        Guid? cardSubId,
        CancellationToken cancellationToken)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = cardSubId.HasValue
            ? $"""
               SELECT id
               FROM target.{Quote(question.table_name)}
               WHERE patient_event_id = @patientEventId
                 AND card_id = @cardId
                 AND card_sub_id = @cardSubId
                 AND COALESCE(is_valid, TRUE) = TRUE
               LIMIT 1
               """
            : $"""
               SELECT id
               FROM target.{Quote(question.table_name)}
               WHERE patient_event_id = @patientEventId
                 AND card_id = @cardId
                 AND card_sub_id IS NULL
                 AND COALESCE(is_valid, TRUE) = TRUE
               LIMIT 1
               """;

        AddParameter(cmd, "patientEventId", patientEventId);
        AddParameter(cmd, "cardId", question.card_id);
        if (cardSubId.HasValue)
            AddParameter(cmd, "cardSubId", cardSubId.Value);

        var result = await ExecuteScalarAsync(cmd, cancellationToken);
        return result is Guid id ? id : null;
    }

    private static async Task UpdateValueAsync(
        IDbConnection conn,
        form_question question,
        Guid rowId,
        object value,
        CancellationToken cancellationToken)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE target.{Quote(question.table_name)}
            SET {Quote(question.column_name)} = @value,
                updated_at = @updatedAt
            WHERE id = @id
            """;

        AddParameter(cmd, "value", NormalizeValue(value));
        AddParameter(cmd, "updatedAt", DateTime.Now);
        AddParameter(cmd, "id", rowId);
        await ExecuteNonQueryAsync(cmd, cancellationToken);
    }

    private static async Task InsertValueAsync(
        IDbConnection conn,
        form_question question,
        Guid patientId,
        Guid patientEventId,
        object value,
        Guid? cardSubId,
        CancellationToken cancellationToken)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO target.{Quote(question.table_name)}
            (
                patient_id,
                patient_event_id,
                card_id,
                card_sub_id,
                form_id,
                form_name,
                form_set_id,
                form_set_name,
                project_id,
                project_name,
                hospital_id,
                hospital_name,
                created_at,
                updated_at,
                is_valid,
                {Quote(question.column_name)}
            )
            VALUES
            (
                @patientId,
                @patientEventId,
                @cardId,
                @cardSubId,
                @formId,
                @formName,
                @formSetId,
                @formSetName,
                @projectId,
                @projectName,
                @hospitalId,
                @hospitalName,
                @createdAt,
                @updatedAt,
                TRUE,
                @value
            )
            """;

        var now = DateTime.Now;
        AddParameter(cmd, "patientId", patientId);
        AddParameter(cmd, "patientEventId", patientEventId);
        AddParameter(cmd, "cardId", question.card_id);
        AddParameter(cmd, "cardSubId", cardSubId);
        AddParameter(cmd, "formId", question.form_id);
        AddParameter(cmd, "formName", question.form_name);
        AddParameter(cmd, "formSetId", question.form_set_id);
        AddParameter(cmd, "formSetName", question.form_set_name);
        AddParameter(cmd, "projectId", question.project_id);
        AddParameter(cmd, "projectName", question.project_name);
        AddParameter(cmd, "hospitalId", question.hospital_id);
        AddParameter(cmd, "hospitalName", question.hospital_name);
        AddParameter(cmd, "createdAt", now);
        AddParameter(cmd, "updatedAt", now);
        AddParameter(cmd, "value", NormalizeValue(value));
        await ExecuteNonQueryAsync(cmd, cancellationToken);
    }

    private static bool IsSafeIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value) && IdentifierRegex.IsMatch(value);

    private static string Quote(string value) => "\"" + value + "\"";

    private static object NormalizeValue(object value)
        => value is List<string> list ? list.ToArray() : value;

    private static void AddParameter(IDbCommand cmd, string name, object? value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(parameter);
    }

    private static async Task<object?> ExecuteScalarAsync(IDbCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd is not System.Data.Common.DbCommand dbCommand)
            return cmd.ExecuteScalar();

        return await dbCommand.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(IDbCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd is not System.Data.Common.DbCommand dbCommand)
        {
            cmd.ExecuteNonQuery();
            return;
        }

        await dbCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
