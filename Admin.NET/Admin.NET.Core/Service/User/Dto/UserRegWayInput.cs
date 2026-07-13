// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 注册方案分页查询输入参数
/// </summary>
public class PageUserRegWayInput : BasePageInput
{
    /// <summary>
    /// 方案名称
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 租户Id
    /// </summary>
    public long TenantId { get; set; }
}

/// <summary>
/// 注册方案增加输入参数
/// </summary>
public class AddUserRegWayInput : SysUserRegWay
{
    /// <summary>
    /// 方案名称
    /// </summary>
    [Required(ErrorMessage = "方案名称不能为空")]
    [MaxLength(32, ErrorMessage = "方案名称字符长度不能超过32")]
    public override string Name { get; set; }

    /// <summary>
    /// 账号类型
    /// </summary>
    [Enum(ErrorMessage = "账号类型不正确")]
    public override AccountTypeEnum AccountType { get; set; }

    /// <summary>
    /// 角色
    /// </summary>
    [Required(ErrorMessage = "角色不能为空")]
    public override long RoleId { get; set; }

    /// <summary>
    /// 机构
    /// </summary>
    [Required(ErrorMessage = "机构不能为空")]
    public override long OrgId { get; set; }

    /// <summary>
    /// 职位
    /// </summary>
    [Required(ErrorMessage = "职位不能为空")]
    public override long PosId { get; set; }
}

/// <summary>
/// 注册方案更新输入参数
/// </summary>
public class UpdateUserRegWayInput : AddUserRegWayInput
{
    /// <summary>
    /// 主键Id
    /// </summary>
    [Required(ErrorMessage = "主键Id不能为空")]
    public override long Id { get; set; }
}