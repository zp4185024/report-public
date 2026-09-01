// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Furion.SpecificationDocument;
using Lazy.Captcha.Core;
using NewLife.Reflection;

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统登录授权服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 500)]
public class SysAuthService : IDynamicApiController, ITransient
{
    private readonly UserManager _userManager;
    private readonly SqlSugarRepository<SysUser> _sysUserRep;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SysMenuService _sysMenuService;
    private readonly SysOnlineUserService _sysOnlineUserService;
    private readonly SysConfigService _sysConfigService;
    private readonly SysUserService _sysUserService;
    private readonly SysTenantService _sysTenantService;
    private readonly SysSmsService _sysSmsService;
    private readonly SysLdapService _sysLdapService;
    private readonly ICaptcha _captcha;
    private readonly IEventPublisher _eventPublisher;
    private readonly SysCacheService _sysCacheService;

    /// <summary>初始化 SysAuthService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="sysUserRep">当前方法执行时使用的 sysUserRep 输入值。</param>
    /// <param name="httpContextAccessor">当前方法执行时使用的 httpContextAccessor 输入值。</param>
    /// <param name="sysOnlineUserService">当前方法执行时使用的 sysOnlineUserService 输入值。</param>
    /// <param name="sysConfigService">当前方法执行时使用的 sysConfigService 输入值。</param>
    /// <param name="sysLdapService">当前方法执行时使用的 sysLdapService 输入值。</param>
    /// <param name="eventPublisher">当前方法执行时使用的 eventPublisher 输入值。</param>
    /// <param name="sysSmsService">当前方法执行时使用的 sysSmsService 输入值。</param>
    /// <param name="sysCacheService">当前方法执行时使用的 sysCacheService 输入值。</param>
    /// <param name="sysMenuService">当前方法执行时使用的 sysMenuService 输入值。</param>
    /// <param name="sysUserService">当前方法执行时使用的 sysUserService 输入值。</param>
    /// <param name="sysTenantService">当前方法执行时使用的 sysTenantService 输入值。</param>
    /// <param name="userManager">当前方法执行时使用的 userManager 输入值。</param>
    /// <param name="captcha">当前方法执行时使用的 captcha 输入值。</param>
    public SysAuthService(
        SqlSugarRepository<SysUser> sysUserRep,
        IHttpContextAccessor httpContextAccessor,
        SysOnlineUserService sysOnlineUserService,
        SysConfigService sysConfigService,
        SysLdapService sysLdapService,
        IEventPublisher eventPublisher,
        SysSmsService sysSmsService,
        SysCacheService sysCacheService,
        SysMenuService sysMenuService,
        SysUserService sysUserService,
        SysTenantService sysTenantService,
        UserManager userManager,
        ICaptcha captcha)
    {
        _captcha = captcha;
        _sysUserRep = sysUserRep;
        _userManager = userManager;
        _sysSmsService = sysSmsService;
        _eventPublisher = eventPublisher;
        _sysUserService = sysUserService;
        _sysTenantService = sysTenantService;
        _sysMenuService = sysMenuService;
        _sysCacheService = sysCacheService;
        _sysConfigService = sysConfigService;
        _httpContextAccessor = httpContextAccessor;
        _sysOnlineUserService = sysOnlineUserService;
        _sysLdapService = sysLdapService;
    }

