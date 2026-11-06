using HD.ReportCenter.Application.CoreValidation;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 字段转换链编译器行为测试：内置转换器语义、目标类型终值收敛、编译期错误与运行期失败定位。
/// 全部直接调用 PocDataSyncConverter.CompileField 的编译产物（Func 委托）验证。
/// </summary>
public sealed class PocDataSyncConverterTests
{
    /// <summary>构造单转换器映射并编译的工具方法，测试用例用它压缩样板代码。</summary>
    /// <param name="targetType">目标类型。</param>
    /// <param name="nullable">是否允许空。</param>
    /// <param name="steps">转换链。</param>
    /// <param name="length">字符串长度（可空）。</param>
    /// <returns>编译完成的字段。</returns>
    private static PocDataSyncCompiledField Compile(
        string targetType,
        bool nullable,
        PocSyncConverterStep[] steps,
        int? length = null) =>
        PocDataSyncConverter.CompileField(new PocSyncColumnMapping(
            "srcField",
            "TGT_FIELD",
            targetType,
            length,
            nullable,
            steps));

    /// <summary>验证 trim 裁剪两端空白且空值保持空。</summary>
    [Fact]
    public void Trim_removes_surrounding_whitespace_and_keeps_null()
    {
        var field = Compile("string", true, [new PocSyncConverterStep("trim")]);

        Assert.Equal("a1", field.Transform("  a1 "));
        Assert.Null(field.Transform(null));
    }

    /// <summary>验证 emptyToNull 把空白文本归一为 null，非空文本原样保留。</summary>
    [Fact]
    public void EmptyToNull_converts_blank_to_null()
    {
        var field = Compile("string", true, [new PocSyncConverterStep("emptyToNull")]);

        Assert.Null(field.Transform("   "));
        Assert.Equal("x", field.Transform("x"));
    }

    /// <summary>验证 nullToDefault 用参数值替换空值，非空值不受影响。</summary>
    [Fact]
    public void NullToDefault_fills_configured_default()
    {
        var field = Compile("string", true, [
            new PocSyncConverterStep("nullToDefault", new Dictionary<string, string?> { ["value"] = "未知" })
        ]);

        Assert.Equal("未知", field.Transform(null));
        Assert.Equal("已知", field.Transform("已知"));
    }

    /// <summary>验证 required 的 failRow 策略：空值抛运行期异常且字段名为映射源字段。</summary>
    [Fact]
    public void Required_failRow_throws_with_source_field_name()
    {
        var field = Compile("string", true, [new PocSyncConverterStep("required")]);

        var exception = Assert.Throws<PocDataSyncTransformException>(() => field.Transform(null));
        Assert.Equal("srcField", exception.Field);
        Assert.Null(exception.OriginalValue);
    }

    /// <summary>验证 required 的 useDefault 策略：空值填 defaultValue 而不是失败。</summary>
    [Fact]
    public void Required_useDefault_fills_default_value()
    {
        var field = Compile("string", true, [
            new PocSyncConverterStep("required", new Dictionary<string, string?>
            {
                ["strategy"] = "useDefault",
                ["defaultValue"] = "0"
            })
        ]);

        Assert.Equal("0", field.Transform(null));
        Assert.Equal("7", field.Transform("7"));
    }

    /// <summary>验证 truncate 把超长文本截断到指定长度。</summary>
    [Fact]
    public void Truncate_limits_text_length()
    {
        var field = Compile("string", true, [
            new PocSyncConverterStep("truncate", new Dictionary<string, string?> { ["length"] = "3" })
        ]);

        Assert.Equal("abc", field.Transform("abcdef"));
        Assert.Equal("ab", field.Transform("ab"));
    }

