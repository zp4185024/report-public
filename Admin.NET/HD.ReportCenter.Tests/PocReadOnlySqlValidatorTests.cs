using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocReadOnlySqlValidatorTests
{
    [Theory]
    [InlineData("SELECT DepartmentName FROM Workload WHERE HospitalId = @hospitalId")]
    [InlineData("WITH source AS (SELECT 1 AS Sequence) SELECT Sequence FROM source")]
    [InlineData("SELECT '-- literal' AS TextValue, @hospitalId AS HospitalId")]
    [InlineData("-- report header\nSELECT DepartmentName FROM Workload WHERE HospitalId = @hospitalId")]
    [InlineData("SELECT DepartmentName FROM Workload /* safe note; DELETE FROM Workload */ WHERE HospitalId = @hospitalId")]
    [InlineData("SELECT/* adjacent comment */DepartmentName FROM Workload WHERE HospitalId = @hospitalId")]
    public void Validate_AllowsSingleReadOnlyQuery(string query)
    {
        var parameters = PocReadOnlySqlValidator.Validate(
            query,
            ["hospitalId"],
            PocPreviewMode.Full);

        Assert.All(parameters, parameter => Assert.Equal("hospitalId", parameter));
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT * FROM #TemporaryWorkload")]
    [InlineData("SELECT * INTO BackupRows FROM Workload")]
    [InlineData("UPDATE Workload SET Value = 1")]
    [InlineData("WITH changed AS (DELETE FROM Workload OUTPUT deleted.Id) SELECT * FROM changed")]
    [InlineData("EXEC dbo.RunReport")]
    public void Validate_RejectsUnsafeQuery(string query)
    {
        var exception = Assert.Throws<PocDatasetConfigurationException>(() =>
            PocReadOnlySqlValidator.Validate(query, [], PocPreviewMode.Full));

        Assert.Equal("DATASET_QUERY_NOT_READ_ONLY", exception.Code);
    }

    [Fact]
    public void Validate_RejectsUndefinedSqlParameter()
    {
        var exception = Assert.Throws<PocDatasetConfigurationException>(() =>
            PocReadOnlySqlValidator.Validate(
                "SELECT * FROM Workload WHERE HospitalId = @hospitalId AND DeptId = @deptId",
                ["hospitalId"],
                PocPreviewMode.Full));

        Assert.Equal("DATASET_PARAMETER_UNDEFINED", exception.Code);
    }

    [Fact]
    public void Validate_RejectsUnclosedBlockComment()
    {
        var exception = Assert.Throws<PocDatasetConfigurationException>(() =>
            PocReadOnlySqlValidator.Validate(
                "SELECT 1 /* unfinished",
                [],
                PocPreviewMode.Full));

        Assert.Equal("DATASET_QUERY_NOT_READ_ONLY", exception.Code);
    }

    [Theory]
    [InlineData("SELECT 1 /*! UNION SELECT secret FROM users */")]
    [InlineData("SELECT 1 /*M! UNION SELECT secret FROM users */")]
    public void Validate_RejectsMySqlExecutableComments(string query)
    {
        var exception = Assert.Throws<PocDatasetConfigurationException>(() =>
            PocReadOnlySqlValidator.Validate(query, [], PocPreviewMode.Full));

        Assert.Equal("DATASET_QUERY_NOT_READ_ONLY", exception.Code);
    }

    [Fact]
    public void Validate_RequiresStableOrderForPagedPreview()
    {
        var exception = Assert.Throws<PocDatasetConfigurationException>(() =>
            PocReadOnlySqlValidator.Validate(
                "SELECT Id FROM Workload",
                [],
                PocPreviewMode.Paged));

        Assert.Equal("PAGING_ORDER_REQUIRED", exception.Code);
    }
}
