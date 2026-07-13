// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using System.Text.Json.Serialization;

namespace Admin.NET.Plugin.ApprovalFlow.Service;

public class ApprovalFlowItem
{
    [JsonPropertyName("nodes")]
    public List<ApprovalFlowNodeItem> Nodes { get; set; }

    [JsonPropertyName("edges")]
    public List<ApprovalFlowEdgeItem> Edges { get; set; }
}

public class ApprovalFlowNodeItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("properties")]
    public FlowProperties Properties { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowTextItem Text { get; set; }
}

public class ApprovalFlowEdgeItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("sourceNodeId")]
    public string SourceNodeId { get; set; }

    [JsonPropertyName("targetNodeId")]
    public string TargetNodeId { get; set; }

    [JsonPropertyName("startPoint")]
    public FlowEdgePointItem StartPoint { get; set; }

    [JsonPropertyName("endPoint")]
    public FlowEdgePointItem EndPoint { get; set; }

    [JsonPropertyName("properties")]
    public FlowProperties Properties { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowTextItem Text { get; set; }

    [JsonPropertyName("pointsList")]
    public List<FlowEdgePointItem> PointsList { get; set; }
}

public class FlowProperties
{
}

public class FlowTextItem
{
    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; }
}

public class FlowEdgePointItem
{
    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }
}