    /// <summary>验证 dictionary 命中映射时替换为目标值，未命中抛运行期异常并携带原始值。</summary>
    [Fact]
    public void Dictionary_maps_known_values_and_fails_on_miss()
    {
        var field = Compile("string", true, [
            new PocSyncConverterStep("dictionary", new Dictionary<string, string?>
            {
                ["items"] = """{"1":"门诊","2":"住院"}"""
            })
        ]);

        Assert.Equal("门诊", field.Transform("1"));
        Assert.Equal("住院", field.Transform(2));
        var exception = Assert.Throws<PocDataSyncTransformException>(() => field.Transform("9"));
        Assert.Equal("9", exception.OriginalValue);
    }

    /// <summary>验证 toNumber：整数字符串收敛为 long、小数收敛为 double、非法文本按失败行抛出。</summary>
    [Fact]
    public void ToNumber_parses_integral_and_real_values()
    {
        var field = Compile("number", true, [new PocSyncConverterStep("toNumber")]);

        Assert.Equal(42L, field.Transform("42"));
        Assert.Equal(4.5d, field.Transform("4.5"));
        Assert.Null(field.Transform(null));
        var exception = Assert.Throws<PocDataSyncTransformException>(() => field.Transform("abc"));
        Assert.Equal("srcField", exception.Field);
    }

    /// <summary>验证 toDecimal 按 scale 四舍五入（AwayFromZero）。</summary>
    [Fact]
    public void ToDecimal_rounds_to_configured_scale()
    {
        var field = Compile("decimal", true, [
            new PocSyncConverterStep("toDecimal", new Dictionary<string, string?> { ["scale"] = "2" })
        ]);

        Assert.Equal(1.24m, field.Transform("1.235"));
        Assert.Equal(1.23m, field.Transform("1.234"));
        Assert.Null(field.Transform(null));
    }

    /// <summary>验证 toDate 按指定格式解析 HIS 常见的 yyyyMMdd 数字日期。</summary>
    [Fact]
    public void ToDate_parses_compact_format()
    {
        var field = Compile("datetime", true, [
            new PocSyncConverterStep("toDate", new Dictionary<string, string?> { ["format"] = "yyyyMMdd" })
        ]);

        Assert.Equal(new DateTime(2024, 1, 2), field.Transform("20240102"));
        Assert.Null(field.Transform(null));
        Assert.Throws<PocDataSyncTransformException>(() => field.Transform("not-a-date"));
    }

    /// <summary>验证 expression 编译执行：表达式能引用 value 完成条件变换。</summary>
    [Fact]
    public void Expression_compiles_and_evaluates()
    {
        var field = Compile("string", true, [
            new PocSyncConverterStep("expression", new Dictionary<string, string?>
            {
                ["expression"] = @"value == null ? ""EMPTY"" : ""HAS_VALUE"""
            })
        ]);

        Assert.Equal("EMPTY", field.Transform(null));
        Assert.Equal("HAS_VALUE", field.Transform("x"));
    }

    /// <summary>验证目标类型 bool 收敛：识别 1/0、true/false 与布尔原值。</summary>
    [Fact]
    public void Bool_target_coerces_common_textual_values()
    {
        var field = Compile("bool", true, []);

        Assert.Equal(true, field.Transform("1"));
        Assert.Equal(false, field.Transform("0"));
        Assert.Equal(true, field.Transform(true));
        Assert.Null(field.Transform(null));
        Assert.Throws<PocDataSyncTransformException>(() => field.Transform("maybe"));
    }

    /// <summary>验证目标类型 datetime 终值收敛：显式枚举的 HIS 常见格式可直接通过。</summary>
    [Fact]
    public void Datetime_target_accepts_common_his_formats()
    {
        var field = Compile("datetime", true, []);

        Assert.Equal(new DateTime(2024, 6, 1, 8, 30, 0), field.Transform("20240601083000"));
        Assert.Equal(new DateTime(2024, 6, 1), field.Transform("2024-06-01"));
    }

