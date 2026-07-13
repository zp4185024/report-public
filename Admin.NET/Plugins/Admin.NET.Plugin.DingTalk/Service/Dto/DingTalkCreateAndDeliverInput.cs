// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.DingTalk;

public class DingTalkCreateAndDeliverInput
{
    /// <summary>
    /// 卡片创建者的userId
    /// </summary>
    public string? userId { get; set; }

    /// <summary>
    /// 卡片内容模板ID
    /// </summary>
    [Required]
    public string cardTemplateId { get; set; }

    /// <summary>
    /// 外部卡片实例Id
    /// </summary>
    [Required]
    public string outTrackId { get; set; }

    /// <summary>
    /// 卡片回调的类型：STREAM：stream模式 HTTP：http模式
    /// </summary>
    public string? callbackType { get; set; }

    /// <summary>
    /// 卡片回调HTTP模式时的路由 Key，用于查询注册的 callbackUrl。
    /// </summary>
    public string? callbackRouteKey { get; set; }

    /// <summary>
    /// 卡片数据
    /// </summary>
    [Required]
    public DingTalk_CardData cardData { get; set; }

    /// <summary>
    /// 用户的私有数据
    /// </summary>
    public PrivateData? crivateData { get; set; }

    /// <summary>
    /// 动态数据源配置
    /// </summary>
    public OpenDynamicDataConfig? openDynamicDataConfig { get; set; }

    /// <summary>
    /// IM单聊酷应用场域信息
    /// </summary>
    public OpenSpaceModel? imSingleOpenSpaceModel { get; set; }

    /// <summary>
    /// IM群聊场域信息。
    /// </summary>
    public OpenSpaceModel? imGroupOpenSpaceModel { get; set; }

    /// <summary>
    /// IM机器人单聊场域信息。
    /// </summary>
    public OpenSpaceModel? imRobotOpenSpaceModel { get; set; }

    /// <summary>
    /// 协作场域信息
    /// </summary>
    public OpenSpaceModel? coFeedOpenSpaceModel { get; set; }

    /// <summary>
    /// 吊顶场域信息
    /// </summary>
    public OpenSpaceModel? topOpenSpaceModel { get; set; }

    /// <summary>
    /// 表示场域及其场域id
    /// </summary>
    /// <remarks>
    /// 其格式为dtv1.card//spaceType1.spaceId1;spaceType2.spaceId2_1;spaceType2.spaceId2_2;spaceType3.spaceId3
    /// </remarks>
    [Required]
    public string openSpaceId { get; set; }

    /// <summary>
    /// 单聊酷应用场域投放参数。
    /// </summary>
    public DingTalkOpenDeliverModel? imSingleOpenDeliverModel { get; set; }

    /// <summary>
    /// 群聊投放参数。
    /// </summary>
    public DingTalkOpenDeliverModel? imGroupOpenDeliverModel { get; set; }

    /// <summary>
    /// IM机器人单聊投放参数。
    /// </summary>
    public DingTalkOpenDeliverModel? imRobotOpenDeliverModel { get; set; }

    /// <summary>
    /// 吊顶投放参数。
    /// </summary>
    public DingTalkOpenDeliverModel? topOpenDeliverModel { get; set; }

    /// <summary>
    /// 协作投放参数。
    /// </summary>
    public DingTalkOpenDeliverModel? coFeedOpenDeliverModel { get; set; }

    /// <summary>
    /// 文档投放参数
    /// </summary>
    public DingTalkOpenDeliverModel? docOpenDeliverModel { get; set; }

    /// <summary>
    /// 用户userId类型:1（默认）：userId模式 2：unionId模式
    /// </summary>
    public int UserIdType { get; set; }
}

public class DingTalk_CardData
{
    public DingTalk_CardParamMap cardParamMap { get; set; }
}

/// <summary>
/// 卡片模板内容替换参数
/// </summary>
public class DingTalk_CardParamMap
{
    /// <summary>
    /// 片模板内容替换参数
    /// </summary>
    [Newtonsoft.Json.JsonProperty("sys_full_json_obj")]
    [System.Text.Json.Serialization.JsonPropertyName("sys_full_json_obj")]
    public string sysFullJsonObj { get; set; }
}

public class PrivateData
{
    public Dictionary<string, DingTalk_CardParamMap> key { get; set; } = new Dictionary<string, DingTalk_CardParamMap>();
}

