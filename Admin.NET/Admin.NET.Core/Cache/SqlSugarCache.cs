// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// SqlSugar二级缓存
/// </summary>
public class SqlSugarCache : ICacheService
{
    /// <summary>
    /// 系统缓存服务
    /// </summary>
    private static readonly SysCacheService _cache = App.GetRequiredService<SysCacheService>();

    /// <summary>构建 SqlSugarCache 所需的 Add 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    public void Add<V>(string key, V value)
    {
        _cache.Set($"{CacheConst.SqlSugar}{key}", value);
    }

    /// <summary>构建 SqlSugarCache 所需的 Add 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="cacheDurationInSeconds">当前方法执行时使用的 cacheDurationInSeconds 输入值。</param>
    public void Add<V>(string key, V value, int cacheDurationInSeconds)
    {
        _cache.Set($"{CacheConst.SqlSugar}{key}", value, TimeSpan.FromSeconds(cacheDurationInSeconds));
    }

    /// <summary>尝试或判断 SqlSugarCache 的 Contains Key 条件，并以明确结果表示是否匹配。</summary>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public bool ContainsKey<V>(string key)
    {
        return _cache.ExistKey($"{CacheConst.SqlSugar}{key}");
    }

    /// <summary>查询或读取 SqlSugarCache 所需的 Get 数据，并按调用约定返回结果。</summary>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public V Get<V>(string key)
    {
        return _cache.Get<V>($"{CacheConst.SqlSugar}{key}");
    }

    /// <summary>查询或读取 SqlSugarCache 所需的 Get All Key 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public IEnumerable<string> GetAllKey<V>()
    {
        return _cache.GetKeysByPrefixKey(CacheConst.SqlSugar);
    }

    /// <summary>查询或读取 SqlSugarCache 所需的 Get Or Create 数据，并按调用约定返回结果。</summary>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <param name="create">当前方法执行时使用的 create 输入值。</param>
    /// <param name="cacheDurationInSeconds">当前方法执行时使用的 cacheDurationInSeconds 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public V GetOrCreate<V>(string key, Func<V> create, int cacheDurationInSeconds = int.MaxValue)
    {
        return _cache.GetOrAdd<V>($"{CacheConst.SqlSugar}{key}", (cacheKey) =>
        {
            return create();
        }, cacheDurationInSeconds);
    }

    /// <summary>移除 SqlSugarCache 中与 Remove 对应的数据，并清理关联状态。</summary>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    public void Remove<V>(string key)
    {
        _cache.Remove(key); // SqlSugar调用Remove方法时，key中已包含了CacheConst.SqlSugar前缀
    }
}