// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 语言服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 100, Description = "语言服务")]
public partial class SysLangService : IDynamicApiController, ITransient
{
    private readonly SqlSugarRepository<SysLang> _sysLangRep;

    public SysLangService(SqlSugarRepository<SysLang> sysLangRep)
    {
        _sysLangRep = sysLangRep;
    }

    /// <summary>
    /// 分页查询语言 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("分页查询语言")]
    [ApiDescriptionSettings(Name = "Page"), HttpPost]
    public async Task<SqlSugarPagedList<SysLangOutput>> Page(PageSysLangInput input)
    {
        input.Keyword = input.Keyword?.Trim();
        var query = _sysLangRep.AsQueryable()
            .WhereIF(!string.IsNullOrWhiteSpace(input.Keyword), u => u.Name.Contains(input.Keyword) || u.Code.Contains(input.Keyword) || u.IsoCode.Contains(input.Keyword) || u.UrlCode.Contains(input.Keyword))
            .WhereIF(!string.IsNullOrWhiteSpace(input.Name), u => u.Name.Contains(input.Name.Trim()))
            .WhereIF(!string.IsNullOrWhiteSpace(input.Code), u => u.Code.Contains(input.Code.Trim()))
            .WhereIF(!string.IsNullOrWhiteSpace(input.IsoCode), u => u.IsoCode.Contains(input.IsoCode.Trim()))
            .WhereIF(!string.IsNullOrWhiteSpace(input.UrlCode), u => u.UrlCode.Contains(input.UrlCode.Trim()))
            .Select<SysLangOutput>();
        return await query.OrderBuilder(input).ToPagedListAsync(input.Page, input.PageSize);
    }

    /// <summary>
    /// 获取语言详情 ℹ️
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("获取语言详情")]
    [ApiDescriptionSettings(Name = "Detail"), HttpGet]
    public async Task<SysLang> Detail([FromQuery] QueryByIdSysLangInput input)
    {
        return await _sysLangRep.GetFirstAsync(u => u.Id == input.Id);
    }

    /// <summary>
    /// 增加语言 ➕
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("增加语言")]
    [ApiDescriptionSettings(Name = "Add"), HttpPost]
    public async Task<long> Add(AddSysLangInput input)
    {
        var entity = input.Adapt<SysLang>();
        return await _sysLangRep.InsertAsync(entity) ? entity.Id : 0;
    }

    /// <summary>
    /// 更新语言 ✏️
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("更新语言")]
    [ApiDescriptionSettings(Name = "Update"), HttpPost]
    public async Task Update(UpdateSysLangInput input)
    {
        var entity = input.Adapt<SysLang>();
        await _sysLangRep.AsUpdateable(entity)
        .ExecuteCommandAsync();
    }

    /// <summary>
    /// 删除语言 ❌
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("删除语言")]
    [ApiDescriptionSettings(Name = "Delete"), HttpPost]
    public async Task Delete(DeleteSysLangInput input)
    {
        var entity = await _sysLangRep.GetFirstAsync(u => u.Id == input.Id) ?? throw Oops.Oh(ErrorCodeEnum.D1002);
        await _sysLangRep.DeleteAsync(entity);   //真删除
    }

    /// <summary>
    /// 获取下拉列表数据 🔖
    /// </summary>
    /// <returns></returns>
    [AllowAnonymous]
    [DisplayName("获取下拉列表数据")]
    [ApiDescriptionSettings(Name = "DropdownData"), HttpPost]
    public async Task<dynamic> DropdownData()
    {
        var data = await _sysLangRep.Context.Queryable<SysLang>()
            .Where(m => m.Active == true)
            .Select(u => new
            {
                Code = u.Code,
                Value = u.UrlCode,
                Label = $"{u.Name}"
            }).ToListAsync();
        return data;
    }
}