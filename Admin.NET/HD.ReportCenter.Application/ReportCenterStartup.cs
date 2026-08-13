using Furion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace HD.ReportCenter.Application;

[AppStartup(101)]
public sealed class ReportCenterStartup : AppStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpClient(
            CoreValidation.PocExternalParameterSourceService.HttpClientName,
            client => client.DefaultRequestHeaders.Accept.ParseAdd("application/json"));
    }

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
    }
}