public class OpenDynamicDataConfig
{
    /// <summary>
    /// 动态数据源配置列表。
    /// </summary>
    public List<DynamicDataSourceConfig>? dynamicDataSourceConfigs { get; set; }
}

public class DynamicDataSourceConfig
{
    /// <summary>
    /// 数据源的唯一 ID, 调用方指定。
    /// </summary>
    public string? dynamicDataSourceId { get; set; }

    /// <summary>
    /// 回调数据源时回传的固定参数。 示例
    /// </summary>
    public Dictionary<string, string>? constParams { get; set; }

    /// <summary>
    /// 数据源拉取配置。
    /// </summary>
    public PullConfig? pullConfig { get; set; }
}

public class PullConfig
{
    /// <summary>
    /// 拉取策略，可选值：NONE：不拉取，无动态数据  INTERVAL：间隔拉取ONCE：只拉取一次
    /// </summary>
    public string pullStrategy { get; set; }

    /// <summary>
    /// 拉取的间隔时间。
    /// </summary>
    public int interval { get; set; }

    /// <summary>
    /// 拉取的间隔时间的单位， 可选值：SECONDS：秒 MINUTES：分钟 HOURS：小时 DAYS：天
    /// </summary>
    public string timeUnit { get; set; }
}

public class OpenSpaceModel
{
    /// <summary>
    /// 吊顶场域属性，通过增加spaeType使卡片支持吊顶场域。
    /// </summary>
    public string? spaceType { get; set; }

    /// <summary>
    /// 卡片标题。
    /// </summary>
    public string? title { get; set; }

    /// <summary>
    /// 酷应用编码。
    /// </summary>
    public string? coolAppCode { get; set; }

    /// <summary>
    /// 是否支持转发, 默认false。
    /// </summary>
    public bool? supportForward { get; set; }

    /// <summary>
    /// 支持国际化的LastMessage。
    /// </summary>
    public Dictionary<string, string>? lastMessageI18n { get; set; }

    /// <summary>
    /// 支持卡片消息可被搜索字段。
    /// </summary>
    public SearchSupport? searchSupport { get; set; }

    /// <summary>
    /// 通知信息。
    /// </summary>
    public Notification? notification { get; set; }
}

public class SearchSupport
{
    /// <summary>
    /// 类型的icon，供搜索展示使用。
    /// </summary>
    public string searchIcon { get; set; }

    /// <summary>
    /// 卡片类型名。
    /// </summary>
    public string searchTypeName { get; set; }

    /// <summary>
    /// 供消息展示与搜索的字段。
    /// </summary>
    public string searchDesc { get; set; }
}

public class Notification
{
    /// <summary>
    /// 供消息展示与搜索的字段。
    /// </summary>
    public string alertContent { get; set; }

    /// <summary>
    /// 是否关闭推送通知：true：关闭 false：不关闭
    /// </summary>
    public bool notificationOff { get; set; }
}

public class DingTalkOpenDeliverModel
{
    /// <summary>
    /// 用于发送卡片的机器人编码。
    /// </summary>
    public string robotCode { get; set; }

    /// <summary>
    /// 消息@人。格式：{"key":"value"}。key：用户的userId value：用户名
    /// </summary>
    public Dictionary<string, string> atUserIds { get; set; }

    /// <summary>
    /// 指定接收人的userId。
    /// </summary>
    public List<string> recipients { get; set; }

    /// <summary>
    /// 扩展字段，示例如下：{"key":"value"}
    /// </summary>
    public Dictionary<string, string> extension { get; set; }

    /// <summary>
    /// IM机器人单聊若未设置其他投放属性，需设置spaeType为IM_ROBOT。
    /// </summary>
    public string spaceType { get; set; }

    /// <summary>
    /// 过期时间戳。若使用topOpenDeliverModel对象，则该字段必填。
    /// </summary>
    public long expiredTimeMillis { get; set; }

    /// <summary>
    /// 可以查看该吊顶卡片的userId。
    /// </summary>
    public List<string> userIds { get; set; }

    /// <summary>
    /// 可以查看该吊顶卡片的设备：android｜ios｜win｜mac。
    /// </summary>
    public List<string> platforms { get; set; }

    /// <summary>
    /// 业务标识。
    /// </summary>
    public string bizTag { get; set; }

    /// <summary>
    /// 协作场域下的排序时间。
    /// </summary>
    public long gmtTimeLine { get; set; }

    /// <summary>
    /// 员工userId信息
    /// </summary>
    public string userId { get; set; }
}