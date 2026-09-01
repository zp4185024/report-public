using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

public sealed class PocTemplateValidatorTests
{
    /// <summary>验证 Scan Returns Canonical Bindings For Valid Minimal Snapshot 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_ReturnsCanonicalBindingsForValidMinimalSnapshot()
    {
        var snapshot = CreateSnapshot();

        var binding = PocTemplateValidator.Scan(snapshot.ToString());

        Assert.Equal(1, binding.Version);
        var parameter = Assert.Single(binding.ParameterBindings);
        Assert.Equal(new PocParameterBinding("hospitalId", "report-sheet", 1, 0), parameter);

        var detail = Assert.IsType<PocDetailBinding>(binding.DetailBinding);
        Assert.Equal("detail", detail.DatasetCode);
        Assert.Equal("report-sheet", detail.SheetId);
        Assert.Equal(5, detail.TemplateRow);
        Assert.Equal(
            [
                new PocDetailFieldBinding("sequence", 0),
                new PocDetailFieldBinding("departmentName", 1),
                new PocDetailFieldBinding("outpatientCount", 2),
                new PocDetailFieldBinding("dischargeCount", 3)
            ],
            detail.Fields);
    }

    /// <summary>验证 Scan Accepts Dataset Field Codes Containing Underscores 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_AcceptsDatasetFieldCodesContainingUnderscores()
    {
        var snapshot = CreateSnapshot();
        snapshot["sheets"]!["report-sheet"]!["cellData"]!["5"] = new JObject
        {
            ["0"] = new JObject { ["v"] = "{{detail.VISIT_ID}}" }
        };
        var definition = PocReportCatalog.GetDefinition() with
        {
            Fields = [new PocDatasetFieldDefinition("VISIT_ID", "就诊标识", "string")]
        };

        var binding = PocTemplateValidator.Scan(snapshot.ToString(), definition);

        Assert.Equal(
            [new PocDetailFieldBinding("VISIT_ID", 0)],
            Assert.IsType<PocDetailBinding>(binding.DetailBinding).Fields);
    }

    /// <summary>验证 Scan Preserves Parameter Bindings When Detail Tokens Are Absent 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_PreservesParameterBindingsWhenDetailTokensAreAbsent()
    {
        var snapshot = new JObject
        {
            ["sheets"] = new JObject
            {
                ["report-sheet"] = new JObject
                {
                    ["cellData"] = new JObject
                    {
                        ["0"] = new JObject
                        {
                            ["0"] = new JObject { ["v"] = "科室工作量" }
                        },
                        ["1"] = new JObject
                        {
                            ["0"] = new JObject { ["v"] = "医院：{{params.hospitalId}}" }
                        }
                    },
                    ["mergeData"] = new JArray()
                }
            }
        };

        var binding = PocTemplateValidator.Scan(snapshot.ToString());

        Assert.Equal(
            [new PocParameterBinding("hospitalId", "report-sheet", 1, 0)],
            binding.ParameterBindings);
        Assert.Null(binding.DetailBinding);
    }

    /// <summary>验证 Scan Sorts Parameter Bindings By Canonical Location And Code 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_SortsParameterBindingsByCanonicalLocationAndCode()
    {
        // 第一阶段：构造 Scan_SortsParameterBindingsByCanonicalLocationAndCode 当前步骤需要的中间数据和执行参数。
        var snapshot = new JObject
        {
            ["sheets"] = new JObject
            {
                ["z-sheet"] = new JObject
                {
                    ["cellData"] = new JObject
                    {
                        ["9"] = new JObject
                        {
                            ["3"] = new JObject { ["v"] = "{{params.personId}}" }
                        }
                    }
                },
                ["a-sheet"] = new JObject
                {
                    ["cellData"] = new JObject
                    {
                        ["2"] = new JObject
                        {
                            ["5"] = new JObject { ["v"] = "{{params.startDate}}" }
                        },
                        ["0"] = new JObject
                        {
                            ["4"] = new JObject { ["v"] = "{{params.hospitalId}}" },
                            ["1"] = new JObject
                            {
                                ["v"] = "截止 {{params.endDate}} / 科室 {{params.deptId}}"
                            }
                        }
                    }
                }
            }
        };

        // 第2阶段：构造 Scan_SortsParameterBindingsByCanonicalLocationAndCode 当前步骤需要的中间数据和执行参数。
        var binding = PocTemplateValidator.Scan(snapshot.ToString());

        Assert.Equal(
            [
                new PocParameterBinding("deptId", "a-sheet", 0, 1),
                new PocParameterBinding("endDate", "a-sheet", 0, 1),
                new PocParameterBinding("hospitalId", "a-sheet", 0, 4),
                new PocParameterBinding("startDate", "a-sheet", 2, 5),
                new PocParameterBinding("personId", "z-sheet", 9, 3)
            ],
            binding.ParameterBindings);
    }

