// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 系统用户角色表种子数据
/// </summary>
public class SysUserRoleSeedData : ISqlSugarEntitySeedData<SysUserRole>
{
    /// <summary>
    /// 种子数据
    /// </summary>
    /// <returns></returns>
    public IEnumerable<SysUserRole> HasData()
    {
        var userList = new SysUserSeedData().HasData().ToList();
        var roleList = new SysRoleSeedData().HasData().ToList();
        return new[]
        {
            new SysUserRole{ Id=1300000000101, UserId=userList.First(u => u.Account == "TestUser1").Id, RoleId=roleList.First(u => u.Code == "sys_deptChild").Id },
            new SysUserRole{ Id=1300000000102, UserId=userList.First(u => u.Account == "TestUser2").Id, RoleId=roleList.First(u => u.Code == "sys_dept").Id },
            new SysUserRole{ Id=1300000000103, UserId=userList.First(u => u.Account == "TestUser3").Id, RoleId=roleList.First(u => u.Code == "sys_self").Id },
            new SysUserRole{ Id=1300000000104, UserId=userList.First(u => u.Account == "TestUser4").Id, RoleId=roleList.First(u => u.Code == "sys_define").Id },
        };
    }
}