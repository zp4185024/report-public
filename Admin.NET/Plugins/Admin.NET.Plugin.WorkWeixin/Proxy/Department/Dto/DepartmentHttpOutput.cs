// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.WorkWeixin.Proxy;

/// <summary>
/// 部门Id列表输出参数
/// </summary>
public class DepartmentIdOutput : BaseWorkOutput
{
    /// <summary>
    /// id
    /// </summary>
    [JsonProperty("department_id")]
    [JsonPropertyName("department_id")]
    public List<DepartmentItemOutput> DepartmentList { get; set; }
}

/// <summary>
/// 部门Id输出参数
/// </summary>
public class DepartmentItemOutput
{
    /// <summary>
    /// 部门名称
    /// </summary>
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    /// <summary>
    /// 父部门id
    /// </summary>
    [JsonProperty("parentid")]
    [JsonPropertyName("parentid")]
    public long? ParentId { get; set; }

    /// <summary>
    /// 序号
    /// </summary>
    [JsonProperty("order")]
    [JsonPropertyName("order")]
    public int? Order { get; set; }
}

/// <summary>
/// 部门输出参数
/// </summary>
public class DepartmentOutput
{
    /// <summary>
    /// 部门名称
    /// </summary>
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    /// <summary>
    /// 父部门id
    /// </summary>
    [JsonProperty("parentid")]
    [JsonPropertyName("parentid")]
    public long? ParentId { get; set; }

    /// <summary>
    /// 部门名称
    /// </summary>
    [JsonProperty("name")]
    [JsonPropertyName("name")]
    public string Name { get; set; }

    /// <summary>
    /// 英文名称
    /// </summary>
    [JsonProperty("name_en")]
    [JsonPropertyName("name_en")]
    public string NameEn { get; set; }

    /// <summary>
    /// 部门负责人列表
    /// </summary>
    [JsonProperty("department_leader")]
    [JsonPropertyName("department_leader")]
    public List<string> Leaders { get; set; }

    /// <summary>
    /// 序号
    /// </summary>
    [JsonProperty("order")]
    [JsonPropertyName("order")]
    public int? Order { get; set; }
}