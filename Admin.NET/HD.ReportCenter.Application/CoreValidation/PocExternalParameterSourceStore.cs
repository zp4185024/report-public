using Newtonsoft.Json;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Application.CoreValidation;

public sealed record PocExternalParameterSourceMutation(
    string Code,
    string Name,
    string BaseUrl,
    string Endpoint,
    string HttpMethod,
    string RequestBodyTemplate,
    string HeadersTemplate,
    string RequestParameterSchemaJson,
    string FieldSchemaJson,
    int TimeoutSeconds,
    bool IsEnabled,
    int ExpectedRevision);

/// <summary>外部参数接口的独立存储边界，并负责首次使用时写入 HIS 默认配置。</summary>
public sealed class PocExternalParameterSourceStore : ITransient
{
    private readonly ISqlSugarClient _database;

    /// <summary>初始化 PocExternalParameterSourceStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="database">database 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocExternalParameterSourceStore(ISqlSugarClient database)
    {
        _database = database;
    }

    /// <summary>调用 PocExternalParameterSourceStore 的异步依赖完成 List Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocExternalParameterSource>> ListAsync()
    {
        await EnsureHisSeedAsync();
        return await _database.Queryable<PocExternalParameterSource>()
            .OrderByDescending(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();
    }

    /// <summary>查询或读取 PocExternalParameterSourceStore 所需的 Get Async 数据，并按调用约定返回结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocExternalParameterSource?> GetAsync(string code)
    {
        await EnsureHisSeedAsync();
        return await _database.Queryable<PocExternalParameterSource>()
            .Where(item => item.Code == code)
            .FirstAsync();
    }

    /// <summary>更新 PocExternalParameterSourceStore 的 Save Async 状态，并同步相关业务数据。</summary>
    /// <param name="mutation">mutation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<int> SaveAsync(PocExternalParameterSourceMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var now = DateTime.Now;
        var revision = mutation.ExpectedRevision + 1;
        var existing = await _database.Queryable<PocExternalParameterSource>()
            .Where(item => item.Code == mutation.Code)
            .FirstAsync();
        if (existing is null)
        // 第一阶段：调用 SaveAsync 依赖的异步能力，等待并校验返回数据。
        {
            if (mutation.ExpectedRevision != 0)
            {
                throw new PocDataAssetRevisionConflictException(
                    "External parameter source",
                    mutation.Code,
                    mutation.ExpectedRevision);
            }
            await _database.Insertable(new PocExternalParameterSource
            {
                Code = mutation.Code,
                Name = mutation.Name,
                BaseUrl = mutation.BaseUrl,
                Endpoint = mutation.Endpoint,
                HttpMethod = mutation.HttpMethod,
                RequestBodyTemplate = mutation.RequestBodyTemplate,
                HeadersTemplate = mutation.HeadersTemplate,
                RequestParameterSchemaJson = mutation.RequestParameterSchemaJson,
                FieldSchemaJson = mutation.FieldSchemaJson,
                TimeoutSeconds = mutation.TimeoutSeconds,
                IsEnabled = mutation.IsEnabled,
                Revision = revision,
                CreateTime = now,
                UpdateTime = now
            }).ExecuteCommandAsync();
            return revision;
        }

        // 第2阶段：遍历 SaveAsync 当前数据集合，逐项转换并维护处理结果。
        var affected = await _database.Updateable<PocExternalParameterSource>()
            .SetColumns(item => new PocExternalParameterSource
            {
                Name = mutation.Name,
                BaseUrl = mutation.BaseUrl,
                Endpoint = mutation.Endpoint,
                HttpMethod = mutation.HttpMethod,
                RequestBodyTemplate = mutation.RequestBodyTemplate,
                HeadersTemplate = mutation.HeadersTemplate,
                RequestParameterSchemaJson = mutation.RequestParameterSchemaJson,
                FieldSchemaJson = mutation.FieldSchemaJson,
                TimeoutSeconds = mutation.TimeoutSeconds,
                IsEnabled = mutation.IsEnabled,
                Revision = revision,
                UpdateTime = now
            })
            .Where(item => item.Code == mutation.Code && item.Revision == mutation.ExpectedRevision)
            .ExecuteCommandAsync();
        if (affected != 1)
        {
            throw new PocDataAssetRevisionConflictException(
                "External parameter source",
                mutation.Code,
                mutation.ExpectedRevision);
        }
        return revision;
    }

