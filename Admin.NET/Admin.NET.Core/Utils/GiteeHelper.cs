// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// Gitee接口帮助类
/// </summary>
public class GiteeHelper
{
    private const string BaseUrl = "https://gitee.com/api/v5/repos/";
    private static readonly HttpClient Client = new();

    /// <summary>
    /// 下载仓库 zip
    /// </summary>
    /// <remarks>https://gitee.com/api/v5/swagger#/getV5ReposOwnerRepoZipball</remarks>
    /// <returns></returns>
    public static async Task<Stream> DownloadRepoZip(string owner, string repo, string accessToken = null, string @ref = null)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw Oops.Bah($"参数 {nameof(owner)} 不能为空");
        if (string.IsNullOrWhiteSpace(repo)) throw Oops.Bah($"参数 {nameof(repo)} 不能为空");
        var query = BuilderQueryString(new
        {
            access_token = accessToken,
            @ref
        });
        return await Client.GetStreamAsync($"{BaseUrl}{owner}/{repo}/zipball?{query}");
    }

    /// <summary>
    /// 构建Query参数
    /// </summary>
    /// <returns></returns>
    private static string BuilderQueryString([System.Diagnostics.CodeAnalysis.NotNull] object obj)
    {
        if (obj == null) return string.Empty;
        var query = HttpUtility.ParseQueryString(string.Empty);
        foreach (var prop in obj.GetType().GetProperties())
        {
            var val = prop.GetValue(obj);
            if (val == null) continue;

            // 以元组形式校验参数集
            var name = prop.Name.Trim('@');
            if (val is Tuple<object, string> { Item1: not null } tuple)
            {
                if (!tuple.Item2.Split(",").Any(x => x.Trim().Equals(tuple.Item1))) throw Oops.Oh($"参数 {name} 的值只能为：{tuple.Item2}");
                query[name] = tuple.Item1.ToString();
                continue;
            }
            query[name] = val.ToString();
        }
        return query.ToString();
    }
}