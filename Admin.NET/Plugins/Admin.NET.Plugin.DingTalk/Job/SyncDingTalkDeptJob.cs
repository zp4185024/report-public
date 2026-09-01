// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Admin.NET.Plugin.DingTalk;
using Furion.Schedule;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Admin.NET.Plugin.Job;

/// <summary>
/// 同步钉钉角色job,自动同步触发器请在web页面按需求设置
/// </summary>
[JobDetail("SyncDingTalkDeptJob", Description = "同步钉钉部门", GroupName = "default", Concurrent = false)]
[Daily(TriggerId = "SyncDingTalkDeptTrigger", Description = "同步钉钉部门")]
public class SyncDingTalkDeptJob : IJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDingTalkApi _dingTalkApi;
    private readonly ILogger _logger;
    private readonly SqlSugarRepository<DingTalkDept> _dingTalkDeptRep;

    /// <summary>初始化 SyncDingTalkDeptJob 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="scopeFactory">scopeFactory 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="dingTalkApi">dingTalkApi 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="dingTalkDeptRep">dingTalkDeptRep 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="loggerFactory">loggerFactory 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public SyncDingTalkDeptJob(
        IServiceScopeFactory scopeFactory,
        IDingTalkApi dingTalkApi,
        SqlSugarRepository<DingTalkDept> dingTalkDeptRep,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _dingTalkApi = dingTalkApi;
        _dingTalkDeptRep = dingTalkDeptRep;
        _logger = loggerFactory.CreateLogger(CommonConst.SysLogCategoryName);
    }

    /// <summary>执行 SyncDingTalkDeptJob 的 Execute Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="stoppingToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task ExecuteAsync(JobExecutingContext context, CancellationToken stoppingToken)
    {
        using var serviceScope = _scopeFactory.CreateScope();
        var _dingTalkOptions = serviceScope.ServiceProvider.GetRequiredService<IOptions<DingTalkOptions>>();

        // 获取Token
        var tokenRes = await _dingTalkApi.GetDingTalkToken(_dingTalkOptions.Value.ClientId, _dingTalkOptions.Value.ClientSecret);
        if (tokenRes.ErrCode != 0)
            throw Oops.Oh(tokenRes.ErrMsg);

        var dingTalkDeptList = new List<DingTalkDept>();
        // 获取部门列表
        var deptIdsRes = await _dingTalkApi.GetDingTalkDept(tokenRes.AccessToken, new GetDingTalkDeptInput
        { dept_id = 1 });
        if (deptIdsRes.ErrCode != 0)
        {
            _logger.LogError(deptIdsRes.ErrMsg);
            throw Oops.Oh(deptIdsRes.ErrMsg);
        }
        dingTalkDeptList.AddRange(deptIdsRes.Result.Select(d => new DingTalkDept
        {
            dept_id = d.dept_id,
            name = d.name,
            parent_id = d.parent_id
        }));
        foreach (var item in deptIdsRes.Result)
        {
            dingTalkDeptList.AddRange(await GetDingTalkDeptList(tokenRes.AccessToken, item.dept_id));
        }
        await _dingTalkDeptRep.InsertOrUpdateAsync(dingTalkDeptList);
        var originColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("【" + DateTime.Now + "】同步钉钉部门");
        Console.ForegroundColor = originColor;
    }

    /// <summary>查询或读取 SyncDingTalkDeptJob 所需的 Get Ding Talk Dept List 数据，并按调用约定返回结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="dept_id">需要定位的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task<List<DingTalkDept>> GetDingTalkDeptList(string token, long dept_id)
    {
        List<DingTalkDept> listTemp = new List<DingTalkDept>();
        var deptIdsRes = await _dingTalkApi.GetDingTalkDept(token, new GetDingTalkDeptInput
        { dept_id = dept_id });
        if (deptIdsRes.ErrCode != 0)
        {
            return null;
        }
        listTemp.AddRange(deptIdsRes.Result.Select(x => new DingTalkDept
        {
            dept_id = x.dept_id,
            name = x.name,
            parent_id = x.parent_id
        }));
        foreach (var item in deptIdsRes.Result)
        {
            listTemp.AddRange(await GetDingTalkDeptList(token, item.dept_id));
        }
        return listTemp;
    }
}