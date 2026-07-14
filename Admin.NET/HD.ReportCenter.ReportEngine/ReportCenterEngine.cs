namespace HD.ReportCenter.ReportEngine;

public static class ReportCenterEngine
{
    private static readonly string[] Capabilities =
    [
        "url-embed",
        "sql-dataset",
        "query-form",
        "formula-cell",
        "excel-export",
        "paged-preview",
        "univer-worker",
        "formula-preserving-export",
        "streaming-export"
    ];

    public static ReportCenterEngineInfo GetInfo()
    {
        return new ReportCenterEngineInfo("HD Report Center", "net8.0", Capabilities);
    }
}

public sealed record ReportCenterEngineInfo(
    string Name,
    string Runtime,
    IReadOnlyCollection<string> Capabilities);
