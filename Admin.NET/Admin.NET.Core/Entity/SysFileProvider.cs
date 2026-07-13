// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Newtonsoft.Json;

namespace Admin.NET.Core;

/// <summary>
/// 系统文件存储提供者表
/// </summary>
[SugarTable(null, "系统文件存储提供者表")]
[SysTable]
[SugarIndex("index_{table}_BucketName", nameof(BucketName), OrderByType.Asc)]
[SugarIndex("index_{table}_IsEnable", nameof(IsEnable), OrderByType.Desc)]
[SugarIndex("index_{table}_IsDefault", nameof(IsDefault), OrderByType.Desc)]
public partial class SysFileProvider : EntityBaseTenant
{
    /// <summary>
    /// 存储提供者（Minio， QCloud，Aliyun 等等）
    /// </summary>
    [SugarColumn(ColumnDescription = "存储提供者", Length = 16)]
    [Required, MaxLength(16)]
    public virtual string Provider { get; set; }

    /// <summary>
    /// 存储桶名称
    /// </summary>
    [SugarColumn(ColumnDescription = "存储桶名称", Length = 32)]
    [Required, MaxLength(32)]
    public virtual string BucketName { get; set; }

    /// <summary>
    /// 访问密钥 （填入 阿里云（Aliyun）/Minio：的 AccessKey，腾讯云（QCloud）: 的 SecretId）
    /// </summary>
    [SugarColumn(ColumnDescription = "访问密钥", Length = 128)]
    [MaxLength(128)]
    public virtual string? AccessKey { get; set; }

    /// <summary>
    /// 密钥
    /// </summary>
    [SugarColumn(ColumnDescription = "密钥", Length = 128)]
    [MaxLength(128)]
    public virtual string? SecretKey { get; set; }

    /// <summary>
    /// 地域
    /// </summary>
    [SugarColumn(ColumnDescription = "地域", Length = 64)]
    [MaxLength(64)]
    public virtual string? Region { get; set; }

    /// <summary>
    /// 端点地址（填入 阿里云（Aliyun）/Minio：的 endpoint/Api address，腾讯云（QCloud）: 的 AppId）
    /// </summary>
    [SugarColumn(ColumnDescription = "端点地址", Length = 256)]
    [MaxLength(256)]
    public virtual string? Endpoint { get; set; }

    /// <summary>
    /// 是否启用HTTPS
    /// </summary>
    [SugarColumn(ColumnDescription = "是否启用HTTPS")]
    public virtual bool? IsEnableHttps { get; set; } = true;

    /// <summary>
    /// 是否启用缓存
    /// </summary>
    [SugarColumn(ColumnDescription = "是否启用缓存")]
    public virtual bool? IsEnableCache { get; set; } = true;

    /// <summary>
    /// 是否启用
    /// </summary>
    [SugarColumn(ColumnDescription = "是否启用")]
    public virtual bool? IsEnable { get; set; } = true;

    /// <summary>
    /// 是否默认提供者
    /// </summary>
    [SugarColumn(ColumnDescription = "是否默认提供者")]
    public virtual bool? IsDefault { get; set; } = false;

    /// <summary>
    /// 自定义域名
    /// </summary>
    [SugarColumn(ColumnDescription = "自定义域名", Length = 256)]
    [MaxLength(256)]
    public virtual string? SinceDomain { get; set; }

    /// <summary>
    /// 排序号
    /// </summary>
    [SugarColumn(ColumnDescription = "排序号")]
    public virtual int? OrderNo { get; set; } = 100;

    /// <summary>
    /// 备注
    /// </summary>
    [SugarColumn(ColumnDescription = "备注", Length = 512)]
    [MaxLength(512)]
    public virtual string? Remark { get; set; }

    /// <summary>
    /// 获取显示名称
    /// </summary>
    [SugarColumn(IsIgnore = true)]
    public virtual string DisplayName => $"{Provider}-{BucketName}";

    /// <summary>
    /// 获取配置键名
    /// </summary>
    [SugarColumn(IsIgnore = true)]
    public virtual string ConfigKey => $"{Provider}_{BucketName}_{Id}";
}