// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统租户配置参数服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 440)]
public class SysTenantConfigService : IDynamicApiController, ITransient
{
    private readonly SysCacheService _sysCacheService;
    private readonly SqlSugarRepository<SysTenantConfig> _sysConfigRep;
    private readonly SqlSugarRepository<SysTenantConfigData> _sysConfigDataRep;
    public readonly ISugarQueryable<SysConfig> VSysConfig;
    private readonly UserManager _userManager;

    /// <summary>初始化 SysTenantConfigService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="sysCacheService">当前方法执行时使用的 sysCacheService 输入值。</param>
    /// <param name="sysConfigRep">当前方法执行时使用的 sysConfigRep 输入值。</param>
    /// <param name="sysConfigDataRep">当前方法执行时使用的 sysConfigDataRep 输入值。</param>
    /// <param name="userManager">当前方法执行时使用的 userManager 输入值。</param>
    public SysTenantConfigService(SysCacheService sysCacheService,
        SqlSugarRepository<SysTenantConfig> sysConfigRep,
        SqlSugarRepository<SysTenantConfigData> sysConfigDataRep,
       UserManager userManager)
    {
        _userManager = userManager;
        _sysCacheService = sysCacheService;
        _sysConfigRep = sysConfigRep;
        _sysConfigDataRep = sysConfigDataRep;
        VSysConfig = _sysConfigRep.AsQueryable()
            .InnerJoin(
                _sysConfigDataRep.AsQueryable().WhereIF(!_userManager.SuperAdmin, cv => cv.TenantId == _userManager.TenantId),
                (c, cv) => c.Id == cv.ConfigId
            )
            //.Select<SysConfig>().MergeTable();
            // 解决PostgreSQL下并启用驼峰转下划线时,报字段不存在,SqlSugar在Select后生成的sql, as后没转下划线导致.
            .SelectMergeTable((c, cv) => new SysConfig { Id = c.Id.SelectAll(), Value = cv.Value });
    }

    /// <summary>
    /// 获取配置参数分页列表 🔖
    /// </summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取配置参数分页列表")]
    public async Task<SqlSugarPagedList<SysConfig>> Page(PageConfigInput input)
    {
        return await VSysConfig
            .WhereIF(!string.IsNullOrWhiteSpace(input.Name?.Trim()), u => u.Name.Contains(input.Name))
            .WhereIF(!string.IsNullOrWhiteSpace(input.Code?.Trim()), u => u.Code.Contains(input.Code))
            .WhereIF(!string.IsNullOrWhiteSpace(input.GroupCode?.Trim()), u => u.GroupCode.Equals(input.GroupCode))
            .OrderBuilder(input)
            .ToPagedListAsync(input.Page, input.PageSize);
    }

    /// <summary>
    /// 获取配置参数列表 🔖
    /// </summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取配置参数列表")]
    public async Task<List<SysConfig>> List(PageConfigInput input)
    {
        return await VSysConfig
            .WhereIF(!string.IsNullOrWhiteSpace(input.GroupCode?.Trim()), u => u.GroupCode.Equals(input.GroupCode))
            .ToListAsync();
    }

    /// <summary>构建 SysTenantConfigService 所需的 Add Config 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Add"), HttpPost]
    [DisplayName("增加配置参数")]
    public async Task AddConfig(AddConfigInput input)
    {
        var isExist = await _sysConfigRep.IsAnyAsync(u => u.Name == input.Name || u.Code == input.Code);
        if (isExist) throw Oops.Oh(ErrorCodeEnum.D9000);

        var configId = _sysConfigRep.InsertReturnSnowflakeId(input.Adapt<SysTenantConfig>());
        await _sysConfigDataRep.InsertAsync(new SysTenantConfigData()
        {
            ConfigId = configId,
            Value = input.Value
        });
    }

    /// <summary>更新 SysTenantConfigService 的 Update Config 状态，并同步相关业务数据。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Update"), HttpPost]
    [DisplayName("更新配置参数")]
    [UnitOfWork]
    public async Task UpdateConfig(UpdateConfigInput input)
    {
        var isExist = await _sysConfigRep.IsAnyAsync(u => (u.Name == input.Name || u.Code == input.Code) && u.Id != input.Id);
        if (isExist) throw Oops.Oh(ErrorCodeEnum.D9000);

        var config = input.Adapt<SysTenantConfig>();
        await _sysConfigRep.AsUpdateable(config).IgnoreColumns(true).ExecuteCommandAsync();
        var configData = await _sysConfigDataRep.GetFirstAsync(cv => cv.ConfigId == input.Id);
        if (configData == null)
            await _sysConfigDataRep.AsInsertable(new SysTenantConfigData() { ConfigId = input.Id, Value = input.Value }).ExecuteCommandAsync();
        else
        {
            configData.Value = input.Value;
            await _sysConfigDataRep.AsUpdateable(configData).IgnoreColumns(true).ExecuteCommandAsync();
        }

        RemoveConfigCache(config);
    }

    /// <summary>移除 SysTenantConfigService 中与 Delete Config 对应的数据，并清理关联状态。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Delete"), HttpPost]
    [DisplayName("删除配置参数")]
    public async Task DeleteConfig(DeleteConfigInput input)
    {
        var config = await _sysConfigRep.GetByIdAsync(input.Id);
        // 禁止删除系统参数
        if (config.SysFlag == YesNoEnum.Y) throw Oops.Oh(ErrorCodeEnum.D9001);

        await _sysConfigRep.DeleteAsync(config);
        await _sysConfigDataRep.DeleteAsync(it => it.TenantId == _userManager.TenantId && it.ConfigId == config.Id);

        RemoveConfigCache(config);
    }

