// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.WorkWeixin.Proxy;

/// <summary>
/// 创建群聊会话输入参数
/// </summary>
public class CreatAppChatInput
{
    /// <summary>
    /// 群名称
    /// </summary>
    [JsonProperty("name")]
    [JsonPropertyName("name")]
    [Required(ErrorMessage = "群名称不能为空"), MaxLength(50, ErrorMessage = "群名称最多不能超过50个字符")]
    public string Name { get; set; }

    /// <summary>
    /// 群主Id
    /// </summary>
    [JsonProperty("owner")]
    [JsonPropertyName("owner")]
    [Required(ErrorMessage = "群主Id不能为空")]
    public string Owner { get; set; }

    /// <summary>
    /// 群成员Id列表
    /// </summary>
    [JsonProperty("userlist")]
    [JsonPropertyName("userlist")]
    [Core.NotEmpty(ErrorMessage = "群成员列表不能为空")]
    public List<string> UserList { get; set; }

    /// <summary>
    /// 群Id
    /// </summary>
    [JsonProperty("chatid")]
    [JsonPropertyName("chatid")]
    [Required(ErrorMessage = "群Id不能为空"), MaxLength(32, ErrorMessage = "群Id最多不能超过32个字符")]
    public string ChatId { get; set; }
}

/// <summary>
/// 修改群聊会话输入参数
/// </summary>
public class UpdateAppChatInput
{
    /// <summary>
    /// 群Id
    /// </summary>
    [JsonProperty("chatid")]
    [JsonPropertyName("chatid")]
    [Required(ErrorMessage = "群Id不能为空"), MaxLength(32, ErrorMessage = "群Id最多不能超过32个字符")]
    public string ChatId { get; set; }

    /// <summary>
    /// 群名称
    /// </summary>
    [JsonProperty("name")]
    [JsonPropertyName("name")]
    [Required(ErrorMessage = "群名称不能为空"), MaxLength(50, ErrorMessage = "群名称最多不能超过50个字符")]
    public string Name { get; set; }

    /// <summary>
    /// 群主Id
    /// </summary>
    [JsonProperty("owner")]
    [JsonPropertyName("owner")]
    [Required(ErrorMessage = "群主Id不能为空")]
    public string Owner { get; set; }

    /// <summary>
    /// 添加成员的id列表
    /// </summary>
    [JsonProperty("add_user_list")]
    [JsonPropertyName("add_user_list")]
    public List<string> AddUserList { get; set; }

    /// <summary>
    /// 踢出成员的id列表
    /// </summary>
    [JsonProperty("del_user_list")]
    [JsonPropertyName("del_user_list")]
    public List<string> DelUserList { get; set; }
}

/// <summary>
/// 应用消息推送输入基类参数
/// </summary>
public class SendBaseAppChatInput
{
    /// <summary>
    /// 群Id
    /// </summary>
    [JsonProperty("chatid")]
    [JsonPropertyName("chatid")]
    [Required(ErrorMessage = "群Id不能为空"), MaxLength(32, ErrorMessage = "群Id最多不能超过32个字符")]
    public string ChatId { get; set; }

    /// <summary>
    /// 消息类型
    /// </summary>
    /// <example>text：文本消息</example>
    /// <example>image：图片消息</example>
    /// <example>voice：图片消息</example>
    /// <example>video：视频消息</example>
    /// <example>file：文件消息</example>
    /// <example>textcard：文本卡片</example>
    /// <example>news：图文消息</example>
    /// <example>mpnews：图文消息（存储在企业微信）</example>
    /// <example>markdown：markdown消息</example>
    [JsonProperty("msgtype")]
    [JsonPropertyName("msgtype")]
    [Required(ErrorMessage = "消息类型不能为空")]
    protected string MsgType { get; set; }

    /// <summary>
    /// 是否是保密消息
    /// </summary>
    [JsonProperty("safe")]
    [JsonPropertyName("safe")]
    [Required(ErrorMessage = "消息类型不能为空")]
    public int Safe { get; set; }

