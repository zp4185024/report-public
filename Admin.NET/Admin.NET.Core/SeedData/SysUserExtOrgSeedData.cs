// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 系统用户扩展机构表种子数据
/// </summary>
public class SysUserExtOrgSeedData : ISqlSugarEntitySeedData<SysUserExtOrg>
{
    /// <summary>
    /// 种子数据
    /// </summary>
    /// <returns></returns>
    public IEnumerable<SysUserExtOrg> HasData()
    {
        var userList = new SysUserSeedData().HasData().ToList();
        var orgList = new SysOrgSeedData().HasData().ToList();
        var posList = new SysPosSeedData().HasData().ToList();
        var admin = userList.First(u => u.Account == "Admin.NET");
        var user3 = userList.First(u => u.Account == "TestUser3");
        var org1 = orgList.First(u => u.Name == "系统默认");
        var org2 = orgList.First(u => u.Name == "开发部");
        var pos1 = posList.First(u => u.Name == "部门经理");
        var pos2 = posList.First(u => u.Name == "主任");
        return new[]
        {
            new SysUserExtOrg{ Id=1300000000101, UserId=admin.Id, OrgId=org1.Id, PosId=pos1.Id },
            new SysUserExtOrg{ Id=1300000000102, UserId=user3.Id, OrgId=org2.Id, PosId=pos2.Id  }
        };
    }
}