using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;

namespace HD.ReportCenter.Tests;

public sealed class PocSerializationTests
{
    /// <summary>验证 Enums Are Serialized As Stable Strings By Host Serializer 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Enums_AreSerializedAsStableStringsByHostSerializer()
    {
        Assert.Equal("\"Paged\"", JsonConvert.SerializeObject(PocPreviewMode.Paged));
        Assert.Equal("\"Normal\"", JsonConvert.SerializeObject(PocDataExportMode.Normal));
        Assert.Equal("\"Pending\"", JsonConvert.SerializeObject(PocExportJobStatus.Pending));
    }
}
