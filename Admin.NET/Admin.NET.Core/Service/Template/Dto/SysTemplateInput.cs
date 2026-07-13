// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

public class PageTemplateInput : BasePageInput
{
    /// <summary>
    /// 名称
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// 编码
    /// </summary>
    public string Code { get; set; }

    /// <summary>
    /// 分组名称
    /// </summary>
    public string GroupName { get; set; }

    /// <summary>
    /// 模板类型
    /// </summary>
    public TemplateTypeEnum? Type { get; set; }

    /// <summary>
    /// 租户Id
    /// </summary>
    public long TenantId { get; set; }
}

/// <summary>
/// 新增模板输入参数
/// </summary>
public class AddTemplateInput : SysTemplate
{
    /// <summary>
    /// 名称
    /// </summary>
    [Required(ErrorMessage = "名称不能为空")]
    public override string Name { get; set; }

    /// <summary>
    /// 模板类型
    /// </summary>
    [Enum]
    public override TemplateTypeEnum Type { get; set; }

    /// <summary>
    /// 编码
    /// </summary>
    [Required(ErrorMessage = "编码不能为空")]
    public override string Code { get; set; }

    /// <summary>
    /// 分组名称
    /// </summary>
    [Required(ErrorMessage = "分组名称不能为空")]
    public override string GroupName { get; set; }

    /// <summary>
    /// 模板内容
    /// </summary>
    [Required(ErrorMessage = "内容名称不能为空")]
    public override string Content { get; set; }
}

/// <summary>
/// 更新模板输入参数
/// </summary>
public class UpdateTemplateInput : AddTemplateInput
{
    /// <summary>
    /// 主键Id
    /// </summary>
    [Required(ErrorMessage = "Id不能为空")]
    [DataValidation(ValidationTypes.Numeric)]
    public override long Id { get; set; }
}

/// <summary>
/// 预览模板输入参数
/// </summary>
public class ProViewTemplateInput : BaseIdInput
{
    /// <summary>
    /// 渲染参数
    /// </summary>
    [Required(ErrorMessage = "渲染参数不能为空")]
    public object Data { get; set; }
}

/// <summary>
/// 模板渲染输入参数
/// </summary>
public class RenderTemplateInput
{
    /// <summary>
    /// 模板内容
    /// </summary>
    [Required(ErrorMessage = "内容名称不能为空")]
    public string Content { get; set; }

    /// <summary>
    /// 渲染参数
    /// </summary>
    [Required(ErrorMessage = "渲染参数不能为空")]
    public object Data { get; set; }
}