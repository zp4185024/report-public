// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using OnceMi.AspNetCore.OSS;

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统文件存储提供者服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 411, Description = "文件存储提供者")]
public class SysFileProviderService : IDynamicApiController, ITransient
{
    private readonly UserManager _userManager;
    private readonly SqlSugarRepository<SysFileProvider> _sysFileProviderRep;
    private readonly SysCacheService _sysCacheService;
    private readonly IOSSServiceFactory _ossServiceFactory;
    private readonly IOSSServiceManager _ossServiceManager;
    private static readonly string CacheKey = "sys_file_provider";

    public SysFileProviderService(UserManager userManager,
        SqlSugarRepository<SysFileProvider> sysFileProviderRep,
        SysCacheService sysCacheService,
        IOSSServiceFactory ossServiceFactory,
        IOSSServiceManager ossServiceManager)
    {
        _userManager = userManager;
        _sysFileProviderRep = sysFileProviderRep;
        _sysCacheService = sysCacheService;
        _ossServiceFactory = ossServiceFactory;
        _ossServiceManager = ossServiceManager;
    }

    /// <summary>
    /// 获取文件存储提供者分页列表 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("获取文件存储提供者分页列表")]
    [NonAction]
    public async Task<SqlSugarPagedList<SysFileProvider>> GetFileProviderPage([FromQuery] PageFileProviderInput input)
    {
        return await _sysFileProviderRep.AsQueryable()
            .WhereIF(!string.IsNullOrWhiteSpace(input.Provider), u => u.Provider.Contains(input.Provider!))
            .WhereIF(!string.IsNullOrWhiteSpace(input.BucketName), u => u.BucketName.Contains(input.BucketName!))
            .WhereIF(input.IsEnable.HasValue, u => u.IsEnable == input.IsEnable)
            .OrderBy(u => u.OrderNo)
            .OrderBy(u => u.Id)
            .ToPagedListAsync(input.Page, input.PageSize);
    }

    /// <summary>
    /// 获取文件存储提供者列表 🔖
    /// </summary>
    /// <returns></returns>
    [DisplayName("获取文件存储提供者列表")]
    [NonAction]
    public async Task<List<SysFileProvider>> GetFileProviderList()
    {
        return await _sysFileProviderRep.AsQueryable()
            .Where(u => u.IsEnable == true)
            .OrderBy(u => u.OrderNo)
            .OrderBy(u => u.Id)
            .ToListAsync();
    }

    /// <summary>
    /// 增加文件存储提供者 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [ApiDescriptionSettings(Name = "Add"), HttpPost]
    [DisplayName("增加文件存储提供者")]
    [NonAction]
    public async Task AddFileProvider(AddFileProviderInput input)
    {
        // 验证输入参数
        if (input == null)
            throw Oops.Oh("输入参数不能为空").StatusCode(400);

        if (string.IsNullOrWhiteSpace(input.Provider))
            throw Oops.Oh("存储提供者不能为空").StatusCode(400);

        if (string.IsNullOrWhiteSpace(input.BucketName))
            throw Oops.Oh("存储桶名称不能为空").StatusCode(400);

        // 验证提供者类型
        if (!Enum.TryParse<OSSProvider>(input.Provider, true, out _))
            throw Oops.Oh($"不支持的存储提供者类型: {input.Provider}").StatusCode(400);

        var isExist = await _sysFileProviderRep.AsQueryable()
            .AnyAsync(u => u.Provider == input.Provider && u.BucketName == input.BucketName);
        if (isExist)
            throw Oops.Oh(ErrorCodeEnum.D1006).StatusCode(400);

        var fileProvider = input.Adapt<SysFileProvider>();

        // 验证配置完整性
        await ValidateProviderConfiguration(fileProvider);

        // 处理默认提供者逻辑
        await HandleDefaultProviderLogic(fileProvider);

        await _sysFileProviderRep.InsertAsync(fileProvider);

        // 清除缓存
        await ClearCache();

        // 清除OSS服务缓存
        _ossServiceManager?.ClearCache();
    }

