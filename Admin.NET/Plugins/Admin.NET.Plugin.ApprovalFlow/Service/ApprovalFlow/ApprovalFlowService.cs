// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using System.Text.Json;

namespace Admin.NET.Plugin.ApprovalFlow.Service;

/// <summary>
/// 审批流程服务
/// </summary>
[ApiDescriptionSettings(ApprovalFlowConst.GroupName, Order = 100)]
public class ApprovalFlowService : IDynamicApiController, ITransient
{
    private readonly SqlSugarRepository<ApprovalFlow> _approvalFlowRep;

    /// <summary>初始化 ApprovalFlowService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="approvalFlowRep">approvalFlowRep 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public ApprovalFlowService(SqlSugarRepository<ApprovalFlow> approvalFlowRep)
    {
        _approvalFlowRep = approvalFlowRep;
    }

    /// <summary>调用 ApprovalFlowService 的异步依赖完成 Page 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [HttpPost]
    [ApiDescriptionSettings(Name = "Page")]
    public async Task<SqlSugarPagedList<ApprovalFlowOutput>> Page(ApprovalFlowInput input)
    {
        return await _approvalFlowRep.AsQueryable()
            .WhereIF(!string.IsNullOrWhiteSpace(input.Keyword), u => u.Code.Contains(input.Keyword.Trim()) || u.Name.Contains(input.Keyword.Trim()) || u.Remark.Contains(input.Keyword.Trim()))
            .WhereIF(!string.IsNullOrWhiteSpace(input.Code), u => u.Code.Contains(input.Code.Trim()))
            .WhereIF(!string.IsNullOrWhiteSpace(input.Name), u => u.Name.Contains(input.Name.Trim()))
            .WhereIF(!string.IsNullOrWhiteSpace(input.Remark), u => u.Remark.Contains(input.Remark.Trim()))
            .Select<ApprovalFlowOutput>()
            .ToPagedListAsync(input.Page, input.PageSize);
    }

    /// <summary>构建 ApprovalFlowService 所需的 Add 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Add"), HttpPost]
    public async Task<long> Add(AddApprovalFlowInput input)
    {
        var entity = input.Adapt<ApprovalFlow>();
        if (input.Code == null)
        {
            entity.Code = await LastCode("");
        }
        await _approvalFlowRep.InsertAsync(entity);
        return entity.Id;
    }

    /// <summary>更新 ApprovalFlowService 的 Update 状态，并同步相关业务数据。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Update"), HttpPost]
    public async Task Update(UpdateApprovalFlowInput input)
    {
        var entity = input.Adapt<ApprovalFlow>();
        await _approvalFlowRep.AsUpdateable(entity).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommandAsync();
    }

    /// <summary>移除 ApprovalFlowService 中与 Delete 对应的数据，并清理关联状态。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Delete"), HttpPost]
    public async Task Delete(DeleteApprovalFlowInput input)
    {
        var entity = await _approvalFlowRep.GetByIdAsync(input.Id) ?? throw Oops.Oh(ErrorCodeEnum.D1002);
        await _approvalFlowRep.FakeDeleteAsync(entity);  // 假删除
    }

    /// <summary>查询或读取 ApprovalFlowService 所需的 Get Detail 数据，并按调用约定返回结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<ApprovalFlow> GetDetail([FromQuery] QueryByIdApprovalFlowInput input)
    {
        return await _approvalFlowRep.GetByIdAsync(input.Id);
    }

    /// <summary>
    /// 根据编码获取审批流信息
    /// </summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<ApprovalFlow> GetInfo([FromQuery] string code)
    {
        return await _approvalFlowRep.GetFirstAsync(u => u.Code == code);
    }

    /// <summary>查询或读取 ApprovalFlowService 所需的 Get List 数据，并按调用约定返回结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<List<ApprovalFlowOutput>> GetList([FromQuery] ApprovalFlowInput input)
    {
        return await _approvalFlowRep.AsQueryable().Select<ApprovalFlowOutput>().ToListAsync();
    }

    /// <summary>
    /// 获取今天创建的最大编号
    /// </summary>
    /// <param name="prefix">prefix 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task<string> LastCode(string prefix)
    {
        var today = DateTime.Now.Date;
        var count = await _approvalFlowRep.AsQueryable().Where(u => u.CreateTime >= today).CountAsync();
        return prefix + DateTime.Now.ToString("yyMMdd") + string.Format("{0:d2}", count + 1);
    }

    /// <summary>调用 ApprovalFlowService 的异步依赖完成 Flow List 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [HttpGet]
    [ApiDescriptionSettings(Name = "FlowList")]
    [DisplayName("获取审批流结构")]
    public async Task<dynamic> FlowList([FromQuery] string code)
    {
        var result = await _approvalFlowRep.AsQueryable().Where(u => u.Code == code).Select<ApprovalFlowOutput>().FirstAsync();
        var FlowJson = result.FlowJson != null ? JsonSerializer.Deserialize<ApprovalFlowItem>(result.FlowJson) : new ApprovalFlowItem();
        var FormJson = result.FormJson != null ? JsonSerializer.Deserialize<ApprovalFormItem>(result.FormJson) : new ApprovalFormItem();
        return new
        {
            FlowJson,
            FormJson
        };
    }

    /// <summary>调用 ApprovalFlowService 的异步依赖完成 Form Routes 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [HttpGet]
    [ApiDescriptionSettings(Name = "FormRoutes")]
    [DisplayName("获取审批流规则")]
    public async Task<List<string>> FormRoutes()
    {
        var results = await _approvalFlowRep.AsQueryable().Select<ApprovalFlowOutput>().ToListAsync();
        var list = new List<string>();
        foreach (var item in results)
        {
            var FormJson = item.FormJson != null ? JsonSerializer.Deserialize<ApprovalFormItem>(item.FormJson) : new ApprovalFormItem();
            if (item.FormJson != null) list.Add(FormJson.Route);
        }
        return list;
    }
}