    /// <summary>调用 SysAuthService 的异步依赖完成 Login 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <remarks>用户名/密码：superadmin/123456</remarks>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [DisplayName("账号密码登录")]
    public virtual async Task<LoginOutput> Login([Required] LoginInput input)
    {
        // 判断密码错误次数（缓存30分钟）
        var keyPasswordErrorTimes = $"{CacheConst.KeyPasswordErrorTimes}{input.Account}";
        var passwordErrorTimes = _sysCacheService.Get<int>(keyPasswordErrorTimes);
        var passwordMaxErrorTimes = await _sysConfigService.GetConfigValue<int>(ConfigConst.SysPasswordMaxErrorTimes);
        // 若未配置或误配置为0、负数, 则默认密码错误次数最大为5次
        if (passwordMaxErrorTimes < 1) passwordMaxErrorTimes = 5;
        if (passwordErrorTimes > passwordMaxErrorTimes) throw Oops.Oh(ErrorCodeEnum.D1027);

        // 判断是否开启验证码，其校验验证码
        if (await _sysConfigService.GetConfigValue<bool>(ConfigConst.SysCaptcha) && !_captcha.Validate(input.CodeId.ToString(), input.Code)) throw Oops.Oh(ErrorCodeEnum.D0008);

        // 获取登录租户和用户
        var (tenant, user) = await GetLoginUserAndTenant(input.TenantId, account: input.Account);

        // 账号是否被冻结
        if (user.Status == StatusEnum.Disable) throw Oops.Oh(ErrorCodeEnum.D1017);

        // 是否开启域登录验证
        if (await _sysConfigService.GetConfigValue<bool>(ConfigConst.SysDomainLogin))
        {
            var userLdap = await _sysUserRep.ChangeRepository<SqlSugarRepository<SysUserLdap>>().GetFirstAsync(u => u.UserId == user.Id && u.TenantId == tenant.Id);
            if (userLdap == null)
            {
                VerifyPassword(input.Password, keyPasswordErrorTimes, passwordErrorTimes, user);
            }
            else if (!await App.GetRequiredService<SysLdapService>().AuthAccount(tenant.Id, userLdap.Account, CryptogramUtil.Decrypt(input.Password)))
            {
                _sysCacheService.Set(keyPasswordErrorTimes, ++passwordErrorTimes, TimeSpan.FromMinutes(30));
                throw Oops.Oh(ErrorCodeEnum.D1000);
            }
        }
        else
            VerifyPassword(input.Password, keyPasswordErrorTimes, passwordErrorTimes, user);

        // 登录成功则清空密码错误次数
        _sysCacheService.Remove(keyPasswordErrorTimes);

        return await CreateToken(user);
    }

    /// <summary>
    /// 获取登录租户和用户
    /// </summary>
    /// <param name="tenantId">需要查询、更新或删除的业务记录标识。</param>
    /// <param name="account">控制数据范围、数量或工作簿坐标的数值。</param>
    /// <param name="phone">当前方法执行时使用的 phone 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [NonAction]
    public async Task<(SysTenant tenant, SysUser user)> GetLoginUserAndTenant(long? tenantId, string account = null, string phone = null)
    {
        // 账号是否存在
        var user = await _sysUserRep.AsQueryable().Includes(u => u.SysOrg).ClearFilter()
            .WhereIF(tenantId > 0, u => (u.AccountType == AccountTypeEnum.SuperAdmin || u.TenantId == tenantId))
            .WhereIF(!string.IsNullOrWhiteSpace(account), u => u.Account.Equals(account))
            .WhereIF(!string.IsNullOrWhiteSpace(phone), u => u.Phone.Equals(phone)).FirstAsync();
        _ = user ?? throw Oops.Oh(ErrorCodeEnum.D1000);

        // 租户是否存在或已禁用
        var tenant = await _sysUserRep.ChangeRepository<SqlSugarRepository<SysTenant>>().AsQueryable()
            .WhereIF(tenantId > 0, u => u.Id == tenantId).WhereIF(tenantId.ToLong() == 0, u => u.Id == user.TenantId).FirstAsync();
        if (tenant?.Status != StatusEnum.Enable) throw Oops.Oh(ErrorCodeEnum.Z1003);

        // 如果是超级管理员，则引用登录选择的租户进入系统
        if (tenantId > 0 && user.AccountType == AccountTypeEnum.SuperAdmin)
            user.TenantId = tenantId;

        return (tenant, user);
    }

