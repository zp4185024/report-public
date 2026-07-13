// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.DingTalk;

[SugarTable("ding_talk_wokerflow_log", "钉钉审批日志")]
public class DingTalkWokerflowLog
{
    /// <summary>
    /// 审批实例ID
    /// </summary>
    [SugarColumn(ColumnDescription = "审批实例ID", IsPrimaryKey = true, IsIdentity = false)]
    public string instanceId { get; set; }

    /// <summary>
    /// 审批单号
    /// </summary>
    [SugarColumn(ColumnDescription = "审批单号")]
    public string? WorkflowId { get; set; }

    /// <summary>
    /// 来源单据
    /// </summary>
    [SugarColumn(ColumnDescription = "来源单据")]
    public string SourceDocument { get; set; }

    /// <summary>
    /// 审批完成时间
    /// </summary>
    [SugarColumn(ColumnDescription = "审批完成时间")]
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 其他信息
    /// </summary>
    [SugarColumn(ColumnDescription = "其他信息", IsJson = true)]
    public Dictionary<string, object>? other_info { get; set; }

    /// <summary>
    /// 是否回传结果给第三方
    /// </summary>
    [SugarColumn(ColumnDescription = "是否回传结果")]
    public bool? isReturn { get; set; }

    /// <summary>
    /// 审批状态
    /// </summary>
    /// <remarks>
    /// RUNNING：审批中 TERMINATED：已撤销 COMPLETED：审批完成
    /// /// </remarks>
    [SugarColumn(ColumnDescription = "审批状态")]
    public string Status { get; set; }

    /// <summary>
    /// 任务ID
    /// </summary>
    [SugarColumn(ColumnDescription = "任务ID")]
    public long? taskId { get; set; }

    /// <summary>
    /// 审批结果 agree：同意 refuse：拒绝
    /// </summary>
    [SugarColumn(ColumnDescription = "审批结果")]
    public string? Result { get; set; }

    /// <summary>
    /// 创建者姓名
    /// </summary>
    [SugarColumn(ColumnDescription = "创建者姓名", Length = 64, IsOnlyIgnoreUpdate = true)]
    public string? CreateUserName { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    [SugarColumn(ColumnDescription = "创建时间", IsNullable = true, IsOnlyIgnoreUpdate = true)]
    public DateTime CreateTime { get; set; }

    /// <summary>
    /// 更新时间
    /// </summary>
    [SugarColumn(ColumnDescription = "更新时间")]
    public virtual DateTime? UpdateTime { get; set; }
}