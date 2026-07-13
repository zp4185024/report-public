// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.DingTalk;

public class DingTalkWorkflowProcessInstancesInput
{
    /// <summary>
    /// 发起人用户ID
    /// </summary>
    public string OriginatorUserId { get; set; }

    /// <summary>
    /// 审批模板的流程编码
    /// </summary>
    public string ProcessCode { get; set; }

    /// <summary>
    /// 部门ID
    /// </summary>
    public long DeptId { get; set; }

    /// <summary>
    /// 微应用AgentId
    /// </summary>
    public long MicroappAgentId { get; set; }

    /// <summary>
    /// 审批人列表（支持多节点）
    /// </summary>
    public List<Approver> Approvers { get; set; }

    /// <summary>
    /// 抄送人列表
    /// </summary>
    public List<string> CcList { get; set; }

    /// <summary>
    /// 抄送位置：START（开始），MIDDLE（中间），END（结束）
    /// </summary>
    public string CcPosition { get; set; }

    /// <summary>
    /// 目标动态选择办理人（用于会签或或签等场景）
    /// </summary>
    public List<TargetSelectActioner> TargetSelectActioners { get; set; }

    /// <summary>
    /// 表单组件值列表
    /// </summary>
    public List<FormComponentValue> FormComponentValues { get; set; }

    /// <summary>
    /// 请求ID，用于幂等控制
    /// </summary>
    public string RequestId { get; set; }
}

/// <summary>
/// 审批人信息
/// </summary>
public class Approver
{
    /// <summary>
    /// 节点类型：AGREE（同意），REFUSE（拒绝）等
    /// </summary>
    public string ActionType { get; set; }

    /// <summary>
    /// 该节点的审批人用户ID列表
    /// </summary>
    public List<string> UserIds { get; set; }
}

/// <summary>
/// 动态选择办理人
/// </summary>
public class TargetSelectActioner
{
    /// <summary>
    /// 办理人Key，对应表单中的人员选择控件的key
    /// </summary>
    public string ActionerKey { get; set; }

    /// <summary>
    /// 该控件选中的用户ID列表
    /// </summary>
    public List<string> ActionerUserIds { get; set; }
}

/// <summary>
/// 表单组件值
/// </summary>
public class FormComponentValue
{
    public string ComponentType { get; set; }
    public string Name { get; set; }
    public string BizAlias { get; set; }
    public string Id { get; set; }
    public string Value { get; set; }
    public string ExtValue { get; set; }
}