    /// <summary>验证 Scan Rejects Unknown Binding Codes 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="column">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="address">address 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData(1, 0, "{{params.unknown}}", "A2")]
    [InlineData(5, 0, "{{detail.unknown}}", "A6")]
    public void Scan_RejectsUnknownBindingCodes(int row, int column, string value, string address)
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, row, column, value);

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains(address, exception.Message);
    }

    /// <summary>验证 Scan Rejects Detail Token Mixed With Literal Text 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_RejectsDetailTokenMixedWithLiteralText()
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 5, 0, "序号：{{detail.sequence}}");

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A6", exception.Message);
    }

    /// <summary>验证 Scan Accepts Ascii Whitespace Inside Tokens And Around Detail 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="whitespace">whitespace 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\f")]
    [InlineData("\v")]
    public void Scan_AcceptsAsciiWhitespaceInsideTokensAndAroundDetail(string whitespace)
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 1, 0, "{{" + whitespace + "params.hospitalId" + whitespace + "}}");
        SetCellValue(
            snapshot,
            5,
            0,
            whitespace + "{{" + whitespace + "detail.sequence" + whitespace + "}}" + whitespace);

        var binding = PocTemplateValidator.Scan(snapshot.ToString());

        Assert.Equal(
            [new PocParameterBinding("hospitalId", "report-sheet", 1, 0)],
            binding.ParameterBindings);
        Assert.NotNull(binding.DetailBinding);
    }

    /// <summary>验证 Scan Rejects Non Ascii Whitespace Inside Token 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="whitespace">whitespace 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("\uFEFF")]
    [InlineData("\u0085")]
    public void Scan_RejectsNonAsciiWhitespaceInsideToken(string whitespace)
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 1, 0, "{{" + whitespace + "params.hospitalId}}");

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A2", exception.Message);
    }

    /// <summary>验证 Scan Rejects Non Ascii Whitespace Around Detail Token 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="whitespace">whitespace 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("\uFEFF")]
    [InlineData("\u0085")]
    public void Scan_RejectsNonAsciiWhitespaceAroundDetailToken(string whitespace)
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 5, 0, whitespace + "{{detail.sequence}}" + whitespace);

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A6", exception.Message);
    }

    /// <summary>验证 Scan Allows Non Ascii Whitespace In Ordinary Text Without Tokens 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_AllowsNonAsciiWhitespaceInOrdinaryTextWithoutTokens()
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 1, 0, "普通\uFEFF文本\u0085内容");

        var binding = PocTemplateValidator.Scan(snapshot.ToString());

        Assert.Empty(binding.ParameterBindings);
        Assert.NotNull(binding.DetailBinding);
    }

    /// <summary>验证 Scan Rejects Detail Tokens On Different Rows 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_RejectsDetailTokensOnDifferentRows()
    {
        var snapshot = CreateSnapshot();
        var cellData = (JObject)snapshot["sheets"]!["report-sheet"]!["cellData"]!;
        ((JObject)cellData["5"]!).Remove("3");
        cellData["6"] = new JObject
        {
            ["3"] = new JObject { ["v"] = "{{detail.dischargeCount}}" }
        };

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("D7", exception.Message);
    }

    /// <summary>验证 Scan Reports Existing Detail Region When Another Sheet Contains Detail Tokens 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_ReportsExistingDetailRegionWhenAnotherSheetContainsDetailTokens()
    {
        var snapshot = CreateSnapshot();
        ((JObject)snapshot["sheets"]!)["new-sheet"] = new JObject
        {
            ["name"] = "新模板",
            ["cellData"] = new JObject
            {
                ["0"] = new JObject
                {
                    ["0"] = new JObject { ["v"] = "{{detail.sequence}}" }
                }
            }
        };

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("已有明细区域：工作表 '科室工作量'（ID: report-sheet）第 6 行", exception.Message);
        Assert.Contains("当前冲突工作表：'新模板'（ID: new-sheet）", exception.Message);
        Assert.Contains("请先删除旧区域中的明细 token", exception.Message);
        Assert.Contains("new-sheet", exception.Message);
        Assert.Contains("A1", exception.Message);
    }

    /// <summary>验证 Scan Rejects Merge That Touches Detail Template Row 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_RejectsMergeThatTouchesDetailTemplateRow()
    {
        var snapshot = CreateSnapshot();
        snapshot["sheets"]!["report-sheet"]!["mergeData"] = new JArray
        {
            new JObject
            {
                ["startRow"] = 4,
                ["endRow"] = 5,
                ["startColumn"] = 0,
                ["endColumn"] = 1
            }
        };

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A6", exception.Message);
    }

    /// <summary>验证 Scan Rejects Malformed Token Syntax 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    [Theory]
    [InlineData("{{params.hospitalId}")]
    [InlineData("{params.hospitalId}}")]
    [InlineData("{{params.}}")]
    [InlineData("{{params.hospitalId}} {{broken}}")]
    public void Scan_RejectsMalformedTokenSyntax(string value)
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 1, 0, value);

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A2", exception.Message);
    }

    /// <summary>验证 Scan Rejects Invalid Coordinate Keys 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="coordinate">coordinate 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="invalidKey">需要读取、校验或转换的业务值。</param>
    [Theory]
    [InlineData("row", "not-a-row")]
    [InlineData("row", "-1")]
    [InlineData("column", "not-a-column")]
    [InlineData("column", "-1")]
    public void Scan_RejectsInvalidCoordinateKeys(string coordinate, string invalidKey)
    {
        var snapshot = CreateSnapshot();
        var cellData = (JObject)snapshot["sheets"]!["report-sheet"]!["cellData"]!;
        if (coordinate == "row")
        {
            cellData[invalidKey] = new JObject();
        }
        else
        {
            ((JObject)cellData["1"]!)[invalidKey] = new JObject();
        }

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains(invalidKey, exception.Message);
    }

    /// <summary>验证 Scan Rejects Primitive Snapshot Nodes 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="target">本次转换或写入使用的数据对象。</param>
    /// <param name="expectedContext">本次操作共享的运行上下文。</param>
    [Theory]
    [InlineData("sheets", "sheets")]
    [InlineData("sheet", "report-sheet")]
    [InlineData("cellData", "report-sheet")]
    [InlineData("row", "report-sheet")]
    [InlineData("cell", "report-sheet")]
    public void Scan_RejectsPrimitiveSnapshotNodes(string target, string expectedContext)
    {
        var snapshot = CreateSnapshot();
        var sheets = (JObject)snapshot["sheets"]!;
        switch (target)
        {
            case "sheets":
                snapshot["sheets"] = 42;
                break;
            case "sheet":
                sheets["report-sheet"] = 42;
                break;
            case "cellData":
                sheets["report-sheet"]!["cellData"] = 42;
                break;
            case "row":
                sheets["report-sheet"]!["cellData"]!["1"] = 42;
                break;
            case "cell":
                sheets["report-sheet"]!["cellData"]!["1"]!["0"] = 42;
                break;
        }

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains(expectedContext, exception.Message);
    }

    /// <summary>验证 Scan Rejects Malformed Merge Data Nodes 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="target">本次转换或写入使用的数据对象。</param>
    [Theory]
    [InlineData("mergeData")]
    [InlineData("mergeEntry")]
    [InlineData("mergeRow")]
    public void Scan_RejectsMalformedMergeDataNodes(string target)
    {
        var snapshot = CreateSnapshot();
        var sheet = snapshot["sheets"]!["report-sheet"]!;
        sheet["mergeData"] = target switch
        {
            "mergeData" => 42,
            "mergeEntry" => new JArray(42),
            _ => new JArray(
                new JObject
                {
                    ["startRow"] = "not-a-row",
                    ["endRow"] = 5
                })
        };

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A6", exception.Message);
    }

    /// <summary>验证 Scan Allows Missing Or Empty Sheets And Cell Data 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="scenario">scenario 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("missing-sheets")]
    [InlineData("empty-sheets")]
    [InlineData("missing-cellData")]
    [InlineData("empty-cellData")]
    public void Scan_AllowsMissingOrEmptySheetsAndCellData(string scenario)
    {
        var snapshot = new JObject();
        if (scenario != "missing-sheets")
        {
            snapshot["sheets"] = new JObject();
        }

        if (scenario is "missing-cellData" or "empty-cellData")
        {
            snapshot["sheets"]!["report-sheet"] = new JObject();
        }

        if (scenario == "empty-cellData")
        {
            snapshot["sheets"]!["report-sheet"]!["cellData"] = new JObject();
        }

        var binding = PocTemplateValidator.Scan(snapshot.ToString());

        Assert.Empty(binding.ParameterBindings);
        Assert.Null(binding.DetailBinding);
    }

    /// <summary>验证 Scan Rejects Token Adjacent To Extra Brace 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    [Theory]
    [InlineData("{{{params.hospitalId}}")]
    [InlineData("{{params.hospitalId}}}")]
    public void Scan_RejectsTokenAdjacentToExtraBrace(string value)
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 1, 0, value);

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A2", exception.Message);
    }

    /// <summary>验证 Scan Allows Ordinary Single Brace Text Around Parameter Token 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_AllowsOrdinarySingleBraceTextAroundParameterToken()
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 1, 0, "医院 {名称}：{{params.hospitalId}} {可选}");

        var binding = PocTemplateValidator.Scan(snapshot.ToString());

        Assert.Equal(
            [new PocParameterBinding("hospitalId", "report-sheet", 1, 0)],
            binding.ParameterBindings);
    }

    /// <summary>验证 Scan Rejects Snapshot Larger Than Four Mebibytes Before Parsing 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Scan_RejectsSnapshotLargerThanFourMebibytesBeforeParsing()
    {
        var oversizedSnapshot = new string('x', 4 * 1024 * 1024 + 1);

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(oversizedSnapshot));

        Assert.Contains("4 MiB", exception.Message);
    }

    /// <summary>构建 PocTemplateValidatorTests 所需的 Create Snapshot 内容，并保证生成结果符合调用约定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject CreateSnapshot() =>
        new()
        {
            ["sheets"] = new JObject
            {
                ["report-sheet"] = new JObject
                {
                    ["name"] = "科室工作量",
                    ["cellData"] = new JObject
                    {
                        ["1"] = new JObject
                        {
                            ["0"] = new JObject { ["v"] = "医院：{{params.hospitalId}}" }
                        },
                        ["5"] = new JObject
                        {
                            ["0"] = new JObject { ["v"] = "{{detail.sequence}}" },
                            ["1"] = new JObject { ["v"] = "{{detail.departmentName}}" },
                            ["2"] = new JObject { ["v"] = "{{detail.outpatientCount}}" },
                            ["3"] = new JObject { ["v"] = "{{detail.dischargeCount}}" },
                            ["4"] = new JObject { ["f"] = "=C6+D6" },
                            ["5"] = new JObject { ["f"] = "=AVERAGE(C6:D6)" }
                        }
                    },
                    ["mergeData"] = new JArray()
                }
            }
        };

    /// <summary>更新 PocTemplateValidatorTests 的 Set Cell Value 状态，并同步相关业务数据。</summary>
    /// <param name="snapshot">包含工作簿结构、单元格和样式信息的快照数据。</param>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="column">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    private static void SetCellValue(JObject snapshot, int row, int column, string value) =>
        snapshot["sheets"]!["report-sheet"]!["cellData"]![row.ToString()]![column.ToString()]!["v"] = value;
}