    /// <summary>
    /// 更新文件存储提供者 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [ApiDescriptionSettings(Name = "Update"), HttpPost]
    [DisplayName("更新文件存储提供者")]
    [NonAction]
    public async Task UpdateFileProvider(UpdateFileProviderInput input)
    {
        // 验证输入参数
        if (input == null)
            throw Oops.Oh("输入参数不能为空").StatusCode(400);

        var isExist = await _sysFileProviderRep.AsQueryable()
            .AnyAsync(u => u.Provider == input.Provider && u.BucketName == input.BucketName && u.Id != input.Id);
        if (isExist)
            throw Oops.Oh(ErrorCodeEnum.D1006).StatusCode(400);

        var fileProvider = input.Adapt<SysFileProvider>();

        // 验证配置完整性
        await ValidateProviderConfiguration(fileProvider);

        // 处理默认提供者逻辑
        await HandleDefaultProviderLogic(fileProvider);

        await _sysFileProviderRep.AsUpdateable(fileProvider).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommandAsync();

        // 清除缓存
        await ClearCache();

        // 清除OSS服务缓存
        _ossServiceManager?.ClearCache();
    }

    /// <summary>
    /// 删除文件存储提供者 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [ApiDescriptionSettings(Name = "Delete"), HttpPost]
    [DisplayName("删除文件存储提供者")]
    [NonAction]
    public async Task DeleteFileProvider(DeleteFileProviderInput input)
    {
        // 检查是否为默认提供者
        var provider = await _sysFileProviderRep.GetByIdAsync(input.Id) ?? throw Oops.Oh("存储提供者不存在").StatusCode(400);
        var isDefault = provider.IsDefault == true;

        await _sysFileProviderRep.DeleteByIdAsync(input.Id);

        // 如果删除的是默认提供者，自动设置第一个启用的提供者为默认
        if (isDefault)
        {
            var firstEnabledProvider = await _sysFileProviderRep.AsQueryable()
                .Where(p => p.IsEnable == true)
                .OrderBy(p => p.OrderNo)
                .OrderBy(p => p.Id)
                .FirstAsync();

            if (firstEnabledProvider != null)
            {
                await _sysFileProviderRep.AsUpdateable()
                    .SetColumns(p => p.IsDefault == true)
                    .Where(p => p.Id == firstEnabledProvider.Id)
                    .ExecuteCommandAsync();

                Debug.WriteLine($"自动设置新的默认提供者: {firstEnabledProvider.DisplayName}");
            }
        }

        // 清除缓存
        await ClearCache();

        // 清除OSS服务缓存
        _ossServiceManager?.ClearCache();
    }

    /// <summary>
    /// 获取文件存储提供者详情 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("获取文件存储提供者详情")]
    [NonAction]
    public async Task<SysFileProvider> GetFileProvider([FromQuery] QueryFileProviderInput input)
    {
        return await _sysFileProviderRep.GetFirstAsync(u => u.Id == input.Id);
    }

    /// <summary>
    /// 根据提供者和存储桶获取配置
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="bucketName"></param>
    /// <returns></returns>
    [NonAction]
    public async Task<SysFileProvider?> GetFileProviderByBucket(string provider, string bucketName)
    {
        var providers = await GetCachedFileProviders();
        return providers.FirstOrDefault(x => x.Provider == provider && x.BucketName == bucketName && x.IsEnable == true);
    }

    /// <summary>
    /// 根据ID获取配置
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [NonAction]
    public async Task<SysFileProvider?> GetFileProviderById(long id)
    {
        var providers = await GetCachedFileProviders();
        return providers.FirstOrDefault(x => x.Id == id && x.IsEnable == true);
    }

    /// <summary>
    /// 根据存储桶名称获取存储提供者
    /// </summary>
    /// <param name="bucketName">存储桶名称</param>
    /// <returns></returns>
    [NonAction]
    public async Task<SysFileProvider?> GetProviderByBucketName(string bucketName)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
            return null;

