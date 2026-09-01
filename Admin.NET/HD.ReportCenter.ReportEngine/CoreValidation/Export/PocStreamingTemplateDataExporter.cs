using ClosedXML.Parser;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HD.ReportCenter.ReportEngine.CoreValidation.Export;

/// <summary>
/// 大文件模板导出器。只把模板 XML 和单个查询页保留在内存中，明细行直接写入 XLSX worksheet 流。
/// </summary>
public sealed class PocStreamingTemplateDataExporter
{
    private const string WorkbookPath = "xl/workbook.xml";
    private const string WorkbookRelationshipsPath = "xl/_rels/workbook.xml.rels";
    private const string SharedStringsPath = "xl/sharedStrings.xml";

    private static readonly XNamespace SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationshipsNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    private readonly PocXlsxExchangeConverter _exchangeConverter;

    /// <summary>初始化 PocStreamingTemplateDataExporter 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocStreamingTemplateDataExporter()
        : this(new PocXlsxExchangeConverter())
    {
    }

    /// <summary>初始化 PocStreamingTemplateDataExporter 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="exchangeConverter">exchangeConverter 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocStreamingTemplateDataExporter(PocXlsxExchangeConverter exchangeConverter)
    {
        _exchangeConverter = exchangeConverter;
    }

