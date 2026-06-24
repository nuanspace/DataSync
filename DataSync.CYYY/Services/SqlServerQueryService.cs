using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace DataSync.CYYY.Services;

/// <summary>
/// 数据库只读查询服务。
/// </summary>
public class DatabaseQueryService
{
    private const int CommandTimeoutSeconds = 120;
    private const string OracleFromParameter = "p_from";
    private const string OracleToParameter = "p_to";

    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseQueryService> _logger;

    public DatabaseQueryService(
        IConfiguration configuration,
        ILogger<DatabaseQueryService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<Dictionary<string, object>>> QueryByTimeRangeAsync(
        string? databaseType,
        string? connectionStringName,
        string? host,
        string? database,
        string? username,
        string? password,
        bool trustCertificate,
        string sql,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        var normalizedType = IngestionService.NormalizeDatabaseType(databaseType);
        ValidateSelectSql(sql, normalizedType);

        await using var conn = CreateConnection(normalizedType, ResolveConnectionString(
            normalizedType, connectionStringName, host, database, username, password, trustCertificate));
        await conn.OpenAsync(ct);

        await using var cmd = CreateCommand(conn, normalizedType, sql);
        AddTimeRangeParameters(cmd, normalizedType, from, to);

        var rows = await ReadRowsAsync(cmd, ct);
        _logger.LogInformation("{DatabaseType} 时间范围查询完成：{Count} 条", normalizedType, rows.Count);
        return rows;
    }

    public async Task<List<Dictionary<string, object>>> QueryByValuesAsync(
        string? databaseType,
        string? connectionStringName,
        string? host,
        string? database,
        string? username,
        string? password,
        bool trustCertificate,
        string sql,
        string? queryField,
        IReadOnlyCollection<string> values,
        CancellationToken ct)
    {
        var normalizedType = IngestionService.NormalizeDatabaseType(databaseType);
        ValidateSelectSql(sql, normalizedType);
        var querySql = BuildValueQuerySql(sql, normalizedType, queryField);

        var result = new List<Dictionary<string, object>>();
        await using var conn = CreateConnection(normalizedType, ResolveConnectionString(
            normalizedType, connectionStringName, host, database, username, password, trustCertificate));
        await conn.OpenAsync(ct);

        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            await using var cmd = CreateCommand(conn, normalizedType, querySql);
            AddOpenTimeRangeParametersIfNeeded(cmd, normalizedType);
            AddQueryValueParameter(cmd, normalizedType, value);
            result.AddRange(await ReadRowsAsync(cmd, ct));
        }

        _logger.LogInformation("{DatabaseType} 参数查询完成：{Count} 条", normalizedType, result.Count);
        return result;
    }

    public async Task<List<Dictionary<string, object>>> QueryByFieldValueSetsAsync(
        string? databaseType,
        string? connectionStringName,
        string? host,
        string? database,
        string? username,
        string? password,
        bool trustCertificate,
        string sql,
        IReadOnlyCollection<IReadOnlyDictionary<string, string>> valueSets,
        CancellationToken ct)
    {
        var normalizedType = IngestionService.NormalizeDatabaseType(databaseType);
        ValidateSelectSql(sql, normalizedType);
        var validSets = valueSets
            .Select(set => set
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase))
            .Where(set => set.Count > 0)
            .ToList();

        if (validSets.Count == 0)
            return [];

        var result = new List<Dictionary<string, object>>();
        await using var conn = CreateConnection(normalizedType, ResolveConnectionString(
            normalizedType, connectionStringName, host, database, username, password, trustCertificate));
        await conn.OpenAsync(ct);

        foreach (var valueSet in validSets)
        {
            ct.ThrowIfCancellationRequested();

            var fields = valueSet.Keys.ToList();
            var querySql = BuildFieldValueSetQuerySql(sql, normalizedType, fields);
            await using var cmd = CreateCommand(conn, normalizedType, querySql);
            AddOpenTimeRangeParametersIfNeeded(cmd, normalizedType);
            for (var i = 0; i < fields.Count; i++)
                AddStringParameter(cmd, normalizedType, $"linkValue{i}", valueSet[fields[i]]);

            result.AddRange(await ReadRowsAsync(cmd, ct));
        }