        var providers = await GetCachedFileProviders();
        return providers.FirstOrDefault(p => p.BucketName == bucketName);
    }

    /// <summary>
    /// 获取默认存储提供者
    /// </summary>
    /// <returns></returns>
    [NonAction]
    public async Task<SysFileProvider?> GetDefaultProvider()
    {
        var providers = await GetCachedFileProviders();

        // 优先返回标记为默认的提供者
        var defaultProvider = providers.FirstOrDefault(p => p.IsDefault == true);
        if (defaultProvider != null)
            return defaultProvider;

        // 如果没有标记为默认的，返回第一个启用的提供者（兼容旧逻辑）
        return providers.FirstOrDefault();
    }

    /// <summary>
    /// 获取默认存储提供者信息 🔖
    /// </summary>
    /// <returns></returns>
    [DisplayName("获取默认存储提供者信息")]
    [NonAction]
    public async Task<SysFileProvider?> GetDefaultProviderInfo()
    {
        return await GetDefaultProvider();
    }

    /// <summary>
    /// 设置默认存储提供者 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [ApiDescriptionSettings(Name = "SetDefault"), HttpPost]
    [DisplayName("设置默认存储提供者")]
    [NonAction]
    public async Task SetDefaultProvider(SetDefaultProviderInput input)
    {
        // 验证提供者是否存在且启用
        var provider = await _sysFileProviderRep.GetByIdAsync(input.Id) ?? throw Oops.Oh("存储提供者不存在").StatusCode(400);
        if (provider.IsEnable != true)
            throw Oops.Oh("只能设置启用状态的存储提供者为默认").StatusCode(400);

        // 开启事务，确保数据一致性
        await _sysFileProviderRep.AsTenant().BeginTranAsync();
        try
        {
            // 先将所有提供者的默认标识设为false
            await _sysFileProviderRep.AsUpdateable()
                .SetColumns(p => p.IsDefault == false)
                .Where(p => p.IsDefault == true)
                .ExecuteCommandAsync();

            // 设置指定提供者为默认
            await _sysFileProviderRep.AsUpdateable()
                .SetColumns(p => p.IsDefault == true)
                .Where(p => p.Id == input.Id)
                .ExecuteCommandAsync();

            await _sysFileProviderRep.AsTenant().CommitTranAsync();

            // 清除缓存
            await ClearCache();

            // 清除OSS服务缓存
            _ossServiceManager?.ClearCache();

            Debug.WriteLine($"已设置默认存储提供者: {provider.DisplayName}");
        }
        catch (Exception)
        {
            await _sysFileProviderRep.AsTenant().RollbackTranAsync();
            throw;
        }
    }

    /// <summary>
    /// 获取缓存的文件提供者列表
    /// </summary>
    /// <returns></returns>
    [NonAction]
    public async Task<List<SysFileProvider>> GetCachedFileProviders()
    {
        return await _sysCacheService.AdGetAsync(CacheKey, async () =>
        {
            return await _sysFileProviderRep.AsQueryable()
                .Where(u => u.IsEnable == true)
                .OrderBy(u => u.OrderNo)
                .OrderBy(u => u.Id)
                .ToListAsync();
        }, TimeSpan.FromMinutes(30));
    }

    /// <summary>
    /// 清除缓存
    /// </summary>
    /// <returns></returns>
    [NonAction]
    public async Task ClearCache()
    {
        _sysCacheService.Remove(CacheKey);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 获取所有可用的存储桶列表
    /// </summary>
    /// <returns></returns>
    [NonAction]
    public async Task<List<string>> GetAvailableBuckets()
    {
        var providers = await GetCachedFileProviders();
        return providers.Select(p => p.BucketName).Distinct().OrderBy(b => b).ToList();
    }

    /// <summary>
    /// 获取存储桶和提供者的映射关系
    /// </summary>
    /// <returns></returns>
    [NonAction]
    public async Task<Dictionary<string, List<SysFileProvider>>> GetBucketProviderMapping()
    {
        var providers = await GetCachedFileProviders();
        var mapping = new Dictionary<string, List<SysFileProvider>>();

        foreach (var provider in providers)
        {
            if (!mapping.TryGetValue(provider.BucketName, out List<SysFileProvider> value))
            {
                value = new List<SysFileProvider>();
                mapping[provider.BucketName] = value;
            }

            value.Add(provider);
        }

        return mapping;
    }

    /// <summary>
    /// 验证存储提供者配置
    /// </summary>
    /// <param name="provider">存储提供者配置</param>
    /// <returns></returns>
    [NonAction]
    private async Task ValidateProviderConfiguration(SysFileProvider provider)
    {
        if (provider == null)
            throw Oops.Oh("存储提供者配置不能为空").StatusCode(400);

        // 基础字段验证
        if (string.IsNullOrWhiteSpace(provider.Provider))
            throw Oops.Oh("存储提供者类型不能为空").StatusCode(400);

        if (string.IsNullOrWhiteSpace(provider.BucketName))
            throw Oops.Oh("存储桶名称不能为空").StatusCode(400);

        if (string.IsNullOrWhiteSpace(provider.Endpoint))
            throw Oops.Oh("端点地址不能为空").StatusCode(400);

        // 所有提供者都需要AccessKey和SecretKey
        if (string.IsNullOrWhiteSpace(provider.AccessKey))
            throw Oops.Oh($"{provider.Provider} AccessKey不能为空").StatusCode(400);
        if (string.IsNullOrWhiteSpace(provider.SecretKey))
            throw Oops.Oh($"{provider.Provider} SecretKey不能为空").StatusCode(400);

        // 根据不同提供者验证特定字段
        switch (provider.Provider.ToUpper())
        {
            case "ALIYUN":
                if (string.IsNullOrWhiteSpace(provider.Region))
                    throw Oops.Oh("阿里云Region不能为空").StatusCode(400);
                break;

            case "QCLOUD":
                if (string.IsNullOrWhiteSpace(provider.Endpoint))
                    throw Oops.Oh("腾讯云Endpoint(AppId)不能为空").StatusCode(400);
                if (string.IsNullOrWhiteSpace(provider.Region))
                    throw Oops.Oh("腾讯云Region不能为空").StatusCode(400);
                break;

            case "MINIO":
                // Minio只需要AccessKey和SecretKey，已在上面验证
                break;

            default:
                throw Oops.Oh($"不支持的存储提供者类型: {provider.Provider}").StatusCode(400);
        }

        // 验证存储桶名称格式
        await ValidateBucketName(provider.Provider, provider.BucketName);
    }

    /// <summary>
    /// 验证存储桶名称格式
    /// </summary>
    /// <param name="provider">存储提供者类型</param>
    /// <param name="bucketName">存储桶名称</param>
    /// <returns></returns>
    [NonAction]
    private async Task ValidateBucketName(string provider, string bucketName)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
            return;

        switch (provider.ToUpper())
        {
            case "ALIYUN":
                // 阿里云存储桶命名规则
                if (bucketName.Length < 3 || bucketName.Length > 63)
                    throw Oops.Oh("阿里云存储桶名称长度必须在3-63字符之间").StatusCode(400);

                if (!Regex.IsMatch(bucketName, @"^[a-z0-9][a-z0-9\-]*[a-z0-9]$"))
                    throw Oops.Oh("阿里云存储桶名称只能包含小写字母、数字和短横线，且必须以字母或数字开头和结尾").StatusCode(400);
                break;

            case "QCLOUD":
                // 腾讯云存储桶命名规则
                if (bucketName.Length < 1 || bucketName.Length > 40)
                    throw Oops.Oh("腾讯云存储桶名称长度必须在1-40字符之间").StatusCode(400);

                if (!Regex.IsMatch(bucketName, @"^[a-z0-9][a-z0-9\-]*[a-z0-9]$"))
                    throw Oops.Oh("腾讯云存储桶名称只能包含小写字母、数字和短横线，且必须以字母或数字开头和结尾").StatusCode(400);
                break;

            case "MINIO":
                // Minio存储桶命名规则
                if (bucketName.Length < 3 || bucketName.Length > 63)
                    throw Oops.Oh("Minio存储桶名称长度必须在3-63字符之间").StatusCode(400);

                if (!Regex.IsMatch(bucketName, @"^[a-z0-9][a-z0-9\-\.]*[a-z0-9]$"))
                    throw Oops.Oh("Minio存储桶名称只能包含小写字母、数字、短横线和点，且必须以字母或数字开头和结尾").StatusCode(400);
                break;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 处理默认提供者逻辑
    /// </summary>
    /// <param name="provider">存储提供者配置</param>
    /// <returns></returns>
    [NonAction]
    private async Task HandleDefaultProviderLogic(SysFileProvider provider)
    {
        // 如果设置为默认提供者
        if (provider.IsDefault == true)
        {
            // 确保只有一个默认提供者，将其他提供者的默认标识设为false
            await _sysFileProviderRep.AsUpdateable()
                .SetColumns(p => p.IsDefault == false)
                .Where(p => p.IsDefault == true && p.Id != provider.Id)
                .ExecuteCommandAsync();
        }
        else
        // 如果没有设置IsDefault值，默认为false
        {
            provider.IsDefault ??= false;
        }

        // 检查是否还有其他默认提供者，如果没有且当前提供者启用，则设为默认
        var hasDefaultProvider = await _sysFileProviderRep.AsQueryable()
            .Where(p => p.IsDefault == true && p.IsEnable == true && p.Id != provider.Id)
            .AnyAsync();

        if (!hasDefaultProvider && provider.IsEnable == true && provider.IsDefault != true)
        {
            // 如果没有其他默认提供者且当前提供者启用，则设为默认
            provider.IsDefault = true;
        }
    }
}