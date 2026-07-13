// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 文件帮助类
/// </summary>
public static class FileHelper
{
    /// <summary>
    /// 尝试删除文件/目录
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public static bool TryDelete(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            // ignored
            return false;
        }
    }

    /// <summary>
    /// 复制目录
    /// </summary>
    /// <param name="sourceDir"></param>
    /// <param name="destinationDir"></param>
    /// <param name="overwrite"></param>
    public static void CopyDirectory(string sourceDir, string destinationDir, bool overwrite = false)
    {
        // 检查源目录是否存在
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException("Source directory not found: " + sourceDir);

        // 如果目标目录不存在，则创建它
        if (!Directory.Exists(destinationDir)) Directory.CreateDirectory(destinationDir!);

        // 获取源目录下的所有文件并复制它们
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string name = Path.GetFileName(file);
            string dest = Path.Combine(destinationDir, name);
            File.Copy(file, dest, overwrite);
        }

        // 递归复制所有子目录
        foreach (string directory in Directory.GetDirectories(sourceDir))
        {
            string name = Path.GetFileName(directory);
            string dest = Path.Combine(destinationDir, name);
            CopyDirectory(directory, dest, overwrite);
        }
    }

    /// <summary>
    /// 在文件倒数第lastIndex个identifier前插入内容（备份原文件）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="insertContent">要插入的内容</param>
    /// <param name="identifier">标识符号</param>
    /// <param name="lastIndex">倒数第几个标识符</param>
    /// <param name="createBackup">是否创建备份文件</param>
    public static async Task InsertsStringAtSpecifiedLocationInFile(string filePath, string insertContent, char identifier, int lastIndex, bool createBackup = false)
    {
        // 参数校验
        if (lastIndex < 1) throw new ArgumentOutOfRangeException(nameof(lastIndex));
        if (identifier == 0) throw new ArgumentException("标识符不能为空字符");

        if (!File.Exists(filePath))
            throw new FileNotFoundException("目标文件不存在", filePath);

        // 创建备份文件
        if (createBackup)
        {
            string backupPath = $"{filePath}.bak_{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(filePath, backupPath, true);
        }

        using var reader = new StreamReader(filePath, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        reader.Close();
        // 逆向查找算法
        int index = content.LastIndexOf(identifier);
        if (index == -1)
        {
            throw new ArgumentException($"文件中未包含{identifier}");
        }

        int resIndex = content.LastIndexOf(identifier, index - lastIndex);
        if (resIndex == -1)
        {
            throw new ArgumentException($"文件中{identifier}不足{lastIndex}个");
        }

        StringBuilder sb = new StringBuilder(content);
        sb = sb.Insert(resIndex, insertContent);
        await WriteToFileAsync(filePath, sb);
    }

    /// <summary>
    /// 写入文件内容
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="sb"></param>
    public static async Task WriteToFileAsync(string filePath, StringBuilder sb)
    {
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        await using var writer = new StreamWriter(filePath, false, new UTF8Encoding(false)); // 无BOM
        await writer.WriteAsync(sb.ToString());
        writer.Close();
        Console.WriteLine($"文件【{filePath}】写入完成");
        Console.ResetColor();
    }
}