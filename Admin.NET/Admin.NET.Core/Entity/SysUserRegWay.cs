// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 系统用户注册方案表
/// </summary>
[SugarTable(null, "系统用户注册方案表")]
[SysTable]
public partial class SysUserRegWay : EntityBaseTenant
{
    /// <summary>
    /// 方案名称
    /// </summary>
    [MaxLength(32)]
    [SugarColumn(ColumnDescription = "方案名称", Length = 32)]
    public virtual string Name { get; set; }

    /// <summary>
    /// 账号类型
    /// </summary>
    [SugarColumn(ColumnDescription = "账号类型")]
    public virtual AccountTypeEnum AccountType { get; set; } = AccountTypeEnum.NormalUser;

    /// <summary>
    /// 注册用户默认角色
    /// </summary>
    [SugarColumn(ColumnDescription = "角色")]
    public virtual long RoleId { get; set; }

    /// <summary>
    /// 注册用户默认机构
    /// </summary>
    [SugarColumn(ColumnDescription = "机构")]
    public virtual long OrgId { get; set; }

    /// <summary>
    /// 注册用户默认职位
    /// </summary>
    [SugarColumn(ColumnDescription = "职位")]
    public virtual long PosId { get; set; }

    /// <summary>
    /// 排序
    /// </summary>
    [SugarColumn(ColumnDescription = "排序")]
    public int OrderNo { get; set; } = 100;

    /// <summary>
    /// 备注
    /// </summary>
    [MaxLength(128)]
    [SugarColumn(ColumnDescription = "备注", Length = 128)]
    public string? Remark { get; set; }
}