    /// <summary>校验 SysAuthService 的 Verify Password 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="password">当前方法执行时使用的 password 输入值。</param>
    /// <param name="keyPasswordErrorTimes">当前方法执行时使用的 keyPasswordErrorTimes 输入值。</param>
    /// <param name="passwordErrorTimes">当前方法执行时使用的 passwordErrorTimes 输入值。</param>
    /// <param name="user">当前方法执行时使用的 user 输入值。</param>
    private void VerifyPassword(string password, string keyPasswordErrorTimes, int passwordErrorTimes, SysUser user)
    {
        try
        {
            // 国密SM2解密（前端密码传输SM2加密后的）
            password = CryptogramUtil.SM2Decrypt(password);
            if (CryptogramUtil.CryptoType == CryptogramEnum.MD5.ToString())
            {
                if (user.Password.Equals(MD5Encryption.Encrypt(password))) return;
            }
            else
            {
                if (CryptogramUtil.Decrypt(user.Password).Equals(password)) return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("用户密码验证异常：", ex);
        }

        _sysCacheService.Set(keyPasswordErrorTimes, ++passwordErrorTimes, TimeSpan.FromMinutes(30));
        throw Oops.Oh(ErrorCodeEnum.D1000);
    }

    /// <summary>调用 SysAuthService 的异步依赖完成 Un Lock Screen 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="password">当前方法执行时使用的 password 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("验证锁屏密码")]
    public virtual async Task<bool> UnLockScreen([Required, FromQuery] string password)
    {
        // 账号是否存在
        var user = await _sysUserRep.GetFirstAsync(u => u.Id == _userManager.UserId);
        _ = user ?? throw Oops.Oh(ErrorCodeEnum.D0009);

        var keyPasswordErrorTimes = $"{CacheConst.KeyPasswordErrorTimes}{user.Account}";
        var passwordErrorTimes = _sysCacheService.Get<int>(keyPasswordErrorTimes);

        // 是否开启域登录验证
        if (await _sysConfigService.GetConfigValue<bool>(ConfigConst.SysDomainLogin))
        {
            var userLdap = await _sysUserRep.ChangeRepository<SqlSugarRepository<SysUserLdap>>().GetFirstAsync(u => u.UserId == user.Id && u.TenantId == user.TenantId);
            if (userLdap == null)
            {
                VerifyPassword(password, keyPasswordErrorTimes, passwordErrorTimes, user);
            }
            else if (!await _sysLdapService.AuthAccount(user.TenantId!.Value, userLdap.Account, CryptogramUtil.Decrypt(password)))
            {
                _sysCacheService.Set(keyPasswordErrorTimes, ++passwordErrorTimes, TimeSpan.FromMinutes(30));
                throw Oops.Oh(ErrorCodeEnum.D1000);
            }
        }
        else
            VerifyPassword(password, keyPasswordErrorTimes, passwordErrorTimes, user);

        return true;
    }

    /// <summary>调用 SysAuthService 的异步依赖完成 Login Phone 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [DisplayName("手机号登录")]
    public virtual async Task<LoginOutput> LoginPhone([Required] LoginPhoneInput input)
    {
        // 校验短信验证码
        _sysSmsService.VerifyCode(new SmsVerifyCodeInput { Phone = input.Phone, Code = input.Code });

        // 获取登录租户和用户
        var (_, user) = await GetLoginUserAndTenant(input.TenantId, phone: input.Phone);

        return await CreateToken(user);
    }

    /// <summary>
    /// 生成Token令牌 🔖
    /// </summary>
    /// <param name="user">需要同步缓存和在线状态的用户实体。</param>
    /// <param name="sysUserEventTypeEnum">说明新增、更新、删除或解锁等用户变更类型。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [NonAction]
    internal virtual async Task<LoginOutput> CreateToken(SysUser user, SysUserEventTypeEnum sysUserEventTypeEnum = SysUserEventTypeEnum.Login)
    {
        if (sysUserEventTypeEnum != SysUserEventTypeEnum.RefreshToken)
        {
            // 单用户登录
            await _sysOnlineUserService.SingleLogin(user.Id);
        }

        // 生成Token令牌
        var tokenExpire = await _sysConfigService.GetTokenExpire();
        var accessToken = JWTEncryption.Encrypt(new Dictionary<string, object>
        {
            { ClaimConst.UserId, user.Id },
            { ClaimConst.TenantId, user.TenantId },
            { ClaimConst.Account, user.Account },
            { ClaimConst.RealName, user.RealName },
            { ClaimConst.AccountType, user.AccountType },
            { ClaimConst.OrgId, user.OrgId },
            { ClaimConst.OrgName, user.SysOrg?.Name },
            { ClaimConst.OrgType, user.SysOrg?.Type },
            { ClaimConst.LangCode, user.LangCode }
        }, tokenExpire);

        // 生成刷新Token令牌
        var refreshTokenExpire = await _sysConfigService.GetRefreshTokenExpire();
        var refreshToken = JWTEncryption.GenerateRefreshToken(accessToken, refreshTokenExpire);

        // 设置响应报文头
        _httpContextAccessor.HttpContext.SetTokensOfResponseHeaders(accessToken, refreshToken);

        // Swagger Knife4UI-AfterScript登录脚本
        // ke.global.setAllHeader('Authorization', 'Bearer ' + ke.response.headers['access-token']);

        // 更新用户登录信息
        user.LastLoginIp = _httpContextAccessor.HttpContext.GetRemoteIpAddressToIPv4(true);
        (user.LastLoginAddress, double? longitude, double? latitude) = CommonUtil.GetIpAddress(user.LastLoginIp);
        user.LastLoginTime = DateTime.Now;
        user.LastLoginDevice = CommonUtil.GetClientDeviceInfo(_httpContextAccessor.HttpContext?.Request?.Headers?.UserAgent);
        await _sysUserRep.AsUpdateable(user).UpdateColumns(u => new
        {
            u.LastLoginIp,
            u.LastLoginAddress,
            u.LastLoginTime,
            u.LastLoginDevice,
        }).ExecuteCommandAsync();

        var payload = new
        {
            Entity = user,
            Output = new LoginOutput
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                Homepage = user.Homepage
            }
        };

        // 发布系统用户操作事件
        await _eventPublisher.PublishAsync(sysUserEventTypeEnum, payload);
        return payload.Output;
    }

