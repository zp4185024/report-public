using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;

namespace HD.ReportCenter.Tests;

public sealed class PocSerializationTests
{
    [Fact]
    public void Enums_AreSerializedAsStableStringsByHostSerializer()
    {
        Assert.Equal("\"Paged\"", JsonConvert.SerializeObject(PocPreviewMode.Paged));
        Assert.Equal("\"Normal\"", JsonConvert.SerializeObject(PocDataExportMode.Normal));
        Assert.Equal("\"Pending\"", JsonConvert.SerializeObject(PocExportJobStatus.Pending));
    }
}
