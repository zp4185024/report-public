using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Admin.NET.Core;

/// <summary>
/// 租户头部参数过滤器
/// </summary>
public class TenantHeaderOperationFilter : IOperationFilter
{
    /// <summary>
    /// 应用租户头部参数过滤器
    /// </summary>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= [];

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = ClaimConst.TenantId,
            In = ParameterLocation.Header,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            Required = false,
            AllowEmptyValue = true,
            Description = "租户ID（留空表示默认租户）"
        });
    }
}