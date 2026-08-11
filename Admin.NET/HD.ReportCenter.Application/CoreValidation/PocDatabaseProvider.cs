using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using SqlSugarDbType = SqlSugar.DbType;

namespace HD.ReportCenter.Application.CoreValidation;

internal sealed record PocDataSourceEndpoint(
    string Server,
    int Port,
    string Database,
    string UserName,
    string Password);

/// <summary>把报表数据源的连接、参数和分页差异收敛到一个数据库提供器。</summary>
internal static class PocDatabaseProvider
{
    private static readonly Regex OracleErrorRegex = new(
        @"\bORA-\d{5}:\s*[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ConnectionSecretRegex = new(
        @"\b(password|pwd)\s*=\s*[^;\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public const string SqlServer = "SqlServer";
    public const string Oracle = "Oracle";
    public const string MySql = "MySql";
    public const string PostgreSql = "PostgreSQL";

    public static readonly IReadOnlyList<string> SupportedTypes =
        [SqlServer, Oracle, MySql, PostgreSql];

    public static string NormalizeType(string dbType)
    {
        var matched = SupportedTypes.FirstOrDefault(item =>
            item.Equals(dbType?.Trim(), StringComparison.OrdinalIgnoreCase));
        return matched ?? throw new PocDatasetConfigurationException(
            "DATA_SOURCE_TYPE_UNSUPPORTED",
            $"暂不支持数据库类型 '{dbType}'。");
    }

    public static int DefaultPort(string dbType) => NormalizeType(dbType) switch
    {
        SqlServer => 1433,
        Oracle => 1521,
        MySql => 3306,
        PostgreSql => 5432,
        _ => throw new UnreachableException()
    };

    public static PocDataSourceEndpoint ParseEndpoint(string dbType, string connectionString)
    {
        try
        {
            return NormalizeType(dbType) switch
            {
                SqlServer => ParseSqlServer(connectionString),
                Oracle => ParseOracle(connectionString),
                MySql => ParseMySql(connectionString),
                PostgreSql => ParsePostgreSql(connectionString),
                _ => throw new UnreachableException()
            };
        }
        catch (ArgumentException exception)
        {
            throw InvalidConnectionString(exception);
        }
    }

    public static string BuildConnectionString(
        string dbType,
        string server,
        int port,
        string database,
        string userName,
        string password,
        int timeoutSeconds) => NormalizeType(dbType) switch
        {
            SqlServer => new SqlConnectionStringBuilder
            {
                DataSource = $"{server},{port}",
                InitialCatalog = database,
                UserID = userName,
                Password = password,
                Encrypt = false,
                TrustServerCertificate = true,
                ConnectTimeout = Math.Clamp(timeoutSeconds, 1, 120),
                ApplicationName = "hd-report-center-runtime"
            }.ConnectionString,
            Oracle => new OracleConnectionStringBuilder
            {
                DataSource = $"{server}:{port}/{database}",
                UserID = userName,
                Password = password,
                ConnectionTimeout = Math.Clamp(timeoutSeconds, 1, 120)
            }.ConnectionString,
            MySql => new MySqlConnectionStringBuilder
            {
                Server = server,
                Port = checked((uint)port),
                Database = database,
                UserID = userName,
                Password = password,
                ConnectionTimeout = checked((uint)Math.Clamp(timeoutSeconds, 1, 120)),
                DefaultCommandTimeout = checked((uint)Math.Clamp(timeoutSeconds, 1, 600)),
                SslMode = MySqlSslMode.Preferred,
                ApplicationName = "hd-report-center-runtime"
            }.ConnectionString,
            PostgreSql => new NpgsqlConnectionStringBuilder
            {
                Host = server,
                Port = port,
                Database = database,
                Username = userName,
                Password = password,
                Timeout = Math.Clamp(timeoutSeconds, 1, 120),
                CommandTimeout = Math.Clamp(timeoutSeconds, 1, 600),
                SslMode = SslMode.Prefer,
                TrustServerCertificate = true,
                ApplicationName = "hd-report-center-runtime"
            }.ConnectionString,
            _ => throw new UnreachableException()
        };

    public static SqlSugarClient CreateClient(
        string dbType,
        string connectionString,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = ToSqlSugarType(dbType),
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        });
        client.Ado.CommandTimeOut = Math.Clamp(timeoutSeconds, 1, 600);
        client.Ado.CancellationToken = cancellationToken;
        return client;
    }

    public static SugarParameter CreateParameter(string code, string dataType, object? value)
    {
        var parameter = new SugarParameter(
            $"@{code}",
            value ?? DBNull.Value,
            dataType.Trim().ToLowerInvariant() switch
            {
                "long" or "int64" => System.Data.DbType.Int64,
                "int" or "int32" or "number" => System.Data.DbType.Int32,
                "decimal" => System.Data.DbType.Decimal,
                "date" => System.Data.DbType.Date,
                "datetime" => System.Data.DbType.DateTime2,
                "bool" or "boolean" => System.Data.DbType.Boolean,
                _ => System.Data.DbType.String
            });
        if (parameter.DbType == System.Data.DbType.String)
        {
            parameter.Size = Math.Max(1, Math.Min(4000, Convert.ToString(value)?.Length ?? 1));
        }
        if (parameter.DbType == System.Data.DbType.Decimal)
        {
            parameter.Precision = 28;
            parameter.Scale = 8;
        }
        return parameter;
    }

    public static string BuildPagedQuery(string dbType, string queryText) =>
        NormalizeType(dbType) switch
        {
            SqlServer or Oracle =>
                $"{queryText.TrimEnd()} OFFSET @__offset ROWS FETCH NEXT @__take ROWS ONLY",
            MySql or PostgreSql =>
                $"{queryText.TrimEnd()} LIMIT @__take OFFSET @__offset",
            _ => throw new UnreachableException()
        };

    public static bool IsTimeout(Exception exception) =>
        exception is SqlException { Number: -2 } ||
        exception is TimeoutException ||
        exception.InnerException is TimeoutException ||
        exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("超时", StringComparison.OrdinalIgnoreCase);

    public static bool IsDatabaseException(Exception exception) =>
        exception is DbException or SqlSugarException;

    public static string GetSafeConnectionError(Exception exception)
    {
        if (IsTimeout(exception))
        {
            return "连接超时。";
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            var oracleError = OracleErrorRegex.Match(current.Message);
            if (oracleError.Success)
            {
                return SanitizeConnectionError(oracleError.Value);
            }

            var providerError = current switch
            {
                SqlException sql => $"SQL Server {sql.Number}: {FirstLine(sql.Message)}",
                MySqlException mysql => $"MySQL {mysql.Number}: {FirstLine(mysql.Message)}",
                PostgresException postgres =>
                    $"PostgreSQL {postgres.SqlState}: {postgres.MessageText}",
                OracleException oracle => FirstLine(oracle.Message),
                DbException database => FirstLine(database.Message),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(providerError))
            {
                return SanitizeConnectionError(providerError);
            }
        }

        return "数据库拒绝连接，未返回可识别的错误说明。";
    }

    private static SqlSugarDbType ToSqlSugarType(string dbType) => NormalizeType(dbType) switch
    {
        SqlServer => SqlSugarDbType.SqlServer,
        Oracle => SqlSugarDbType.Oracle,
        MySql => SqlSugarDbType.MySql,
        PostgreSql => SqlSugarDbType.PostgreSQL,
        _ => throw new UnreachableException()
    };

    private static PocDataSourceEndpoint ParseSqlServer(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var parts = builder.DataSource.Split(',', 2, StringSplitOptions.TrimEntries);
        return new PocDataSourceEndpoint(
            parts[0],
            parts.Length == 2 && int.TryParse(parts[1], out var port) ? port : 1433,
            builder.InitialCatalog,
            builder.UserID,
            builder.Password);
    }

    private static PocDataSourceEndpoint ParseOracle(string connectionString)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        var slashIndex = dataSource.LastIndexOf('/');
        var colonIndex = slashIndex > 0
            ? dataSource.LastIndexOf(':', slashIndex - 1)
            : -1;
        if (slashIndex <= 0 || colonIndex <= 0 ||
            !int.TryParse(dataSource[(colonIndex + 1)..slashIndex], out var port))
        {
            throw new ArgumentException("Oracle 数据源必须使用 host:port/serviceName 格式。");
        }
        return new PocDataSourceEndpoint(
            dataSource[..colonIndex],
            port,
            dataSource[(slashIndex + 1)..],
            builder.UserID,
            builder.Password);
    }

    private static PocDataSourceEndpoint ParseMySql(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return new PocDataSourceEndpoint(
            builder.Server,
            checked((int)builder.Port),
            builder.Database,
            builder.UserID,
            builder.Password);
    }

    private static PocDataSourceEndpoint ParsePostgreSql(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return new PocDataSourceEndpoint(
            builder.Host ?? string.Empty,
            builder.Port,
            builder.Database ?? string.Empty,
            builder.Username ?? string.Empty,
            builder.Password ?? string.Empty);
    }

    private static PocDatasetConfigurationException InvalidConnectionString(Exception inner)
    {
        _ = inner;
        return new PocDatasetConfigurationException(
            "DATA_SOURCE_INVALID",
            "数据库连接配置无效。");
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
        ?? "数据库连接失败。";

    private static string SanitizeConnectionError(string value)
    {
        var sanitized = ConnectionSecretRegex.Replace(value, "$1=[已隐藏]").Trim();
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }
}
