// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Newtonsoft.Json;

namespace Admin.NET.Core;

/// <summary>
/// 自定义序列化提供器 Newtonsoft.Json 实现
/// </summary>
public class NewtonsoftJsonSerializerProvider : IJsonSerializerProvider, ISingleton
{
    /// <summary>将 NewtonsoftJsonSerializerProvider 中的 Serialize 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="jsonSerializerOptions">控制本次处理行为的配置选项。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public string Serialize(object value, object jsonSerializerOptions = null)
    {
        return JsonConvert.SerializeObject(value, (jsonSerializerOptions ?? GetSerializerOptions()) as JsonSerializerSettings);
    }

    /// <summary>将 NewtonsoftJsonSerializerProvider 中的 Deserialize 输入转换为调用方需要的稳定格式。</summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="json">json 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="jsonSerializerOptions">控制本次处理行为的配置选项。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public T Deserialize<T>(string json, object jsonSerializerOptions = null)
    {
        return JsonConvert.DeserializeObject<T>(json, (jsonSerializerOptions ?? GetSerializerOptions()) as JsonSerializerSettings);
    }

    /// <summary>将 NewtonsoftJsonSerializerProvider 中的 Deserialize 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="json">json 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="returnType">returnType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="jsonSerializerOptions">控制本次处理行为的配置选项。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public object Deserialize(string json, Type returnType, object jsonSerializerOptions = null)
    {
        return JsonConvert.DeserializeObject(json, returnType, (jsonSerializerOptions ?? GetSerializerOptions()) as JsonSerializerSettings);
    }

    /// <summary>
    /// 返回读取全局配置的 JSON 选项
    /// </summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public object GetSerializerOptions()
    {
        return App.GetOptions<MvcNewtonsoftJsonOptions>()?.SerializerSettings;
    }
}