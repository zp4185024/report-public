namespace HD.ReportCenter.Application;

[AllowAnonymous]
[ApiDescriptionSettings("报表中心", Name = "ReportCenter", Order = 900)]
public class ReportCenterHealthApi : IDynamicApiController
{
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
