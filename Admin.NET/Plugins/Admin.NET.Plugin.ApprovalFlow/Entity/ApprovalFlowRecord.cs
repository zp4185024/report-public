// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.ApprovalFlow;

/// <summary>
/// 审批流流程记录
/// </summary>
[SugarTable(null, "审批流流程记录")]
public class ApprovalFlowRecord : EntityBaseOrg
{
    /// <summary>
    /// 表单名称
    /// </summary>
    [SugarColumn(ColumnDescription = "表单名称", Length = 255)]
    public string? FormName { get; set; }

    /// <summary>
    /// 表单状态
    /// </summary>
    [SugarColumn(ColumnDescription = "表单状态", Length = 32)]
    public string? FormStatus { get; set; }

    /// <summary>
    /// 表单触发
    /// </summary>
    [SugarColumn(ColumnDescription = "表单触发", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string? FormJson { get; set; }

    /// <summary>
    /// 表单结果
    /// </summary>
    [SugarColumn(ColumnDescription = "表单结果", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string? FormResult { get; set; }

    /// <summary>
    /// 流程结构
    /// </summary>
    [SugarColumn(ColumnDescription = "流程结构", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string? FlowJson { get; set; }

    /// <summary>
    /// 流程结果
    /// </summary>
    [SugarColumn(ColumnDescription = "流程结果", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string? FlowResult { get; set; }
}