    public SendBaseAppChatInput(string chatId, string msgType, bool safe = false)
    {
        ChatId = chatId;
        MsgType = msgType;
        Safe = safe ? 1 : 0;
    }
}

/// <summary>
/// 推送文本消息输入参数
/// </summary>
public class SendTextAppChatInput : SendBaseAppChatInput
{
    /// <summary>
    /// 消息内容
    /// </summary>
    [JsonProperty("text")]
    [JsonPropertyName("text")]
    public object Text { get; set; }

    /// <summary>
    /// 文本消息
    /// </summary>
    /// <param name="chatId"></param>
    /// <param name="content"></param>
    /// <param name="safe"></param>
    public SendTextAppChatInput(string chatId, string content, bool safe = false) : base(chatId, "text", safe)
    {
        Text = new { content };
    }
}

/// <summary>
/// 推送图片消息输入参数
/// </summary>
public class SendImageAppChatInput : SendBaseAppChatInput
{
    /// <summary>
    /// 消息内容
    /// </summary>
    [JsonProperty("image")]
    [JsonPropertyName("image")]
    public object Image { get; set; }

    /// <summary>
    /// 图片消息
    /// </summary>
    /// <param name="chatId"></param>
    /// <param name="mediaId"></param>
    /// <param name="safe"></param>
    public SendImageAppChatInput(string chatId, string mediaId, bool safe = false) : base(chatId, "image", safe)
    {
        Image = new { media_id = mediaId };
    }
}

/// <summary>
/// 推送语音消息输入参数
/// </summary>
public class SendVoiceAppChatInput : SendBaseAppChatInput
{
    /// <summary>
    /// 消息内容
    /// </summary>
    [JsonProperty("voice")]
    [JsonPropertyName("voice")]
    public object Voice { get; set; }

    /// <summary>
    /// 语音消息
    /// </summary>
    /// <param name="chatId"></param>
    /// <param name="mediaId"></param>
    /// <param name="safe"></param>
    public SendVoiceAppChatInput(string chatId, string mediaId, bool safe = false) : base(chatId, "voice", safe)
    {
        Voice = new { media_id = mediaId };
    }
}

/// <summary>
/// 推送视频消息输入参数
/// </summary>
public class SendVideoAppChatInput : SendBaseAppChatInput
{
    /// <summary>
    /// 消息内容
    /// </summary>
    [JsonProperty("video")]
    [JsonPropertyName("video")]
    public object Video { get; set; }

    /// <summary>
    /// 视频消息
    /// </summary>
    /// <param name="chatId"></param>
    /// <param name="title"></param>
    /// <param name="description"></param>
    /// <param name="mediaId"></param>
    /// <param name="safe"></param>
    public SendVideoAppChatInput(string chatId, string title, string description, string mediaId, bool safe = false) : base(chatId, "video", safe)
    {
        Video = new
        {
            media_id = mediaId,
            description,
            title
        };
    }
}

/// <summary>
/// 推送视频消息输入参数
/// </summary>
public class SendFileAppChatInput : SendBaseAppChatInput
{
    /// <summary>
    /// 消息内容
    /// </summary>
    [JsonProperty("file")]
    [JsonPropertyName("file")]
    public object File { get; set; }

    /// <summary>
    /// 文件消息
    /// </summary>
    /// <param name="chatId"></param>
    /// <param name="mediaId"></param>
    /// <param name="safe"></param>
    public SendFileAppChatInput(string chatId, string mediaId, bool safe = false) : base(chatId, "video", safe)
    {
        File = new { media_id = mediaId };
    }
}

/// <summary>
/// 推送文本卡片消息输入参数
/// </summary>
public class SendTextCardAppChatInput : SendBaseAppChatInput
{
    /// <summary>
    /// 消息内容
    /// </summary>
    [JsonProperty("textcard")]
    [JsonPropertyName("textcard")]
    public object TextCard { get; set; }

