// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Novell.Directory.Ldap;

namespace Admin.NET.Core;

/// <summary>
/// 系统域登录配置服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 496, Description = "域登录配置")]
public class SysLdapService : IDynamicApiController, ITransient
{
    private readonly SqlSugarRepository<SysLdap> _sysLdapRep;

    public SysLdapService(SqlSugarRepository<SysLdap> sysLdapRep)
    {
        _sysLdapRep = sysLdapRep;
    }

    /// <summary>
    /// 获取系统域登录配置分页列表 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("获取系统域登录配置分页列表")]
    public async Task<SqlSugarPagedList<SysLdap>> Page(SysLdapInput input)
    {
        return await _sysLdapRep.AsQueryable()
            .WhereIF(!string.IsNullOrWhiteSpace(input.Keyword), u => u.Host.Contains(input.Keyword.Trim()))
            .WhereIF(!string.IsNullOrWhiteSpace(input.Host), u => u.Host.Contains(input.Host.Trim()))
            .OrderBy(u => u.CreateTime, OrderByType.Desc)
            .ToPagedListAsync(input.Page, input.PageSize);
    }

    /// <summary>
    /// 增加系统域登录配置 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [ApiDescriptionSettings(Name = "Add"), HttpPost]
    [DisplayName("增加系统域登录配置")]
    public async Task<long> Add(AddSysLdapInput input)
    {
        var entity = input.Adapt<SysLdap>();
        entity.BindPass = CryptogramUtil.Encrypt(input.BindPass);
        await _sysLdapRep.InsertAsync(entity);
        return entity.Id;
    }

    /// <summary>
    /// 更新系统域登录配置 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [ApiDescriptionSettings(Name = "Update"), HttpPost]
    [DisplayName("更新系统域登录配置")]
    public async Task Update(UpdateSysLdapInput input)
    {
        var entity = input.Adapt<SysLdap>();
        if (!string.IsNullOrEmpty(input.BindPass) && input.BindPass.Length < 32)
        {
            entity.BindPass = CryptogramUtil.Encrypt(input.BindPass); // 加密
        }

        await _sysLdapRep.AsUpdateable(entity).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommandAsync();
    }

    /// <summary>
    /// 删除系统域登录配置 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [ApiDescriptionSettings(Name = "Delete"), HttpPost]
    [DisplayName("删除系统域登录配置")]
    public async Task Delete(DeleteSysLdapInput input)
    {
        var entity = await _sysLdapRep.GetFirstAsync(u => u.Id == input.Id) ?? throw Oops.Oh(ErrorCodeEnum.D1002);
        await _sysLdapRep.FakeDeleteAsync(entity); // 假删除
        //await _rep.DeleteAsync(entity); // 真删除
    }

    /// <summary>
    /// 获取系统域登录配置详情 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("获取系统域登录配置详情")]
    public async Task<SysLdap> GetDetail([FromQuery] DetailSysLdapInput input)
    {
        return await _sysLdapRep.GetFirstAsync(u => u.Id == input.Id);
    }

    /// <summary>
    /// 获取系统域登录配置列表 🔖
    /// </summary>
    /// <returns></returns>
    [DisplayName("获取系统域登录配置列表")]
    public async Task<List<SysLdap>> GetList()
    {
        return await _sysLdapRep.AsQueryable().Select<SysLdap>().ToListAsync();
    }

