// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.DingTalk;

public class DingTalkGetProcessInstancesOutput
{
    public ResultData Result { get; set; }
    public bool Success { get; set; }
}

public class OperationRecord
{
    public DateTime? Date { get; set; }
    public string Result { get; set; }
    public List<object> Images { get; set; } // 图片可能是字符串 URL 或对象
    public string ShowName { get; set; }
    public string Type { get; set; }
    public string UserId { get; set; }
}

// 表格行中的子项（用于 TableField 的解析）
public class TableRowItem
{
    public string BizAlias { get; set; }
    public string Label { get; set; }
    public string Value { get; set; }
    public string Key { get; set; }
    public bool Mask { get; set; }
}

// 完整的一行表格数据
public class TableRow
{
    public List<TableRowItem> RowValue { get; set; }
    public string RowNumber { get; set; }
}

public class TaskItem
{
    public string Result { get; set; }
    public string ActivityId { get; set; }
    public string PcUrl { get; set; }
    public DateTime? CreateTime { get; set; }
    public string MobileUrl { get; set; }
    public string UserId { get; set; }
    public long TaskId { get; set; }
    public string Status { get; set; }
}

public class ResultData
{
    public List<string> AttachedProcessInstanceIds { get; set; }
    public string BusinessId { get; set; }
    public string Title { get; set; }
    public string OriginatorDeptId { get; set; }
    public List<OperationRecord> OperationRecords { get; set; }
    public List<FormComponentValue> FormComponentValues { get; set; }

    /// <summary>
    /// 审批结果 agree：同意 refuse：拒绝
    /// </summary>
    public string Result { get; set; }

    public string BizAction { get; set; }
    public DateTime? CreateTime { get; set; }
    public string OriginatorUserId { get; set; }
    public List<TaskItem> Tasks { get; set; }
    public string OriginatorDeptName { get; set; }
    public string Status { get; set; }
}