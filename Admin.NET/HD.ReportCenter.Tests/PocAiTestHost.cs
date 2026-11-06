using System.Reflection;
using System.Text;
using System.Threading;
using Admin.NET.Core;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using SqlSugar;

namespace HD.ReportCenter.Tests;

/// <summary>
/// AI 助手测试宿主：SQLite 内存库 + 可脚本化的伪 LLM HTTP 通道，
/// 让工具执行器、会话存储、设置服务与对话循环的单测完全不依赖真实 LLM 端点与真实业务库。
/// </summary>
internal sealed class PocAiTestHost : IDisposable
{
    /// <summary>共享 SQLite 连接；SqlSugar 释放连接时由 NonClosingSqliteConnection 拦截，内存库保持存活。</summary>
    public readonly SqliteConnection Connection;

    /// <summary>绑定共享连接的 SqlSugar 客户端；同时充当“报表中心主库”和“目标业务库”。</summary>
    public readonly SqlSugarClient Database;

    /// <summary>会话存储；测试通过拨动时钟驱动 30 分钟过期。</summary>
    public readonly PocAiChatSessionStore Sessions;

    /// <summary>全局数据源存储。</summary>
    public readonly PocDataAssetStore DataAssets;

    /// <summary>AI 设置存储与服务。</summary>
    public readonly PocAiSettingsStore SettingsStore;

    /// <summary>AI 设置服务。</summary>
    public readonly PocAiSettingsService SettingsService;

    /// <summary>工具执行器。</summary>
    public readonly PocAiToolExecutor ToolExecutor;

    /// <summary>工具审计存储。</summary>
    public readonly PocAiAuditStore AuditStore;

    /// <summary>可脚本化的伪 LLM HTTP 通道。</summary>
    public readonly ScriptedLlmHandler LlmHandler;

    /// <summary>LLM 客户端（走伪通道）。</summary>
    public readonly PocLlmClient LlmClient;

    /// <summary>对话服务。</summary>
    public readonly PocAiChatService ChatService;

    /// <summary>报表包服务（全链校验与受限试跑），供产物工具测试使用。</summary>
    public readonly PocReportPackageService PackageService;

    /// <summary>同步 AI 智能匹配服务。</summary>
    public readonly PocAiSyncMatchService SyncMatch;

    /// <summary>报表包导入审计目录（重定向到一次性临时目录，避免污染仓库）。</summary>
    public readonly string PackageAuditDirectory;

    /// <summary>可拨动的假时钟；默认从当前时间起跳。</summary>
    public Func<DateTime> Clock = () => DateTime.Now;

