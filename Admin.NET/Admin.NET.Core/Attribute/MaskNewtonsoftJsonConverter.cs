// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Newtonsoft.Json;

namespace Admin.NET.Core;

/// <summary>
/// 字符串掩码
/// </summary>
[SuppressSniffer]
public class MaskNewtonsoftJsonConverter : JsonConverter<string>
{
    /// <summary>查询或读取 MaskNewtonsoftJsonConverter 所需的 Read Json 数据，并按调用约定返回结果。</summary>
    /// <param name="reader">当前方法执行时使用的 reader 输入值。</param>
    /// <param name="objectType">决定处理分支、数据类型或运行状态的枚举值。</param>
    /// <param name="existingValue">需要读取、校验或转换的业务值。</param>
    /// <param name="hasExistingValue">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">当前方法执行时使用的 serializer 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public override string ReadJson(JsonReader reader, Type objectType, string existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        return reader.Value.ToString();
    }

    /// <summary>更新 MaskNewtonsoftJsonConverter 的 Write Json 状态，并同步相关业务数据。</summary>
    /// <param name="writer">当前方法执行时使用的 writer 输入值。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">当前方法执行时使用的 serializer 输入值。</param>
    public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer)
    {
        writer.WriteValue(value?.ToString().Mask());
    }
}

/// <summary>
/// 身份证掩码
/// </summary>
[SuppressSniffer]
public class MaskIdCardNewtonsoftJsonConverter : JsonConverter<string>
{
    /// <summary>查询或读取 MaskNewtonsoftJsonConverter 所需的 Read Json 数据，并按调用约定返回结果。</summary>
    /// <param name="reader">当前方法执行时使用的 reader 输入值。</param>
    /// <param name="objectType">决定处理分支、数据类型或运行状态的枚举值。</param>
    /// <param name="existingValue">需要读取、校验或转换的业务值。</param>
    /// <param name="hasExistingValue">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">当前方法执行时使用的 serializer 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public override string ReadJson(JsonReader reader, Type objectType, string existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        return reader.Value.ToString();
    }

    /// <summary>更新 MaskNewtonsoftJsonConverter 的 Write Json 状态，并同步相关业务数据。</summary>
    /// <param name="writer">当前方法执行时使用的 writer 输入值。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">当前方法执行时使用的 serializer 输入值。</param>
    public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer)
    {
        writer.WriteValue(value?.ToString().MaskIdCard());
    }
}

/// <summary>
/// 邮箱掩码
/// </summary>
[SuppressSniffer]
public class MaskEmailNewtonsoftJsonConverter : JsonConverter<string>
{
    /// <summary>查询或读取 MaskNewtonsoftJsonConverter 所需的 Read Json 数据，并按调用约定返回结果。</summary>
    /// <param name="reader">当前方法执行时使用的 reader 输入值。</param>
    /// <param name="objectType">决定处理分支、数据类型或运行状态的枚举值。</param>
    /// <param name="existingValue">需要读取、校验或转换的业务值。</param>
    /// <param name="hasExistingValue">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">当前方法执行时使用的 serializer 输入值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public override string ReadJson(JsonReader reader, Type objectType, string existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        return reader.Value.ToString();
    }

    /// <summary>更新 MaskNewtonsoftJsonConverter 的 Write Json 状态，并同步相关业务数据。</summary>
    /// <param name="writer">当前方法执行时使用的 writer 输入值。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">当前方法执行时使用的 serializer 输入值。</param>
    public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer)
    {
        writer.WriteValue(value?.ToString().MaskEmail());
    }
}