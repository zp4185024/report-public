using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation;

public sealed record PocTemplateDocument(
    string ReportCode,
    JObject Snapshot,
    PocTemplateBinding Binding,
    int Revision);

public sealed record PocSaveTemplateRequest(
    JObject Snapshot,
    PocTemplateBinding Binding,
    int Revision);

public sealed class PocTemplateBindingMismatchException : Exception
{
    public const string ErrorCode = "TEMPLATE_BINDING_MISMATCH";

    public PocTemplateBindingMismatchException()
        : base(ErrorCode)
    {
    }

    public PocTemplateBindingMismatchException(Exception innerException)
        : base(ErrorCode, innerException)
    {
    }

    public string Code => ErrorCode;
}

public sealed class PocTemplateService : ITransient
{
    private const string SeedResourceName =
        "HD.ReportCenter.Application.CoreValidation.Seeds.dept-workload.json";

    private readonly PocTemplateStore _store;

    public PocTemplateService(PocTemplateStore store)
    {
        _store = store;
    }

    public async Task<PocTemplateDocument> GetAsync(string reportCode)
    {
        var stored = await _store.GetAsync(reportCode);
        if (stored is null)
        {
            var seedJson = LoadSeedJson();
            return CreateDocument(reportCode, seedJson, null, 0);
        }

        return CreateDocument(
            stored.ReportCode,
            stored.SnapshotJson,
            stored.BindingJson,
            stored.Revision);
    }

    public async Task<PocTemplateDocument> SaveAsync(
        string reportCode,
        PocSaveTemplateRequest request)
    {
        var snapshotJson = request.Snapshot.ToString(Formatting.None);
        var canonicalBinding = PocTemplateValidator.Scan(snapshotJson);
        var canonicalBindingJson = SerializeBinding(canonicalBinding);
        if (!string.Equals(
                SerializeBinding(request.Binding),
                canonicalBindingJson,
                StringComparison.Ordinal))
        {
            throw new PocTemplateBindingMismatchException();
        }

        var stored = await _store.SaveAsync(
            reportCode,
            snapshotJson,
            canonicalBindingJson,
            request.Revision);

        return new PocTemplateDocument(
            stored.ReportCode,
            JObject.Parse(stored.SnapshotJson),
            canonicalBinding,
            stored.Revision);
    }

    private static PocTemplateDocument CreateDocument(
        string reportCode,
        string snapshotJson,
        string? storedBindingJson,
        int revision)
    {
        var snapshot = JObject.Parse(snapshotJson);
        var canonicalBinding = PocTemplateValidator.Scan(snapshot.ToString(Formatting.None));
        if (storedBindingJson is not null)
        {
            string normalizedStoredBinding;
            try
            {
                var storedBinding = JsonConvert.DeserializeObject<PocTemplateBinding>(storedBindingJson);
                if (storedBinding is null)
                {
                    throw new JsonSerializationException("Stored template binding is null.");
                }

                normalizedStoredBinding = SerializeBinding(storedBinding);
            }
            catch (JsonException exception)
            {
                throw new PocTemplateBindingMismatchException(exception);
            }

            if (!string.Equals(
                    normalizedStoredBinding,
                    SerializeBinding(canonicalBinding),
                    StringComparison.Ordinal))
            {
                throw new PocTemplateBindingMismatchException();
            }
        }

        return new PocTemplateDocument(reportCode, snapshot, canonicalBinding, revision);
    }

    private static string SerializeBinding(PocTemplateBinding binding) =>
        JsonConvert.SerializeObject(binding, Formatting.None);

    private static string LoadSeedJson()
    {
        var assembly = typeof(PocTemplateService).Assembly;
        using var stream = assembly.GetManifestResourceStream(SeedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded report template seed '{SeedResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
