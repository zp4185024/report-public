// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using System.Linq.Dynamic.Core.CustomTypeProviders;

namespace Admin.NET.Core;

/// <summary>
/// 扩展支持 SqlFunc，不支持 Subqueryable
/// </summary>
public class SqlSugarTypeProvider : DefaultDynamicLinqCustomTypeProvider
{
    /// <summary>初始化 SqlSugarTypeProvider 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="cacheCustomTypes">cacheCustomTypes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public SqlSugarTypeProvider(bool cacheCustomTypes = true) : base(ParsingConfig.Default, cacheCustomTypes)
    {
    }

    /// <summary>查询或读取 SqlSugarTypeProvider 所需的 Get Custom Types 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public override HashSet<Type> GetCustomTypes()
    {
        var customTypes = base.GetCustomTypes();
        customTypes.Add(typeof(SqlFunc));
        return customTypes;
    }
}