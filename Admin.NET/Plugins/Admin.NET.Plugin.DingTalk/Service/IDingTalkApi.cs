// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.DingTalk;

public interface IDingTalkApi : IHttpDeclarative
{
    /// <summary>
    /// 获取企业内部应用的access_token
    /// </summary>
    /// <param name="appkey">应用的唯一标识key</param>
    /// <param name="appsecret"> 应用的密钥。AppKey和AppSecret可在钉钉开发者后台的应用详情页面获取。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Get("https://oapi.dingtalk.com/gettoken")]
    Task<GetDingTalkTokenOutput> GetDingTalkToken([QueryParam] string appkey, [QueryParam] string appsecret);

    /// <summary>
    /// 获取在职员工列表
    /// </summary>
    /// <param name="access_token">调用该接口的应用凭证</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("https://oapi.dingtalk.com/topapi/smartwork/hrm/employee/queryonjob")]
    Task<
        DingTalkBaseResponse<GetDingTalkCurrentEmployeesListOutput>
    > GetDingTalkCurrentEmployeesList(
        [QueryParam] string access_token,
        [Body(ContentType = "application/json", UseStringContent = true), Required]
            GetDingTalkCurrentEmployeesListInput input
    );

    /// <summary>
    /// 获取员工花名册字段信息
    /// </summary>
    /// <param name="access_token">调用该接口的应用凭证</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("https://oapi.dingtalk.com/topapi/smartwork/hrm/employee/v2/list")]
    Task<
        DingTalkBaseResponse<List<DingTalkEmpRosterFieldVo>>
    > GetDingTalkCurrentEmployeesRosterList(
        [QueryParam] string access_token,
        [Body(ContentType = "application/json", UseStringContent = true), Required]
            GetDingTalkCurrentEmployeesRosterListInput input
    );

    /// <summary>
    /// 发送钉钉互动卡片
    /// </summary>
    /// <param name="token">调用该接口的访问凭证</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    /// <remarks>
    /// 钉钉官方文档显示接口不再支持新应用接入, 已接入的应用可继续调用
    /// 推荐更新接口https://open.dingtalk.com/document/orgapp/create-and-deliver-cards?spm=ding_open_doc.document.0.0.67fc50988Pf0mc
    /// </remarks>
    [Post("https://api.dingtalk.com/v1.0/im/interactiveCards/send")]
    [Obsolete]
    Task<DingTalkSendInteractiveCardsOutput> DingTalkSendInteractiveCards(
        [Header("x-acs-dingtalk-access-token")] string token,
        [Body(ContentType = "application/json", UseStringContent = true)]
            DingTalkSendInteractiveCardsInput input
    );

    /// <summary>
    /// 获取钉钉卡片消息读取状态
    /// </summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Get("https://api.dingtalk.com/v1.0/robot/oToMessages/readStatus")]
    Task<GetDingTalkCardMessageReadStatusOutput> GetDingTalkCardMessageReadStatus(
        [Header("x-acs-dingtalk-access-token")] string token,
        [QueryParam] GetDingTalkCardMessageReadStatusInput input
    );

    /// <summary>查询或读取 IDingTalkApi 所需的 Get Ding Talk Role List 数据，并按调用约定返回结果。</summary>
    /// <param name="access_token">调用该接口的应用凭证</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("https://oapi.dingtalk.com/topapi/role/list")]
    Task<DingTalkBaseResponse<DingTalkRoleListOutput>> GetDingTalkRoleList(
        [QueryParam] string access_token,
        [Body(ContentType = "application/json", UseStringContent = true), Required]
            GetDingTalkCurrentRoleListInput input
    );

    /// <summary>
    /// 获取指定角色的员工列表
    /// </summary>
    /// <param name="access_token">调用该接口的应用凭证</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("https://oapi.dingtalk.com/topapi/role/simplelist")]
    Task<DingTalkBaseResponse<DingTalkRoleSimplelistOutput>> GetDingTalkRoleSimplelist(
        [QueryParam] string access_token,
        [Body(ContentType = "application/json", UseStringContent = true), Required]
            GetDingTalkCurrentRoleSimplelistInput input
    );

    /// <summary>
    /// 创建并投放钉钉消息卡片
    /// </summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("https://api.dingtalk.com/v1.0/card/instances/createAndDeliver")]
    Task<DingTalkCreateAndDeliverOutput> DingTalkCreateAndDeliver(
        [Header("x-acs-dingtalk-access-token")] string token,
        [Body(ContentType = "application/json", UseStringContent = true)]
            DingTalkCreateAndDeliverInput input
    );

    /// <summary>
    /// 获取部门列表列表
    /// </summary>
    /// <param name="access_token">调用该接口的应用凭证</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("https://oapi.dingtalk.com/topapi/v2/department/listsub")]
    Task<DingTalkBaseResponse<List<DingTalkDeptOutput>>> GetDingTalkDept(
        [QueryParam] string access_token,
        [Body(ContentType = "application/json", UseStringContent = true), Required]
            GetDingTalkDeptInput input
    );

    /// <summary>负责 IDingTalkApi 的 Ding Talk Workflow Process Instances 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("https://api.dingtalk.com/v1.0/workflow/processInstances")]
    Task<DingTalkWorkflowProcessInstancesOutput> DingTalkWorkflowProcessInstances(
        [Header("x-acs-dingtalk-access-token")] string token,
        [Body(ContentType = "application/json", UseStringContent = true), Required]
            DingTalkWorkflowProcessInstancesInput input
    );

    /// <summary>查询或读取 IDingTalkApi 所需的 Get Process Instances 数据，并按调用约定返回结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="processInstanceId">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Get("https://api.dingtalk.com/v1.0/workflow/processInstances")]
    Task<DingTalkGetProcessInstancesOutput> GetProcessInstances(
        [Header("x-acs-dingtalk-access-token")] string token,
        [QueryParam] string processInstanceId
    );
}