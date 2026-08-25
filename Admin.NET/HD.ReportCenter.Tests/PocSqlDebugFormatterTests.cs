using HD.ReportCenter.Application.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocSqlDebugFormatterTests
{
    [Fact]
    public void Create_PreservesParameterizedSqlAndBuildsDisplayOnlySql()
    {
        const string sql = """
            SELECT *
            FROM REG_TRADE
            WHERE DEPARTMENT_ID = @hisDepartmentId
              AND USER_NAME = @userName
            OFFSET @rcPageOffset ROWS FETCH NEXT @rcPageTake ROWS ONLY
            """;
        var parameters = new[]
        {
            PocDatabaseProvider.CreateParameter("hisDepartmentId", "string", "D'03"),
            PocDatabaseProvider.CreateParameter("userName", "string", "super"),
            PocDatabaseProvider.CreateParameter("rcPageOffset", "int", 0),
            PocDatabaseProvider.CreateParameter("rcPageTake", "int", 21)
        };

        var debug = PocSqlDebugFormatter.Create(sql, parameters);

        Assert.Equal(sql, debug.ParameterizedSql);
        Assert.Contains("DEPARTMENT_ID = 'D''03'", debug.DisplaySql);
        Assert.Contains("USER_NAME = 'super'", debug.DisplaySql);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 21 ROWS ONLY", debug.DisplaySql);
        Assert.StartsWith("-- 仅用于开发调试展示", debug.DisplaySql);
        Assert.Collection(
            debug.Parameters,
            parameter => Assert.Equal("@hisDepartmentId", parameter.Name),
            parameter => Assert.Equal("@userName", parameter.Name),
            parameter => Assert.Equal("@rcPageOffset", parameter.Name),
            parameter => Assert.Equal("@rcPageTake", parameter.Name));

        var logParameters = PocSqlDebugFormatter.FormatParametersForLog(debug.Parameters);
        Assert.Contains("@hisDepartmentId [String] = 'D''03'", logParameters);
        Assert.Contains("@userName [String] = 'super'", logParameters);
        Assert.Contains("@rcPageOffset [Int32] = 0", logParameters);
        Assert.Contains("@rcPageTake [Int32] = 21", logParameters);
    }
}
