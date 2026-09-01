// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统用户菜单快捷导航服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 445)]
public class SysUserMenuService : IDynamicApiController, ITransient
{
    private readonly SqlSugarRepository<SysUserMenu> _sysUserMenuRep;
    private readonly UserManager _userManager;

    /// <summary>初始化 SysUserMenuService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="sysUserMenuRep">sysUserMenuRep 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="userManager">userManager 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public SysUserMenuService(SqlSugarRepository<SysUserMenu> sysUserMenuRep, UserManager userManager)
    {
        _sysUserMenuRep = sysUserMenuRep;
        _userManager = userManager;
    }

    /// <summary>构建 SysUserMenuService 所需的 Add User Menu 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [UnitOfWork]
    [DisplayName("收藏菜单")]
    [ApiDescriptionSettings(Name = "Add"), HttpPost]
    public async Task AddUserMenu(UserMenuInput input)
    {
        await _sysUserMenuRep.DeleteAsync(u => u.UserId == _userManager.UserId);

        if (input.MenuIdList == null || input.MenuIdList.Count == 0) return;
        var menus = input.MenuIdList.Select(u => new SysUserMenu
        {
            UserId = _userManager.UserId,
            MenuId = u
        }).ToList();
        await _sysUserMenuRep.InsertRangeAsync(menus);
    }

    /// <summary>移除 SysUserMenuService 中与 Delete User Menu 对应的数据，并清理关联状态。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "DeleteUserMenu"), HttpPost]
    [DisplayName("取消收藏菜单")]
    public async Task DeleteUserMenu(UserMenuInput input)
    {
        await _sysUserMenuRep.DeleteAsync(u => u.UserId == _userManager.UserId && input.MenuIdList.Contains(u.MenuId));
    }

    /// <summary>
    /// 获取当前用户收藏的菜单集合 🔖
    /// </summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取当前用户收藏的菜单集合")]
    public async Task<List<MenuOutput>> GetUserMenuList()
    {
        var sysUserMenuList = await _sysUserMenuRep.AsQueryable()
            .Includes(u => u.SysMenu)
            .Where(u => u.UserId == _userManager.UserId).ToListAsync();
        return sysUserMenuList.Where(u => u.SysMenu != null).Select(u => u.SysMenu).ToList().Adapt<List<MenuOutput>>();
    }

    /// <summary>
    /// 获取当前用户收藏的菜单Id集合 🔖
    /// </summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取当前用户收藏的菜单Id集合")]
    public async Task<List<long>> GetUserMenuIdList()
    {
        return await _sysUserMenuRep.AsQueryable()
            .Where(u => u.UserId == _userManager.UserId).Select(u => u.MenuId).ToListAsync();
    }

    /// <summary>
    /// 删除指定用户的收藏菜单
    /// </summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [NonAction]
    public async Task DeleteUserMenuList(long userId)
    {
        await _sysUserMenuRep.DeleteAsync(u => u.UserId == userId);
    }

    /// <summary>
    /// 批量删除收藏菜单
    /// </summary>
    /// <param name="ids">ids 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [NonAction]
    public async Task DeleteMenuList(List<long> ids)
    {
        if (ids == null || ids.Count == 0) return;
        await _sysUserMenuRep.DeleteAsync(u => ids.Contains(u.MenuId));
    }
}