    /// <summary>初始化测试宿主：建表、绑定存储与服务，并注册默认数据源 ai_main（SQLite 客户端伪装）。</summary>
    public PocAiTestHost()
    {
        Connection = new NonClosingSqliteConnection("Data Source=:memory:");
        Connection.Open();
        Database = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityService = (property, column) =>
                {
                    if (new NullabilityInfoContext().Create(property).WriteState is NullabilityState.Nullable)
                    {
                        column.IsNullable = true;
                    }
                }
            }
        });
        Database.Ado.Connection = Connection;
        Database.Ado.Open();
        // 生产环境由 Admin.NET 的全局 AOP 在插入时补雪花 Id；测试客户端没有该配置，
        // 这里用等价的本地 AOP 保证实体主键唯一，避免多行插入撞主键。
        var idSeed = 1000L;
        Database.Aop.DataExecuting = (_, entityInfo) =>
        {
            if (entityInfo.EntityColumnInfo.IsPrimarykey &&
                !entityInfo.EntityColumnInfo.IsIdentity &&
                entityInfo.EntityColumnInfo.PropertyInfo.PropertyType == typeof(long))
            {
                var value = entityInfo.EntityColumnInfo.PropertyInfo.GetValue(entityInfo.EntityValue);
                if (value is null || (long)value == 0)
                {
                    entityInfo.SetValue(Interlocked.Increment(ref idSeed));
                }
            }
            else if (entityInfo.PropertyName == nameof(EntityBase.CreateTime))
            {
                var createTime = entityInfo.EntityColumnInfo.PropertyInfo.GetValue(entityInfo.EntityValue);
                if (createTime is null || createTime.Equals(DateTime.MinValue))
                {
                    entityInfo.SetValue(DateTime.Now);
                }
            }
        };
        // 与 ReportCenterStartup 相同的建表口径：分组显式初始化，确保 AI 配置/审计/数据源表全部就位。
        Database.CodeFirst.InitTables(typeof(PocAiSettings));
        Database.CodeFirst.InitTables(typeof(PocAiAuditLog));
        Database.CodeFirst.InitTables(typeof(PocDataSource));
        // 产物工具（submit/dry_run）要触达报表包全链校验，补齐配置/模板/参数/模板快照四张表。
        Database.CodeFirst.InitTables(
            typeof(HD.ReportCenter.Application.CoreValidation.PocReport),
            typeof(HD.ReportCenter.Application.CoreValidation.PocReportDataset),
            typeof(HD.ReportCenter.Application.CoreValidation.PocReportParameter),
            typeof(HD.ReportCenter.Application.CoreValidation.PocReportTemplate));

        DataAssets = new PocDataAssetStore(Database);
        SettingsStore = new PocAiSettingsStore(Database);
        Sessions = new PocAiChatSessionStore(() => Clock());
        AuditStore = new PocAiAuditStore(Database);
        LlmHandler = new ScriptedLlmHandler();
        LlmClient = new PocLlmClient(new HttpClient(LlmHandler));
        SettingsService = new PocAiSettingsService(SettingsStore, LlmClient);
        ToolExecutor = new PocAiToolExecutor(DataAssets, SqliteClientFactory, SettingsService);
        // 报表包链路按 PocReportPackageTests 的装配方式串起配置/模板/包服务，审计目录指向临时目录。
        PackageAuditDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hd-report-ai-tests",
            Guid.NewGuid().ToString("N"));
        var dataAssetService = new PocDataAssetService(DataAssets);
        var configurationStore = new PocReportConfigurationStore(Database);
        var templateStore = new PocTemplateStore(Database);
        var configurationService = new PocReportConfigurationService(
            configurationStore,
            DataAssets,
            Database,
            templateStore);
        var templateService = new PocTemplateService(templateStore, configurationService);
        PackageService = new PocReportPackageService(
            configurationStore,
            DataAssets,
            dataAssetService,
            configurationService,
            templateService,
            templateStore)
        {
            AuditDirectory = PackageAuditDirectory
        };
        ChatService = new PocAiChatService(
            SettingsService,
            Sessions,
            ToolExecutor,
            AuditStore,
            LlmClient,
            null, // 视觉分析器：阶段四附件参考走主链路默认构造，单测宿主无需特判。
            DataAssets,
            SqliteClientFactory,
            new PocAiSpecCompiler(),
            PackageService);
        // 同步智能匹配：复用同步存储/数据资产存储与伪 LLM 通道。
        SyncMatch = new PocAiSyncMatchService(
            SettingsService,
            LlmClient,
            AuditStore,
            new PocDataSyncStore(Database),
            DataAssets);

        // 统一注入一个已启用数据源：dbType 用 PostgreSQL 让采样 SQL 以 LIMIT 方言生成（SQLite 兼容），
        // 连接串字段只是占位——实际取数走注入的 SQLite 客户端工厂，测试不触碰真实网络。
        Database.Insertable(new PocDataSource
        {
            Code = "ai_main",
            Name = "AI 测试数据源",
            DbType = "PostgreSql",
            ConnectionString = "Host=unused;Database=unused;Username=unused;Password=unused",
            DefaultTimeoutSeconds = 5,
            DefaultMaxRows = 1000,
            Revision = 1,
            IsEnabled = true
        }).ExecuteCommandAsync();
    }

    /// <summary>把 AI 设置保存为启用状态；限额参数可由用例覆盖。</summary>
    /// <param name="baseUrl">服务地址。</param>
    /// <param name="model">模型名。</param>
    /// <param name="apiKey">密钥。</param>
    /// <param name="sampleRowLimit">采样上限。</param>
    /// <param name="sqlProbeEnabled">是否允许 SQL 探测。</param>
    /// <param name="sqlProbeRowLimit">探测行数上限。</param>
    /// <param name="maxToolRounds">工具轮数上限。</param>
    /// <returns>保存后的配置实体。</returns>
    public async Task<PocAiSettings> EnableAiAsync(
        string baseUrl = "http://llm.test/v1",
        string model = "test-model",
        string apiKey = "sk-test1234567890",
        int sampleRowLimit = 20,
        bool sqlProbeEnabled = true,
        int sqlProbeRowLimit = 100,
        int maxToolRounds = 8)
    {
        await SettingsService.SaveAsync(new PocAiSettingsInput(
            true,
            baseUrl,
            apiKey,
            model,
            sampleRowLimit,
            sqlProbeEnabled,
            sqlProbeRowLimit,
            maxToolRounds));
        return await SettingsStore.GetAsync();
    }

    /// <summary>在共享 SQLite 库里建一张业务表并写入行，供采样/探测工具取数。</summary>
    /// <param name="tableName">表名。</param>
    /// <param name="rowCount">写入行数。</param>
    public void SeedDataTable(string tableName, int rowCount)
    {
        var safeName = string.Concat(tableName.Where(char.IsLetterOrDigit));
        Database.Ado.ExecuteCommand($"CREATE TABLE \"{safeName}\" (Id INTEGER PRIMARY KEY, PatientName TEXT, FeeAmount REAL)");
        var builder = new StringBuilder();
        for (var index = 1; index <= rowCount; index++)
        {
            builder.Append($"INSERT INTO \"{safeName}\" (PatientName, FeeAmount) VALUES ('患者{index}', {index * 10}); ");
        }
        Database.Ado.ExecuteCommand(builder.ToString());
    }

    /// <summary>加载仓库样例 spec（scripts/report-design/samples/report-asset-inventory.spec.json）文本。</summary>
    /// <returns>spec JSON 文本。</returns>
    public static string LoadSampleSpec()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(
                [current.FullName, "scripts", "report-design", "samples", "report-asset-inventory.spec.json"]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }
        throw new FileNotFoundException("未找到样例 spec scripts/report-design/samples/report-asset-inventory.spec.json。");
    }

    /// <summary>按会话号统计审计行数，供断言“每次工具调用落一行审计”。</summary>
    /// <param name="sessionId">会话编号。</param>
    /// <returns>审计行数。</returns>
    public async Task<int> CountAuditAsync(string sessionId) =>
        await Database.Queryable<PocAiAuditLog>().Where(item => item.SessionId == sessionId).CountAsync();

    /// <summary>释放共享连接，销毁内存库。</summary>
    public void Dispose()
    {
        Database.Dispose();
        Connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>工具与提示词共用的客户端工厂：每次调用新建 SqlSugar 客户端并绑定共享连接，
    /// 与 PocDataSyncExecutorTests 的做法一致——调用方会释放该客户端，NonClosing 连接保证内存库不清空。</summary>
    /// <param name="dbType">数据源声明的数据库类型（测试中忽略）。</param>
    /// <param name="connectionString">数据源连接串（测试中忽略）。</param>
    /// <param name="timeoutSeconds">命令超时秒数。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>绑定共享 SQLite 连接的新客户端。</returns>
    private SqlSugarClient SqliteClientFactory(
        string dbType,
        string connectionString,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        _ = dbType;
        _ = connectionString;
        _ = timeoutSeconds;
        _ = cancellationToken;
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute
        });
        client.Ado.Connection = Connection;
        return client;
    }
}

