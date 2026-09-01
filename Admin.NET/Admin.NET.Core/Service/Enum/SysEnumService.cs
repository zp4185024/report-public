// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统枚举服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 275)]
public class SysEnumService : IDynamicApiController, ITransient
{
    private readonly EnumOptions _enumOptions;

    /// <summary>初始化 SysEnumService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="enumOptions">控制本次处理行为的配置选项。</param>
    public SysEnumService(IOptions<EnumOptions> enumOptions)
    {
        _enumOptions = enumOptions.Value;
    }

    /// <summary>
    /// 获取所有枚举类型 🔖
    /// </summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取所有枚举类型")]
    public List<EnumTypeOutput> GetEnumTypeList()
    {
        var enumTypeList = App.EffectiveTypes.Where(t => t.IsEnum)
            .Where(t => _enumOptions.EntityAssemblyNames.Contains(t.Assembly.GetName().Name) || _enumOptions.EntityAssemblyNames.Any(name => t.Assembly.GetName().Name!.Contains(name)))
            .Where(t => t.GetCustomAttributes(typeof(IgnoreEnumToDictAttribute), false).Length == 0) // 排除有忽略转字典特性类型
            .Where(t => t.GetCustomAttributes(typeof(ErrorCodeTypeAttribute), false).Length == 0) // 排除错误代码类型
            .OrderBy(u => u.Name).ThenBy(u => u.FullName)
            .ToList();

        // 如果存在同名枚举类，则依次增加 "_序号" 后缀
        var list = enumTypeList.Select(GetEnumDescription).ToList();
        foreach (var enumType in list.GroupBy(u => u.TypeName).Where(g => g.Count() > 1))
        {
            int i = 1;
            foreach (var item in list.Where(u => u.TypeName == enumType.Key).Skip(1)) item.TypeName = $"{item.TypeName}_{i++}";
        }
        return list;
    }

    /// <summary>查询或读取 SysEnumService 所需的 Get Enum Description 数据，并按调用约定返回结果。</summary>
    /// <param name="type">决定处理分支、数据类型或运行状态的枚举值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static EnumTypeOutput GetEnumDescription(Type type)
    {
        string description = type.Name;
        var attrs = type.GetCustomAttributes(typeof(DescriptionAttribute), false);
        if (attrs.Length != 0)
        {
            var att = ((DescriptionAttribute[])attrs)[0];
            description = att.Description;
        }
        var enumType = App.EffectiveTypes.FirstOrDefault(t => t.IsEnum && t.Name == type.Name);
        return new EnumTypeOutput
        {
            TypeDescribe = description,
            TypeName = type.Name,
            TypeRemark = description,
            TypeFullName = type.FullName,
            EnumEntities = (enumType ?? type).EnumToList()
        };
    }

    /// <summary>
    /// 通过枚举类型获取枚举值集合 🔖
    /// </summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("通过枚举类型获取枚举值集合")]
    public List<EnumEntity> GetEnumDataList([FromQuery] EnumInput input)
    {
        var enumType = App.EffectiveTypes.FirstOrDefault(u => u.IsEnum && u.Name == input.EnumName);
        if (enumType is not { IsEnum: true }) throw Oops.Oh(ErrorCodeEnum.D1503);

        return enumType.EnumToList();
    }

    /// <summary>
    /// 通过实体的字段名获取相关枚举值集合（目前仅支持枚举类型） 🔖
    /// </summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("通过实体的字段名获取相关枚举值集合")]
    public static List<EnumEntity> GetEnumDataListByField([FromQuery] QueryEnumDataInput input)
    {
        // 获取实体类型属性
        Type entityType = App.EffectiveTypes.FirstOrDefault(u => u.Name == input.EntityName) ?? throw Oops.Oh(ErrorCodeEnum.D1504);

        // 获取字段类型
        var fieldType = entityType.GetProperties().FirstOrDefault(u => u.Name == input.FieldName)?.PropertyType;
        if (fieldType is not { IsEnum: true }) throw Oops.Oh(ErrorCodeEnum.D1503);

        return fieldType.EnumToList();
    }
}