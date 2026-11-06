using Furion.FriendlyException;
using HD.ReportCenter.ReportEngine.CoreValidation;
using System.Data.Common;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 数据同步任务 API。控制器只负责 HTTP 契约与错误映射：
/// 任务 CRUD/试跑/运行/日志、结构元数据（表/列清单）与源取数预览、
/// 同步包导出/校验/试跑/导入，业务规则下沉到服务层。
/// </summary>
[AllowAnonymous]
[ApiDescriptionSettings("报表中心验证", Name = "ReportCenterDataSync", Order = 902)]
public sealed class PocDataSyncApi : IDynamicApiController
{
    private readonly PocDataSyncService? _syncService;
    private readonly PocDataSyncPackageService? _packageService;

    /// <summary>初始化 PocDataSyncApi 实例，保存后续处理所需的服务依赖。</summary>
    /// <param name="syncService">同步任务应用服务，缺失时任务接口返回 503。</param>
    /// <param name="packageService">同步包服务，缺失时包接口返回 503。</param>
    public PocDataSyncApi(
        PocDataSyncService? syncService = null,
        PocDataSyncPackageService? packageService = null)
    {
        _syncService = syncService;
        _packageService = packageService;
    }

    /// <summary>列出全部同步任务及其映射定义。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/data-sync-tasks")]
    public async Task<IReadOnlyList<PocSyncTaskDocument>> Tasks() =>
        await RequireSyncService().ListAsync();

    /// <summary>按编码读取单个同步任务及其映射定义。</summary>
    /// <param name="code">任务编码。</param>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/data-sync-tasks/{code}")]
    public async Task<PocSyncTaskDocument> GetTask([FromRoute] string code) =>
        await RequireSyncService().GetAsync(code);

    /// <summary>新增或更新同步任务；映射与调度在保存时完成编译级校验。</summary>
    /// <param name="code">任务编码。</param>
    /// <param name="request">任务保存输入（含乐观并发期望修订号）。</param>
    [SuppressMonitor]
    [HttpPut("/api/report-center/poc/data-sync-tasks/{code}")]
    public async Task<PocSyncTaskDocument> SaveTask(
        [FromRoute] string code,
        [FromBody] PocSyncTaskInput request) =>
        await HandleSyncOperation(() => RequireSyncService().SaveAsync(code, request));

    /// <summary>删除同步任务；运行日志保留作执行审计。</summary>
    /// <param name="code">任务编码。</param>
    [SuppressMonitor]
    [HttpDelete("/api/report-center/poc/data-sync-tasks/{code}")]
    public async Task<PocDeletedDocument> DeleteTask([FromRoute] string code) =>
        await HandleSyncOperation(() => RequireSyncService().DeleteAsync(code));

    /// <summary>
    /// 列出数据源连接库内的基础表名（仅 TABLE 类型，不含视图），供设计器源取数选表与目标表下拉。
    /// 只读元数据查询；数据源不存在/停用或连接失败按稳定错误码返回。
    /// </summary>
    /// <param name="dataSourceCode">数据源编码。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表清单文档。</returns>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/data-sync-tasks/metadata/tables")]
    public async Task<PocSyncTableMetadataDocument> MetadataTables(
        [FromQuery] string? dataSourceCode,
        CancellationToken cancellationToken) =>
        await HandleSyncOperation(
            () => RequireSyncService().ListSourceTablesAsync(dataSourceCode, cancellationToken));

    /// <summary>
    /// 读取目标表的真实列清单（列名/数据库类型/可空/长度/注释），供映射表格的目标字段下拉、
    /// 类型推断与列注释展示。注释按方言读取数据库数据字典，取不到为 null；表不存在时返回
    /// SYNC_METADATA_TABLE_NOT_FOUND；只读查询不写库。
    /// </summary>
    /// <param name="dataSourceCode">数据源编码。</param>
    /// <param name="table">表名。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>列清单文档（含注释）。</returns>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/data-sync-tasks/metadata/columns")]
    public async Task<PocSyncTableColumnsDocument> MetadataColumns(
        [FromQuery] string? dataSourceCode,
        [FromQuery] string? table,
        CancellationToken cancellationToken) =>
        await HandleSyncOperation(
            () => RequireSyncService().GetTableColumnsAsync(dataSourceCode, table, cancellationToken));