/// <summary>
/// 可脚本化的伪 LLM HttpMessageHandler：按入队顺序回放预设响应或异常，
/// 并记录每次请求的方法、地址与请求体，供“请求不外发/请求形状/循环收敛”断言使用。
/// </summary>
internal sealed class ScriptedLlmHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _scripted = new();
    private readonly List<string> _requestBodies = [];

    /// <summary>历史请求体快照（每次请求的 JSON 文本）。</summary>
    public IReadOnlyList<string> RequestBodies => _requestBodies;

    /// <summary>已收到的请求数量；断言“AI 关闭时零请求”用。</summary>
    public int RequestCount => _requestBodies.Count;

    /// <summary>入队一个 200 JSON 响应。</summary>
    /// <param name="body">响应 JSON 文本。</param>
    public void EnqueueJson(string body) =>
        _scripted.Enqueue(_ => Task.FromResult(HttpResponse(Text(body))));

    /// <summary>入队一个非 200 状态响应。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="body">响应正文。</param>
    public void EnqueueError(int statusCode, string body) =>
        _scripted.Enqueue(_ => Task.FromResult(HttpResponse(Text(body), statusCode)));

    /// <summary>入队一个延迟后成功的响应，用于超时用例。</summary>
    /// <param name="body">响应 JSON 文本。</param>
    /// <param name="delay">延迟时长。</param>
    public void EnqueueDelayedJson(string body, TimeSpan delay) =>
        _scripted.Enqueue(async token =>
        {
            await Task.Delay(delay, token);
            return HttpResponse(Text(body));
        });

    /// <summary>发送一次请求：记录请求体并回放下一个脚本项；脚本耗尽视为测试编排错误。</summary>
    /// <param name="request">HTTP 请求消息。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>脚本预设的响应。</returns>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        if (_scripted.Count == 0)
        {
            throw new InvalidOperationException("伪 LLM 通道脚本耗尽：测试没有为这次请求安排响应。");
        }
        return await _scripted.Dequeue()(cancellationToken);
    }

    /// <summary>构造 JSON 文本响应。</summary>
    /// <param name="body">响应正文。</param>
    /// <returns>字符串内容。</returns>
    private static StringContent Text(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>构造 HTTP 响应消息。</summary>
    /// <param name="content">响应内容。</param>
    /// <param name="statusCode">HTTP 状态码，默认 200。</param>
    /// <returns>响应消息。</returns>
    private static HttpResponseMessage HttpResponse(StringContent content, int statusCode = 200) =>
        new((System.Net.HttpStatusCode)statusCode) { Content = content };
}

