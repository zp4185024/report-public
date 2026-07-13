// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Microsoft.AspNetCore.Http;

namespace Admin.NET.Plugin.ApprovalFlow.Service;

public class SysApprovalService : ITransient
{
    private readonly SqlSugarRepository<ApprovalFlowRecord> _approvalFlowRep;
    private readonly SqlSugarRepository<ApprovalFormRecord> _approvalFormRep;
    private readonly ApprovalFlowService _approvalFlowService;

    public SysApprovalService(SqlSugarRepository<ApprovalFlowRecord> approvalFlowRep, SqlSugarRepository<ApprovalFormRecord> approvalFormRep, ApprovalFlowService approvalFlowService)
    {
        _approvalFlowRep = approvalFlowRep;
        _approvalFormRep = approvalFormRep;
        _approvalFlowService = approvalFlowService;
    }

    /// <summary>
    /// 匹配审批流程
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    [NonAction]
    public async Task MatchApproval(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;

        var path = request.Path.ToString().Split("/");

        var method = request.Method;
        var qs = request.QueryString;
        var h = request.Headers;
        var b = request.Body;

        var requestHeaders = request.Headers;
        var responseHeaders = response.Headers;

        var serviceName = path[1];
        if (serviceName.StartsWith("api"))
        {
            if (path.Length > 3)
            {
                var funcName = path[2];
                var typeName = path[3];

                var list = await _approvalFlowService.FormRoutes();
                if (list.Any(u => u.Contains(funcName) && u.Contains(typeName)))
                {
                    var approvalFlow = new ApprovalFlowRecord
                    {
                        FormName = funcName,
                        CreateTime = DateTime.Now,
                    };

                    // 判断是否需要审批
                    await _approvalFlowRep.InsertAsync(approvalFlow);

                    var approvalForm = new ApprovalFormRecord
                    {
                        FlowId = approvalFlow.Id,
                        FormName = funcName,
                        FormType = typeName,
                        CreateTime = DateTime.Now,
                    };

                    // 判断是否需要审批
                    await _approvalFormRep.InsertAsync(approvalForm);
                }
            }
        }

        await Task.CompletedTask;
    }
}