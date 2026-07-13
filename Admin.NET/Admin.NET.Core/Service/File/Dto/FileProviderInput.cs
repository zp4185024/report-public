// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 文件存储提供者分页查询输入参数
/// </summary>
public class PageFileProviderInput : BasePageInput
{
    /// <summary>
    /// 存储提供者
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// 存储桶名称
    /// </summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool? IsEnable { get; set; }
}

/// <summary>
/// 增加文件存储提供者输入参数
/// </summary>
public class AddFileProviderInput
{
    /// <summary>
    /// 存储提供者
    /// </summary>
    [Required(ErrorMessage = "存储提供者不能为空")]
    public string Provider { get; set; }

    /// <summary>
    /// 存储桶名称
    /// </summary>
    [Required(ErrorMessage = "存储桶名称不能为空")]
    public string BucketName { get; set; }

    /// <summary>
    /// 访问密钥ID（所有云服务商统一使用此字段）
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>
    /// 密钥
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// 地域
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// 端点地址
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// 是否启用HTTPS
    /// </summary>
    public bool? IsEnableHttps { get; set; } = true;

    /// <summary>
    /// 是否启用缓存
    /// </summary>
    public bool? IsEnableCache { get; set; } = true;

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool? IsEnable { get; set; } = true;

    /// <summary>
    /// 是否默认提供者
    /// </summary>
    public bool? IsDefault { get; set; } = false;

    /// <summary>
    /// 自定义域名
    /// </summary>
    public string? SinceDomain { get; set; }

    /// <summary>
    /// 排序号
    /// </summary>
    public int? OrderNo { get; set; } = 100;

    /// <summary>
    /// 备注
    /// </summary>
    public string? Remark { get; set; }

    /// <summary>
    /// 支持的业务类型（JSON格式）
    /// </summary>
    public string? BusinessTypes { get; set; }

    /// <summary>
    /// 优先级
    /// </summary>
    public int Priority { get; set; } = 100;
}

/// <summary>
/// 更新文件存储提供者输入参数
/// </summary>
public class UpdateFileProviderInput : AddFileProviderInput
{
    /// <summary>
    /// 主键Id
    /// </summary>
    [Required(ErrorMessage = "主键Id不能为空")]
    public long Id { get; set; }
}

/// <summary>
/// 删除文件存储提供者输入参数
/// </summary>
public class DeleteFileProviderInput : BaseIdInput
{
}

/// <summary>
/// 查询文件存储提供者输入参数
/// </summary>
public class QueryFileProviderInput : BaseIdInput
{
}

/// <summary>
/// 测试连接输入参数
/// </summary>
public class TestConnectionInput : BaseIdInput
{
}

/// <summary>
/// 设置默认存储提供者输入参数
/// </summary>
public class SetDefaultProviderInput
{
    /// <summary>
    /// 存储提供者ID
    /// </summary>
    [Required(ErrorMessage = "存储提供者ID不能为空")]
    public long Id { get; set; }
}

/// <summary>
/// 文件上传选择存储提供者输入参数
/// </summary>
public class SelectProviderInput
{
    /// <summary>
    /// 文件类型
    /// </summary>
    public string? FileType { get; set; }

    /// <summary>
    /// 业务类型
    /// </summary>
    public string? BusinessType { get; set; }

    /// <summary>
    /// 指定提供者ID
    /// </summary>
    public long? ProviderId { get; set; }

    /// <summary>
    /// 指定存储桶名称
    /// </summary>
    public string? BucketName { get; set; }
}