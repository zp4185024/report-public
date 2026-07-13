// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using SqlSugar;

namespace Admin.NET.Application;

/// <summary>
/// 用户表视图（必须加IgnoreTable，防止被生成为表）
/// </summary>
[SugarTable(null, "用户表视图"), IgnoreTable]
public class TestViewSysUser : EntityBase, ISqlSugarView
{
    /// <summary>
    /// 账号
    /// </summary>
    [SugarColumn(ColumnDescription = "账号")]
    public virtual string Account { get; set; }

    /// <summary>
    /// 真实姓名
    /// </summary>
    [SugarColumn(ColumnDescription = "真实姓名")]
    public virtual string RealName { get; set; }

    /// <summary>
    /// 昵称
    /// </summary>
    [SugarColumn(ColumnDescription = "昵称")]
    public string? NickName { get; set; }

    /// <summary>
    /// 机构名称
    /// </summary>
    [SugarColumn(ColumnDescription = "机构名称")]
    public string? OrgName { get; set; }

    /// <summary>
    /// 职位名称
    /// </summary>
    [SugarColumn(ColumnDescription = "职位名称")]
    public string? PosName { get; set; }

    /// <summary>
    /// 查询实例
    /// </summary>
    /// <param name="db"></param>
    /// <returns></returns>
    public string GetQueryableSqlString(SqlSugarScopeProvider db)
    {
        return db.Queryable<SysUser>()
            .LeftJoin<SysOrg>((u, a) => u.OrgId == a.Id)
            .LeftJoin<SysPos>((u, a, b) => u.PosId == b.Id)
            .Select((u, a, b) => new TestViewSysUser
            {
                Id = u.Id,
                Account = u.Account,
                RealName = u.RealName,
                NickName = u.NickName,
                OrgName = a.Name,
                PosName = b.Name,
            }).ToMappedSqlString();
    }
}