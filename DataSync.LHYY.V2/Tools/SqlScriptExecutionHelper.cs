using Npgsql;

namespace DataSync.LHYY.V2.Tools;

internal static class SqlScriptExecutionHelper
{
    private const string NonTransactionalMarker = "-- DATASYNC:NONTRANSACTIONAL";

    public static bool RequiresNonTransactionalExecution(string sql) =>
        sql.Contains(NonTransactionalMarker, StringComparison.OrdinalIgnoreCase)
        || sql.Contains("CREATE INDEX CONCURRENTLY", StringComparison.OrdinalIgnoreCase);

    public static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        if (RequiresNonTransactionalExecution(sql))
        {
            foreach (var statement in SplitStatements(sql))
            {
                if (string.IsNullOrWhiteSpace(statement))
                    continue;

                await using var command = new NpgsqlCommand(statement, connection) { CommandTimeout = 0 };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 })
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static List<string> SplitStatements(string sql)
    {
        var result = new List<string>();
        var start = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inLineComment = false;
        var blockCommentDepth = 0;
        string? dollarQuoteTag = null;

        for (var i = 0; i < sql.Length; i++)
        {
            var current = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (current is '\r' or '\n')
                    inLineComment = false;
                continue;
            }

            if (blockCommentDepth > 0)
            {
                if (current == '/' && next == '*')
                {
                    blockCommentDepth++;
                    i++;
                    continue;
                }

                if (current == '*' && next == '/')
                {
                    blockCommentDepth--;
                    i++;
                }

                continue;
            }

            if (dollarQuoteTag != null)
            {
                if (MatchesAt(sql, i, dollarQuoteTag))
                {
                    i += dollarQuoteTag.Length - 1;
                    dollarQuoteTag = null;
                }

                continue;
            }

            if (inSingleQuote)
            {
                if (current == '\'' && next == '\'')
                {
                    i++;
                    continue;
                }

                if (current == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (current == '"' && next == '"')
                {
                    i++;
                    continue;
                }

                if (current == '"')
                    inDoubleQuote = false;
                continue;
            }

            if (current == '-' && next == '-')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                blockCommentDepth = 1;
                i++;
                continue;
            }

            if (current == '\'')
            {
                inSingleQuote = true;
                continue;
            }

            if (current == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            if (current == '$')
            {
                var tag = TryReadDollarQuoteTag(sql, i);
                if (tag != null)
                {
                    dollarQuoteTag = tag;
                    i += tag.Length - 1;
                    continue;
                }
            }

            if (current == ';')
            {
                var statement = sql[start..i].Trim();
                if (!string.IsNullOrWhiteSpace(statement))
                    result.Add(statement);
                start = i + 1;
            }
        }

        var tail = sql[start..].Trim();
        if (!string.IsNullOrWhiteSpace(tail))
            result.Add(tail);

        return result;
    }

    private static string? TryReadDollarQuoteTag(string sql, int start)
    {
        var end = start + 1;
        while (end < sql.Length && IsDollarQuoteTagChar(sql[end]))
            end++;

        if (end < sql.Length && sql[end] == '$')
            return sql[start..(end + 1)];

        return null;
    }

    private static bool IsDollarQuoteTagChar(char value) =>
        value == '_' || char.IsLetterOrDigit(value);

    private static bool MatchesAt(string text, int start, string value)
    {
        if (start + value.Length > text.Length)
            return false;

        return string.CompareOrdinal(text, start, value, 0, value.Length) == 0;
    }
}
