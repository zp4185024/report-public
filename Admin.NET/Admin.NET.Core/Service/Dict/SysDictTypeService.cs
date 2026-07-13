// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统字典类型服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 430, Description = "系统字典类型")]
public class SysDictTypeService : IDynamicApiController, ITransient
{
    private readonly SqlSugarRepository<SysDictType> _sysDictTypeRep;
    private readonly SysDictDataService _sysDictDataService;
    private readonly SysCacheService _sysCacheService;
    private readonly UserManager _userManager;
    private readonly SysLangTextCacheService _sysLangTextCacheService;

    public SysDictTypeService(SqlSugarRepository<SysDictType> sysDictTypeRep,
        SysDictDataService sysDictDataService,
        SysCacheService sysCacheService,
        UserManager userManager,
        SysLangTextCacheService sysLangTextCacheService)
    {
        _sysDictTypeRep = sysDictTypeRep;
        _sysDictDataService = sysDictDataService;
        _sysCacheService = sysCacheService;
        _userManager = userManager;
        _sysLangTextCacheService = sysLangTextCacheService;
    }

    /// <summary>
    /// 获取字典类型分页列表 🔖
    /// </summary>
    /// <returns></returns>
    [DisplayName("获取字典类型分页列表")]
    public async Task<SqlSugarPagedList<SysDictType>> Page(PageDictTypeInput input)
    {
        var langCode = _userManager.LangCode;
        var baseQuery = _sysDictTypeRep.AsQueryable()
            .WhereIF(!_userManager.SuperAdmin, u => u.IsTenant == YesNoEnum.Y)
            .WhereIF(!string.IsNullOrEmpty(input.Code?.Trim()), u => u.Code.Contains(input.Code))
            .WhereIF(!string.IsNullOrEmpty(input.Name?.Trim()), u => u.Name.Contains(input.Name));
        //.OrderBy(u => new { u.OrderNo, u.Code })
        var pageList = await baseQuery.ToPagedListAsync(input.Page, input.PageSize);
        var list = pageList.Items;
        var ids = list.Select(d => d.Id).Distinct().ToList();
        var translations = await _sysLangTextCacheService.GetTranslations(
                               "SysDictType",
                               "Name",
                               ids,
                               langCode);
        foreach (var item in list)
        {
            if (translations.TryGetValue(item.Id, out var translatedName) && !string.IsNullOrEmpty(translatedName))
            {
                item.Name = translatedName;
            }
        }
        pageList.Items = list;
        return pageList;
    }

    /// <summary>
    /// 获取字典类型列表 🔖
    /// </summary>
    /// <returns></returns>
    [DisplayName("获取字典类型列表")]
    public async Task<List<SysDictType>> GetList()
    {
        var langCode = _userManager.LangCode;
        var list = await _sysDictTypeRep.AsQueryable().OrderBy(u => new { u.OrderNo, u.Code }).ToListAsync();
        var ids = list.Select(d => d.Id).Distinct().ToList();
        var translations = await _sysLangTextCacheService.GetTranslations(
                               "SysDictType",
                               "Name",
                               ids,
                               langCode);
        foreach (var item in list)
        {
            if (translations.TryGetValue(item.Id, out var translatedName) && !string.IsNullOrEmpty(translatedName))
            {
                item.Name = translatedName;
            }
        }
        return list;
    }

    /// <summary>
    /// 获取字典类型-值列表 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("获取字典类型-值列表")]
    public async Task<List<SysDictData>> GetDataList([FromQuery] GetDataDictTypeInput input)
    {
        var dictType = await _sysDictTypeRep.GetFirstAsync(u => u.Code == input.Code) ?? throw Oops.Oh(ErrorCodeEnum.D3000);
        var langCode = _userManager.LangCode;
        var list = await _sysDictDataService.GetDictDataListByDictTypeId(dictType.Id);
        var ids = list.Select(d => d.Id).Distinct().ToList();
        var translations = await _sysLangTextCacheService.GetTranslations(
                               "SysDictType",
                               "Name",
                               ids,
                               langCode);
        foreach (var item in list)
        {
            if (translations.TryGetValue(item.Id, out var translatedName) && !string.IsNullOrEmpty(translatedName))
            {
                item.Name = translatedName;
            }
        }
        return list;
    }

    /// <summary>
    /// 添加字典类型 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [ApiDescriptionSettings(Name = "Add"), HttpPost]
    [DisplayName("添加字典类型")]
    public async Task AddDictType(AddDictTypeInput input)
    {
        if (input.Code.ToLower().EndsWith("enum")) throw Oops.Oh(ErrorCodeEnum.D3006);
        if (input.SysFlag == YesNoEnum.Y && !_userManager.SuperAdmin) throw Oops.Oh(ErrorCodeEnum.D3008);

        var isExist = await _sysDictTypeRep.IsAnyAsync(u => u.Code == input.Code);
        if (isExist) throw Oops.Oh(ErrorCodeEnum.D3001);

        if (_userManager.SuperAdmin) input.IsTenant = YesNoEnum.N;  // 超级管理员添加的字典类型默认非租户级

        await _sysDictTypeRep.InsertAsync(input.Adapt<SysDictType>());
    }