        _logger.LogInformation("{DatabaseType} 关联字段查询完成：{Count} 条", normalizedType, result.Count);
        return result;
    }

    public async Task<List<Dictionary<string, object>>> QueryByNamedParametersAsync(
        string? databaseType,
        string? connectionStringName,
        string? host,
        string? database,
        string? username,
        string? password,
        bool trustCertificate,
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        var normalizedType = IngestionService.NormalizeDatabaseType(databaseType);
        ValidateSelectSql(sql, normalizedType);
        var parameterNames = parameters.Keys.Select(NormalizeParameterName).ToList();

        await using var conn = CreateConnection(normalizedType, ResolveConnectionString(
            normalizedType, connectionStringName, host, database, username, password, trustCertificate));
        await conn.OpenAsync(ct);

        await using var cmd = CreateCommand(conn, normalizedType, sql, parameterNames);
        foreach (var (name, value) in parameters)
            AddObjectParameter(cmd, normalizedType, NormalizeParameterName(name), value);

        var rows = await ReadRowsAsync(cmd, ct);
        _logger.LogInformation("{DatabaseType} 命名参数查询完成：{Count} 条", normalizedType, rows.Count);
        return rows;
    }

    public async Task TestConnectionAsync(
        string? databaseType,
        string? connectionStringName,
        string? host,
        string? database,
        string? username,
        string? password,
        bool trustCertificate,
        CancellationToken ct)
    {
        var normalizedType = IngestionService.NormalizeDatabaseType(databaseType);
        await using var conn = CreateConnection(normalizedType, ResolveConnectionString(
            normalizedType, connectionStringName, host, database, username, password, trustCertificate));
        await conn.OpenAsync(ct);

        var sql = IngestionService.IsOracleDatabaseType(normalizedType)
            ? "SELECT 1 FROM DUAL"
            : "SELECT 1";
        await using var cmd = CreateCommand(conn, normalizedType, sql);
        await cmd.ExecuteScalarAsync(ct);
    }

    private string ResolveConnectionString(
        string databaseType,
        string? connectionStringName,
        string? host,
        string? database,
        string? username,
        string? password,
        bool trustCertificate)
    {
        if (!string.IsNullOrWhiteSpace(host))
        {
            if (IngestionService.IsOracleDatabaseType(databaseType))
                return ResolveOracleConnectionString(host, database, username, password);

            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException("SQL Server 连接配置缺少数据库");
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidOperationException("SQL Server 连接配置缺少用户名");

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = host,
                InitialCatalog = database,
                UserID = username,
                Password = password ?? "",
                TrustServerCertificate = trustCertificate
            };
            return builder.ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(connectionStringName))
            throw new InvalidOperationException($"{databaseType} 查询未配置连接信息");

        var configured = _configuration.GetConnectionString(connectionStringName);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        if (connectionStringName.Contains(';') && connectionStringName.Contains('='))
            return connectionStringName;

        throw new InvalidOperationException($"未找到 {databaseType} 连接串：{connectionStringName}");
    }

    private static string ResolveOracleConnectionString(
        string host,
        string? database,
        string? username,
        string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Oracle 连接配置缺少用户名");

        var dataSource = BuildOracleDataSource(host, database);
        var builder = new OracleConnectionStringBuilder
        {
            ["Data Source"] = dataSource,
            ["User Id"] = username,
            ["Password"] = password ?? ""
        };
        return builder.ConnectionString;
    }

    private static string BuildOracleDataSource(string host, string? database)
    {
        var value = host.Trim();
        if (value.Contains('=') || value.Contains('/'))
            return value;

        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("Oracle 连接配置缺少服务名");

        return value.Contains(':')
            ? $"{value}/{database.Trim()}"
            : $"{value}:1521/{database.Trim()}";
    }

    private static DbConnection CreateConnection(string databaseType, string connectionString)
    {
        return IngestionService.IsOracleDatabaseType(databaseType)
            ? new OracleConnection(connectionString)
            {
                UseHourOffsetForUnsupportedTimezone = true
            }
            : new SqlConnection(connectionString);
    }

    private static DbCommand CreateCommand(
        DbConnection conn,
        string databaseType,
        string sql,
        IEnumerable<string>? parameterNames = null)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = PrepareSql(databaseType, sql, parameterNames);
        cmd.CommandTimeout = CommandTimeoutSeconds;
        if (cmd is OracleCommand oracleCommand)
            oracleCommand.BindByName = true;
        return cmd;
    }

    private static void AddTimeRangeParameters(DbCommand cmd, string databaseType, DateTime from, DateTime to)
    {
        if (IngestionService.IsOracleDatabaseType(databaseType))
        {
            var oracleCommand = (OracleCommand)cmd;
            oracleCommand.Parameters.Add(OracleFromParameter, OracleDbType.TimeStamp).Value = from;
            oracleCommand.Parameters.Add(OracleToParameter, OracleDbType.TimeStamp).Value = to;
            return;
        }

        var sqlCommand = (SqlCommand)cmd;
        sqlCommand.Parameters.Add("@from", SqlDbType.DateTime2).Value = from;
        sqlCommand.Parameters.Add("@to", SqlDbType.DateTime2).Value = to;
    }

    private static void AddQueryValueParameter(DbCommand cmd, string databaseType, string value)
        => AddStringParameter(cmd, databaseType, "queryValue", value);

    private static void AddStringParameter(DbCommand cmd, string databaseType, string name, string value)
    {
        if (IngestionService.IsOracleDatabaseType(databaseType))
        {
            var oracleCommand = (OracleCommand)cmd;
            oracleCommand.Parameters.Add(name, OracleDbType.Varchar2).Value = value;
            return;
        }

        ((SqlCommand)cmd).Parameters.Add($"@{name}", SqlDbType.NVarChar).Value = value;
    }

    private static void AddObjectParameter(DbCommand cmd, string databaseType, string name, object? value)
    {
        if (value is DateTime dateTime)
        {
            AddDateTimeParameter(cmd, IngestionService.IsOracleDatabaseType(databaseType), name, dateTime);
            return;
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            AddDateTimeParameter(cmd, IngestionService.IsOracleDatabaseType(databaseType), name, dateTimeOffset.DateTime);
            return;
        }

        AddStringParameter(cmd, databaseType, name, value?.ToString() ?? "");
    }

    private static void AddOpenTimeRangeParametersIfNeeded(DbCommand cmd, string databaseType)
    {
        var isOracle = IngestionService.IsOracleDatabaseType(databaseType);
        var fromName = isOracle ? OracleFromParameter : "from";
        var toName = isOracle ? OracleToParameter : "to";

        if (ContainsNamedParameter(cmd.CommandText, isOracle ? ':' : '@', fromName))
            AddDateTimeParameter(cmd, isOracle, fromName, new DateTime(1753, 1, 1));

        if (ContainsNamedParameter(cmd.CommandText, isOracle ? ':' : '@', toName))
            AddDateTimeParameter(cmd, isOracle, toName, new DateTime(9999, 12, 31, 23, 59, 59));
    }

    private static void AddDateTimeParameter(DbCommand cmd, bool isOracle, string name, DateTime value)
    {
        if (isOracle)
        {
            ((OracleCommand)cmd).Parameters.Add(name, OracleDbType.TimeStamp).Value = value;
            return;
        }

        ((SqlCommand)cmd).Parameters.Add($"@{name}", SqlDbType.DateTime2).Value = value;
    }

    private static bool ContainsNamedParameter(string sql, char prefix, string name)
    {
        var marker = $"{prefix}{name}";
        var index = 0;
        while ((index = sql.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = index + marker.Length;
            if (end >= sql.Length || (!char.IsLetterOrDigit(sql[end]) && sql[end] != '_'))
                return true;

            index = end;
        }

        return false;
    }

    private static string PrepareSql(string databaseType, string sql, IEnumerable<string>? parameterNames = null)
    {
        if (!IngestionService.IsOracleDatabaseType(databaseType))
            return sql;

        var prepared = ReplaceOracleParameter(sql, "from", OracleFromParameter);
        prepared = ReplaceOracleParameter(prepared, "to", OracleToParameter);
        prepared = ReplaceNamedParameter(prepared, '@', "queryValue", ":queryValue");
        if (parameterNames != null)
        {
            foreach (var parameterName in parameterNames)
                prepared = ReplaceNamedParameter(prepared, '@', parameterName, $":{parameterName}");
        }

        return prepared;
    }

    private static string NormalizeParameterName(string name)
        => name.Trim().TrimStart('@', ':');

    private static string ReplaceOracleParameter(string sql, string sourceName, string targetName)
    {
        return ReplaceNamedParameter(
            ReplaceNamedParameter(sql, '@', sourceName, $":{targetName}"),
            ':',
            sourceName,
            $":{targetName}");
    }

    private static string ReplaceNamedParameter(string sql, char prefix, string name, string replacement)
    {
        return Regex.Replace(
            sql,
            $"{Regex.Escape($"{prefix}{name}")}(?![A-Za-z0-9_])",
            replacement,
            RegexOptions.IgnoreCase);
    }

    private static void ValidateSelectSql(string sql, string databaseType)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException($"{databaseType} 查询 SQL 不能为空");

        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{databaseType} 查询仅允许 SELECT 或 WITH 开头的只读 SQL");
        }

        if (trimmed.Contains(';'))
            throw new InvalidOperationException($"{databaseType} 查询不允许包含分号");
    }

    private static string BuildValueQuerySql(string sql, string databaseType, string? queryField)
    {
        if (string.IsNullOrWhiteSpace(queryField))
            throw new InvalidOperationException("数据库接口未配置查询字段，无法自动生成过滤条件");

        var trimmed = sql.Trim();
        if (trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WITH 查询无法自动追加过滤条件，请在 SQL 中手动使用 @queryValue");

        var field = BuildColumnReference(databaseType, queryField);
        var parameter = IngestionService.IsOracleDatabaseType(databaseType) ? ":queryValue" : "@queryValue";
        return $"SELECT * FROM ({trimmed}) q WHERE {field} = {parameter}";
    }

    private static string BuildFieldValueSetQuerySql(string sql, string databaseType, IReadOnlyList<string> fields)
    {
        if (fields.Count == 0)
            throw new InvalidOperationException("关联字段不能为空");

        var trimmed = sql.Trim();
        if (trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WITH 查询无法自动追加关联字段条件，请在 SQL 中手动处理");

        var conditions = fields
            .Select((field, index) =>
                $"{BuildColumnReference(databaseType, field)} = {BuildParameterReference(databaseType, $"linkValue{index}")}")
            .ToList();
        return $"SELECT * FROM ({trimmed}) q WHERE {string.Join(" AND ", conditions)}";
    }

    private static string BuildParameterReference(string databaseType, string name) =>
        IngestionService.IsOracleDatabaseType(databaseType) ? $":{name}" : $"@{name}";

    private static string BuildColumnReference(string databaseType, string queryField)
    {
        var field = queryField.Trim();
        if (field.Length == 0 ||
            field.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
        {
            throw new InvalidOperationException($"查询字段 [{queryField}] 只能包含字母、数字和下划线");
        }

        return IngestionService.IsOracleDatabaseType(databaseType)
            ? $"q.{field}"
            : $"q.[{field.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static async Task<List<Dictionary<string, object>>> ReadRowsAsync(DbCommand cmd, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : NormalizeValue(reader.GetValue(i));

            rows.Add(row);
        }

        return rows;
    }

    private static object NormalizeValue(object value) => value switch
    {
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss"),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value.ToString() ?? ""
    };
}