    /// <summary>查询或读取 SysAuthService 所需的 Get User Info 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取登录账号")]
    public virtual async Task<LoginUserOutput> GetUserInfo()
    {
        var user = await _sysUserRep.AsQueryable().ClearFilter().FirstAsync(u => u.Id == _userManager.UserId) ?? throw Oops.Oh(ErrorCodeEnum.D1011).StatusCode(401);
        // 获取机构
        var org = await _sysUserRep.ChangeRepository<SqlSugarRepository<SysOrg>>().GetFirstAsync(u => u.Id == user.OrgId);
        // 获取职位
        var pos = await _sysUserRep.ChangeRepository<SqlSugarRepository<SysPos>>().GetFirstAsync(u => u.Id == user.PosId);
        // 获取按钮集合
        var buttons = await _sysMenuService.GetOwnBtnPermList();
        // 获取角色集合
        var roleIds = await _sysUserRep.ChangeRepository<SqlSugarRepository<SysUserRole>>().AsQueryable()
            .Where(u => u.UserId == user.Id).Select(u => u.RoleId).ToListAsync();
        // 获取水印文字（若系统水印为空则全局为空）
        var watermarkText = (await _sysUserRep.Context.Queryable<SysTenant>().FirstAsync(u => u.Id == user.TenantId))?.Watermark;
        if (!string.IsNullOrWhiteSpace(watermarkText)) watermarkText += $"-{user.RealName}";
        var loginUser = new LoginUserOutput
        {
            Id = user.Id,
            Account = user.Account,
            RealName = user.RealName,
            Phone = user.Phone,
            IdCardNum = user.IdCardNum,
            Email = user.Email,
            AccountType = user.AccountType,
            Avatar = user.Avatar,
            Address = user.Address,
            Signature = user.Signature,
            OrgId = user.OrgId,
            OrgName = org?.Name,
            OrgType = org?.Type,
            PosName = pos?.Name,
            Buttons = buttons,
            RoleIds = roleIds,
            TenantId = user.TenantId,
            WatermarkText = watermarkText,
            LangCode = user.LangCode,
        };

        //将登录信息中的当前租户id，更新为当前所切换到的租户
        long? currentTenantId = App.User.FindFirst(ClaimConst.TenantId)?.Value?.ToLong(0);
        loginUser.CurrentTenantId = currentTenantId > 0 ? currentTenantId : user.TenantId;

        return loginUser;
    }

    /// <summary>
    /// 获取刷新Token 🔖
    /// </summary>
    /// <param name="accessToken">旧的AccessToken</param>
    /// <returns>新的AccessToken和RefreshToken</returns>
    [DisplayName("获取刷新Token")]
    public virtual async Task<LoginOutput> GetRefreshToken([FromQuery] string accessToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null) throw Oops.Oh(ErrorCodeEnum.D1016);

        if (string.IsNullOrWhiteSpace(accessToken)) throw Oops.Oh(ErrorCodeEnum.D1011);

        if (string.IsNullOrWhiteSpace(_userManager.Account)) throw Oops.Oh(ErrorCodeEnum.D1011);

        // 黑名单校验
        if (_sysCacheService.ExistKey($"blacklist:token:{accessToken}")) throw Oops.Oh(ErrorCodeEnum.D1011);

        // 解析Token
        var (isValid, tokenData, validationResult) = JWTEncryption.Validate(accessToken);
        if (!isValid) throw Oops.Oh(ErrorCodeEnum.D1016);

