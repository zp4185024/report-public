// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.DingTalk.Service;

/// <summary>
/// 钉钉服务 🧩
/// </summary>
[ApiDescriptionSettings(DingTalkConst.GroupName, Order = 100)]
public class DingTalkService : IDynamicApiController, IScoped
{
    private readonly IDingTalkApi _dingTalkApi;
    private readonly DingTalkOptions _dingTalkOptions;
    private readonly SqlSugarRepository<DingTalkWokerflowLog> _dingTalkWokerflowLogRep;

    /// <summary>初始化 DingTalkService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="dingTalkApi">dingTalkApi 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="dingTalkOptions">控制本次处理行为的配置选项。</param>
    /// <param name="dingTalkWokerflowLogRep">dingTalkWokerflowLogRep 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public DingTalkService(
        IDingTalkApi dingTalkApi,
        IOptions<DingTalkOptions> dingTalkOptions,
        SqlSugarRepository<DingTalkWokerflowLog> dingTalkWokerflowLogRep
    )
    {
        _dingTalkApi = dingTalkApi;
        _dingTalkOptions = dingTalkOptions.Value;
        _dingTalkWokerflowLogRep = dingTalkWokerflowLogRep;
    }

    /// <summary>
    /// 获取企业内部应用的access_token
    /// </summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取企业内部应用的access_token")]
    public async Task<GetDingTalkTokenOutput> GetDingTalkToken()
    {
        var tokenRes = await _dingTalkApi.GetDingTalkToken(
            _dingTalkOptions.ClientId,
            _dingTalkOptions.ClientSecret
        );
        if (tokenRes.ErrCode != 0)
        {
            throw Oops.Oh(tokenRes.ErrMsg);
        }
        return tokenRes;
    }

    /// <summary>
    /// 获取在职员工列表 🔖
    /// </summary>
    /// <param name="access_token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [HttpPost, DisplayName("获取在职员工列表")]
    public async Task<
        DingTalkBaseResponse<GetDingTalkCurrentEmployeesListOutput>
    > GetDingTalkCurrentEmployeesList(
        string access_token,
        [Required] GetDingTalkCurrentEmployeesListInput input
    )
    {
        return await _dingTalkApi.GetDingTalkCurrentEmployeesList(access_token, input);
    }

    /// <summary>
    /// 获取员工花名册字段信息 🔖
    /// </summary>
    /// <param name="access_token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [HttpPost, DisplayName("获取员工花名册字段信息")]
    public async Task<
        DingTalkBaseResponse<List<DingTalkEmpRosterFieldVo>>
    > GetDingTalkCurrentEmployeesRosterList(
        string access_token,
        [Required] GetDingTalkCurrentEmployeesRosterListInput input
    )
    {
        return await _dingTalkApi.GetDingTalkCurrentEmployeesRosterList(access_token, input);
    }

    /// <summary>
    /// 发送钉钉互动卡片 🔖
    /// </summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("给指定用户发送钉钉互动卡片")]
    [Obsolete]
    public async Task<DingTalkSendInteractiveCardsOutput> DingTalkSendInteractiveCards(
        string token,
        DingTalkSendInteractiveCardsInput input
    )
    {
        return await _dingTalkApi.DingTalkSendInteractiveCards(token, input);
    }

    /// <summary>
    /// 创建并投放钉钉消息卡片 🔖
    /// </summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("给指定用户发送钉钉消息卡片")]
    public async Task<DingTalkCreateAndDeliverOutput> DingTalkCreateAndDeliver(
        string token,
        DingTalkCreateAndDeliverInput input
    )
    {
        return await _dingTalkApi.DingTalkCreateAndDeliver(token, input);
    }

    /// <summary>调用 DingTalkService 的异步依赖完成 Ding Talk Workflow Process Instances 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("用于发起OA审批实例")]
    public async Task<DingTalkWorkflowProcessInstancesOutput> DingTalkWorkflowProcessInstances(
        string token,
        DingTalkWorkflowProcessInstancesInput input
    )
    {
        var temp = await _dingTalkApi.DingTalkWorkflowProcessInstances(token, input);
        return temp;
    }

    /// <summary>调用 DingTalkService 的异步依赖完成 Ding Talk Workflow Process Instances 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("查询审批实例")]
    public async Task<DingTalkGetProcessInstancesOutput> DingTalkWorkflowProcessInstances(
        string token,
        string input
    )
    {
        var temp = await _dingTalkApi.GetProcessInstances(token, input);
        DingTalkWokerflowLog flow = await _dingTalkWokerflowLogRep.GetFirstAsync(t =>
            t.Status == "RUNNING" && t.instanceId == input
        );

        if ((flow != null) && (temp.Result.Status != flow.Status))
        {
            flow.Status = temp.Result.Status;
            flow.UpdateTime = DateTime.Now;
            flow.WorkflowId = temp.Result.BusinessId;
            flow.Result = temp.Result.Result;
            flow.taskId = temp.Result.Tasks.FirstOrDefault(t => t.Status == "RUNNING")?.TaskId;
            await _dingTalkWokerflowLogRep.UpdateAsync(flow);
        }
        return temp;
    }
}