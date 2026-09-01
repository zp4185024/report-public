using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;

namespace HD.ReportCenter.Tests;

public sealed class PocReportContractsTests
{
    /// <summary>验证 Execute Request Deserializes Dynamic Parameter Dictionary 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void ExecuteRequest_DeserializesDynamicParameterDictionary()
    {
        const string json = """
            {
              "page": 2,
              "parameters": {
                "hospitalId": "H01",
                "customFlag": null
              }
            }
            """;

        var request = JsonConvert.DeserializeObject<PocExecuteRequest>(json);

        Assert.NotNull(request);
        Assert.Equal(2, request.Page);
        Assert.Equal("H01", request.Parameters["hospitalId"]);
        Assert.Null(request.Parameters["customFlag"]);
    }
}