    /// <summary>
    /// 批量删除配置参数 🔖
    /// </summary>
    /// <param name="ids">当前方法执行时使用的 ids 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "BatchDelete"), HttpPost]
    [DisplayName("批量删除配置参数")]
    public async Task BatchDeleteConfig(List<long> ids)
    {
        foreach (var id in ids)
        {
            var config = await _sysConfigRep.GetByIdAsync(id);
            // 禁止删除系统参数
            if (config.SysFlag == YesNoEnum.Y) continue;

            await _sysConfigRep.DeleteAsync(config);
            await _sysConfigDataRep.DeleteAsync(it => it.TenantId == _userManager.TenantId && it.ConfigId == config.Id);

            RemoveConfigCache(config);
        }
    }

    /// <summary>
    /// 获取配置参数详情 🔖
    /// </summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取配置参数详情")]
    public async Task<SysConfig> GetDetail([FromQuery] ConfigInput input)
    {
        return await VSysConfig.FirstAsync(u => u.Id == input.Id);
    }

    /// <summary>
    /// 根据Code获取配置参数 🔖
    /// </summary>
    /// <param name="code">需要读取、校验、比较或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [NonAction]
    public async Task<SysConfig> GetConfig(string code)
    {
        return await VSysConfig.FirstAsync(u => u.Code == code);
    }

    /// <summary>
    /// 根据Code获取配置参数值 🔖
    /// </summary>
    /// <param name="code">编码</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("根据Code获取配置参数值")]
    public async Task<string> GetConfigValueByCode(string code)
    {
        return await GetConfigValueByCode<string>(code);
    }

    /// <summary>查询或读取 SysTenantConfigService 所需的 Get Config Value By Code 数据，并按调用约定返回结果。</summary>
    /// <param name="code">编码</param>
    /// <param name="defaultValue">默认值</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [NonAction]
    public async Task<string> GetConfigValueByCode(string code, string defaultValue = default)
    {
        return await GetConfigValueByCode<string>(code, defaultValue);
    }

    /// <summary>查询或读取 SysTenantConfigService 所需的 Get Config Value By Code 数据，并按调用约定返回结果。</summary>
    /// <typeparam name="T">类型</typeparam>
    /// <param name="code">编码</param>
    /// <param name="defaultValue">默认值</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [NonAction]
    public async Task<T> GetConfigValueByCode<T>(string code, T defaultValue = default)
    {
        return await GetConfigValueByCode<T>(code, _userManager.TenantId, defaultValue);
    }

    /// <summary>查询或读取 SysTenantConfigService 所需的 Get Config Value By Code 数据，并按调用约定返回结果。</summary>
    /// <typeparam name="T">类型</typeparam>
    /// <param name="code">编码</param>
    /// <param name="tenantId">租户Id</param>
    /// <param name="defaultValue">默认值</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [NonAction]
    public async Task<T> GetConfigValueByCode<T>(string code, long tenantId, T defaultValue = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return defaultValue;

        var value = _sysCacheService.Get<string>($"{CacheConst.KeyTenantConfig}{tenantId}{code}");
        if (string.IsNullOrEmpty(value))
        {
            value = (await VSysConfig.FirstAsync(u => u.Code == code))?.Value;
            _sysCacheService.Set($"{CacheConst.KeyTenantConfig}{tenantId}{code}", value);
        }
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return (T)Convert.ChangeType(value, typeof(T));
    }

    /// <summary>更新 SysTenantConfigService 的 Update Config Value 状态，并同步相关业务数据。</summary>
    /// <param name="code">需要读取、校验、比较或转换的业务值。</param>
    /// <param name="value">需要读取、校验、比较或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [NonAction]
    public async Task UpdateConfigValue(string code, string value)
    {
        var config = await _sysConfigRep.GetFirstAsync(u => u.Code == code);
        if (config == null) return;

        await _sysConfigDataRep.AsUpdateable().SetColumns(it => it.Value == value).Where(it => it.TenantId == _userManager.TenantId && it.ConfigId == config.Id).ExecuteCommandAsync();

        RemoveConfigCache(config);
    }

    /// <summary>查询或读取 SysTenantConfigService 所需的 Get Group List 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取分组列表")]
    public async Task<List<string>> GetGroupList()
    {
        return await VSysConfig
            .GroupBy(u => u.GroupCode)
            .Select(u => u.GroupCode).ToListAsync();
    }

    /// <summary>
    /// 批量更新配置参数值 🔖
    /// </summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "BatchUpdate"), HttpPost]
    [DisplayName("批量更新配置参数值")]
    public async Task BatchUpdateConfig(List<BatchConfigInput> input)
    {
        foreach (var config in input)
        {
            await UpdateConfigValue(config.Code, config.Value);
        }
    }

    /// <summary>移除 SysTenantConfigService 中与 Remove Config Cache 对应的数据，并清理关联状态。</summary>
    /// <param name="config">控制本次调用行为、超时或展示方式的配置选项。</param>
    private void RemoveConfigCache(SysTenantConfig config)
    {
        _sysCacheService.Remove($"{CacheConst.KeyTenantConfig}{_userManager.TenantId}{config.Code}");
    }
}