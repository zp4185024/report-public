// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using MiniExcelLibs;

namespace Admin.NET.Core;

public static class MiniExcelUtil
{
    private const string SheetName = "ImportTemplate";
    private const string DirectoryName = "export";

    /// <summary>
    /// 导出模板Excel
    /// </summary>
    /// <returns></returns>
    public static async Task<IActionResult> ExportExcelTemplate<T>(string fileName = null) where T : class, new()
    {
        var values = Array.Empty<T>();
        // 在内存中当开辟空间
        var memoryStream = new MemoryStream();
        // 将数据写到内存当中
        await memoryStream.SaveAsAsync(values, sheetName: SheetName);
        // 从0的位置开始写入
        memoryStream.Seek(0, SeekOrigin.Begin);
        return new FileStreamResult(memoryStream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
        {
            FileDownloadName = $"{(string.IsNullOrEmpty(fileName) ? typeof(T).Name : fileName)}.xlsx"
        };
    }

    /// <summary>
    /// 获取导入数据Excel
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public static async Task<IEnumerable<T>> GetImportExcelData<T>([Required] IFormFile file) where T : class, new()
    {
        using MemoryStream stream = new MemoryStream();
        await file.CopyToAsync(stream);
        var res = await stream.QueryAsync<T>(sheetName: SheetName);
        return res.ToArray();
    }

    /// <summary>
    /// 获取导出数据excel地址
    /// </summary>
    /// <returns></returns>
    public static async Task<string> GetExportDataExcelUrl<T>(IEnumerable<T> exportData) where T : class, new()
    {
        var fileName = string.Format("{0}.xlsx", YitIdHelper.NextId());
        try
        {
            var path = Path.Combine(App.WebHostEnvironment.WebRootPath, DirectoryName);
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            var filePath = Path.Combine(path, fileName);
            await MiniExcel.SaveAsAsync(filePath, exportData, overwriteFile: true);
        }
        catch (Exception error)
        {
            throw Oops.Oh("出现错误：" + error);
        }
        var host = CommonUtil.GetLocalhost();
        return $"{host}/{DirectoryName}/{fileName}";
    }
}