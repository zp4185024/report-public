using HD.ReportCenter.Application.CoreValidation;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace HD.ReportCenter.Tests;

public sealed class PocJnpfMigrationRunnerTests : IDisposable
{
    private readonly SqlSugarClient _database;
    private readonly PocJnpfMigrationRunner _runner;

    /// <summary>初始化 PocJnpfMigrationRunnerTests 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocJnpfMigrationRunnerTests()
    {
        _database = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute
        });
        _database.Ado.Open();
        _database.CodeFirst.InitTables<
            PocDataSource,
            PocReportDataset,
            PocReport,
            PocReportParameter,
            PocReportTemplate>();
        _runner = new PocJnpfMigrationRunner(new PocDataAssetStore(_database), _database);
    }

    /// <summary>验证 Resolve Data Source Async Reads Stored Jnpf Credential 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ResolveDataSourceAsync_ReadsStoredJnpfCredential()
    {
        InsertDataSource(
            1,
            "jnpf_metadata",
            "MySql",
            PocDatabaseProvider.BuildConnectionString(
                "MySql", "188.188.2.5", 13306, "jnpf_test_520", "root", "jnpf-secret", 30));
        var jnpf = await _runner.ResolveDataSourceAsync(
            "jnpf_metadata",
            PocDatabaseProvider.MySql,
            "JNPF 来源");

        Assert.Equal("jnpf_test_520", jnpf.Database);
        Assert.Equal("jnpf-secret", jnpf.Password);
    }

    /// <summary>验证 Resolve Data Source Async Rejects Wrong Database Type 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ResolveDataSourceAsync_RejectsWrongDatabaseType()
    {
        InsertDataSource(
            1,
            "his_oracle",
            "Oracle",
            PocDatabaseProvider.BuildConnectionString(
                "Oracle", "188.188.2.5", 2521, "orclpdb", "HIS0806", "his-secret", 30));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _runner.ResolveDataSourceAsync(
                "his_oracle",
                PocDatabaseProvider.MySql,
                "JNPF 来源"));
    }

    /// <summary>验证 Resolve Data Source Async Allows Selected Report Runtime Database Type 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ResolveDataSourceAsync_AllowsSelectedReportRuntimeDatabaseType()
    {
        InsertDataSource(
            1,
            "report_oracle",
            "Oracle",
            PocDatabaseProvider.BuildConnectionString(
                "Oracle", "188.188.2.5", 2521, "orclpdb", "report-reader", "report-secret", 30));

        var reportSource = await _runner.ResolveDataSourceAsync(
            "report_oracle",
            expectedType: null,
            "报表运行");

        Assert.Equal("orclpdb", reportSource.Database);
    }

    /// <summary>验证迁移请求保留超过 50 个源报表标识，避免批量全选时静默丢失后续报表。</summary>
    [Fact]
    public void MigrationRequest_PreservesLargeSourceReportSelection()
    {
        var sourceReportIds = Enumerable.Range(1, 120).Select(index => $"report-{index}").ToArray();
        var request = new PocJnpfMigrationExecuteRequest(SourceReportIds: sourceReportIds);

        Assert.Equal(120, request.SourceReportIds!.Length);
        Assert.Equal(0, request.MaxReports);
    }

    /// <summary>验证 Resolve Target Connection String Async Uses Current Report Center Connection 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ResolveTargetConnectionStringAsync_UsesCurrentReportCenterConnection()
    {
        var connectionString = PocDatabaseProvider.BuildConnectionString(
            "SqlServer", "188.188.2.5", 1433, "hd_report_center", "hdhis", "target-secret", 30);
        _database.CurrentConnectionConfig.ConnectionString = connectionString;
        _database.CurrentConnectionConfig.DbType = DbType.SqlServer;

        var resolved = await _runner.ResolveTargetConnectionStringAsync();

        Assert.Equal(connectionString, resolved);
    }

    /// <summary>验证 Resolve Target Connection String Async Rejects Non Sql Server Current Connection 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ResolveTargetConnectionStringAsync_RejectsNonSqlServerCurrentConnection()
    {
        _database.CurrentConnectionConfig.DbType = DbType.Sqlite;

        await Assert.ThrowsAnyAsync<Exception>(() => _runner.ResolveTargetConnectionStringAsync());
    }

    /// <summary>验证 Resolve Target Connection String Async Rejects Wrong Current Database 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ResolveTargetConnectionStringAsync_RejectsWrongCurrentDatabase()
    {
        _database.CurrentConnectionConfig.DbType = DbType.SqlServer;
        _database.CurrentConnectionConfig.ConnectionString = PocDatabaseProvider.BuildConnectionString(
            "SqlServer", "188.188.2.5", 1433, "master", "hdhis", "target-secret", 30);

        await Assert.ThrowsAnyAsync<Exception>(() => _runner.ResolveTargetConnectionStringAsync());
    }

    /// <summary>验证 Converter Transforms Fields Sql Parameters And Template Without Power Shell 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Converter_TransformsFieldsSqlParametersAndTemplateWithoutPowerShell()
    {
        var fields = PocJnpfMigrationConverter.ConvertFields("""
            [
              {"field":"PID","fieldName":"病历号","dataType":"string"},
              {"field":"患者姓名","fieldName":"姓名","dataType":"string"},
              {"field":"AGE","fieldName":"年龄","dataType":"Integer"}
            ]
            """);

        // 第一阶段：构造 Converter_TransformsFieldsSqlParametersAndTemplateWithoutPowerShell 当前步骤需要的中间数据和执行参数。
        var sql = PocJnpfMigrationConverter.ConvertSql(
            "SELECT PID, 患者姓名, AGE FROM V_YB_FEE_DETAIL WHERE CREATE_DATE >= {sdate} AND CREATE_DATE < {edate}",
            fields,
            "Oracle");
        var template = PocJnpfMigrationConverter.ConvertSnapshot(
            """
            {
              "sheets": {
                "sheet1": {
                  "name": "门诊明细",
                  "cellData": {
                    "0": {
                      "0": {"v":"${门诊数据集.PID}"},
                      "1": {"v":"${门诊数据集.患者姓名}"}
                    },
                    "1": {"0": {"f":"=SUM(A2)"}}
                  },
                  "mergeData": [],
                  "floatImages": ["legacy-image"]
                }
              },
              "sheetOrder": ["sheet1"],
              "resources": ["legacy-resource"]
            }
            """,
            "门诊数据集",
            fields);

        // 第2阶段：根据 Converter_TransformsFieldsSqlParametersAndTemplateWithoutPowerShell 当前分支转换输入数据，并保存该阶段产生的中间结果。
        Assert.Equal(3, fields.Count);
        Assert.Equal("PID", fields[0].Code);
        Assert.Equal("field_02", fields[1].Code);
        Assert.Equal("number", fields[2].DataType);
        Assert.Contains("@sdate", sql.QueryText);
        Assert.Contains("@edate", sql.QueryText);
        Assert.Contains("field_02", sql.QueryText);
        Assert.Contains("DEV_SQL_OBJECT_REVIEW:V_YB_FEE_DETAIL", sql.Markers);
        Assert.Contains("{{detail.PID}}", template.SnapshotJson);
        Assert.Contains("{{detail.field_02}}", template.SnapshotJson);
        Assert.Contains("LEGACY_WORKBOOK_RESOURCES_REMOVED", template.Markers);
        Assert.Contains("UNSUPPORTED_TEMPLATE_ASSET_REMOVED:floatImages", template.Markers);
        Assert.Equal(1, template.FormulaRangeFixCount);
        Assert.Equal(2, template.DetailFieldCount);
    }

    /// <summary>验证多行旧明细模板会选择字段最多的一行并移除其它行占位符，避免迁移后无法预览。</summary>
    [Fact]
    public void Converter_NormalizesDetailTokensAcrossMultipleRows()
    {
        // 构造跨多行的旧模板，模拟 JNPF 迁移时明细占位符分散在不同明细行的边界输入。
        var fields = new[]
        {
            new JnpfField("education", "education", "学历", "string"),
            new JnpfField("orgname", "orgname", "组织", "string"),
            new JnpfField("depName", "depName", "部门", "string"),
            new JnpfField("salary", "salary", "薪资", "string")
        };

        var template = PocJnpfMigrationConverter.ConvertSnapshot(
            """
            {
              "sheets": {
                "sheet1": {
                  "name": "Sheet1",
                  "cellData": {
                    "2": {"2": {"v":"${用户数据集.education}"}},
                    "3": {
                      "0": {"v":"${用户数据集.orgname}"},
                      "1": {"v":"${用户数据集.depName}"},
                      "2": {"v":"${用户数据集.salary}"}
                    }
                  },
                  "mergeData": []
                }
              },
              "sheetOrder": ["sheet1"]
            }
            """,
            "用户数据集",
            fields);

        // 校验转换器选定主明细行、移除冲突占位符并同步生成可展开的字段绑定。
        var snapshot = JObject.Parse(template.SnapshotJson);
        var binding = JObject.Parse(template.BindingJson);

        Assert.Null(snapshot["sheets"]?["sheet1"]?["cellData"]?["2"]?["2"]?["v"]);
        Assert.Equal("{{detail.orgname}}", snapshot["sheets"]?["sheet1"]?["cellData"]?["3"]?["0"]?["v"]);
        Assert.Equal("{{detail.depName}}", snapshot["sheets"]?["sheet1"]?["cellData"]?["3"]?["1"]?["v"]);
        Assert.Equal("{{detail.salary}}", snapshot["sheets"]?["sheet1"]?["cellData"]?["3"]?["2"]?["v"]);
        Assert.Equal(3, binding["detailBinding"]?["fields"]?.Count());
        Assert.Equal(3, binding["detailBinding"]?["templateRow"]?.Value<int>());
        Assert.Contains("MULTI_DETAIL_PRIMARY_SELECTED:2", template.Markers);
        Assert.Contains("SECONDARY_DETAIL_TOKEN_REMOVED:1", template.Markers);
    }

    /// <summary>验证 Create Candidate Preserves Rich Text Heading From Jnpf Template 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void CreateCandidate_PreservesRichTextHeadingFromJnpfTemplate()
    {
        var sourceInterface = new JnpfSourceInterface(
            "interface-1",
            "医保接口",
            "{}",
            "[]",
            "[{\"field\":\"医疗类别\",\"fieldName\":\"医疗类别\",\"dataType\":\"string\"}]");
        // 第一阶段：构造 CreateCandidate_PreservesRichTextHeadingFromJnpfTemplate 当前步骤需要的中间数据和执行参数。
        var package = new JnpfSourcePackage(
            "report-1",
            "医保-锦州市城镇职工医疗保险费用申报表-异地",
            "BB20250913000001242e9",
            "version-1",
            1,
            1,
            """
            {
              "sheets": {
                "sheet1": {
                  "name": "Sheet1",
                  "cellData": {
                    "1": {
                      "3": {
                        "p": {
                          "body": {
                            "dataStream": "模板原始标题1\r\n",
                            "textRuns": [{"st":0,"ed":7,"ts":{"fs":"16","bl":"1"}}]
                          }
                        }
                      }
                    },
                    "5": {"0":{"v":"${医保数据集.医疗类别}"}}
                  },
                  "mergeData": [
                    {"startRow": "1", "endRow": "1", "startColumn": "3", "endColumn": "8"}
                  ]
                }
              },
              "sheetOrder": ["sheet1"]
            }
            """,
            "[]",
            "[]",
            [new JnpfSourceDataset(
                "dataset-1",
                "医保数据集",
                "[]",
                sourceInterface,
                new JnpfDatabaseLink("database-1", "DATA_TRANSFER", "Oracle"),
                "SELECT 医疗类别 FROM V_YB_FEE_DETAIL")]);

        // 第2阶段：构造 CreateCandidate_PreservesRichTextHeadingFromJnpfTemplate 当前步骤需要的中间数据和执行参数。
        var candidate = PocJnpfMigrationConverter.CreateCandidate(package, "report_oracle", "Oracle");

        var migration = Assert.IsType<JnpfMigration>(candidate.Migration);
        var snapshot = JObject.Parse(migration.SnapshotJson);
        Assert.Equal(
            "模板原始标题1\r\n",
            snapshot["sheets"]?["sheet1"]?["cellData"]?["1"]?["3"]?["p"]?["body"]?["dataStream"]?.Value<string>());
        Assert.Null(snapshot["sheets"]?["sheet1"]?["cellData"]?["1"]?["3"]?["v"]);
    }

    /// <summary>验证 Converter Rejects Unclosed Jnpf Parameter Marker 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Converter_RejectsUnclosedJnpfParameterMarker()
    {
        var fields = PocJnpfMigrationConverter.ConvertFields(
            "[{\"field\":\"PID\",\"fieldName\":\"病历号\",\"dataType\":\"string\"}]");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PocJnpfMigrationConverter.ConvertSql(
                "SELECT PID FROM patients WHERE created_at >= #{bad",
                fields,
                "SqlServer"));

        Assert.Contains("未识别的 JNPF 参数标记", exception.Message);
    }

    /// <summary>验证 Create Candidate Uses Template Name For Dataset Display Name 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void CreateCandidate_UsesTemplateNameForDatasetDisplayName()
    {
        var sourceInterface = new JnpfSourceInterface(
            "interface-1",
            "门诊数据接口",
            "{}",
            "[]",
            "[{\"field\":\"PID\",\"fieldName\":\"病历号\",\"dataType\":\"string\"}]");
        // 第一阶段：构造 CreateCandidate_UsesTemplateNameForDatasetDisplayName 当前步骤需要的中间数据和执行参数。
        var package = new JnpfSourcePackage(
            "report-1",
            "\t 门诊护士材料统计 ",
            "BB20260609000001",
            "version-1",
            1,
            1,
            """
            {
              "sheets": {
                "sheet1": {
                  "name": "Sheet1",
                  "cellData": {"0":{"0":{"v":"${DataSet680f97.PID}"}}},
                  "mergeData": []
                }
              },
              "sheetOrder": ["sheet1"]
            }
            """,
            "[]",
            "[]",
            [new JnpfSourceDataset(
                "dataset-1",
                "DataSet680f97",
                "[]",
                sourceInterface,
                new JnpfDatabaseLink("database-1", "DATA_TRANSFER", "Oracle"),
                "SELECT PID FROM PATIENT")]);

        // 第2阶段：构造 CreateCandidate_UsesTemplateNameForDatasetDisplayName 当前步骤需要的中间数据和执行参数。
        var candidate = PocJnpfMigrationConverter.CreateCandidate(
            package,
            "data_source_5",
            "Oracle");

        Assert.True(candidate.Migratable, string.Join("; ", candidate.Blockers));
        var migration = Assert.IsType<JnpfMigration>(candidate.Migration);
        Assert.Equal("门诊护士材料统计（JNPF迁移）", migration.ReportName);
        Assert.Equal(migration.ReportName, migration.DatasetName);
    }

    /// <summary>验证 Create Candidate Uses Jnpf Query Parameter Metadata Before Name Heuristics 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void CreateCandidate_UsesJnpfQueryParameterMetadataBeforeNameHeuristics()
    {
        var sourceInterface = new JnpfSourceInterface(
            "interface-1",
            "医保结算查询",
            "{}",
            "[{\"id\":\"49d00c\",\"field\":\"med_type\",\"dataType\":\"varchar\",\"defaultValue\":\"\",\"required\":0,\"fieldName\":\"\"},{\"id\":\"92063c\",\"field\":\"localType\",\"dataType\":\"varchar\",\"defaultValue\":\"\",\"required\":0,\"fieldName\":\"\"},{\"id\":\"848528\",\"field\":\"startDate\",\"dataType\":\"varchar\",\"defaultValue\":\"\",\"required\":0,\"fieldName\":\"\"}]",
            "[{\"field\":\"AMOUNT\",\"fieldName\":\"金额\",\"dataType\":\"number\"}]");
        // 第一阶段：构造 CreateCandidate_UsesJnpfQueryParameterMetadataBeforeNameHeuristics 当前步骤需要的中间数据和执行参数。
        var package = new JnpfSourcePackage(
            "report-1",
            "医保结算查询",
            "BB20250913000001",
            "version-1",
            1,
            1,
            """
            {
              "sheets": {
                "sheet1": {
                  "name": "Sheet1",
                  "cellData": {
                    "0": {"0": {"v": "#{DataParameter|848528.startDate}"}},
                    "1": {"0": {"v": "#{@hisOrganizeName}"}},
                    "2": {"0": {"v": "#{@hisYbOrgId}"}},
                    "5": {"0": {"v": "${DataSet848528.AMOUNT}"}},
                    "9": {"0": {"v": "#{DataSet848528.AMOUNT}"}}
                  },
                  "mergeData": [
                    {"startRow":"1","endRow":"1","startColumn":"1","endColumn":"2","rangeType":"0"}
                  ]
                }
              },
              "sheetOrder": ["sheet1"]
            }
            """,
            "{\"queryList\":[{\"vModel\":\"DataParameter|848528.startDate\",\"label\":\"结算时间\",\"type\":\"input\",\"config\":{\"format\":\"yyyy-MM-dd\"}}]}",
            "[]",
            [new JnpfSourceDataset(
                "dataset-1",
                "DataSet848528",
                "[{\"id\":\"49d00c\",\"field\":\"med_type\",\"dataType\":\"varchar\",\"defaultValue\":\"\",\"required\":0,\"fieldName\":\"\",\"relationField\":\"390\",\"sourceType\":2},{\"id\":\"92063c\",\"field\":\"localType\",\"dataType\":\"varchar\",\"defaultValue\":\"\",\"required\":0,\"fieldName\":\"\",\"relationField\":\"异地\",\"sourceType\":2},{\"id\":\"848528\",\"field\":\"startDate\",\"dataType\":\"varchar\",\"defaultValue\":\"\",\"required\":0,\"fieldName\":\"\",\"relationField\":\"\",\"sourceType\":3}]",
                sourceInterface,
                new JnpfDatabaseLink("database-1", "DATA_TRANSFER", "Oracle"),
                "SELECT AMOUNT FROM V_YB_FEE_O WHERE FEE_DATE >= TO_DATE({startDate} || '-01', 'YYYY-MM-DD') AND MED_TYPE = {med_type} AND LOCAL_TYPE = {localType} AND ORG_ID = @hisYbOrgId")]);

        // 第2阶段：构造 CreateCandidate_UsesJnpfQueryParameterMetadataBeforeNameHeuristics 当前步骤需要的中间数据和执行参数。
        var candidate = PocJnpfMigrationConverter.CreateCandidate(
            package,
            "data_source_5",
            "Oracle");

        Assert.True(candidate.Migratable, string.Join("; ", candidate.Blockers));
        var migration = Assert.IsType<JnpfMigration>(candidate.Migration);
        var startDate = Assert.Single(migration.Parameters, item => item.Code == "startDate");
        Assert.Equal("结算时间", startDate.DisplayName);
        Assert.Equal("month", startDate.ControlType);
        Assert.Equal("string", startDate.DataType);

        var medicalType = Assert.Single(migration.Parameters, item => item.Code == "med_type");
        Assert.Equal("Fixed", medicalType.SourceType);
        Assert.Equal("390", medicalType.FixedValue);
        Assert.False(medicalType.IsVisible);
        Assert.True(medicalType.IsReadOnly);
        Assert.False(medicalType.AllowUrlPrefill);
        Assert.Null(medicalType.UrlKey);

        var localType = Assert.Single(migration.Parameters, item => item.Code == "localType");
        Assert.Equal("Fixed", localType.SourceType);
        Assert.Equal("异地", localType.FixedValue);
        Assert.Contains("PARAMETER_FIXED_VALUE_MIGRATED:med_type", candidate.Markers);
        Assert.Contains("PARAMETER_FIXED_VALUE_MIGRATED:localType", candidate.Markers);

        var organizationName = Assert.Single(
            migration.Parameters,
            item => item.Code == "hisOrganizeName");
        Assert.Equal("医院名称", organizationName.DisplayName);
        Assert.Equal("External", organizationName.SourceType);
        Assert.Equal("his", organizationName.SourceCode);
        Assert.Equal("hisOrganizeName", organizationName.SourceFieldCode);
        Assert.False(organizationName.IsVisible);
        // 第3阶段：根据 CreateCandidate_UsesJnpfQueryParameterMetadataBeforeNameHeuristics 当前分支转换输入数据，并保存该阶段产生的中间结果。
        Assert.True(organizationName.IsReadOnly);

        var insuranceOrganization = Assert.Single(
            migration.Parameters,
            item => item.Code == "hisYbOrgId");
        Assert.Equal("定点医疗机构编码", insuranceOrganization.DisplayName);
        Assert.Equal("External", insuranceOrganization.SourceType);
        Assert.Equal("hisOrganizeCode", insuranceOrganization.SourceFieldCode);

        var snapshot = Newtonsoft.Json.Linq.JObject.Parse(migration.SnapshotJson);
        Assert.Equal("{{params.startDate}}", snapshot["sheets"]?["sheet1"]?["cellData"]?["0"]?["0"]?["v"]?.ToString());
        Assert.Equal("{{params.hisOrganizeName}}", snapshot["sheets"]?["sheet1"]?["cellData"]?["1"]?["0"]?["v"]?.ToString());
        Assert.Equal("{{params.hisYbOrgId}}", snapshot["sheets"]?["sheet1"]?["cellData"]?["2"]?["0"]?["v"]?.ToString());
        Assert.Null(snapshot["sheets"]?["sheet1"]?["cellData"]?["9"]?["0"]?["v"]);
        var merge = Assert.IsType<Newtonsoft.Json.Linq.JObject>(snapshot["sheets"]?["sheet1"]?["mergeData"]?[0]);
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Integer, merge["startRow"]?.Type);
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Integer, merge["rangeType"]?.Type);

        var binding = Newtonsoft.Json.Linq.JObject.Parse(migration.BindingJson);
        Assert.Equal(3, binding["parameterBindings"]?.Count());
        var schema = Newtonsoft.Json.Linq.JObject.Parse(migration.FieldSchemaJson);
        Assert.Equal(4, schema["variables"]?.Count());
        Assert.DoesNotContain(schema["variables"]!, item =>
            item?["code"]?.ToString() == "hisOrganizeName");
        Assert.Contains("STALE_DETAIL_TOKEN_REMOVED:1", candidate.Markers);
        Assert.Contains("LEGACY_MERGE_COORDINATE_NORMALIZED:5", candidate.Markers);
    }

    /// <summary>验证缺失数据集的旧静态模板迁移后默认整表预览，并生成不返回业务数据的只读占位查询。</summary>
    [Fact]
    public void CreateCandidate_MigratesStaticTemplateWithoutDataset()
    {
        var package = new JnpfSourcePackage(
            "report-static",
            "药品销售汇总表",
            "BB20250922000001",
            "version-static",
            1,
            1,
            """
            {
              "sheets": {
                "sheet1": {
                  "name": "汇总表",
                  "cellData": {"0":{"0":{"v":"药品销售汇总表"}}},
                  "mergeData": []
                }
              },
              "sheetOrder": ["sheet1"]
            }
            """,
            "[]",
            "[]",
            []);

        var candidate = PocJnpfMigrationConverter.CreateCandidate(package, "report_oracle", "Oracle");

        Assert.True(candidate.Migratable, string.Join("; ", candidate.Blockers));
        var migration = Assert.IsType<JnpfMigration>(candidate.Migration);
        Assert.Equal("SELECT NULL AS \"legacy_value\" FROM dual WHERE 1 = 0 ORDER BY 1", migration.QueryText);
        Assert.Equal("{\"mode\":\"Full\",\"pageSize\":20,\"showExactTotal\":false}", migration.PreviewSettingsJson);
        Assert.Contains("DATASET_MISSING_PLACEHOLDER_CREATED", candidate.Markers);
        Assert.Contains("STATIC_TEMPLATE_NO_DETAIL_BINDING", candidate.Markers);
        Assert.Equal(JTokenType.Null, JObject.Parse(migration.BindingJson)["detailBinding"]?.Type);
    }

    /// <summary>验证以说明性注释开头的只读 SQL 可迁移，避免合法旧接口被误判为非查询语句。</summary>
    [Fact]
    public void ConvertSql_AllowsLeadingCommentsBeforeSelect()
    {
        var fields = PocJnpfMigrationConverter.ConvertFields(
            "[{\"field\":\"PID\",\"fieldName\":\"病历号\",\"dataType\":\"string\"}]");

        var result = PocJnpfMigrationConverter.ConvertSql(
            "-- 报表字段命名为大写\r\n/* 只读接口 */\r\nSELECT PID FROM PATIENT_FEE_O",
            fields,
            "Oracle");

        Assert.StartsWith("SELECT PID", result.QueryText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证 Write Migration Async Inserts Configuration Graph And Skips Existing Report 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task WriteMigrationAsync_InsertsConfigurationGraphAndSkipsExistingReport()
    {
        InsertDataSource(
            10,
            "report_center",
            "Oracle",
            PocDatabaseProvider.BuildConnectionString(
                "Oracle", "188.188.2.5", 2521, "orclpdb", "report-reader", "report-secret", 30));
        // 第一阶段：构造 WriteMigrationAsync_InsertsConfigurationGraphAndSkipsExistingReport 当前步骤需要的中间数据和执行参数。
        var migration = new JnpfMigration(
            "source-report-1",
            "source-version-1",
            "jnpf_outpatient_materials",
            "门诊护士材料统计（JNPF迁移）",
            "jnpf_outpatient_materials",
            "门诊护士材料统计",
            "report_center",
            "Oracle",
            "SELECT @sdate AS START_DATE",
            "{\"variables\":[],\"fields\":[{\"code\":\"START_DATE\",\"displayName\":\"开始日期\",\"dataType\":\"datetime\"}]}",
            [new JnpfParameter(
                "sdate", "开始日期", "string", "date", "Form", null, null, "sdate",
                null, null, null, true, false, true, true, 0)],
            "门诊护士材料统计",
            "{\"sheets\":{},\"sheetOrder\":[]}",
            "{\"version\":1}",
            1,
            "{\"mode\":\"Full\",\"pageSize\":20,\"showExactTotal\":false}",
            "{\"defaultMode\":\"Normal\"}",
            "[]",
            1,
            1,
            1);

        // 第2阶段：调用 WriteMigrationAsync_InsertsConfigurationGraphAndSkipsExistingReport 依赖的异步能力，等待并校验返回数据。
        var inserted = await _runner.WriteMigrationAsync(migration);
        var skipped = await _runner.WriteMigrationAsync(migration);

        Assert.Equal("Inserted", inserted.Status);
        Assert.Equal("Skipped", skipped.Status);
        Assert.Equal("jnpf_outpatient_materials", Assert.Single(_database.Queryable<PocReport>().ToList()).Code);
        Assert.Equal("report_center", Assert.Single(_database.Queryable<PocReportDataset>().ToList()).DataSourceCode);
        Assert.Equal("sdate", Assert.Single(_database.Queryable<PocReportParameter>().ToList()).Code);
        Assert.Equal(1, Assert.Single(_database.Queryable<PocReportTemplate>().ToList()).Revision);
    }

    /// <summary>负责 PocJnpfMigrationRunnerTests 的 Insert Data Source 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="id">需要定位的业务记录标识。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="dbType">dbType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="connectionString">connectionString 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private void InsertDataSource(long id, string code, string dbType, string connectionString)
    {
        _database.Insertable(new PocDataSource
        {
            Id = id,
            Code = code,
            Name = code,
            DbType = dbType,
            ConnectionString = connectionString,
            DefaultTimeoutSeconds = 30,
            DefaultMaxRows = 20_000,
            Revision = 1,
            IsEnabled = true,
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now,
            CreateUserId = 0,
            CreateUserName = string.Empty,
            UpdateUserId = 0,
            UpdateUserName = string.Empty
        }).ExecuteCommand();
    }

    /// <summary>释放 PocJnpfMigrationRunnerTests 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
