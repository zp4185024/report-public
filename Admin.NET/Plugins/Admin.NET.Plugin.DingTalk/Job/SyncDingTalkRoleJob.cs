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
[JobDetail("SyncDingTalkRoleJob", Description = "同步钉钉角色", GroupName = "default", Concurrent = false)]
public class SyncDingTalkRoleJob : IJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDingTalkApi _dingTalkApi;
    private readonly ILogger _logger;

    public SyncDingTalkRoleJob(IServiceScopeFactory scopeFactory, IDingTalkApi dingTalkApi, ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _dingTalkApi = dingTalkApi;
        _logger = loggerFactory.CreateLogger(CommonConst.SysLogCategoryName);
    }

    public async Task ExecuteAsync(JobExecutingContext context, CancellationToken stoppingToken)
    {
        using var serviceScope = _scopeFactory.CreateScope();
        var _dingTalkRoleRepo = serviceScope.ServiceProvider.GetRequiredService<SqlSugarRepository<DingTalkRoleUser>>();
        var _dingTalkOptions = serviceScope.ServiceProvider.GetRequiredService<IOptions<DingTalkOptions>>();

        // 获取Token
        var tokenRes = await _dingTalkApi.GetDingTalkToken(_dingTalkOptions.Value.ClientId, _dingTalkOptions.Value.ClientSecret);
        if (tokenRes.ErrCode != 0)
            throw Oops.Oh(tokenRes.ErrMsg);

        var dingTalkRoleUserList = new List<DingTalkRoleUser>();
        // 获取角色列表
        var roleIdsRes = await _dingTalkApi.GetDingTalkRoleList(tokenRes.AccessToken, new GetDingTalkCurrentRoleListInput
        { });
        if (roleIdsRes.Success)
        {
            _logger.LogError(roleIdsRes.ErrMsg);
            throw Oops.Oh(roleIdsRes.ErrMsg);
        }
        foreach (var item in roleIdsRes.Result.list)
        {
            foreach (var role_item in item.roles)
            {
                // 根据角色id获取指定角色的员工列表
                var role_user = await _dingTalkApi.GetDingTalkRoleSimplelist(
                    tokenRes.AccessToken,
                    new GetDingTalkCurrentRoleSimplelistInput()
                    {
                        role_id = role_item.id,
                    }
                );

                if (role_user.Success)
                {
                    _logger.LogError(role_user.ErrMsg);
                    break;
                }
                var tempList = role_user.Result.list.Select(u => new DingTalkRoleUser
                {
                    DingTalkUserId = u.userid,
                    groupId = item.groupId,
                    groupName = item.name,
                    roleId = role_item.id,
                    roleName = role_item.name
                }).ToList();
                if (tempList?.Count > 0)
                {
                    dingTalkRoleUserList.AddRange(tempList);
                }
            }
        }

        // 判断新增还是更新
        var sysDingTalkRoleList = await _dingTalkRoleRepo.AsQueryable().ToListAsync();
        // 需要更新的用户Id
        var uDingTalkRole = dingTalkRoleUserList.Where(u => sysDingTalkRoleList.Any(m => m.DingTalkUserId == u.DingTalkUserId && m.groupId == u.groupId));
        // 需要新增的用户Id
        var iDingTalkRole = dingTalkRoleUserList.Where(u => !sysDingTalkRoleList.Any(m => m.DingTalkUserId == u.DingTalkUserId && m.groupId == u.groupId));
        // 需要删除的数据
        var dDingTalkRole = sysDingTalkRoleList.Where(u => !dingTalkRoleUserList.Any(m => m.DingTalkUserId == u.DingTalkUserId && m.groupId == u.groupId)).ToList();
        // 新增钉钉角色
        var iUser = iDingTalkRole.Select(res => new DingTalkRoleUser
        {
            DingTalkUserId = res.DingTalkUserId,
            groupId = res.groupId,
            groupName = res.groupName,
            roleId = res.roleId,
            roleName = res.roleName,
        }).ToList();
        if (iUser.Count > 0)
        {
            await _dingTalkRoleRepo.CopyNew().AsInsertable(iUser).ExecuteCommandAsync();
        }

        // 更新钉钉角色
        var uUser = uDingTalkRole.Select(res => new DingTalkRoleUser
        {
            Id = sysDingTalkRoleList.Where(u => u.DingTalkUserId == res.DingTalkUserId).Select(u => u.Id).FirstOrDefault(),
            DingTalkUserId = res.DingTalkUserId,
            groupId = res.groupId,
            groupName = res.groupName,
            roleId = res.roleId,
            roleName = res.roleName
        }).ToList();
        //添加需要删除的数据
        Parallel.ForEach(dDingTalkRole, user => user.IsDelete = true);
        uUser.AddRange(dDingTalkRole);
        if (uUser.Count > 0)
        {
            await _dingTalkRoleRepo.CopyNew().AsUpdateable(uUser).UpdateColumns(u => new
            {
                u.DingTalkUserId,
                u.groupId,
                u.groupName,
                u.roleId,
                u.roleName,
                u.UpdateTime,
                u.UpdateUserName,
                u.UpdateUserId,
                u.IsDelete
            }).ExecuteCommandAsync();
        }

        var originColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("【" + DateTime.Now + "】同步钉钉角色");
        Console.ForegroundColor = originColor;
    }
}