    /// <summary>
    /// 更新字典类型 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [UnitOfWork]
    [ApiDescriptionSettings(Name = "Update"), HttpPost]
    [DisplayName("更新字典类型")]
    public async Task UpdateDictType(UpdateDictTypeInput input)
    {
        var dict = await _sysDictTypeRep.GetFirstAsync(x => x.Id == input.Id);
        if (dict.IsTenant != input.IsTenant) throw Oops.Oh(ErrorCodeEnum.D3012);
        if (dict == null) throw Oops.Oh(ErrorCodeEnum.D3000);

        if (dict.Code.ToLower().EndsWith("enum") && input.Code != dict.Code) throw Oops.Oh(ErrorCodeEnum.D3007);
        if (input.SysFlag == YesNoEnum.Y && !_userManager.SuperAdmin) throw Oops.Oh(ErrorCodeEnum.D3009);

        var isExist = await _sysDictTypeRep.IsAnyAsync(u => u.Code == input.Code && u.Id != input.Id);
        if (isExist) throw Oops.Oh(ErrorCodeEnum.D3001);

        _sysCacheService.Remove($"{CacheConst.KeyDict}{input.Code}");
        await _sysDictTypeRep.UpdateAsync(input.Adapt<SysDictType>());
    }

    /// <summary>
    /// 删除字典类型 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [UnitOfWork]
    [ApiDescriptionSettings(Name = "Delete"), HttpPost]
    [DisplayName("删除字典类型")]
    public async Task DeleteDictType(DeleteDictTypeInput input)
    {
        var dictType = await _sysDictTypeRep.GetByIdAsync(input.Id) ?? throw Oops.Oh(ErrorCodeEnum.D3000);
        if (dictType.SysFlag == YesNoEnum.Y && !_userManager.SuperAdmin) throw Oops.Oh(ErrorCodeEnum.D3010);

        // 删除字典值
        await _sysDictTypeRep.DeleteAsync(dictType);
        await _sysDictDataService.DeleteDictData(input.Id);
    }

    /// <summary>
    /// 获取字典类型详情 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("获取字典类型详情")]
    public async Task<SysDictType> GetDetail([FromQuery] DictTypeInput input)
    {
        return await _sysDictTypeRep.GetByIdAsync(input.Id);
    }

    /// <summary>
    /// 修改字典类型状态 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [UnitOfWork]
    [DisplayName("修改字典类型状态")]
    public async Task SetStatus(DictTypeInput input)
    {
        var dictType = await _sysDictTypeRep.GetByIdAsync(input.Id) ?? throw Oops.Oh(ErrorCodeEnum.D3000);
        if (dictType.SysFlag == YesNoEnum.Y && !_userManager.SuperAdmin) throw Oops.Oh(ErrorCodeEnum.D3009);

        _sysCacheService.Remove($"{CacheConst.KeyDict}{dictType.Code}");

        dictType.Status = input.Status;
        await _sysDictTypeRep.AsUpdateable(dictType).UpdateColumns(u => new { u.Status }, true).ExecuteCommandAsync();
    }

    /// <summary>
    /// 获取所有字典集合 🔖
    /// </summary>
    /// <returns></returns>
    [DisplayName("获取所有字典集合")]
    public async Task<dynamic> GetAllDictList()
    {
        var langCode = _userManager.LangCode;
        var ds = await _sysDictTypeRep.AsQueryable()
            .InnerJoin(_sysDictDataService.VSysDictData, (u, w) => u.Id == w.DictTypeId)
            .Select((u, w) => new DictDataOutput
            {
                DictDataId = w.Id, // 给翻译用
                TypeCode = u.Code,
                Label = w.Label,
                Value = w.Value,
                Code = w.Code,
                TagType = w.TagType,
                StyleSetting = w.StyleSetting,
                ClassSetting = w.ClassSetting,
                ExtData = w.ExtData,
                Remark = w.Remark,
                OrderNo = w.OrderNo,
                Status = w.Status == StatusEnum.Enable && u.Status == StatusEnum.Enable ? StatusEnum.Enable : StatusEnum.Disable
            })
            .ToListAsync();
        var ids = ds.Select(x => x.DictDataId).Distinct().ToList();

        Dictionary<long, string> translations = new();
        if (ids.Any())
        {
            translations = await _sysLangTextCacheService.GetTranslations(
                "SysDictData",
                "Label",
                ids,
                langCode
            );
        }
        foreach (var item in ds)
        {
            if (translations.TryGetValue(item.DictDataId, out var translated) && !string.IsNullOrEmpty(translated))
            {
                item.Label = translated;
            }
        }

        var result = ds
            .OrderBy(u => u.OrderNo)
            .GroupBy(u => u.TypeCode)
            .ToDictionary(u => u.Key, u => u.ToList());

        return result;
    }
}