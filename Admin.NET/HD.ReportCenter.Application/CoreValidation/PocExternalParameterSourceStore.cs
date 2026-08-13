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

    public PocExternalParameterSourceStore(ISqlSugarClient database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<PocExternalParameterSource>> ListAsync()
    {
        await EnsureHisSeedAsync();
        return await _database.Queryable<PocExternalParameterSource>()
            .OrderByDescending(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();
    }

    public async Task<PocExternalParameterSource?> GetAsync(string code)
    {
        await EnsureHisSeedAsync();
        return await _database.Queryable<PocExternalParameterSource>()
            .Where(item => item.Code == code)
            .FirstAsync();
    }

    public async Task<int> SaveAsync(PocExternalParameterSourceMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var now = DateTime.Now;
        var revision = mutation.ExpectedRevision + 1;
        var existing = await _database.Queryable<PocExternalParameterSource>()
            .Where(item => item.Code == mutation.Code)
            .FirstAsync();
        if (existing is null)
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

    private async Task EnsureHisSeedAsync()
    {
        var existing = await _database.Queryable<PocExternalParameterSource>()
            .Where(item => item.Code == "his")
            .FirstAsync();
        if (existing is not null)
        {
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
