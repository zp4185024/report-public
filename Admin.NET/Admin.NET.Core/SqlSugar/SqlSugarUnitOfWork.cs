// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// SqlSugar 事务和工作单元
/// </summary>
public sealed class SqlSugarUnitOfWork : IUnitOfWork
{
    /// <summary>
    /// SqlSugar 对象
    /// </summary>
    private readonly ISqlSugarClient _sqlSugarClient;

    /// <summary>初始化 SqlSugarUnitOfWork 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="sqlSugarClient">sqlSugarClient 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public SqlSugarUnitOfWork(ISqlSugarClient sqlSugarClient)
    {
        _sqlSugarClient = sqlSugarClient;
    }

    /// <summary>
    /// 开启工作单元处理
    /// </summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="unitOfWork">unitOfWork 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <exception cref="NotImplementedException"></exception>
    public async Task BeginTransactionAsync(FilterContext context, UnitOfWorkAttribute unitOfWork)
    {
        await _sqlSugarClient.AsTenant().BeginTranAsync();
    }

    /// <summary>
    /// 提交工作单元处理
    /// </summary>
    /// <param name="resultContext">本次操作共享的运行上下文。</param>
    /// <param name="unitOfWork">unitOfWork 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <exception cref="NotImplementedException"></exception>
    public async Task CommitTransactionAsync(FilterContext resultContext, UnitOfWorkAttribute unitOfWork)
    {
        await _sqlSugarClient.AsTenant().CommitTranAsync();
    }

    /// <summary>
    /// 回滚工作单元处理
    /// </summary>
    /// <param name="resultContext">本次操作共享的运行上下文。</param>
    /// <param name="unitOfWork">unitOfWork 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <exception cref="NotImplementedException"></exception>
    public async Task RollbackTransactionAsync(FilterContext resultContext, UnitOfWorkAttribute unitOfWork)
    {
        await _sqlSugarClient.AsTenant().RollbackTranAsync();
    }

    /// <summary>
    /// 执行完毕（无论成功失败）
    /// </summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="resultContext">本次操作共享的运行上下文。</param>
    /// <exception cref="NotImplementedException"></exception>
    public Task OnCompletedAsync(FilterContext context, FilterContext resultContext)
    {
        _sqlSugarClient.Dispose();
        return Task.CompletedTask;
    }
}