/// <summary>构造 OpenAI 兼容响应 JSON 的小工具，供 LLM 客户端与对话循环测试复用。</summary>
internal static class PocAiLlmFixtures
{
    /// <summary>构造纯文本回复响应。</summary>
    /// <param name="content">模型文本。</param>
    /// <returns>响应 JSON。</returns>
    public static string ContentResponse(string content) => JsonConvert.SerializeObject(new
    {
        choices = new[]
        {
            new
            {
                index = 0,
                finish_reason = "stop",
                message = new { role = "assistant", content }
            }
        }
    });

    /// <summary>构造单个工具调用响应。</summary>
    /// <param name="callId">工具调用编号。</param>
    /// <param name="toolName">工具名。</param>
    /// <param name="argumentsJson">参数 JSON 文本。</param>
    /// <param name="content">随附文本，可为 null。</param>
    /// <returns>响应 JSON。</returns>
    public static string ToolCallResponse(string callId, string toolName, string argumentsJson, string? content = null) =>
        JsonConvert.SerializeObject(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    finish_reason = "tool_calls",
                    message = new
                    {
                        role = "assistant",
                        content,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = callId,
                                type = "function",
                                function = new { name = toolName, arguments = argumentsJson }
                            }
                        }
                    }
                }
            }
        });
}