    /// <summary>
    /// 验证账号
    /// </summary>
    /// <param name="account">域用户</param>
    /// <param name="password">密码</param>
    /// <param name="tenantId">租户</param>
    /// <returns></returns>
    [NonAction]
    public async Task<bool> AuthAccount(long? tenantId, string account, string password)
    {
        var sysLdap = await _sysLdapRep.GetFirstAsync(u => u.TenantId == tenantId) ?? throw Oops.Oh(ErrorCodeEnum.D1002);
        var ldapConn = new LdapConnection();
        try
        {
            await ldapConn.ConnectAsync(sysLdap.Host, sysLdap.Port);
            string bindPass = CryptogramUtil.Decrypt(sysLdap.BindPass);
            await ldapConn.BindAsync(sysLdap.Version, sysLdap.BindDn, bindPass);
            var ldapSearchResults = await ldapConn.SearchAsync(sysLdap.BaseDn, LdapConnection.ScopeSub, sysLdap.AuthFilter.Replace("%s", account), null, false);
            string dn = string.Empty;
            while (await ldapSearchResults.HasMoreAsync())
            {
                var ldapEntry = await ldapSearchResults.NextAsync();
                var sAmAccountName = ldapEntry.GetAttributeSet().GetAttribute(sysLdap.BindAttrAccount)?.StringValue;
                if (string.IsNullOrEmpty(sAmAccountName)) continue;
                dn = ldapEntry.Dn;
                break;
            }

            if (string.IsNullOrEmpty(dn)) throw Oops.Oh(ErrorCodeEnum.D1002);
            // var attr = new LdapAttribute("userPassword", password);
            await ldapConn.BindAsync(dn, password);
        }
        catch (LdapException e)
        {
            return e.ResultCode switch
            {
                LdapException.NoSuchObject or LdapException.NoSuchAttribute => throw Oops.Oh(ErrorCodeEnum.D0009),
                LdapException.InvalidCredentials => false,
                _ => throw Oops.Oh(e.Message),
            };
        }
        finally
        {
            ldapConn.Disconnect();
        }

        return true;
    }

    /// <summary>
    /// 同步域用户 🔖
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    [DisplayName("同步域用户")]
    [NonAction]
    public async Task<List<SysUserLdap>> SyncUserTenant(long tenantId)
    {
        var sysLdap = await _sysLdapRep.GetFirstAsync(c => c.TenantId == tenantId && c.IsDelete == false && c.Status == StatusEnum.Enable) ?? throw Oops.Oh(ErrorCodeEnum.D1002);
        return await SysLdapService.SyncUser(sysLdap);
    }

    /// <summary>
    /// 同步域用户 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("同步域用户")]
    public async Task<List<SysUserLdap>> SyncUser(SyncSysLdapInput input)
    {
        var sysLdap = await _sysLdapRep.GetByIdAsync(input.Id) ?? throw Oops.Oh(ErrorCodeEnum.D1002);
        return await SysLdapService.SyncUser(sysLdap);
    }

    /// <summary>
    /// 同步域用户 🔖
    /// </summary>
    /// <param name="sysLdap"></param>
    /// <returns></returns>
    private static async Task<List<SysUserLdap>> SyncUser(SysLdap sysLdap)
    {
        if (sysLdap == null) throw Oops.Oh(ErrorCodeEnum.D1002);
        var ldapConn = new LdapConnection();
        try
        {
            await ldapConn.ConnectAsync(sysLdap.Host, sysLdap.Port);
            string bindPass = CryptogramUtil.Decrypt(sysLdap.BindPass);
            await ldapConn.BindAsync(sysLdap.Version, sysLdap.BindDn, bindPass);
            var ldapSearchResults = await ldapConn.SearchAsync(sysLdap.BaseDn, LdapConnection.ScopeOne, "(objectClass=*)", null, false);
            var userLdapList = new List<SysUserLdap>();
            while (await ldapSearchResults.HasMoreAsync())
            {
                LdapEntry ldapEntry;
                try
                {
                    ldapEntry = await ldapSearchResults.NextAsync();
                    if (ldapEntry == null) continue;
                }
                catch (LdapException)
                {
                    continue;
                }

                var attrs = ldapEntry.GetAttributeSet();
                var deptCode = GetDepartmentCode(attrs, sysLdap.BindAttrCode);
                if (attrs.Count == 0 || attrs.ContainsKey("OU"))
                {
                    await SearchDnLdapUser(ldapConn, sysLdap, userLdapList, ldapEntry.Dn, deptCode);
                }
                else
                {
                    var sysUserLdap = CreateSysUserLdap(attrs, sysLdap.BindAttrAccount, sysLdap.BindAttrEmployeeId, deptCode);
                    sysUserLdap.Dn = ldapEntry.Dn;
                    sysUserLdap.TenantId = sysLdap.TenantId;
                    userLdapList.Add(sysUserLdap);
                }
            }

            if (userLdapList.Count == 0) return null;

            await App.GetRequiredService<SysUserLdapService>().InsertUserLdapList(sysLdap.TenantId!.Value, userLdapList);
            return userLdapList;
        }
        catch (LdapException e)
        {
            throw e.ResultCode switch
            {
                LdapException.NoSuchObject or LdapException.NoSuchAttribute => Oops.Oh(ErrorCodeEnum.D0009),
                _ => Oops.Oh(e.Message),
            };
        }
        finally
        {
            ldapConn.Disconnect();
        }
    }