    /// <summary>
    /// 源取数预览（任务保存前可用）：对“数据源 + SQL”做真实受限查询（≤50 行，不写库），
    /// 返回动态列与样例行，供映射表格在任务尚未落库时取到真实源字段。SQL 必须通过只读校验。
    /// 请求可选携带 SourceTable（选表模式）：按列名匹配数据库注释填充字段 description，
    /// 写 SQL 模式不传，description 为 null。
    /// </summary>
    /// <param name="request">源取数预览请求（数据源编码 + SQL 文本 + 可选源表名）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>动态列与样例行（字段含可选注释）。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/data-sync-tasks/preview-source")]
    public async Task<PocDatasetPreviewResult> PreviewSource(
        [FromBody] PocSyncSourcePreviewRequest? request,
        CancellationToken cancellationToken) =>
        await HandleSyncOperation(
            () => RequireSyncService().PreviewSourceAsync(request, cancellationToken));

    /// <summary>预览源数据集的真实列与样例行（不做映射转换），供映射画布拉线。</summary>
    /// <param name="code">任务编码。</param>
    /// <param name="request">可选的数据集变量取值。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/data-sync-tasks/{code}/preview-mapping")]
    public async Task<PocDatasetPreviewResult> PreviewMapping(
        [FromRoute] string code,
        [FromBody] PocDatasetPreviewRequest? request,
        CancellationToken cancellationToken) =>
        await HandleSyncOperation(
            () => RequireSyncService().PreviewMappingAsync(code, request, cancellationToken));

    /// <summary>端到端试跑：真实读源并按映射转换（最多 50 行），不写目标库。</summary>
    /// <param name="code">任务编码。</param>
    /// <param name="request">试跑请求（行数上限与变量取值）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/data-sync-tasks/{code}/try-run")]
    public async Task<PocDataSyncTryRunResult> TryRun(
        [FromRoute] string code,
        [FromBody] PocDataSyncTryRunRequest? request,
        CancellationToken cancellationToken) =>
        await HandleSyncOperation(
            () => RequireSyncService().TryRunAsync(code, request, cancellationToken));

    /// <summary>启动后台同步并立即返回运行编号；防重入由 Running 日志占位保证。</summary>
    /// <param name="code">任务编码。</param>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/data-sync-tasks/{code}/run")]
    public async Task<PocSyncRunLogDocument> RunTask([FromRoute] string code) =>
        await HandleSyncOperation(() => RequireSyncService().RunAsync(code));

    /// <summary>按任务编码列出最近的运行日志（倒序）。</summary>
    /// <param name="code">任务编码。</param>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/data-sync-tasks/{code}/runs")]
    public async Task<IReadOnlyList<PocSyncRunLogDocument>> TaskRuns([FromRoute] string code) =>
        await HandleSyncOperation(() => RequireSyncService().ListRunLogsAsync(code));

    /// <summary>
    /// 把已保存的同步任务（映射 + 调度 + 源数据集快照 + 目标描述）聚合导出为同步包 JSON；
    /// 包内不含连接串/密码。任务或其依赖的数据源缺失时返回业务错误。
    /// </summary>
    /// <param name="code">任务编码。</param>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/data-sync-tasks/{code}/package")]
    public async Task<PocDataSyncPackage> ExportTaskPackage([FromRoute] string code) =>
        await HandleSyncOperation(() => RequirePackageService().ExportAsync(code));

    /// <summary>只读校验上传的同步包并探测与当前库的冲突；响应不写库、不执行 SQL。</summary>
    /// <param name="request">校验请求（携带整份同步包）。</param>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/data-sync-packages/validate")]
    public async Task<PocDataSyncValidateResult> ValidatePackage(
        [FromBody] PocDataSyncValidateRequest request) =>
        await HandleSyncOperation(() => RequirePackageService().ValidateAsync(request?.Package));

    /// <summary>在选定的目标数据源上受限试跑同步包（最多 50 行真实查询 + 映射转换，不写目标库）。</summary>
    /// <param name="request">试跑请求：包 + 目标数据源 + 变量取值。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/data-sync-packages/dry-run")]
    public async Task<PocDataSyncTryRunResult> DryRunPackage(
        [FromBody] PocDataSyncDryRunRequest request,
        CancellationToken cancellationToken) =>
        await HandleSyncOperation(
            () => RequirePackageService().DryRunAsync(request, cancellationToken));

