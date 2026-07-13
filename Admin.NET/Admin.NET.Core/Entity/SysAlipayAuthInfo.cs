// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 支付宝授权记录表
/// </summary>
[SugarTable(null, "支付宝授权记录表")]
[SysTable]
[SugarIndex("index_{table}_U", nameof(UserId), OrderByType.Asc)]
[SugarIndex("index_{table}_T", nameof(OpenId), OrderByType.Asc)]
public class SysAlipayAuthInfo : EntityBase
{
    /// <summary>
    /// 商户AppId
    /// </summary>
    [SugarColumn(ColumnDescription = "商户AppId", Length = 64)]
    public string? AppId { get; set; }

    /// <summary>
    /// 开放ID
    /// </summary>
    [SugarColumn(ColumnDescription = "开放ID", Length = 64)]
    public string? OpenId { get; set; }

    /// <summary>
    /// 用户ID
    /// </summary>
    [SugarColumn(ColumnDescription = "用户ID", Length = 64)]
    public string? UserId { get; set; }

    /// <summary>
    /// 性别
    /// </summary>
    [SugarColumn(ColumnDescription = "性别", Length = 8)]
    public GenderEnum Gender { get; set; }

    /// <summary>
    /// 年龄
    /// </summary>
    [SugarColumn(ColumnDescription = "年龄", Length = 16)]
    public int Age { get; set; }

    /// <summary>
    /// 手机号
    /// </summary>
    [SugarColumn(ColumnDescription = "手机号", Length = 32)]
    public string Mobile { get; set; }

    /// <summary>
    /// 显示名称
    /// </summary>
    [SugarColumn(ColumnDescription = "显示名称", Length = 128)]
    public string DisplayName { get; set; }

    /// <summary>
    /// 昵称
    /// </summary>
    [SugarColumn(ColumnDescription = "昵称", Length = 64)]
    public string NickName { get; set; }

    /// <summary>
    /// 用户名
    /// </summary>
    [SugarColumn(ColumnDescription = "用户名", Length = 64)]
    public string UserName { get; set; }

    /// <summary>
    /// 头像
    /// </summary>
    [SugarColumn(ColumnDescription = "头像", Length = 512)]
    public string? Avatar { get; set; }

    /// <summary>
    /// 邮箱
    /// </summary>
    [SugarColumn(ColumnDescription = "邮箱", Length = 128)]
    public string? Email { get; set; }

    /// <summary>
    /// 用户民族
    /// </summary>
    [SugarColumn(ColumnDescription = "用户民族", Length = 32)]
    public string? UserNation { get; set; }

    /// <summary>
    /// 淘宝ID
    /// </summary>
    [SugarColumn(ColumnDescription = "淘宝ID", Length = 64)]
    public string? TaobaoId { get; set; }

    /// <summary>
    /// 电话
    /// </summary>
    [SugarColumn(ColumnDescription = "电话", Length = 32)]
    public string? Phone { get; set; }

    /// <summary>
    /// 生日
    /// </summary>
    [SugarColumn(ColumnDescription = "生日", Length = 32)]
    public string? PersonBirthday { get; set; }

    /// <summary>
    /// 职业
    /// </summary>
    [SugarColumn(ColumnDescription = "职业", Length = 64)]
    public string? Profession { get; set; }

    /// <summary>
    /// 省份
    /// </summary>
    [SugarColumn(ColumnDescription = "省份", Length = 64)]
    public string? Province { get; set; }

    /// <summary>
    /// 用户状态
    /// </summary>
    [SugarColumn(ColumnDescription = "用户状态", Length = 32)]
    public string? UserStatus { get; set; }

    /// <summary>
    /// 学历
    /// </summary>
    [SugarColumn(ColumnDescription = "学历", Length = 32)]
    public string? Degree { get; set; }

    /// <summary>
    /// 用户类型
    /// </summary>
    [SugarColumn(ColumnDescription = "用户类型", Length = 32)]
    public string? UserType { get; set; }

    /// <summary>
    /// 邮编
    /// </summary>
    [SugarColumn(ColumnDescription = "邮编", Length = 16)]
    public string? Zip { get; set; }

    /// <summary>
    /// 地址
    /// </summary>
    [SugarColumn(ColumnDescription = "地址", Length = 256)]
    public string? Address { get; set; }
}