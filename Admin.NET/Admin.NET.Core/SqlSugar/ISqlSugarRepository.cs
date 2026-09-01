// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 分表操作仓储接口
/// </summary>
/// <typeparam name="T"></typeparam>
public interface ISqlSugarRepository<T> : ISugarRepository, ISimpleClient<T> where T : class, new()
{
    /// <summary>负责 ISqlSugarRepository 的 Split Table Insert Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<bool> SplitTableInsertAsync(T input);

    /// <summary>负责 ISqlSugarRepository 的 Split Table Insert Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<bool> SplitTableInsertAsync(List<T> input);

    /// <summary>负责 ISqlSugarRepository 的 Split Table Update Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<bool> SplitTableUpdateAsync(T input);

    /// <summary>负责 ISqlSugarRepository 的 Split Table Update Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<bool> SplitTableUpdateAsync(List<T> input);

    /// <summary>负责 ISqlSugarRepository 的 Split Table Deleteable Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<bool> SplitTableDeleteableAsync(T input);

    /// <summary>负责 ISqlSugarRepository 的 Split Table Deleteable Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<bool> SplitTableDeleteableAsync(List<T> input);

    /// <summary>负责 ISqlSugarRepository 的 Split Table Get First Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="whereExpression">whereExpression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<T> SplitTableGetFirstAsync(Expression<Func<T, bool>> whereExpression);

    /// <summary>负责 ISqlSugarRepository 的 Split Table Is Any Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="whereExpression">whereExpression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<bool> SplitTableIsAnyAsync(Expression<Func<T, bool>> whereExpression);

    /// <summary>负责 ISqlSugarRepository 的 Split Table Get List Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<List<T>> SplitTableGetListAsync();

    /// <summary>负责 ISqlSugarRepository 的 Split Table Get List Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="whereExpression">whereExpression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<List<T>> SplitTableGetListAsync(Expression<Func<T, bool>> whereExpression);

    /// <summary>负责 ISqlSugarRepository 的 Split Table Get List Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="whereExpression">whereExpression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="tableNames">表名</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task<List<T>> SplitTableGetListAsync(Expression<Func<T, bool>> whereExpression, string[] tableNames);
}