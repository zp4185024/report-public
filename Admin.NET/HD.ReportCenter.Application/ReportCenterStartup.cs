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
        // AI LLM 客户端：不引入 SDK，超时按调用方逐次控制（对话不设每调用硬超时、仅由取消令牌
        // 中止；连通性测试 30s），这里把 HttpClient 自身的固定超时放开，避免默认值提前截断长对话。
        services.AddHttpClient(
            CoreValidation.AiAssistant.PocLlmClient.HttpClientName,
            client => {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            });
        // PocLlmClient 无自动注册标记（单测手工构造），生产容器必须显式登记；两个单参构造歧义，用工厂固定走命名 HttpClient。
        services.AddTransient<CoreValidation.AiAssistant.PocLlmClient>(provider =>
            new CoreValidation.AiAssistant.PocLlmClient(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient(
                    CoreValidation.AiAssistant.PocLlmClient.HttpClientName)));
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
        database.CodeFirst.InitTables<
            CoreValidation.PocSyncTask,
            CoreValidation.PocSyncRunLog>();
        // AI 助手表：单行配置默认关闭 + 工具审计；结构也可由 scripts/database/Apply-AiAssistantSchema.ps1 预先建立。
        database.CodeFirst.InitTables<
            CoreValidation.AiAssistant.PocAiSettings,
            CoreValidation.AiAssistant.PocAiAuditLog>();
    }
}
