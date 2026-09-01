// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 用户域账号服务
/// </summary>
public class SysUserLdapService : ITransient
{
    private readonly SqlSugarRepository<SysUserLdap> _sysUserLdapRep;

    /// <summary>初始化 SysUserLdapService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="sysUserLdapRep">sysUserLdapRep 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public SysUserLdapService(SqlSugarRepository<SysUserLdap> sysUserLdapRep)
    {
        _sysUserLdapRep = sysUserLdapRep;
    }

    /// <summary>调用 SysUserLdapService 的异步依赖完成 Insert User Ldap List 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="tenantId">需要查询、更新或删除的业务记录标识。</param>
    /// <param name="sysUserLdapList">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task InsertUserLdapList(long tenantId, List<SysUserLdap> sysUserLdapList)
    {
        await _sysUserLdapRep.DeleteAsync(u => u.TenantId == tenantId);

        await _sysUserLdapRep.InsertRangeAsync(sysUserLdapList);

        await _sysUserLdapRep.AsUpdateable()
            .InnerJoin<SysUser>((l, u) => l.EmployeeId == u.Account)
            .SetColumns((l, u) => new SysUserLdap { UserId = u.Id })
            .Where((l, u) => l.TenantId == tenantId && u.Status == StatusEnum.Enable)
            .ExecuteCommandAsync();
    }

    /// <summary>构建 SysUserLdapService 所需的 Add User Ldap 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="tenantId">需要查询、更新或删除的业务记录标识。</param>
    /// <param name="userId">需要查询、更新或删除的业务记录标识。</param>
    /// <param name="account">控制数据范围或位置的数值。</param>
    /// <param name="domainAccount">控制数据范围或位置的数值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task AddUserLdap(long tenantId, long userId, string account, string domainAccount)
    {
        var userLdap = await _sysUserLdapRep.GetFirstAsync(u => u.TenantId == tenantId && (u.Account == account || u.UserId == userId || u.EmployeeId == domainAccount));
        if (userLdap != null) await _sysUserLdapRep.DeleteByIdAsync(userLdap.Id);

        if (!string.IsNullOrWhiteSpace(domainAccount))
            await _sysUserLdapRep.InsertAsync(new SysUserLdap { EmployeeId = account, TenantId = tenantId, UserId = userId, Account = domainAccount });
    }

    /// <summary>移除 SysUserLdapService 中与 Delete User Ldap By User Id 对应的数据，并清理关联状态。</summary>
    /// <param name="userId">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task DeleteUserLdapByUserId(long userId)
    {
        await _sysUserLdapRep.DeleteAsync(u => u.UserId == userId);
    }
}