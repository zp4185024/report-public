// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Furion.InstantMessaging;
using Microsoft.AspNetCore.SignalR;

namespace Admin.NET.Core;

/// <summary>
/// 在线用户集线器
/// </summary>
[MapHub("/hubs/onlineUser")]
public class OnlineUserHub : Hub<IOnlineUserHub>
{
    private const string GROUP_ONLINE = "GROUP_ONLINE_"; // 租户分组前缀

    private readonly SqlSugarRepository<SysOnlineUser> _sysOnlineUerRep;
    private readonly SysMessageService _sysMessageService;
    private readonly IHubContext<OnlineUserHub, IOnlineUserHub> _onlineUserHubContext;
    private readonly SysCacheService _sysCacheService;
    private readonly SysConfigService _sysConfigService;

    /// <summary>初始化 OnlineUserHub 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="sysOnlineUerRep">当前方法执行时使用的 sysOnlineUerRep 输入值。</param>
    /// <param name="sysMessageService">当前方法执行时使用的 sysMessageService 输入值。</param>
    /// <param name="onlineUserHubContext">本次操作共享的运行上下文。</param>
    /// <param name="sysCacheService">当前方法执行时使用的 sysCacheService 输入值。</param>
    /// <param name="sysConfigService">当前方法执行时使用的 sysConfigService 输入值。</param>
    public OnlineUserHub(SqlSugarRepository<SysOnlineUser> sysOnlineUerRep,
        SysMessageService sysMessageService,
        IHubContext<OnlineUserHub, IOnlineUserHub> onlineUserHubContext,
        SysCacheService sysCacheService,
        SysConfigService sysConfigService)
    {
        _sysOnlineUerRep = sysOnlineUerRep;
        _sysMessageService = sysMessageService;
        _onlineUserHubContext = onlineUserHubContext;
        _sysCacheService = sysCacheService;
        _sysConfigService = sysConfigService;
    }

    /// <summary>响应 OnlineUserHub 的 On Connected Async 事件，并协调相关状态和后续操作。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var userId = (httpContext.User.FindFirst(ClaimConst.UserId)?.Value).ToLong();
        var account = httpContext.User.FindFirst(ClaimConst.Account)?.Value;
        var realName = httpContext.User.FindFirst(ClaimConst.RealName)?.Value;
        var tenantId = (httpContext.User.FindFirst(ClaimConst.TenantId)?.Value).ToLong();

        if (userId < 0 || string.IsNullOrWhiteSpace(account)) return;
        var user = new SysOnlineUser
        {
            ConnectionId = Context.ConnectionId,
            UserId = userId,
            UserName = account,
            RealName = realName,
            Time = DateTime.Now,
            Ip = httpContext.GetRemoteIpAddressToIPv4(true),
            Browser = httpContext.GetClientBrowser(),
            Os = httpContext.GetClientOs(),
            TenantId = tenantId,
        };
        await _sysOnlineUerRep.InsertAsync(user);

        // 是否开启单用户登录
        if (await _sysConfigService.GetConfigValue<bool>(ConfigConst.SysSingleLogin))
        {
            _sysCacheService.HashAddOrUpdate(CacheConst.KeyUserOnline, "" + user.UserId, user);
        }
        else  // 非单用户登录则绑定用户连接Id
        {
            _sysCacheService.HashAdd(CacheConst.KeyUserOnline, user.UserId + Context.ConnectionId, user);
        }

        // 以租户Id进行分组
        var groupName = $"{GROUP_ONLINE}{user.TenantId}";
        await _onlineUserHubContext.Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        var userList = await _sysOnlineUerRep.AsQueryable().Filter("", true)
            .Where(u => u.TenantId == user.TenantId).Take(10).ToListAsync();

        if (await _sysConfigService.GetConfigValue<bool>(ConfigConst.SysLoginOutReminder))
            await _onlineUserHubContext.Clients.Groups(groupName).OnlineUserList(new OnlineUserList
            {
                RealName = user.RealName,
                Online = true,
                UserList = userList
            });
    }

    /// <summary>响应 OnlineUserHub 的 On Disconnected Async 事件，并协调相关状态和后续操作。</summary>
    /// <param name="exception">需要记录、展示或继续传递的错误上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        if (string.IsNullOrEmpty(Context.ConnectionId)) return;

        var httpContext = Context.GetHttpContext();

        var user = await _sysOnlineUerRep.AsQueryable().Filter("", true).FirstAsync(u => u.ConnectionId == Context.ConnectionId);
        if (user == null) return;

        await _sysOnlineUerRep.DeleteByIdAsync(user.Id);

        // 是否开启单用户登录
        if (await _sysConfigService.GetConfigValue<bool>(ConfigConst.SysSingleLogin))
        {
            _sysCacheService.HashDel<SysOnlineUser>(CacheConst.KeyUserOnline, "" + user.UserId);
            // _sysCacheService.Remove(CacheConst.KeyUserOnline + user.UserId);
        }
        else
        {
            _sysCacheService.HashDel<SysOnlineUser>(CacheConst.KeyUserOnline, user.UserId + Context.ConnectionId);
            // _sysCacheService.Remove(CacheConst.KeyUserOnline + user.UserId + Context.ConnectionId);
        }

        // 通知当前组用户变动
        var userList = await _sysOnlineUerRep.AsQueryable().Filter("", true)
            .Where(u => u.TenantId == user.TenantId).Take(10).ToListAsync();

        if (await _sysConfigService.GetConfigValue<bool>(ConfigConst.SysLoginOutReminder))
            await _onlineUserHubContext.Clients.Groups($"{GROUP_ONLINE}{user.TenantId}").OnlineUserList(new OnlineUserList
            {
                RealName = user.RealName,
                Online = false,
                UserList = userList
            });
    }

    /// <summary>调用 OnlineUserHub 的异步依赖完成 Force Offline 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task ForceOffline(OnlineUserHubInput input)
    {
        await _onlineUserHubContext.Clients.Client(input.ConnectionId).ForceOffline("强制下线");
    }

    /// <summary>
    /// 发送信息给某个人
    /// </summary>
    /// <param name="message">需要记录、展示或继续传递的错误上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task ClientsSendMessage(MessageInput message)
    {
        await _sysMessageService.SendUser(message);
    }

    /// <summary>
    /// 发送信息给所有人
    /// </summary>
    /// <param name="message">需要记录、展示或继续传递的错误上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task ClientsSendMessageToAll(MessageInput message)
    {
        await _sysMessageService.SendAllUser(message);
    }

    /// <summary>
    /// 发送消息给某些人（除了本人）
    /// </summary>
    /// <param name="message">需要记录、展示或继续传递的错误上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task ClientsSendMessageToOther(MessageInput message)
    {
        await _sysMessageService.SendOtherUser(message);
    }

    /// <summary>
    /// 发送消息给某些人
    /// </summary>
    /// <param name="message">需要记录、展示或继续传递的错误上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task ClientsSendMessageToUsers(MessageInput message)
    {
        await _sysMessageService.SendUsers(message);
    }
}