using System.Data.Common;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocDatasetValueNormalizerTests
{
    /// <summary>验证 Normalize Converts Provider Numeric Text To Decimal 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("12345678901", "12345678901")]
    [InlineData("12.5", "12.5")]
    [InlineData("1.25E+3", "1250")]
    public void Normalize_ConvertsProviderNumericTextToDecimal(string input, string expected)
    {
        var field = new PocDatasetFieldDefinition("VISIT_ID", "就诊标识", "number");

        var result = PocDatasetValueNormalizer.Normalize(new ProviderValue(input), field);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result);
    }

    /// <summary>验证 Normalize Converts Clr Integer To Decimal 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Normalize_ConvertsClrIntegerToDecimal()
    {
        var field = new PocDatasetFieldDefinition("VISIT_ID", "就诊标识", "number");

        var result = PocDatasetValueNormalizer.Normalize(10000165072L, field);

        Assert.Equal(10000165072M, Assert.IsType<decimal>(result));
    }

    /// <summary>验证 Normalize Converts Provider Boolean Text 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Normalize_ConvertsProviderBooleanText(string input, bool expected)
    {
        var field = new PocDatasetFieldDefinition("ENABLED", "启用", "bool");

        var result = PocDatasetValueNormalizer.Normalize(new ProviderValue(input), field);

        Assert.Equal(expected, result);
    }

    /// <summary>验证 Normalize Preserves Text And Null 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Normalize_PreservesTextAndNull()
    {
        var field = new PocDatasetFieldDefinition("USER_NAME", "姓名", "string");

        Assert.Equal("张三", PocDatasetValueNormalizer.Normalize(new ProviderValue("张三"), field));
        Assert.Null(PocDatasetValueNormalizer.Normalize(DBNull.Value, field));
    }

    /// <summary>验证 Normalize Converts Clr Number To Configured Text 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Normalize_ConvertsClrNumberToConfiguredText()
    {
        var field = new PocDatasetFieldDefinition("CODE", "编码", "string");

        var result = PocDatasetValueNormalizer.Normalize(1001L, field);

        Assert.Equal("1001", Assert.IsType<string>(result));
    }

    /// <summary>验证 Normalize Rejects Provider Value That Violates Number Contract 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Normalize_RejectsProviderValueThatViolatesNumberContract()
    {
        var field = new PocDatasetFieldDefinition("VISIT_ID", "就诊标识", "number");

        var exception = Assert.Throws<PocDatasetExecutionException>(
            () => PocDatasetValueNormalizer.Normalize(new ProviderValue("not-a-number"), field));

        Assert.Equal("DATASET_FIELD_TYPE_MISMATCH", exception.Code);
        Assert.Contains("VISIT_ID", exception.Message);
    }

    /// <summary>
    /// 验证合法数值文本超出 decimal 表示范围时按字符串降级返回，而不是让整张报表执行失败
    /// （对应 Oracle NUMBER 超大 SUM 值经 ReadColumnValue 兜底后的场景）。
    /// </summary>
    [Fact]
    public void Normalize_DegradesOutOfRangeNumericTextToString()
    {
        var field = new PocDatasetFieldDefinition("TOTAL_AMOUNT", "合计金额", "number");
        var hugeNumber = "123456789012345678901234567890123456789";

        var result = PocDatasetValueNormalizer.Normalize(new ProviderValue(hugeNumber), field);

        Assert.Equal(hugeNumber, Assert.IsType<string>(result));
    }

    /// <summary>
    /// 验证 ReadColumnValue 在驱动 GetValue 因数值溢出抛 InvalidCastException 时，
    /// 降级返回提供程序专用值的文本形式，保证单行超大数值不会中断整个查询。
    /// </summary>
    [Fact]
    public void ReadColumnValue_FallsBackToProviderTextWhenDecimalOverflows()
    {
        var reader = new StubOverflowReader("123456789012345678901234567890123456789");

        var result = PocDatasetValueNormalizer.ReadColumnValue(reader, 0);

        Assert.Equal("123456789012345678901234567890123456789", Assert.IsType<string>(result));
    }

    /// <summary>验证 Read Column Value Fallback Returns Null 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void ReadColumnValue_FallbackReturnsNullWhenProviderTextAlsoFails()
    {
        var reader = new StubOverflowReader(null);

        var result = PocDatasetValueNormalizer.ReadColumnValue(reader, 0);

        Assert.Null(result);
    }

    private sealed class ProviderValue(string value)
    {
        /// <summary>将 PocDatasetValueNormalizerTests 的当前值转换为稳定文本，供日志或界面展示使用。</summary>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public override string ToString() => value;
    }

    /// <summary>
    /// 模拟 Oracle 驱动在 NUMBER 超出 decimal 范围时的读取行为：GetValue 抛
    /// InvalidCastException（内层 OverflowException），GetProviderSpecificValue 返回
    /// 可转全精度文本的专用值；providerText 为 null 表示专用值兜底同样失败。
    /// </summary>
    private sealed class StubOverflowReader(string? providerText) : DbDataReader
    {
        /// <summary>抛出与 Oracle 驱动一致的溢出转换异常，模拟 decimal 范围外的 NUMBER 列。</summary>
        /// <param name="ordinal">目标列序号，桩实现忽略具体取值。</param>
        /// <returns>此桩实现总是抛出异常，不会正常返回。</returns>
        public override object GetValue(int ordinal) =>
            throw new InvalidCastException(
                "Specified cast is not valid.",
                new OverflowException("Arithmetic operation resulted in an overflow."));

        /// <summary>返回提供程序专用值文本；providerText 为 null 时同样抛溢出异常，模拟兜底不可用。</summary>
        /// <param name="ordinal">目标列序号，桩实现忽略具体取值。</param>
        /// <returns>专用值的文本表示。</returns>
        public override object GetProviderSpecificValue(int ordinal) =>
            providerText ?? throw new OverflowException("Arithmetic operation resulted in an overflow.");

        /// <summary>固定返回 false，使兜底路径继续读取专用值文本。</summary>
        /// <param name="ordinal">目标列序号，桩实现忽略具体取值。</param>
        /// <returns>始终返回 false。</returns>
        public override bool IsDBNull(int ordinal) => false;

        // 以下成员为 DbDataReader 抽象契约的最小实现，本桩只服务于溢出兜底测试，不承载真实数据。
        /// <summary>抽象契约占位：返回固定深度。</summary>
        public override int Depth => 0;
        /// <summary>抽象契约占位：返回单列字段数。</summary>
        public override int FieldCount => 1;
        /// <summary>抽象契约占位：返回无结果行标记。</summary>
        public override bool HasRows => false;
        /// <summary>抽象契约占位：返回未关闭标记。</summary>
        public override bool IsClosed => false;
        /// <summary>抽象契约占位：返回零影响行数。</summary>
        public override int RecordsAffected => 0;
        /// <summary>抽象契约占位：不支持枚举。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override System.Collections.IEnumerator GetEnumerator() =>
            throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取布尔列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取字节列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override byte GetByte(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取字节数组。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取字符列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取字符数组。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();
        /// <summary>抽象契约占位：返回固定类型名。</summary>
        public override string GetDataTypeName(int ordinal) => "OracleDecimal";
        /// <summary>抽象契约占位：不支持读取日期列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取 decimal 列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取双精度列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：返回固定 CLR 类型。</summary>
        public override Type GetFieldType(int ordinal) => typeof(decimal);
        /// <summary>抽象契约占位：不支持读取浮点列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取 Guid 列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取短整型列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取整型列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override int GetInt32(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持读取长整型列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override long GetInt64(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：返回固定列名。</summary>
        public override string GetName(int ordinal) => "TOTAL_AMOUNT";
        /// <summary>抽象契约占位：仅支持第 0 列。</summary>
        /// <returns>第 0 列返回 0，其余抛出 ArgumentOutOfRangeException。</returns>
        public override int GetOrdinal(string name) =>
            name == "TOTAL_AMOUNT" ? 0 : throw new ArgumentOutOfRangeException(nameof(name));
        /// <summary>抽象契约占位：按列名读取时模拟溢出异常，与 GetValue 行为一致。</summary>
        /// <param name="name">目标列名，桩实现忽略具体取值。</param>
        /// <returns>此桩实现总是抛出异常，不会正常返回。</returns>
        public override object this[string name] => GetValue(0);
        /// <summary>抽象契约占位：按列序读取时模拟溢出异常，与 GetValue 行为一致。</summary>
        /// <param name="ordinal">目标列序号，桩实现忽略具体取值。</param>
        /// <returns>此桩实现总是抛出异常，不会正常返回。</returns>
        public override object this[int ordinal] => GetValue(ordinal);
        /// <summary>抽象契约占位：不支持读取字符串列。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override string GetString(int ordinal) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持批量取值。</summary>
        /// <returns>始终抛出 NotSupportedException。</returns>
        public override int GetValues(object[] values) => throw new NotSupportedException();
        /// <summary>抽象契约占位：不支持行读取。</summary>
        /// <returns>始终返回 false。</returns>
        public override bool Read() => false;
        /// <summary>抽象契约占位：不支持下一结果集。</summary>
        /// <returns>始终返回 false。</returns>
        public override bool NextResult() => false;
    }
}
