namespace HD.ReportCenter.ReportEngine;

/// <summary>
/// 报表引擎程序集的轻量能力描述入口，用于验证宿主确实加载了 .NET 8 报表模块及预期能力集合。
/// </summary>
public static class ReportCenterEngine
{
    // 字符串能力清单便于宿主/健康检查展示；它不是运行时开关，实际功能仍由对应服务和测试保证。
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

    /// <summary>返回当前程序集验证过的运行时和能力快照。</summary>
    public static ReportCenterEngineInfo GetInfo()
    {
        return new ReportCenterEngineInfo("HD Report Center", "net8.0", Capabilities);
    }
}

/// <summary>报表引擎自描述信息。</summary>
public sealed record ReportCenterEngineInfo(
    string Name,
    string Runtime,
    IReadOnlyCollection<string> Capabilities);
