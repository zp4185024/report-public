// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Swashbuckle.AspNetCore.SwaggerGen;

namespace Admin.NET.Core.Service;

[ApiDescriptionSettings(Order = 100, Description = "Swagger")]
public class SwaggerService : IDynamicApiController, ITransient
{
    private readonly SwaggerGenOptions _genOptions;

    /// <summary>初始化 SwaggerService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="genOptions">控制本次处理行为的配置选项。</param>
    public SwaggerService(IOptions<SwaggerGenOptions> genOptions)
    {
        _genOptions = genOptions.Value;
    }

    /// <summary>查询或读取 SwaggerService 所需的 Get Swagger Groups 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取Swagger接口分组列表"), HttpGet]
    public async Task<IActionResult> GetSwaggerGroups()
    {
        var docs = _genOptions.SwaggerGeneratorOptions.SwaggerDocs;
        if (docs == null || !docs.Any()) Oops.Oh("No Swagger documents configured.");

        return new JsonResult(docs.Select(d => new
        {
            name = d.Key,
            url = $"/swagger/{d.Key}/swagger.json"
        }));
    }
}