        // 获取用户Id
        var user = await _sysUserRep.AsQueryable().ClearFilter().FirstAsync(u => u.Id == _userManager.UserId) ?? throw Oops.Oh(ErrorCodeEnum.D1011).StatusCode(401);
        return await CreateToken(user, SysUserEventTypeEnum.RefreshToken);
    }

    /// <summary>调用 SysAuthService 的异步依赖完成 Logout 操作，并按当前返回类型传递处理结果。</summary>
    [DisplayName("退出系统")]
    public async Task Logout()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null) throw Oops.Oh(ErrorCodeEnum.D1016);

        var token = httpContext.Request.Headers["Authorization"].ToString().Replace("Bearer ", "");

        if (string.IsNullOrWhiteSpace(token))
            throw Oops.Oh(ErrorCodeEnum.D1011);

        if (string.IsNullOrWhiteSpace(_userManager.Account))
            throw Oops.Oh(ErrorCodeEnum.D1011);

        // 写入黑名单（设置过期时间，避免Redis膨胀）
        var tokenExpire = await _sysConfigService.GetTokenExpire();
        _sysCacheService.Set($"blacklist:token:{token}", "1", TimeSpan.FromMinutes(tokenExpire));

        // 发布登出事件（用户退出）
        var user = await _sysUserRep.GetByIdAsync(_userManager.UserId);
        await _eventPublisher.PublishAsync(SysUserEventTypeEnum.LoginOut, new { Entity = user });

        // 清除 Swagger 登录信息
        httpContext.SignoutToSwagger();
    }

    /// <summary>查询或读取 SysAuthService 所需的 Get Captcha 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [SuppressMonitor]
    [DisplayName("获取验证码")]
    public dynamic GetCaptcha()
    {
        var codeId = YitIdHelper.NextId().ToString();
        var captcha = _captcha.Generate(codeId);
        var expirySeconds = App.GetOptions<CaptchaOptions>()?.ExpirySeconds ?? 60;
        return new { Id = codeId, Img = captcha.Base64, ExpirySeconds = expirySeconds };
    }

    /// <summary>调用 SysAuthService 的异步依赖完成 User Registration 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [UnitOfWork]
    [AllowAnonymous]
    [HttpPost, ApiDescriptionSettings(Description = "用户注册", DisableInherite = true)]
    public async Task UserRegistration(UserRegistrationInput input)
    {
        // 校验验证码
        if (!_captcha.Validate(input.CodeId.ToString(), input.Code)) throw Oops.Oh(ErrorCodeEnum.D0008);
        _captcha.Generate(input.CodeId.ToString());

        // 登录时隐藏租户，查找对应租户信息
        input.TenantId = input.TenantId <= 0 ? (await _sysTenantService.GetCurrentTenantSysInfo()).Id : input.TenantId;

        // 判断租户是否有效且启用注册功能
        var tenant = await _sysUserRep.Context.Queryable<SysTenant>().FirstAsync(u => u.Id == input.TenantId && u.Status == StatusEnum.Enable);
        if (tenant?.EnableReg != YesNoEnum.Y) throw Oops.Oh(ErrorCodeEnum.D1034);

        // 查找注册方案
        var wayId = input.WayId <= 0 ? tenant.RegWayId : input.WayId;
        var regWay = await _sysUserRep.Context.Queryable<SysUserRegWay>().FirstAsync(u => u.Id == wayId) ?? throw Oops.Oh(ErrorCodeEnum.D1035);

        var addUserInput = new AddUserInput
        {
            AccountType = regWay.AccountType,
            NickName = "注册用户-" + input.Account,
            OrgId = regWay.OrgId,
            PosId = regWay.PosId,
            TenantId = input.TenantId,
            RoleIdList = new List<long> { regWay.RoleId },
        };
        addUserInput.Copy(input);
        await _sysUserService.RegisterUser(addUserInput);
    }

    /// <summary>
    /// Swagger登录检查 🔖
    /// </summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [HttpPost("/api/swagger/checkUrl"), NonUnify]
    [ApiDescriptionSettings(Description = "Swagger登录检查", DisableInherite = true)]
    public int SwaggerCheckUrl()
    {
        return _httpContextAccessor.HttpContext.User.Identity.IsAuthenticated ? 200 : 401;
    }

    /// <summary>
    /// Swagger登录提交 🔖
    /// </summary>
    /// <param name="auth">当前方法执行时使用的 auth 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [HttpPost("/api/swagger/submitUrl"), NonUnify]
    [ApiDescriptionSettings(Description = "Swagger登录提交", DisableInherite = true)]
    public async Task<int> SwaggerSubmitUrl([FromForm] SpecificationAuth auth)
    {
        try
        {
            _sysCacheService.Set($"{CacheConst.KeyConfig}{ConfigConst.SysCaptcha}", false);

            await Login(new LoginInput
            {
                Account = auth.UserName,
                Password = CryptogramUtil.SM2Encrypt(auth.Password)
            });

            _sysCacheService.Remove($"{CacheConst.KeyConfig}{ConfigConst.SysCaptcha}");

            return 200;
        }
        catch (Exception)
        {
            return 401;
        }
    }
}