    /// <summary>
    /// 获取部门代码
    /// </summary>
    /// <param name="attrs"></param>
    /// <param name="bindAttrCode"></param>
    /// <returns></returns>
    private static string GetDepartmentCode(LdapAttributeSet attrs, string bindAttrCode)
    {
        return bindAttrCode == "objectGUID"
            ? new Guid(attrs.GetAttribute(bindAttrCode)?.ByteValue!).ToString()
            : attrs.GetAttribute(bindAttrCode)?.StringValue ?? "0";
    }

    /// <summary>
    /// 创建同步对象
    /// </summary>
    /// <param name="attrs"></param>
    /// <param name="bindAttrAccount"></param>
    /// <param name="bindAttrEmployeeId"></param>
    /// <param name="deptCode"></param>
    /// <returns></returns>
    private static SysUserLdap CreateSysUserLdap(LdapAttributeSet attrs, string bindAttrAccount, string bindAttrEmployeeId, string deptCode)
    {
        var userLdap = new SysUserLdap
        {
            Account = attrs.ContainsKey(bindAttrAccount) ? attrs.GetAttribute(bindAttrAccount)?.StringValue : null,
            EmployeeId = attrs.ContainsKey(bindAttrEmployeeId) ? attrs.GetAttribute(bindAttrEmployeeId)?.StringValue : null,
            DeptCode = deptCode,
            UserName = attrs.ContainsKey("name") ? attrs.GetAttribute("name")?.StringValue : null,
            Mail = attrs.ContainsKey("mail") ? attrs.GetAttribute("mail")?.StringValue : null
        };
        var pwdLastSet = attrs.ContainsKey("pwdLastSet") ? attrs.GetAttribute("pwdLastSet")?.StringValue : null;
        if (pwdLastSet != null && !pwdLastSet.Equals("0")) userLdap.PwdLastSetTime = DateTime.FromFileTime(Convert.ToInt64(pwdLastSet));
        var userAccountControl = attrs.ContainsKey("userAccountControl") ? attrs.GetAttribute("userAccountControl")?.StringValue : null;
        if ((Convert.ToInt32(userAccountControl) & 0x2) == 0x2) // 检查账户是否已过期（通过检查userAccountControl属性的特定位）
            userLdap.AccountExpiresFlag = true;
        if ((Convert.ToInt32(userAccountControl) & 0x10000) == 0x10000) // 检查账户密码设置是否永不过期
            userLdap.DontExpiresFlag = true;
        return userLdap;
    }

    /// <summary>
    /// 遍历查询域用户
    /// </summary>
    /// <param name="ldapConn"></param>
    /// <param name="sysLdap"></param>
    /// <param name="userLdapList"></param>
    /// <param name="baseDn"></param>
    /// <param name="deptCode"></param>
    private static async Task SearchDnLdapUser(LdapConnection ldapConn, SysLdap sysLdap, List<SysUserLdap> userLdapList, string baseDn, string deptCode)
    {
        var ldapSearchResults = await ldapConn.SearchAsync(baseDn, LdapConnection.ScopeOne, "(objectClass=*)", null, false);
        while (await ldapSearchResults.HasMoreAsync())
        {
            LdapEntry ldapEntry;
            try
            {
                ldapEntry = await ldapSearchResults.NextAsync();
                if (ldapEntry == null) continue;
            }
            catch (LdapException)
            {
                continue;
            }

            var attrs = ldapEntry.GetAttributeSet();
            deptCode = GetDepartmentCode(attrs, sysLdap.BindAttrCode);

            if (attrs.Count == 0 || attrs.ContainsKey("OU"))
                await SearchDnLdapUser(ldapConn, sysLdap, userLdapList, ldapEntry.Dn, deptCode);
            else
            {
                var sysUserLdap = CreateSysUserLdap(attrs, sysLdap.BindAttrAccount, sysLdap.BindAttrEmployeeId, deptCode);
                sysUserLdap.Dn = ldapEntry.Dn;
                sysUserLdap.TenantId = sysLdap.TenantId;
                if (string.IsNullOrEmpty(sysUserLdap.EmployeeId)) continue;
                userLdapList.Add(sysUserLdap);
            }
        }
    }