    /// <summary>按页消费全部数据并写入一个模板工作簿，进度只在完整页面写入后报告。</summary>
    public async Task WriteLargeAsync(
        Stream destination,
        PocTemplateDataExportPlan plan,
        IAsyncEnumerable<IReadOnlyList<PocDataRow>> pages,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pages);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("大文件导出输出流必须可写。", nameof(destination));
        }
        if (!plan.Export.LargeExportEnabled)
        // 第一阶段：根据 WriteLargeAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            throw new InvalidOperationException("当前模板未启用大文件导出。");
        }
        var detail = plan.Binding.DetailBinding
            ?? throw new InvalidOperationException("模板没有明细绑定，不能执行大文件导出。");

        await using var templateStream = new MemoryStream();
        _exchangeConverter.Export(plan.Snapshot, plan.Mappings, templateStream);
        templateStream.Position = 0;
        using var templateArchive = new ZipArchive(templateStream, ZipArchiveMode.Read, leaveOpen: true);

        var sheetName = plan.Snapshot["sheets"]?[detail.SheetId]?["name"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            throw new InvalidOperationException($"导出模板找不到绑定工作表 '{detail.SheetId}'。");
        }
        var worksheetPath = ResolveWorksheetPath(templateArchive, sheetName);
        var worksheet = LoadXml(templateArchive, worksheetPath);
        var sharedStrings = LoadSharedStrings(templateArchive);

        if (destination.CanSeek)
        {
            destination.Position = 0;
            destination.SetLength(0);
        }
        using (var outputArchive = new ZipArchive(
                   destination,
                   ZipArchiveMode.Create,
                   leaveOpen: true,
                   Encoding.UTF8))
        {
            foreach (var entry in templateArchive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 第2阶段：检查 WriteLargeAsync 当前条件，并按匹配分支执行对应处理。
                if (string.Equals(entry.FullName, worksheetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                await CopyEntryAsync(entry, outputArchive, cancellationToken);
            }

            var worksheetEntry = outputArchive.CreateEntry(worksheetPath, CompressionLevel.Fastest);
            await using (var worksheetStream = worksheetEntry.Open())
            {
                await RewriteWorksheetAsync(
                    worksheetStream,
                    worksheet,
                    sharedStrings,
                    plan,
                    pages,
                    progress,
                    cancellationToken);
            }
        }

        // ZipArchive.Dispose 会写中央目录；必须在它完成后才能把可定位输出流重置到开头。
        if (destination.CanSeek)
        {
            destination.Position = 0;
        }
    }

    /// <summary>调用 PocStreamingTemplateDataExporter 的异步依赖完成 Rewrite Worksheet Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="output">output 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="worksheet">当前读取或修改的工作表对象。</param>
    /// <param name="sharedStrings">sharedStrings 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="pages">pages 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="progress">progress 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static async Task RewriteWorksheetAsync(
        Stream output,
        XDocument worksheet,
        IReadOnlyList<string> sharedStrings,
        PocTemplateDataExportPlan plan,
        IAsyncEnumerable<IReadOnlyList<PocDataRow>> pages,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var root = worksheet.Root
            ?? throw new InvalidDataException("模板工作表 XML 缺少根节点。");
        var sheetData = root.Element(SpreadsheetNamespace + "sheetData")
            ?? throw new InvalidDataException("模板工作表 XML 缺少 sheetData。");
        var detail = plan.Binding.DetailBinding!;
        var templateRowNumber = detail.TemplateRow + 1;
        var templateRow = sheetData.Elements(SpreadsheetNamespace + "row")
            .SingleOrDefault(row => ReadRowNumber(row) == templateRowNumber)
            ?? throw new InvalidDataException($"模板工作表缺少第 {templateRowNumber} 行明细模板。");
        // 第一阶段：遍历 RewriteWorksheetAsync 当前数据集合，逐项转换并维护处理结果。
        var headerRows = sheetData.Elements(SpreadsheetNamespace + "row")
            .Where(row => ReadRowNumber(row) < templateRowNumber)
            .ToArray();
        var tailRows = sheetData.Elements(SpreadsheetNamespace + "row")
            .Where(row => ReadRowNumber(row) > templateRowNumber)
            .ToArray();
        var parameterValues = ResolveParameterValues(plan);
        var parameterBindings = plan.Binding.ParameterBindings
            .Where(binding => binding.SheetId == detail.SheetId)
            .GroupBy(binding => (Row: binding.Row + 1, Column: binding.Column + 1))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var detailFields = detail.Fields.ToDictionary(field => field.Column + 1, field => field.Code);

        using var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Async = true,
            CloseOutput = false,
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false
        });
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, root.Name.LocalName, root.Name.NamespaceName);
        foreach (var attribute in root.Attributes().Where(attribute =>
                     attribute.IsNamespaceDeclaration &&
                     attribute.Name.LocalName != "xmlns"))
        {
            // mc:Ignorable 等兼容属性会在值中引用命名空间前缀；这些声明必须保留在根节点上。
            await writer.WriteAttributeStringAsync(
                "xmlns",
                attribute.Name.LocalName,
                XmlnsNamespace,
                attribute.Value);
        }
        foreach (var attribute in root.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            // 第2阶段：调用 RewriteWorksheetAsync 依赖的异步能力，等待并校验返回数据。
            await writer.WriteAttributeStringAsync(
                root.GetPrefixOfNamespace(attribute.Name.Namespace),
                attribute.Name.LocalName,
                attribute.Name.NamespaceName.Length == 0 ? null : attribute.Name.NamespaceName,
                attribute.Value);
        }

        foreach (var element in root.Elements().TakeWhile(element => element != sheetData))
        {
            // dimension 是提示性范围；省略后 Excel/WPS 会根据实际 sheetData 重新识别，避免先知道总行数。
            if (element.Name != SpreadsheetNamespace + "dimension")
            {
                element.WriteTo(writer);
            }
        }

        await writer.WriteStartElementAsync(null, "sheetData", SpreadsheetNamespace.NamespaceName);
        foreach (var header in headerRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateTemplateRow(
                header,
                ReadRowNumber(header),
                ReadRowNumber(header),
                sharedStrings,
                parameterBindings,
                parameterValues,
                null,
                null,
                null).WriteTo(writer);
        }

        long writtenRows = 0;
        await foreach (var page in pages.WithCancellation(cancellationToken))
        {
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 第3阶段：检查 RewriteWorksheetAsync 当前条件，并按匹配分支执行对应处理。
                if (writtenRows >= plan.Export.RowsPerSheet)
                {
                    throw new InvalidOperationException(
                        $"单工作表数据超过 {plan.Export.RowsPerSheet} 行；当前验证版本尚未启用自动拆分工作表。");
                }
                var targetRow = checked(templateRowNumber + (int)writtenRows);
                CreateTemplateRow(
                    templateRow,
                    templateRowNumber,
                    targetRow,
                    sharedStrings,
                    parameterBindings,
                    parameterValues,
                    detailFields,
                    row,
                    null).WriteTo(writer);
                writtenRows += 1;
            }
            progress?.Invoke(writtenRows);
        }

        if (writtenRows == 0)
        {
            CreateTemplateRow(
                templateRow,
                templateRowNumber,
                templateRowNumber,
                sharedStrings,
                parameterBindings,
                parameterValues,
                detailFields,
                null,
                null).WriteTo(writer);
        }

        var insertedRowCount = checked((int)Math.Max(0, writtenRows - 1));
        foreach (var tail in tailRows)
        // 第4阶段：根据 RewriteWorksheetAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = ReadRowNumber(tail);
            var targetRow = checked(sourceRow + insertedRowCount);
            CreateTemplateRow(
                tail,
                sourceRow,
                targetRow,
                sharedStrings,
                parameterBindings,
                parameterValues,
                null,
                null,
                (plan.Snapshot["sheets"]?[detail.SheetId]?["name"]?.Value<string>()!,
                    templateRowNumber,
                    insertedRowCount)).WriteTo(writer);
        }
        await writer.WriteEndElementAsync();

        foreach (var element in root.Elements().SkipWhile(element => element != sheetData).Skip(1))
        {
            var clone = new XElement(element);
            AdjustWorksheetReferences(clone, templateRowNumber, insertedRowCount);
            clone.WriteTo(writer);
        }
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    /// <summary>构建 PocStreamingTemplateDataExporter 所需的 Create Template Row 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="sourceRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="targetRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="sharedStrings">sharedStrings 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="parameterBindings">模板字段与工作簿坐标之间的绑定定义。</param>
    /// <param name="parameterValues">parameterValues 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="detailFields">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="detailRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="insertion">insertion 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static XElement CreateTemplateRow(
        XElement source,
        int sourceRow,
        int targetRow,
        IReadOnlyList<string> sharedStrings,
        IReadOnlyDictionary<(int Row, int Column), PocParameterBinding[]> parameterBindings,
        IReadOnlyDictionary<string, string> parameterValues,
        IReadOnlyDictionary<int, string>? detailFields,
        PocDataRow? detailRow,
        (string SheetName, int InsertAfterRow, int InsertedRowCount)? insertion)
    {
        var clone = new XElement(source);
        clone.SetAttributeValue("r", targetRow.ToString(CultureInfo.InvariantCulture));
        foreach (var cell in clone.Elements(SpreadsheetNamespace + "c"))
        {
            var sourceReference = cell.Attribute("r")?.Value
                ?? throw new InvalidDataException("模板单元格缺少 r 引用。");
            var parsedReference = ReferenceParser.ParseA1(sourceReference);
            // 第一阶段：构造 CreateTemplateRow 当前步骤需要的中间数据和执行参数。
            var column = parsedReference.First.ColumnValue;
            cell.SetAttributeValue("r", $"{ColumnName(column)}{targetRow}");

            if (parameterBindings.TryGetValue((sourceRow, column), out var bindings))
            {
                var text = ReadCellText(cell, sharedStrings);
                foreach (var binding in bindings)
                {
                    text = text.Replace(
                        $"{{{{params.{binding.Code}}}}}",
                        parameterValues[binding.Code],
                        StringComparison.Ordinal);
                }
                SetInlineText(cell, text);
            }

            if (detailFields is not null && detailFields.TryGetValue(column, out var code))
            {
                if (detailRow is null)
                {
                    ClearCellValue(cell);
                }
                else
                {
                    SetDetailValue(cell, code, detailRow);
                }
                continue;
            }

            var formula = cell.Element(SpreadsheetNamespace + "f");
            // 第2阶段：检查 CreateTemplateRow 当前条件，并按匹配分支执行对应处理。
            if (formula is null)
            {
                continue;
            }
            var adjusted = insertion is null
                ? PocFormulaTranslator.MoveFormula(
                    $"={formula.Value}",
                    sourceRow,
                    column,
                    targetRow,
                    column)
                : PocFormulaTranslator.InsertRows(
                    $"={formula.Value}",
                    insertion.Value.SheetName,
                    sourceRow,
                    column,
                    insertion.Value.SheetName,
                    insertion.Value.InsertAfterRow,
                    insertion.Value.InsertedRowCount);
            formula.Value = adjusted[1..];
            cell.Element(SpreadsheetNamespace + "v")?.Remove();
        }
        return clone;
    }

    /// <summary>更新 PocStreamingTemplateDataExporter 的 Set Detail Value 状态，并同步相关业务数据。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    private static void SetDetailValue(XElement cell, string code, PocDataRow row)
    {
        if (!row.TryGetValue(code, out var value))
        {
            throw new InvalidOperationException($"导出数据缺少明细字段 '{code}'。");
        }

        switch (value)
        {
            case null:
            case DBNull:
                ClearCellValue(cell);
                break;
            case string text:
                SetInlineText(cell, text);
                break;
            case bool boolean:
                SetBoolean(cell, boolean);
                break;
            case DateTime dateTime:
                SetNumber(cell, dateTime.ToOADate());
                break;
            case DateTimeOffset dateTimeOffset:
                SetNumber(cell, dateTimeOffset.DateTime.ToOADate());
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                SetNumber(cell, Convert.ToString(value, CultureInfo.InvariantCulture)!);
                break;
            default:
                throw new InvalidOperationException(
                    $"明细字段 '{code}' 的类型 '{value.GetType().Name}' 不能写入 Excel。");
        }
    }

    /// <summary>更新 PocStreamingTemplateDataExporter 的 Set Number 状态，并同步相关业务数据。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    private static void SetNumber(XElement cell, double value) =>
        SetNumber(cell, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>更新 PocStreamingTemplateDataExporter 的 Set Number 状态，并同步相关业务数据。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    private static void SetNumber(XElement cell, string value)
    {
        cell.SetAttributeValue("t", null);
        cell.RemoveNodes();
        cell.Add(new XElement(SpreadsheetNamespace + "v", value));
    }

    /// <summary>更新 PocStreamingTemplateDataExporter 的 Set Boolean 状态，并同步相关业务数据。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    private static void SetBoolean(XElement cell, bool value)
    {
        cell.SetAttributeValue("t", "b");
        cell.RemoveNodes();
        cell.Add(new XElement(SpreadsheetNamespace + "v", value ? "1" : "0"));
    }

    /// <summary>更新 PocStreamingTemplateDataExporter 的 Set Inline Text 状态，并同步相关业务数据。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    private static void SetInlineText(XElement cell, string value)
    {
        cell.SetAttributeValue("t", "inlineStr");
        cell.RemoveNodes();
        var text = new XElement(SpreadsheetNamespace + "t", value);
        if (value.Length > 0 &&
            (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
        {
            text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        }
        cell.Add(new XElement(SpreadsheetNamespace + "is", text));
    }

    /// <summary>移除 PocStreamingTemplateDataExporter 中与 Clear Cell Value 对应的数据，并清理关联状态。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void ClearCellValue(XElement cell)
    {
        cell.SetAttributeValue("t", null);
        cell.RemoveNodes();
    }

    /// <summary>查询或读取 PocStreamingTemplateDataExporter 所需的 Read Cell Text 数据，并按调用约定返回结果。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sharedStrings">sharedStrings 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ReadCellText(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "s")
        {
            var indexText = cell.Element(SpreadsheetNamespace + "v")?.Value;
            if (int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                index >= 0 && index < sharedStrings.Count)
            {
                return sharedStrings[index];
            }
            throw new InvalidDataException($"模板共享字符串索引 '{indexText}' 无效。");
        }
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value));
        }
        return cell.Element(SpreadsheetNamespace + "v")?.Value ?? string.Empty;
    }

    /// <summary>解析并计算 PocStreamingTemplateDataExporter 的 Resolve Parameter Values 结果，处理必要的边界条件。</summary>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static IReadOnlyDictionary<string, string> ResolveParameterValues(
        PocTemplateDataExportPlan plan)
    {
        var parameters = plan.Parameters
            ?? PocReportCatalog.Execute(plan.Query with { Page = 1 }).Parameters;
        return new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>遍历 PocStreamingTemplateDataExporter 当前数据并执行 Adjust Worksheet References 转换，生成调用方需要的结果集合。</summary>
    /// <param name="element">element 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="insertAfterRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="insertedRowCount">控制数据范围或位置的数值。</param>
    private static void AdjustWorksheetReferences(
        XElement element,
        int insertAfterRow,
        int insertedRowCount)
    {
        if (insertedRowCount <= 0)
        {
            return;
        }
        foreach (var attribute in element.DescendantsAndSelf().Attributes())
        {
            if (attribute.Name.LocalName == "ref")
            {
                attribute.Value = AdjustReference(attribute.Value, insertAfterRow, insertedRowCount);
            }
            else if (attribute.Name.LocalName == "sqref")
            {
                attribute.Value = string.Join(
                    ' ',
                    attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(reference => AdjustReference(
                            reference,
                            insertAfterRow,
                            insertedRowCount)));
            }
        }
    }

    /// <summary>根据 PocStreamingTemplateDataExporter 当前输入计算 Adjust Reference 结果，并按方法声明的类型返回。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="insertAfterRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="insertedRowCount">控制数据范围或位置的数值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string AdjustReference(
        string value,
        int insertAfterRow,
        int insertedRowCount)
    {
        ReferenceArea area;
        try
        {
            // 第一阶段：更新 AdjustReference 当前分支对应的业务状态，并保持关联数据一致。
            area = ReferenceParser.ParseA1(value);
        }
        catch (ParsingException)
        {
            return value;
        }

        var first = area.First;
        var second = area.Second;
        if (first.RowType == ReferenceAxisType.None)
        {
            return value;
        }
        var firstRow = first.RowValue;
        var secondRow = second.RowValue;
        var adjustedFirst = firstRow > insertAfterRow
            ? checked(firstRow + insertedRowCount)
            : firstRow;
        var adjustedSecond = secondRow > insertAfterRow
            ? checked(secondRow + insertedRowCount)
            : secondRow;
        // 第2阶段：检查 AdjustReference 当前条件，并按匹配分支执行对应处理。
        if (value.Contains(':') &&
            firstRow <= insertAfterRow &&
            secondRow <= insertAfterRow &&
            Math.Max(firstRow, secondRow) == insertAfterRow)
        {
            if (firstRow == secondRow || secondRow == insertAfterRow)
            {
                adjustedSecond = checked(secondRow + insertedRowCount);
            }
            else
            {
                adjustedFirst = checked(firstRow + insertedRowCount);
            }
        }
        return new ReferenceArea(
            CopyWithRow(first, adjustedFirst),
            CopyWithRow(second, adjustedSecond)).GetDisplayStringA1();
    }

    /// <summary>按照 PocStreamingTemplateDataExporter 的业务约束完成 Copy With Row 数据合并或复制。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static RowCol CopyWithRow(RowCol source, int row) =>
        new(source.RowType, row, source.ColumnType, source.ColumnValue, source.Style);

    /// <summary>查询或读取 PocStreamingTemplateDataExporter 所需的 Read Row Number 数据，并按调用约定返回结果。</summary>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static int ReadRowNumber(XElement row)
    {
        var value = row.Attribute("r")?.Value;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number < 1)
        {
            throw new InvalidDataException($"模板包含无效行号 '{value}'。");
        }
        return number;
    }

    /// <summary>查询或读取 PocStreamingTemplateDataExporter 所需的 Load Shared Strings 数据，并按调用约定返回结果。</summary>
    /// <param name="archive">archive 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static IReadOnlyList<string> LoadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry(SharedStringsPath);
        if (entry is null)
        {
            return Array.Empty<string>();
        }
        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.None);
        return document.Root?
            .Elements(SpreadsheetNamespace + "si")
            .Select(item => string.Concat(
                item.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value)))
            .ToArray() ?? Array.Empty<string>();
    }

    /// <summary>解析并计算 PocStreamingTemplateDataExporter 的 Resolve Worksheet Path 结果，处理必要的边界条件。</summary>
    /// <param name="archive">archive 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sheetName">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ResolveWorksheetPath(ZipArchive archive, string sheetName)
    {
        var workbook = LoadXml(archive, WorkbookPath);
        var sheet = workbook.Root?
            .Element(SpreadsheetNamespace + "sheets")?
            .Elements(SpreadsheetNamespace + "sheet")
            .SingleOrDefault(item => string.Equals(
                item.Attribute("name")?.Value,
                sheetName,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"模板工作簿找不到工作表 '{sheetName}'。");
        var relationshipId = sheet.Attribute(OfficeRelationshipsNamespace + "id")?.Value
            ?? throw new InvalidDataException($"模板工作表 '{sheetName}' 缺少关系标识。");
        var relationships = LoadXml(archive, WorkbookRelationshipsPath);
        var target = relationships.Root?
            .Elements(PackageRelationshipsNamespace + "Relationship")
            .SingleOrDefault(item => item.Attribute("Id")?.Value == relationshipId)?
            .Attribute("Target")?.Value
            ?? throw new InvalidDataException($"模板工作表 '{sheetName}' 缺少部件关系。");

        var normalized = target.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"xl/{normalized}";
    }

    /// <summary>查询或读取 PocStreamingTemplateDataExporter 所需的 Load Xml 数据，并按调用约定返回结果。</summary>
    /// <param name="archive">archive 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="path">path 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static XDocument LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"模板 XLSX 缺少部件 '{path}'。");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    /// <summary>按照 PocStreamingTemplateDataExporter 的业务约束完成 Copy Entry Async 数据合并或复制。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="destination">destination 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static async Task CopyEntryAsync(
        ZipArchiveEntry source,
        ZipArchive destination,
        CancellationToken cancellationToken)
    {
        var target = destination.CreateEntry(source.FullName, CompressionLevel.Fastest);
        await using var input = source.Open();
        await using var output = target.Open();
        await input.CopyToAsync(output, 128 * 1024, cancellationToken);
    }

    /// <summary>把零基列序号转换为 Excel 列名，供工作表单元格引用使用。</summary>
    /// <param name="number">number 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ColumnName(int number)
    {
        Span<char> buffer = stackalloc char[3];
        var index = buffer.Length;
        while (number > 0)
        {
            number -= 1;
            buffer[--index] = (char)('A' + number % 26);
            number /= 26;
        }
        return new string(buffer[index..]);
    }
}
