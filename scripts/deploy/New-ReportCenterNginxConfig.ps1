[CmdletBinding()]
param(
    # nginx 监听端口，默认 80；与现有服务冲突时用 -ListenPort 换端口。
    [ValidateRange(1, 65535)]
    [int]$ListenPort = 80,

    # server_name，默认 '_' 接收所有 Host；多域名用空格分隔传入。
    [ValidatePattern('^[A-Za-z0-9.\-*_]+( [A-Za-z0-9.\-*_]+)*$')]
    [string]$ServerName = '_',

    # 前端静态文件根目录，默认仓库 dist\report-center\frontend；Linux 主机填 / 开头的绝对路径。
    [string]$FrontendRoot,

    # 后端反代目标主机：只填主机名或 IP，不要带协议与路径；模板统一拼 http://。
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$BackendHost = '127.0.0.1',

    # 后端反代目标端口，与 IIS/Kestrel 监听的 BackendPort 一致。
    [ValidateRange(1, 65535)]
    [int]$BackendPort = 5006,

    # 后端 API 前缀；后端路由自带 /api，nginx 不做 rewrite，仅用于匹配 location。
    [ValidatePattern('^/[\w\-/]*$')]
    [string]$ApiPrefix = '/api',

    # client_max_body_size：大文件上传/导出场景放开限制，与后端 requestLimits 同量级。
    [ValidatePattern('^\d+[kKmMgG]?$')]
    [string]$ClientMaxBodySize = '1g',

    # 代理超时（nginx 时间字面量），大导出任务建议 300s 起。
    [ValidatePattern('^\d+(ms|s|m|h)$')]
    [string]$ProxyTimeout = '300s',

    # 输出的 conf 文件路径，默认仓库 dist\report-center\nginx\report-center.conf。
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$templatePath = Join-Path $PSScriptRoot 'nginx\report-center.conf.template'
if ([string]::IsNullOrWhiteSpace($FrontendRoot)) {
    $FrontendRoot = Join-Path $repoRoot 'dist\report-center\frontend'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot 'dist\report-center\nginx\report-center.conf'
}
$outputPathFull = [System.IO.Path]::GetFullPath($OutputPath)

# 把 Windows 风格路径转换为 nginx 可用的正斜杠路径，Linux 绝对路径原样返回。
function Convert-ToNginxPath {
    param([Parameter(Mandatory)][string]$Path)

    return $Path.Replace('\', '/')
}

# 读取模板并按参数注入占位符；渲染后仍残留 __TOKEN__ 时视为未知占位符并直接失败。
function New-NginxConfigContent {
    param(
        [Parameter(Mandatory)][string]$TemplateFile,
        [Parameter(Mandatory)][hashtable]$Tokens
    )

    # 阶段一：读取模板原文，缺模板属于脚本自带资源缺失，立即失败。
    if (-not (Test-Path -LiteralPath $TemplateFile -PathType Leaf)) {
        throw "nginx 配置模板缺失：$TemplateFile"
    }

    # 阶段二：逐一替换占位符；模板与脚本同步演进，替换必须完整覆盖。
    $content = [System.IO.File]::ReadAllText($TemplateFile)
    foreach ($entry in $Tokens.GetEnumerator()) {
        $content = $content.Replace($entry.Key, [string]$entry.Value)
    }

    # 阶段三：校验无残留占位符，防止模板新增 token 而脚本漏配造成带 __TOKEN__ 的坏配置上线。
    if ($content -match '__[A-Z_]+__') {
        throw "模板渲染后仍残留未注入的占位符：$($Matches[0])，请检查脚本与模板是否同步。"
    }

    return $content
}

# 主流程：构造占位符表、渲染模板并把 conf 写到输出路径，最后打印 include 用法提示。
function Invoke-NewNginxConfig {
    # 阶段一：组装注入参数；前端根目录统一转成正斜杠，跨 Windows/Linux 生成均可直接使用。
    $tokens = @{
        '__LISTEN_PORT__'          = [string]$ListenPort
        '__SERVER_NAME__'          = $ServerName
        '__FRONTEND_ROOT__'        = Convert-ToNginxPath $FrontendRoot
        '__API_PREFIX__'           = $ApiPrefix
        '__BACKEND_HOST__'         = $BackendHost
        '__BACKEND_PORT__'         = [string]$BackendPort
        '__CLIENT_MAX_BODY_SIZE__' = $ClientMaxBodySize
        '__PROXY_TIMEOUT__'        = $ProxyTimeout
    }

    # 阶段二：渲染并落盘；输出目录不存在时先创建，方便发布流水线串联。
    $content = New-NginxConfigContent -TemplateFile $templatePath -Tokens $tokens
    $outputDirectory = Split-Path -Parent $outputPathFull
    if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }

    Set-Content -LiteralPath $outputPathFull -Value $content -Encoding UTF8

    # 阶段三：输出验证指引，帮助运维把 conf 挂进 http{} 上下文并完成 nginx 侧检查。
    Write-Host "==> nginx 配置已生成：$outputPathFull" -ForegroundColor Green
    Write-Host '用法：将该文件 include 到 nginx.conf 的 http{} 中（如 include conf.d/*.conf;），然后执行 nginx -t 验证后 reload。'
}

Invoke-NewNginxConfig
