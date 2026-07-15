namespace HD.ReportCenter.ReportEngine.CoreValidation;

/// <summary>
/// 一个参数 token 在 Univer 工作簿中的零基坐标。
/// 同一单元格可以包含多个参数 token，因此参数绑定不是按单元格唯一。
/// </summary>
public sealed record PocParameterBinding(string Code, string SheetId, int Row, int Column);

/// <summary>
/// 明细模板行中的字段与列坐标。行坐标由所属的 <see cref="PocDetailBinding"/> 统一保存。
/// </summary>
public sealed record PocDetailFieldBinding(string Code, int Column);

/// <summary>
/// 单一明细区域定义。当前验证明确限制为一个工作表中的一条模板行，预览时由该行向下展开。
/// </summary>
public sealed record PocDetailBinding(
    string DatasetCode,
    string SheetId,
    int TemplateRow,
    IReadOnlyList<PocDetailFieldBinding> Fields);

/// <summary>
/// 从工作簿快照扫描得到的规范化绑定。Version 用于以后升级绑定协议时拒绝不兼容数据。
/// </summary>
public sealed record PocTemplateBinding(
    int Version,
    IReadOnlyList<PocParameterBinding> ParameterBindings,
    PocDetailBinding? DetailBinding);

/// <summary>
/// 模板快照结构、token 或坐标不符合约束时抛出的领域异常。
/// API 层会把它转换为可识别的 400 响应，不向前端暴露内部堆栈。
/// </summary>
public sealed class PocTemplateValidationException : Exception
{
    public PocTemplateValidationException(string message)
        : base(message)
    {
    }

    public PocTemplateValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
