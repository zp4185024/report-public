// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 枚举值合规性校验特性
/// </summary>
[SuppressSniffer]
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Enum | AttributeTargets.Field, AllowMultiple = true)]
public class EnumAttribute : ValidationAttribute, ITransient
{
    /// <summary>
    /// 枚举值合规性校验特性
    /// </summary>
    /// <param name="errorMessage">需要记录、展示或继续传递的错误上下文。</param>
    public EnumAttribute(string errorMessage = "枚举值不合法！")
    {
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// 枚举值合规性校验
    /// </summary>
    /// <param name="value">需要读取、校验、比较或转换的业务值。</param>
    /// <param name="validationContext">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    protected override ValidationResult IsValid(object value, ValidationContext validationContext)
    {
        // 获取属性的类型
        var property = validationContext.ObjectType.GetProperty(validationContext.MemberName);
        if (property == null)
            return new ValidationResult($"未知属性: {validationContext.MemberName}");

        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        // 检查属性类型是否为枚举或可空枚举类型
        if (!propertyType.IsEnum)
            return new ValidationResult($"属性类型'{validationContext.MemberName}'不是有效的枚举类型！");

        // 检查枚举值是否有效
        if (value == null && Nullable.GetUnderlyingType(property.PropertyType) == null)
            return new ValidationResult($"提示：{ErrorMessage}|枚举值不能为 null！");

        if (value != null && !Enum.IsDefined(propertyType, value))
            return new ValidationResult($"提示：{ErrorMessage}|枚举值【{value}】不是有效的【{propertyType.Name}】枚举类型值！");

        return ValidationResult.Success;
    }
}