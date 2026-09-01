using Furion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace HD.ReportCenter.Application;

[AppStartup(101)]
public sealed class ReportCenterStartup : AppStartup
{
    /// <summary>向依赖注入容器注册 ReportCenterStartup 启动所需的服务和运行配置。</summary>
    /// <param name="services">services 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpClient(
            CoreValidation.PocExternalParameterSourceService.HttpClientName,
            client => client.DefaultRequestHeaders.Accept.ParseAdd("application/json"));
    }

    /// <summary>配置 ReportCenterStartup 的运行管道与启动行为，使后续请求按既定顺序执行。</summary>
    /// <param name="app">app 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="env">env 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="database">database 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public void Configure(
        IApplicationBuilder app,
        IWebHostEnvironment env,
        ISqlSugarClient database)
    {
        // 报表中心的实体由动态业务程序集加载，显式初始化可确保新增表和新增列在升级时补齐。
        database.CodeFirst.InitTables<
            CoreValidation.PocReport,
            CoreValidation.PocDataSource,
            CoreValidation.PocReportDataset,
            CoreValidation.PocReportParameter,
            CoreValidation.PocExternalParameterSource>();
        database.CodeFirst.InitTables<CoreValidation.PocReportTemplate>();
    }
}