    /// <summary>验证目标类型 string 超长抛失败行异常，提示先配置 truncate。</summary>
    [Fact]
    public void String_target_rejects_value_longer_than_declared_length()
    {
        var field = Compile("string", true, [], length: 4);

        var exception = Assert.Throws<PocDataSyncTransformException>(() => field.Transform("toolong"));
        Assert.Contains("truncate", exception.Reason, StringComparison.Ordinal);
    }

    /// <summary>验证非空映射的终值为 null 时按失败行抛出。</summary>
    [Fact]
    public void Non_nullable_mapping_fails_when_result_is_null()
    {
        var field = Compile("string", nullable: false, [new PocSyncConverterStep("emptyToNull")]);

        var exception = Assert.Throws<PocDataSyncTransformException>(() => field.Transform(" "));
        Assert.Equal("srcField", exception.Field);
    }

    /// <summary>验证未知转换器在编译期即拒绝（SYNC_MAPPING_INVALID），而不是运行到一半失败。</summary>
    [Fact]
    public void Unknown_converter_is_rejected_at_compile_time()
    {
        var exception = Assert.Throws<PocDataSyncException>(() =>
            Compile("string", true, [new PocSyncConverterStep("magic")]));
        Assert.Equal("SYNC_MAPPING_INVALID", exception.Code);
    }

    /// <summary>验证缺少必填参数（truncate 无 length）在编译期拒绝。</summary>
    [Fact]
    public void Missing_converter_argument_is_rejected_at_compile_time()
    {
        var exception = Assert.Throws<PocDataSyncException>(() =>
            Compile("string", true, [new PocSyncConverterStep("truncate")]));
        Assert.Equal("SYNC_MAPPING_INVALID", exception.Code);
        Assert.Contains("length", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>验证表达式语法错误在编译期拒绝，避免坏表达式留给数据撞出来。</summary>
    [Fact]
    public void Invalid_expression_is_rejected_at_compile_time()
    {
        var exception = Assert.Throws<PocDataSyncException>(() =>
            Compile("string", true, [
                new PocSyncConverterStep("expression", new Dictionary<string, string?>
                {
                    ["expression"] = "value ==="
                })
            ]));
        Assert.Equal("SYNC_MAPPING_INVALID", exception.Code);
    }

    /// <summary>验证 required 的 useDefault 缺 defaultValue 在编译期拒绝。</summary>
    [Fact]
    public void Required_useDefault_without_defaultValue_is_rejected()
    {
        var exception = Assert.Throws<PocDataSyncException>(() =>
            Compile("string", true, [
                new PocSyncConverterStep("required", new Dictionary<string, string?> { ["strategy"] = "useDefault" })
            ]));
        Assert.Equal("SYNC_MAPPING_INVALID", exception.Code);
    }

    /// <summary>验证 dictionary 的 items 不是合法 JSON 对象时在编译期拒绝。</summary>
    [Fact]
    public void Dictionary_with_invalid_items_json_is_rejected()
    {
        var exception = Assert.Throws<PocDataSyncException>(() =>
            Compile("string", true, [
                new PocSyncConverterStep("dictionary", new Dictionary<string, string?> { ["items"] = "not-json" })
            ]));
        Assert.Equal("SYNC_MAPPING_INVALID", exception.Code);
    }

    /// <summary>验证非法目标类型在编译期拒绝。</summary>
    [Fact]
    public void Unsupported_target_type_is_rejected()
    {
        var exception = Assert.Throws<PocDataSyncException>(() =>
            Compile("binary", true, []));
        Assert.Equal("SYNC_MAPPING_INVALID", exception.Code);
    }

    /// <summary>验证 toDate 的 format 不是合法日期格式时在编译期拒绝。</summary>
    [Fact]
    public void Invalid_date_format_is_rejected_at_compile_time()
    {
        var exception = Assert.Throws<PocDataSyncException>(() =>
            Compile("datetime", true, [
                new PocSyncConverterStep("toDate", new Dictionary<string, string?> { ["format"] = "yolo" })
            ]));
        Assert.Equal("SYNC_MAPPING_INVALID", exception.Code);
    }
}