    /// <summary>导入同步包：服务端重跑全部校验后按冲突策略落库（数据集新建/复用/替换，任务 Create/Replace）。</summary>
    /// <param name="request">导入请求：包 + 目标数据源 + 模式。</param>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/data-sync-packages/import")]
    public async Task<PocDataSyncImportResult> ImportPackage(
        [FromBody] PocDataSyncImportRequest request) =>
        await HandleSyncOperation(() => RequirePackageService().ImportAsync(request));

    /// <summary>获取同步任务服务；依赖未注册时返回稳定的 503 业务错误而不是空引用。</summary>
    /// <returns>可用的同步任务服务实例。</returns>
    private PocDataSyncService RequireSyncService() =>
        _syncService
        ?? throw Oops.Oh("DATA_SYNC_SERVICE_NOT_AVAILABLE").StatusCode(503);

    /// <summary>获取同步包服务；依赖未注册时返回稳定的 503 业务错误而不是空引用。</summary>
    /// <returns>可用的同步包服务实例。</returns>
    private PocDataSyncPackageService RequirePackageService() =>
        _packageService
        ?? throw Oops.Oh("DATA_SYNC_PACKAGE_SERVICE_NOT_AVAILABLE").StatusCode(503);

    /// <summary>
    /// 同步接口的统一异常翻译：领域错误按错误码区分 404（任务不存在）、409（任务已存在、
    /// 任务运行中、数据集被引用）与 400；同时覆盖编排触达的既有数据资产/执行层异常。
    /// </summary>
    /// <param name="operation">待执行的同步操作委托。</param>
    /// <returns>操作结果，异常时抛出 FriendlyException。</returns>
    private static async Task<T> HandleSyncOperation<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        // 第一层：同步领域自身的业务拒绝码。任务不存在按 404；撞码/运行中/数据集被占用按 409；其余按 400。
        catch (PocDataSyncException exception)
        {
            var statusCode = exception.Code switch
            {
                "SYNC_TASK_NOT_FOUND" => 404,
                "SYNC_TASK_ALREADY_EXISTS" or
                "SYNC_TASK_ALREADY_RUNNING" or
                "SYNC_DATASET_IN_USE" => 409,
                _ => 400
            };
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(statusCode);
        }
        // 第二层：单行转换失败在服务层已被记入失败明细，冒泡到此属于编译/配置缺陷，按 400 返回。
        catch (PocDataSyncTransformException exception)
        {
            throw Oops.Oh($"SYNC_TRANSFORM_FAILED: {exception.Message}").StatusCode(400);
        }
        // 第三层：乐观并发冲突。保存用期望修订号匹配，只有并发改动才会触发，统一按 409 返回。
        catch (PocDataSyncRevisionConflictException)
        {
            throw Oops.Oh("SYNC_TASK_REVISION_CONFLICT").StatusCode(409);
        }
        // 第四层：编排触达的既有领域异常，翻译逻辑与配置/数据集入口保持一致。
        catch (PocDataAssetRevisionConflictException)
        {
            throw Oops.Oh("DATA_ASSET_REVISION_CONFLICT").StatusCode(409);
        }
        catch (PocResourceInUseException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(409);
        }
        catch (PocParameterResolutionException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.ParameterCode}").StatusCode(400);
        }
        catch (PocDatasetConfigurationException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(400);
        }
        catch (PocDatasetExecutionException exception)
        {
            throw Oops.Oh(exception.Code).StatusCode(400);
        }
        catch (KeyNotFoundException exception)
        {
            // 导出目标在读取瞬间被删除时按 404 处理，与 REPORT_NOT_FOUND 语义一致。
            throw Oops.Oh($"SYNC_TASK_NOT_FOUND: {exception.Message}").StatusCode(404);
        }
        catch (DbException)
        {
            throw Oops.Oh("DATA_SOURCE_CONNECTION_FAILED").StatusCode(400);
        }
        catch (SqlSugarException)
        {
            throw Oops.Oh("DATA_SOURCE_CONNECTION_FAILED").StatusCode(400);
        }
    }
}
