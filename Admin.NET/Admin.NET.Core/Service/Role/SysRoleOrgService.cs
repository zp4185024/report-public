// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统角色机构服务
/// </summary>
public class SysRoleOrgService : ITransient
{
    private readonly SqlSugarRepository<SysRoleOrg> _sysRoleOrgRep;

    /// <summary>初始化 SysRoleOrgService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="sysRoleOrgRep">sysRoleOrgRep 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public SysRoleOrgService(SqlSugarRepository<SysRoleOrg> sysRoleOrgRep)
    {
        _sysRoleOrgRep = sysRoleOrgRep;
    }

    /// <summary>调用 SysRoleOrgService 的异步依赖完成 Grant Role Org 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task GrantRoleOrg(RoleOrgInput input)
    {
        await _sysRoleOrgRep.DeleteAsync(u => u.RoleId == input.Id);
        if (input.DataScope == (int)DataScopeEnum.Define)
        {
            var roleOrgList = input.OrgIdList.Select(u => new SysRoleOrg
            {
                RoleId = input.Id,
                OrgId = u
            }).ToList();
            await _sysRoleOrgRep.InsertRangeAsync(roleOrgList);
        }
    }

    /// <summary>
    /// 根据角色Id集合获取角色机构Id集合
    /// </summary>
    /// <param name="roleIdList">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<List<long>> GetRoleOrgIdList(List<long> roleIdList)
    {
        if (roleIdList?.Count > 0)
        {
            return await _sysRoleOrgRep.AsQueryable()
                .Where(u => roleIdList.Contains(u.RoleId))
                .Select(u => u.OrgId).ToListAsync();
        }
        else return new List<long>();
    }

    /// <summary>
    /// 根据机构Id集合删除角色机构
    /// </summary>
    /// <param name="orgIdList">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task DeleteRoleOrgByOrgIdList(List<long> orgIdList)
    {
        await _sysRoleOrgRep.DeleteAsync(u => orgIdList.Contains(u.OrgId));
    }

    /// <summary>
    /// 根据角色Id删除角色机构
    /// </summary>
    /// <param name="roleId">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task DeleteRoleOrgByRoleId(long roleId)
    {
        await _sysRoleOrgRep.DeleteAsync(u => u.RoleId == roleId);
    }
}