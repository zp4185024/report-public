// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

public static class ListExtensions
{
    /// <summary>调用 ListExtensions 的异步依赖完成 For Each Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="list">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="func">当前方法执行时使用的 func 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static async Task ForEachAsync<T>(this List<T> list, Func<T, Task> func)
    {
        foreach (var value in list)
        {
            await func(value);
        }
    }

    /// <summary>调用 ListExtensions 的异步依赖完成 For Each Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="action">当前方法执行时使用的 action 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static async Task ForEachAsync<T>(this IEnumerable<T> source, Func<T, Task> action)
    {
        foreach (var value in source)
        {
            await action(value);
        }
    }

    /// <summary>遍历 ListExtensions 当前数据并执行 For Each 转换，生成调用方需要的结果集合。</summary>
    /// <param name="enumerable">当前方法执行时使用的 enumerable 输入值。</param>
    /// <param name="consumer">当前方法执行时使用的 consumer 输入值。</param>
    public static void ForEach<T>(this IEnumerable<T> enumerable, Action<T> consumer)
    {
        foreach (T item in enumerable)
        {
            consumer(item);
        }
    }

    /// <summary>构建 ListExtensions 所需的 Add Range 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="list">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="items">需要遍历、筛选或写入的业务数据集合。</param>
    public static void AddRange<T>(this IList<T> list, IEnumerable<T> items)
    {
        if (list is List<T> list2)
        {
            list2.AddRange(items);
            return;
        }

        foreach (T item in items)
        {
            list.Add(item);
        }
    }
}