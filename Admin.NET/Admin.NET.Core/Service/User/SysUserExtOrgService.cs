// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统用户扩展机构服务
/// </summary>
public class SysUserExtOrgService : ITransient
{
    private readonly SqlSugarRepository<SysUserExtOrg> _sysUserExtOrgRep;

    /// <summary>初始化 SysUserExtOrgService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="sysUserExtOrgRep">sysUserExtOrgRep 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public SysUserExtOrgService(SqlSugarRepository<SysUserExtOrg> sysUserExtOrgRep)
    {
        _sysUserExtOrgRep = sysUserExtOrgRep;
    }

    /// <summary>
    /// 获取用户扩展机构集合
    /// </summary>
    /// <param name="userId">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<List<SysUserExtOrg>> GetUserExtOrgList(long userId)
    {
        return await _sysUserExtOrgRep.GetListAsync(u => u.UserId == userId);
    }

    /// <summary>
    /// 更新用户扩展机构
    /// </summary>
    /// <param name="userId">需要查询、更新或删除的业务记录标识。</param>
    /// <param name="extOrgList">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task UpdateUserExtOrg(long userId, List<SysUserExtOrg> extOrgList)
    {
        await _sysUserExtOrgRep.DeleteAsync(u => u.UserId == userId);

        if (extOrgList == null || extOrgList.Count < 1) return;
        extOrgList.ForEach(u =>
        {
            u.UserId = userId;
        });
        await _sysUserExtOrgRep.InsertRangeAsync(extOrgList);
    }

    /// <summary>
    /// 根据机构Id集合删除扩展机构
    /// </summary>
    /// <param name="orgIdList">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task DeleteUserExtOrgByOrgIdList(List<long> orgIdList)
    {
        await _sysUserExtOrgRep.DeleteAsync(u => orgIdList.Contains(u.OrgId));
    }

    /// <summary>
    /// 根据用户Id删除扩展机构
    /// </summary>
    /// <param name="userId">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task DeleteUserExtOrgByUserId(long userId)
    {
        await _sysUserExtOrgRep.DeleteAsync(u => u.UserId == userId);
    }

    /// <summary>
    /// 根据机构Id判断是否有用户
    /// </summary>
    /// <param name="orgId">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<bool> HasUserOrg(long orgId)
    {
        return await _sysUserExtOrgRep.IsAnyAsync(u => u.OrgId == orgId);
    }

    /// <summary>
    /// 根据职位Id判断是否有用户
    /// </summary>
    /// <param name="posId">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<bool> HasUserPos(long posId)
    {
        return await _sysUserExtOrgRep.IsAnyAsync(u => u.PosId == posId);
    }
}