    /// <summary>
    /// 文本卡片消息
    /// </summary>
    /// <param name="chatId"></param>
    /// <param name="title">标题</param>
    /// <param name="description">描述</param>
    /// <param name="url">点击后跳转的链接</param>
    /// <param name="btnTxt">按钮文字</param>
    /// <param name="safe"></param>
    public SendTextCardAppChatInput(string chatId, string title, string description, string url, string btnTxt, bool safe = false) : base(chatId, "textcard", safe)
    {
        TextCard = new
        {
            title,
            description,
            url,
            btntxt = btnTxt
        };
    }
}

/// <summary>
/// 图文消息项
/// </summary>
public class SendNewsItem
{
    /// <summary>
    /// 标题
    /// </summary>
    [JsonProperty("title")]
    [JsonPropertyName("title")]
    public string Title { get; set; }

    /// <summary>
    /// 描述
    /// </summary>
    [JsonProperty("description")]
    [JsonPropertyName("description")]
    public string Description { get; set; }

    /// <summary>
    /// 描述
    /// </summary>
    [JsonProperty("url")]
    [JsonPropertyName("url")]
    public string Url { get; set; }

    /// <summary>
    /// 图文消息的图片链接（推荐大图1068 * 455，小图150 * 150）
    /// </summary>
    [JsonProperty("picurl")]
    [JsonPropertyName("picurl")]
    public string PicUrl { get; set; }
}

/// <summary>
/// 推送图文消息输入参数
/// </summary>
public class SendNewsAppChatInput : SendBaseAppChatInput
{
    /// <summary>
    /// 消息内容
    /// </summary>
    [JsonProperty("news")]
    [JsonPropertyName("news")]
    public object News { get; set; }

    /// <summary>
    /// 图文消息
    /// </summary>
    /// <param name="chatId"></param>
    /// <param name="newsList">图文消息列表</param>
    /// <param name="safe"></param>
    public SendNewsAppChatInput(string chatId, List<SendNewsItem> newsList, bool safe = false) : base(chatId, "news", safe)
    {
        News = new { articles = newsList };
    }
}

/// <summary>
/// 图文消息项
/// </summary>
public class SendMpNewsItem
{
    /// <summary>
    /// 标题
    /// </summary>
    [JsonProperty("title")]
    [JsonPropertyName("title")]
    public string Title { get; set; }

    /// <summary>
    /// 缩略图media_id
    /// </summary>
    [JsonProperty("thumb_media_id")]
    [JsonPropertyName("thumb_media_id")]
    public string ThumbMediaId { get; set; }

    /// <summary>
    /// 作者
    /// </summary>
    [JsonProperty("author")]
    [JsonPropertyName("author")]
    public string Author { get; set; }

    /// <summary>
    /// 点击“阅读原文”之后的页面链接
    /// </summary>
    [JsonProperty("content_source_url")]
    [JsonPropertyName("content_source_url")]
    public string ContentSourceUrl { get; set; }

    /// <summary>
    /// 图文消息的内容
    /// </summary>
    [JsonProperty("content")]
    [JsonPropertyName("content")]
    public string Content { get; set; }

    /// <summary>
    /// 图文消息的描述
    /// </summary>
    [JsonProperty("digest")]
    [JsonPropertyName("digest")]
    public string Digest { get; set; }
}

/// <summary>
/// 推送图文消息(存储在企业微信)输入参数
/// </summary>
public class SendMpNewsAppChatInput : SendBaseAppChatInput
{
    /// <summary>
    /// 消息内容
    /// </summary>
    [JsonProperty("mpnews")]
    [JsonPropertyName("mpnews")]
    public object MpNews { get; set; }

    /// <summary>
    /// 图文消息
    /// </summary>
    /// <param name="chatId"></param>
    /// <param name="mpNewsList">图文消息列表</param>
    /// <param name="safe"></param>
    public SendMpNewsAppChatInput(string chatId, List<SendMpNewsItem> mpNewsList, bool safe = false) : base(chatId, "mpnews", safe)
    {
        MpNews = new { articles = mpNewsList };
    }
}