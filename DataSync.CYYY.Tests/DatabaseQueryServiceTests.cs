using DataSync.CYYY.Services;
using MySqlConnector;

namespace DataSync.CYYY.Tests;

public class DatabaseQueryServiceTests
{
    [Theory]
    [InlineData("Doris")]
    [InlineData("doris")]
    public void NormalizeDatabaseType_识别Doris(string value)
    {
        Assert.Equal(IngestionService.DatabaseTypeDoris, IngestionService.NormalizeDatabaseType(value));
        Assert.True(IngestionService.IsDorisDatabaseType(value));
    }

    [Theory]
    [InlineData("MySql")]
    [InlineData("mysql")]
    public void NormalizeDatabaseType_识别MySql(string value)
    {
        Assert.Equal(IngestionService.DatabaseTypeMySql, IngestionService.NormalizeDatabaseType(value));
        Assert.True(IngestionService.IsMySqlDatabaseType(value));
        Assert.True(IngestionService.IsMySqlProtocolDatabaseType(value));
    }

    [Fact]
    public void NormalizeSourceType_兼容旧式MySql来源类型()
    {
        Assert.True(IngestionService.IsDatabaseSourceType(IngestionService.SourceTypeMySql));
        Assert.Equal(
            IngestionService.SourceTypeDatabase,
            IngestionService.NormalizeSourceType(IngestionService.SourceTypeMySql));
        Assert.Equal(
            IngestionService.DatabaseTypeMySql,
            IngestionService.NormalizeDatabaseType(null, IngestionService.SourceTypeMySql));
    }

    [Fact]
    public void NormalizeSourceType_兼容旧式Doris来源类型()
    {
        Assert.True(IngestionService.IsDatabaseSourceType(IngestionService.SourceTypeDoris));
        Assert.Equal(
            IngestionService.SourceTypeDatabase,
            IngestionService.NormalizeSourceType(IngestionService.SourceTypeDoris));
        Assert.Equal(
            IngestionService.DatabaseTypeDoris,
            IngestionService.NormalizeDatabaseType(null, IngestionService.SourceTypeDoris));
    }

    [Theory]
    [InlineData("10.0.0.1:9030", "10.0.0.1", 9030u)]
    [InlineData("doris-fe", "doris-fe", 9030u)]
    [InlineData("[::1]:19030", "::1", 19030u)]
    public void ResolveDorisConnectionString_生成MySql协议连接串(
        string endpoint,
        string expectedServer,
        uint expectedPort)
    {
        var connectionString = DatabaseQueryService.ResolveDorisConnectionString(
            endpoint,
            "hospital_cdm",
            "reader",
            "secret");

        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.Equal(expectedServer, builder.Server);
        Assert.Equal(expectedPort, builder.Port);
        Assert.Equal("hospital_cdm", builder.Database);
        Assert.Equal("reader", builder.UserID);
        Assert.Equal(MySqlSslMode.None, builder.SslMode);
        Assert.False(builder.AllowUserVariables);
        Assert.True(builder.AllowZeroDateTime);
    }

    [Theory]
    [InlineData("mysql-source", "mysql-source", 3306u)]
    [InlineData("mysql-source:13306", "mysql-source", 13306u)]
    [InlineData("[::1]", "::1", 3306u)]
    public void ResolveMySqlConnectionString_使用MySql默认端口(
        string endpoint,
        string expectedServer,
        uint expectedPort)
    {
        var connectionString = DatabaseQueryService.ResolveMySqlConnectionString(
            endpoint,
            "cdm_for_fuchanke",
            "reader",
            "secret");

        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.Equal(expectedServer, builder.Server);
        Assert.Equal(expectedPort, builder.Port);
        Assert.Equal("cdm_for_fuchanke", builder.Database);
        Assert.Equal("reader", builder.UserID);
        Assert.Equal(MySqlSslMode.Preferred, builder.SslMode);
    }

    [Theory]
    [InlineData("DELETE FROM patient")]
    [InlineData("WITH removed AS (DELETE FROM patient) SELECT * FROM removed")]
    [InlineData("SELECT * FROM patient INTO OUTFILE '/tmp/patient.csv'")]
    [InlineData("SELECT 1; DROP TABLE patient")]
    public void ValidateSelectSql_拒绝非只读语句(string sql)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DatabaseQueryService.ValidateSelectSql(sql, IngestionService.DatabaseTypeDoris));

        Assert.Contains("查询", error.Message);
    }

    [Fact]
    public void ValidateSelectSql_允许字符串中出现只读关键字名称()
    {
        DatabaseQueryService.ValidateSelectSql(
            "SELECT 'DELETE' AS action_name FROM VIEW_P_V",
            IngestionService.DatabaseTypeDoris);
    }

    [Fact]
    public void BuildColumnReference_Doris使用反引号()
    {
        Assert.Equal(
            "q.`PATIENT_SN`",
            DatabaseQueryService.BuildColumnReference(
                IngestionService.DatabaseTypeDoris,
                "PATIENT_SN"));
        Assert.Equal(
            "@visitSn",
            DatabaseQueryService.BuildParameterReference(
                IngestionService.DatabaseTypeDoris,
                "visitSn"));
    }

    [Fact]
    public void BuildColumnReference_MySql使用反引号()
    {
        Assert.Equal(
            "q.`PATIENT_SN`",
            DatabaseQueryService.BuildColumnReference(
                IngestionService.DatabaseTypeMySql,
                "PATIENT_SN"));
    }

    [Theory]
    [InlineData("PATIENT-SN")]
    [InlineData("PATIENT_SN DESC")]
    public void BuildColumnReference_拒绝不安全字段名(string field)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseQueryService.BuildColumnReference(IngestionService.DatabaseTypeDoris, field));
    }
}

public class PendingSyncContentTests
{
    [Fact]
    public void HasSourceRecordChanged_忽略Json属性顺序()
    {
        Assert.False(PendingSyncService.HasSourceRecordChanged(
            "{\"PATIENT_SN\":\"P1\",\"genesis_upd\":0}",
            "{\"genesis_upd\":0,\"PATIENT_SN\":\"P1\"}"));
    }

    [Fact]
    public void HasSourceRecordChanged_识别内容更新()
    {
        Assert.True(PendingSyncService.HasSourceRecordChanged(
            "{\"PATIENT_SN\":\"P1\",\"genesis_upd\":0}",
            "{\"PATIENT_SN\":\"P1\",\"genesis_upd\":1}"));
    }
}
