namespace HD.ReportCenter.ReportEngine.CoreValidation;

public sealed record PocParameterBinding(string Code, string SheetId, int Row, int Column);

public sealed record PocDetailFieldBinding(string Code, int Column);

public sealed record PocDetailBinding(
    string DatasetCode,
    string SheetId,
    int TemplateRow,
    IReadOnlyList<PocDetailFieldBinding> Fields);

public sealed record PocTemplateBinding(
    int Version,
    IReadOnlyList<PocParameterBinding> ParameterBindings,
    PocDetailBinding? DetailBinding);

public sealed class PocTemplateValidationException : Exception
{
    public PocTemplateValidationException(string message)
        : base(message)
    {
    }

    public PocTemplateValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
