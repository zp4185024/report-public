// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Admin.NET.Core;

/// <summary>
/// JSON时间序列化yyyy-MM-dd HH:mm:ss
/// </summary>
public class ChinaDateTimeConverter : DateTimeConverterBase
{
    private static readonly IsoDateTimeConverter DtConverter = new() { DateTimeFormat = "yyyy-MM-dd HH:mm:ss" };

    /// <summary>查询或读取 ChinaDateTimeConverter 所需的 Read Json 数据，并按调用约定返回结果。</summary>
    /// <param name="reader">reader 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="objectType">objectType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="existingValue">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">serializer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        return DtConverter.ReadJson(reader, objectType, existingValue, serializer);
    }

    /// <summary>更新 ChinaDateTimeConverter 的 Write Json 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">serializer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        DtConverter.WriteJson(writer, value, serializer);
    }
}

/// <summary>
/// JSON时间序列化yyyy-MM-dd HH:mm
/// </summary>
public class ChinaDateTimeConverterHH : DateTimeConverterBase
{
    private static readonly IsoDateTimeConverter DtConverter = new() { DateTimeFormat = "yyyy-MM-dd HH:mm" };

    /// <summary>查询或读取 ChinaDateTimeConverter 所需的 Read Json 数据，并按调用约定返回结果。</summary>
    /// <param name="reader">reader 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="objectType">objectType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="existingValue">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">serializer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        return DtConverter.ReadJson(reader, objectType, existingValue, serializer);
    }

    /// <summary>更新 ChinaDateTimeConverter 的 Write Json 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">serializer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        DtConverter.WriteJson(writer, value, serializer);
    }
}

/// <summary>
/// JSON时间序列化yyyy-MM-dd
/// </summary>
public class ChinaDateTimeConverterDate : DateTimeConverterBase
{
    private static readonly IsoDateTimeConverter DtConverter = new() { DateTimeFormat = "yyyy-MM-dd" };

    /// <summary>查询或读取 ChinaDateTimeConverter 所需的 Read Json 数据，并按调用约定返回结果。</summary>
    /// <param name="reader">reader 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="objectType">objectType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="existingValue">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">serializer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        return DtConverter.ReadJson(reader, objectType, existingValue, serializer);
    }

    /// <summary>更新 ChinaDateTimeConverter 的 Write Json 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="serializer">serializer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        DtConverter.WriteJson(writer, value, serializer);
    }
}