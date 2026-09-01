namespace HD.ReportCenter.Application;

[AllowAnonymous]
[ApiDescriptionSettings("报表中心", Name = "ReportCenter", Order = 900)]
public class ReportCenterHealthApi : IDynamicApiController
{
    /// <summary>根据 ReportCenterHealthApi 当前输入计算 Health 结果，并按方法声明的类型返回。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [HttpGet("/api/report-center/health")]
    public ReportCenterHealthResult Health()
    {
        var engine = ReportCenterEngine.GetInfo();

        return new ReportCenterHealthResult(
            engine.Name,
            engine.Runtime,
            engine.Capabilities,
            DateTimeOffset.Now);
    }
}

public sealed record ReportCenterHealthResult(
    string Name,
    string Runtime,
    IReadOnlyCollection<string> Capabilities,
    DateTimeOffset CheckedAt);