    /// <summary>
    /// 同步域组织 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("同步域组织")]
    public async Task SyncDept(SyncSysLdapInput input)
    {
        var sysLdap = await _sysLdapRep.GetFirstAsync(u => u.Id == input.Id) ?? throw Oops.Oh(ErrorCodeEnum.D1002);
        var ldapConn = new LdapConnection();
        try
        {
            await ldapConn.ConnectAsync(sysLdap.Host, sysLdap.Port);
            string bindPass = CryptogramUtil.Decrypt(sysLdap.BindPass);
            await ldapConn.BindAsync(sysLdap.Version, sysLdap.BindDn, bindPass);
            var ldapSearchResults = await ldapConn.SearchAsync(sysLdap.BaseDn, LdapConnection.ScopeOne, "(objectClass=*)", null, false);
            var orgList = new List<SysOrg>();
            while (await ldapSearchResults.HasMoreAsync())
            {
                LdapEntry ldapEntry;
                try
                {
                    ldapEntry = await ldapSearchResults.NextAsync();
                    if (ldapEntry == null) continue;
                }
                catch (LdapException)
                {
                    continue;
                }

                var attrs = ldapEntry.GetAttributeSet();
                if (attrs.Count != 0 && !attrs.ContainsKey("OU")) continue;

                var sysOrg = CreateSysOrg(attrs, sysLdap, orgList, new SysOrg { Id = 0, Level = 0 });
                orgList.Add(sysOrg);

                await SearchDnLdapDept(ldapConn, sysLdap, orgList, ldapEntry.Dn, sysOrg);
            }

            if (orgList.Count == 0)
                return;

            await App.GetRequiredService<SysOrgService>().BatchAddOrgs(orgList);
        }
        catch (LdapException e)
        {
            throw e.ResultCode switch
            {
                LdapException.NoSuchObject or LdapException.NoSuchAttribute => Oops.Oh(ErrorCodeEnum.D0009),
                _ => Oops.Oh(e.Message),
            };
        }
        finally
        {
            ldapConn.Disconnect();
        }
    }

    /// <summary>
    /// 遍历查询域用户
    /// </summary>
    /// <param name="ldapConn"></param>
    /// <param name="sysLdap"></param>
    /// <param name="listOrgs"></param>
    /// <param name="baseDn"></param>
    /// <param name="org"></param>
    private static async Task SearchDnLdapDept(LdapConnection ldapConn, SysLdap sysLdap, List<SysOrg> listOrgs, string baseDn, SysOrg org)
    {
        var ldapSearchResults = await ldapConn.SearchAsync(baseDn, LdapConnection.ScopeOne, "(objectClass=*)", null, false);
        while (await ldapSearchResults.HasMoreAsync())
        {
            LdapEntry ldapEntry;
            try
            {
                ldapEntry = await ldapSearchResults.NextAsync();
                if (ldapEntry == null) continue;
            }
            catch (LdapException)
            {
                continue;
            }

            var attrs = ldapEntry.GetAttributeSet();
            if (attrs.Count != 0 && !attrs.ContainsKey("OU")) continue;

            var sysOrg = CreateSysOrg(attrs, sysLdap, listOrgs, org);
            listOrgs.Add(sysOrg);

            await SearchDnLdapDept(ldapConn, sysLdap, listOrgs, ldapEntry.Dn, sysOrg);
        }
    }

    /// <summary>
    /// 创建架构对象
    /// </summary>
    /// <param name="attrs"></param>
    /// <param name="sysLdap"></param>
    /// <param name="listOrgs"></param>
    /// <param name="org"></param>
    /// <returns></returns>
    private static SysOrg CreateSysOrg(LdapAttributeSet attrs, SysLdap sysLdap, List<SysOrg> listOrgs, SysOrg org)
    {
        return new SysOrg
        {
            Pid = org.Id,
            Id = YitIdHelper.NextId(),
            Code = attrs.ContainsKey(sysLdap.BindAttrCode) ? new Guid(attrs.GetAttribute(sysLdap.BindAttrCode)?.ByteValue).ToString() : null,
            Level = org.Level + 1,
            Name = attrs.ContainsKey(sysLdap.BindAttrAccount) ? attrs.GetAttribute(sysLdap.BindAttrAccount)?.StringValue : null,
            OrderNo = listOrgs.Count + 1,
        };
    }
}