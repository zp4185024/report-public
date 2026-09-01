// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统日程服务
/// </summary>
[ApiDescriptionSettings(Order = 295)]
public class SysScheduleService : IDynamicApiController, ITransient
{
    private readonly UserManager _userManager;
    private readonly SqlSugarRepository<SysSchedule> _sysSchedule;

    /// <summary>初始化 SysScheduleService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="userManager">userManager 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sysSchedule">sysSchedule 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public SysScheduleService(UserManager userManager,
        SqlSugarRepository<SysSchedule> sysSchedule)
    {
        _userManager = userManager;
        _sysSchedule = sysSchedule;
    }

    /// <summary>调用 SysScheduleService 的异步依赖完成 Page 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取日程列表")]
    public async Task<List<SysSchedule>> Page(ListScheduleInput input)
    {
        return await _sysSchedule.AsQueryable()
            .Where(u => u.UserId == _userManager.UserId)
            .WhereIF(_userManager.SuperAdmin && input.TenantId > 0, u => u.TenantId == input.TenantId)
            .WhereIF(!string.IsNullOrWhiteSpace(input.StartTime.ToString()), u => u.ScheduleTime >= input.StartTime)
            .WhereIF(!string.IsNullOrWhiteSpace(input.EndTime.ToString()), u => u.ScheduleTime <= input.EndTime)
            .OrderBy(u => u.StartTime, OrderByType.Asc)
            .ToListAsync();
    }

    /// <summary>查询或读取 SysScheduleService 所需的 Get Detail 数据，并按调用约定返回结果。</summary>
    /// <param name="id">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取日程详情")]
    public async Task<SysSchedule> GetDetail(long id)
    {
        return await _sysSchedule.GetFirstAsync(u => u.Id == id);
    }

    /// <summary>构建 SysScheduleService 所需的 Add User Schedule 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Add"), HttpPost]
    [DisplayName("增加日程")]
    public async Task AddUserSchedule(AddScheduleInput input)
    {
        input.UserId = _userManager.UserId;
        await _sysSchedule.InsertAsync(input.Adapt<SysSchedule>());
    }

    /// <summary>更新 SysScheduleService 的 Update User Schedule 状态，并同步相关业务数据。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Update"), HttpPost]
    [DisplayName("更新日程")]
    public async Task UpdateUserSchedule(UpdateScheduleInput input)
    {
        await _sysSchedule.AsUpdateable(input.Adapt<SysSchedule>()).IgnoreColumns(true).ExecuteCommandAsync();
    }

    /// <summary>移除 SysScheduleService 中与 Delete User Schedule 对应的数据，并清理关联状态。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Delete"), HttpPost]
    [DisplayName("删除日程")]
    public async Task DeleteUserSchedule(DeleteScheduleInput input)
    {
        await _sysSchedule.DeleteAsync(u => u.Id == input.Id);
    }

    /// <summary>更新 SysScheduleService 的 Set Status 状态，并同步相关业务数据。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("设置日程状态")]
    public async Task<int> SetStatus(ScheduleInput input)
    {
        if (!Enum.IsDefined(typeof(FinishStatusEnum), input.Status)) throw Oops.Oh(ErrorCodeEnum.D3005);

        return await _sysSchedule.AsUpdateable()
            .SetColumns(u => u.Status == input.Status)
            .Where(u => u.Id == input.Id)
            .ExecuteCommandAsync();
    }
}