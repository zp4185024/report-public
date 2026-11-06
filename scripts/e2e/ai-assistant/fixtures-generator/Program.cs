using ClosedXML.Excel;

// 阶段四 S7 夹具生成器：产出「表头乱序 + 改名列 + 合并标题行」的参考 Excel，
// 供 AI 助手 E2E 验证"模板列名列序对齐参考"——生成一次入库，E2E 直接引用固定文件。
var workbook = new XLWorkbook();
var worksheet = workbook.Worksheets.Add("门诊费用汇总");

// 行 1：合并标题（探测标题与合并摘要）；行 2：留空（表头探测须跳过）。
worksheet.Cell(1, 1).Value = "凌河区人民医院 6 月门诊费用汇总表";
worksheet.Range("A1:D1").Merge();

// 行 3：表头——列序刻意与标准骨架相反（类别→单据数→金额→科室），且"科室"改名为"就诊科室"。
worksheet.Cell(3, 1).Value = "费用类别";
worksheet.Cell(3, 2).Value = "单据数";
worksheet.Cell(3, 3).Value = "金额合计";
worksheet.Cell(3, 4).Value = "就诊科室";

// 行 4~9：样例数据（数值列真数值，便于类型倾向推断）。
var rows = new (string Category, int Count, decimal Amount, string Dept)[]
{
    ("西药费", 42, 1286.50m, "内科"),
    ("检查费", 18, 2350.00m, "外科"),
    ("治疗费", 25, 1780.25m, "儿科"),
    ("化验费", 31, 962.75m, "妇科"),
    ("材料费", 9, 421.10m, "骨科"),
    ("其他", 4, 88.60m, "五官科"),
};
for (var index = 0; index < rows.Length; index++)
{
    worksheet.Cell(4 + index, 1).Value = rows[index].Category;
    worksheet.Cell(4 + index, 2).Value = rows[index].Count;
    worksheet.Cell(4 + index, 3).Value = rows[index].Amount;
    worksheet.Cell(4 + index, 4).Value = rows[index].Dept;
}

// 行 10：合计行（探测 totalsHint）。
worksheet.Cell(10, 1).Value = "合计";
worksheet.Cell(10, 2).Value = rows.Sum(row => row.Count);
worksheet.Cell(10, 3).Value = rows.Sum(row => row.Amount);

var target = Path.Combine(args.Length > 0 ? args[0] : ".", "费用参考.xlsx");
workbook.SaveAs(target);
Console.WriteLine($"夹具已生成：{target}");
