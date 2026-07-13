// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 通用接口参数验证特性类
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class CommonValidationAttribute : ValidationAttribute
{
    private readonly Dictionary<string, string> _conditions;
    private static readonly Dictionary<string, Delegate> CompiledConditions = new();

    /// <summary>
    /// </summary>
    /// <param name="conditionPairs">条件对参数，长度必须为偶数<br/>
    /// 奇数字符串参数：动态条件<br/>
    /// 偶数字符串参数：提示消息
    /// </param>
    /// <example>
    /// <code lang="C">
    /// public class ModelInput {
    ///
    ///
    ///     public string A { get; set; }
    ///
    ///
    ///     [CommonValidation(
    ///         "A == 1 <value>&amp;&amp;</value> B == null", "当 A == 1时，B不能为空",
    ///         "C == 2 <value>&amp;&amp;</value> B == null", "当 C == 2时，B不能为空"
    ///     )]
    ///     public string B { get; set; }
    /// }
    /// </code>
    /// </example>
    public CommonValidationAttribute(params string[] conditionPairs)
    {
        if (conditionPairs.Length % 2 != 0) throw new ArgumentException("条件对必须以偶数个字符串的形式提供。");

        var conditions = new Dictionary<string, string>();
        for (int i = 0; i < conditionPairs.Length; i += 2)
            conditions.Add(conditionPairs[i], conditionPairs[i + 1]);

        _conditions = conditions;
    }

    protected override ValidationResult IsValid(object value, ValidationContext validationContext)
    {
        foreach (var (expr, errorMessage) in _conditions)
        {
            var conditionKey = $"{validationContext.ObjectType.FullName}.{expr}";
            if (!CompiledConditions.TryGetValue(conditionKey, out var condition))
            {
                condition = CreateCondition(validationContext.ObjectType, expr);
                CompiledConditions[conditionKey] = condition;
            }

            if ((bool)condition.DynamicInvoke(validationContext.ObjectInstance)!)
            {
                return new ValidationResult(errorMessage ?? $"[{validationContext.MemberName}]校验失败");
            }
        }

        return ValidationResult.Success;
    }

    private Delegate CreateCondition(Type modelType, string expression)
    {
        try
        {
            // 创建参数表达式
            var parameter = Expression.Parameter(typeof(object), "x");

            // 构建 Lambda 表达式
            var lambda = DynamicExpressionParser.ParseLambda(new[] { Expression.Parameter(modelType, "x") }, typeof(bool), expression);

            // 创建新的 Lambda 表达式，接受 object 参数并调用编译后的表达式
            var invokeExpression = Expression.Invoke(lambda, Expression.Convert(parameter, modelType));
            var finalLambda = Expression.Lambda<Func<object, bool>>(invokeExpression, parameter);

            return finalLambda.Compile();
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"无法解析表达式 '{expression}': {ex.Message}", ex);
        }
    }
}