    /// <summary>校验 PocExternalParameterSourceStore 的 Ensure His Seed Async 条件，并返回可供后续流程判断的结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task EnsureHisSeedAsync()
    {
        var existing = await _database.Queryable<PocExternalParameterSource>()
            .Where(item => item.Code == "his")
            .FirstAsync();
        if (existing is not null)
        {
            // 第一阶段：调用 EnsureHisSeedAsync 依赖的异步能力，等待并校验返回数据。
            await EnsureHisOptionalTokenParameterAsync(existing);
            return;
        }

        var fields = new[]
        {
            new PocExternalParameterFieldDefinition("hisUserId", "人员ID", "string", "content.userId"),
            new PocExternalParameterFieldDefinition("hisUserName", "人员姓名", "string", "content.userName"),
            new PocExternalParameterFieldDefinition("hisOrganizeId", "医院ID", "string", "content.organId"),
            new PocExternalParameterFieldDefinition("hisOrganizeName", "医院名称", "string", "content.organName"),
            new PocExternalParameterFieldDefinition("hisDepartmentId", "科室ID", "string", "content.departmentId"),
            new PocExternalParameterFieldDefinition("hisDepartmentName", "科室名称", "string", "content.departmentName"),
            new PocExternalParameterFieldDefinition("hisOrganizeCode", "医院编码", "string", "content.organCode"),
            new PocExternalParameterFieldDefinition("hisRegionCode", "区域编码", "string", "content.regionCode")
        };
        try
        {
            // 第2阶段：调用 EnsureHisSeedAsync 依赖的异步能力，等待并校验返回数据。
            await _database.Insertable(new PocExternalParameterSource
            {
                Code = "his",
                Name = "HIS 登录信息",
                BaseUrl = "http://192.168.2.5:8889/hospital/",
                Endpoint = "SystemUserService/GetUserInfoByToken",
                HttpMethod = "POST",
                RequestBodyTemplate = "{}",
                HeadersTemplate = "{\"token\":\"1\"}",
                RequestParameterSchemaJson = JsonConvert.SerializeObject(
                    new[] { new PocExternalRequestParameterDefinition("content.token", "token", "Body", false) },
                    Formatting.None),
                FieldSchemaJson = JsonConvert.SerializeObject(fields, Formatting.None),
                TimeoutSeconds = 10,
                IsEnabled = true,
                Revision = 1,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }
        catch (Exception exception) when (
            exception.Message.Contains("RcExternalParameterSource", StringComparison.OrdinalIgnoreCase) &&
            exception.Message.Contains("unique", StringComparison.OrdinalIgnoreCase))
        {
            // 多实例同时首次访问时由唯一索引决定唯一写入者。
        }
    }

    /// <summary>校验 PocExternalParameterSourceStore 的 Ensure His Optional Token Parameter Async 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task EnsureHisOptionalTokenParameterAsync(PocExternalParameterSource source)
    {
        // 新增列在旧数据库记录上为空；保存后的空数组表示用户明确删除，不应再次写回默认项。
        if (!string.IsNullOrWhiteSpace(source.RequestParameterSchemaJson))
        {
            return;
        }

        await _database.Updateable<PocExternalParameterSource>()
            .SetColumns(item => new PocExternalParameterSource
            {
                RequestParameterSchemaJson = JsonConvert.SerializeObject(
                    new[] { new PocExternalRequestParameterDefinition("content.token", "token", "Body", false) },
                    Formatting.None),
                Revision = source.Revision + 1,
                UpdateTime = DateTime.Now
            })
            .Where(item => item.Code == source.Code && item.Revision == source.Revision)
            .ExecuteCommandAsync();
    }
}
