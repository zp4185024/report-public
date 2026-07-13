// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 百度翻译结果
/// </summary>
public class BaiDuTranslationResult
{
    /// <summary>
    /// 源语种
    /// </summary>
    public string From { get; set; }

    /// <summary>
    /// 目标语种
    /// </summary>
    public string To { get; set; }

    /// <summary>
    /// 翻译结果
    /// </summary>
    public List<TransResult> trans_result { get; set; }

    /// <summary>
    /// 错误码 正常为0
    /// </summary>
    public string error_code { get; set; } = "0";

    /// <summary>
    /// 错误信息
    /// </summary>
    public string error_msg { get; set; } = String.Empty;
}

/// <summary>
/// 翻译结果
/// </summary>
public class TransResult
{
    /// <summary>
    /// 源字符
    /// </summary>
    public string Src { get; set; }

    /// <summary>
    /// 目标字符
    /// </summary>
    public string Dst { get; set; } = string.Empty;
}