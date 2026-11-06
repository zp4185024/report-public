[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # 后端 IIS 站点名，默认 hd-report-center-api；已存在同名站点时执行更新而不是报错。
    [string]$BackendSiteName = 'hd-report-center-api',

    # 前端 IIS 站点名，默认 hd-report-center-web；与后端站点互相独立。
    [string]$FrontendSiteName = 'hd-report-center-web',

    # 后端 HTTP 端口，与本地开发 5006 保持一致，便于排障时对照日志。
    [ValidateRange(1, 65535)]
    [int]$BackendPort = 5006,

    # 前端 HTTP 端口；生产接入 nginx 反代时建议保留 80/443 给反代层。
    [ValidateRange(1, 65535)]
    [int]$FrontendPort = 8080,

    # 统一物理根目录，默认仓库 dist\report-center（与 Publish-ReportCenter.ps1 输出一致）。
    [string]$PhysicalRoot,

    # 后端站点物理路径，默认 <PhysicalRoot>\backend；指定后覆盖默认推导。
    [string]$BackendPhysicalPath,

    # 前端站点物理路径，默认 <PhysicalRoot>\frontend；指定后覆盖默认推导。
    [string]$FrontendPhysicalPath,

    # ASP.NET Core 托管模型：InProcess 性能更好，OutOfProcess 适合与站点内其他模块隔离排障。
    [ValidateSet('InProcess', 'OutOfProcess')]
    [string]$HostingModel = 'InProcess',

    # 可选：写入发布目录 Configuration\Database.json 的主库连接串。注意明文落盘有泄露风险，
    # 生产建议改用机器级环境变量 HD_REPORT_CENTER_DB_CONNECTION_STRING 或 -PersistDbEnvironmentToMachine。
    [string]$DbConnectionString,

    # 可选：把当前进程环境中的 HD_REPORT_CENTER_DB_CONNECTION_STRING / HD_REPORT_CENTER_DB_PASSWORD
    # 持久化为机器级环境变量，供 IIS 工作进程读取，避免密码写入任何配置文件。
    [switch]$PersistDbEnvironmentToMachine,

    # 在后端 web.config 打开 ANCM stdout 日志（.\logs\stdout），仅排障时开启，避免磁盘膨胀。
    [switch]$EnableStdoutLog,

    # 按顺序调用既有 scripts/database 初始化脚本（建库、报表配置表、可复用数据资产表）。
    [switch]$RunDatabaseInit,

    # 数据库初始化：SQL Server 地址与端口，默认与 scripts/database 脚本保持一致。
    [string]$DbServer = '188.188.2.5',
    [ValidateRange(1, 65535)]
    [int]$DbPort = 1433,

    # 数据库初始化：目标库名与登录账号，密码只从环境变量 HD_REPORT_CENTER_DB_PASSWORD 读取。
    [string]$DbName = 'hd_report_center',
    [string]$DbUser = 'hdhis',

    # 数据库初始化完成后顺带执行 JNPF 患者费用明细报表导入预检（Import-JnpfPatientFeeReport.ps1）。
    [switch]$ImportJnpfReport,

    # JNPF 报表导入的目标数据源编码，是 Import-JnpfPatientFeeReport.ps1 的必填参数；
    # 仅在启用 -ImportJnpfReport 时需要，缺失时脚本在预检阶段直接失败。
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]{0,63}$')]
    [string]$JnpfTargetDataSourceCode,

    # 跳过 Hosting Bundle/ANCM 预检；仅在已确认装机情况或自动化镜像内使用。
    [switch]$SkipRuntimeChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($PhysicalRoot)) {
    $PhysicalRoot = Join-Path $repoRoot 'dist\report-center'
}
$physicalRootFull = [System.IO.Path]::GetFullPath($PhysicalRoot)
if ([string]::IsNullOrWhiteSpace($BackendPhysicalPath)) {
    $BackendPhysicalPath = Join-Path $physicalRootFull 'backend'
}
if ([string]::IsNullOrWhiteSpace($FrontendPhysicalPath)) {
    $FrontendPhysicalPath = Join-Path $physicalRootFull 'frontend'
}
$backendPathFull = [System.IO.Path]::GetFullPath($BackendPhysicalPath)
$frontendPathFull = [System.IO.Path]::GetFullPath($FrontendPhysicalPath)
$applyChanges = -not $WhatIfPreference

# 校验当前会话具备管理员权限；IIS 站点与应用池变更依赖本机管理员令牌。
function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '部署 IIS 站点需要管理员权限，请用管理员身份运行 PowerShell 后重试。'
    }
}

# 加载 WebAdministration 管理单元；IIS 功能未安装时报出可操作的修复指引。
function Import-IisManagementModule {
    if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
        throw '未找到 WebAdministration 模块，请先安装 IIS 功能（Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole）。'
    }

    Import-Module WebAdministration -ErrorAction Stop
}

# 校验 .NET 8 ASP.NET Core 运行时已随 Hosting Bundle 安装，避免部署后出现 502.5。
function Test-AspNetCoreRuntimeAvailable {
    $runtimes = & dotnet --list-runtimes 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw '无法执行 dotnet --list-runtimes，请先安装 .NET 8 ASP.NET Core Hosting Bundle。'
    }

    $matched = @($runtimes | Where-Object { $_ -match 'Microsoft\.AspNetCore\.App\s+8\.' })
    if ($matched.Count -eq 0) {
        throw '未检测到 Microsoft.AspNetCore.App 8.x 运行时，请安装 .NET 8 ASP.NET Core Hosting Bundle 后重试。'
    }
}

# 校验 IIS 的 ASP.NET Core Module V2 已注册，覆盖 Hosting Bundle 与独立 ANCM 两种安装形态。
function Test-AspNetCoreModuleInstalled {
    # 形态一：系统 inetsrv 目录存在 ANCM 本体，这是 Hosting Bundle 落地的主要标志。
    $ancmDll = Join-Path $env:windir 'System32\inetsrv\aspnetcore.dll'
    if (-not (Test-Path -LiteralPath $ancmDll -PathType Leaf)) {
        throw "未找到 ASP.NET Core Module（$ancmDll），请安装 .NET 8 ASP.NET Core Hosting Bundle。"
    }

    # 形态二：applicationHost.config 必须注册 AspNetCoreModuleV2 处理程序映射，缺了会导致 500.19。
    $hostConfig = Join-Path $env:windir 'System32\inetsrv\config\applicationHost.config'
    if ((Test-Path -LiteralPath $hostConfig -PathType Leaf) -and
        -not (Select-String -LiteralPath $hostConfig -Pattern 'AspNetCoreModuleV2' -Quiet)) {
        throw 'applicationHost.config 未注册 AspNetCoreModuleV2，请重装 .NET 8 ASP.NET Core Hosting Bundle。'
    }
}

# 校验发布产物齐备：后端入口程序集与前端 index.html 必须先由 Publish-ReportCenter.ps1 生成。
function Assert-PublishedArtifacts {
    # 后端产物：入口 dll 是 IIS 站点能否启动的最低要求。
    if (-not (Test-Path -LiteralPath (Join-Path $backendPathFull 'Admin.NET.Web.Entry.dll') -PathType Leaf)) {
        throw "后端发布产物缺失（Admin.NET.Web.Entry.dll），请先执行 Publish-ReportCenter.ps1：$backendPathFull"
    }

    # 前端产物：index.html 是静态站点与 history 路由回退的入口文件。
    if (-not (Test-Path -LiteralPath (Join-Path $frontendPathFull 'index.html') -PathType Leaf)) {
        throw "前端发布产物缺失（index.html），请先执行 Publish-ReportCenter.ps1：$frontendPathFull"
    }
}

# 生成后端 web.config 内容：固定 processPath 指向 dotnet 与入口 dll，覆盖发布产物中的 VS 占位符。
function New-BackendWebConfigContent {
    param(
        [Parameter(Mandatory)]
        [bool]$StdoutLogEnabled,
        [Parameter(Mandatory)]
        [ValidateSet('InProcess', 'OutOfProcess')]
        [string]$Model
    )

    # ANCM 托管模型取值统一小写；InProcess 走 IIS 内托管，OutOfProcess 由 ANCM 反代 Kestrel。
    $hostingModel = if ($Model -eq 'InProcess') { 'inprocess' } else { 'outofprocess' }

    return @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <security>
      <requestFiltering>
        <!-- 放宽请求体到 1GB，配合大数据量 Excel 导出与模板导入场景 -->
        <requestLimits maxAllowedContentLength="1073741824" maxQueryString="1073741824" />
      </requestFiltering>
    </security>
    <handlers>
      <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
    </handlers>
    <aspNetCore processPath="dotnet" arguments=".\Admin.NET.Web.Entry.dll" stdoutLogEnabled="$StdoutLogEnabled" stdoutLogFile=".\logs\stdout" hostingModel="$hostingModel" />
  </system.webServer>
</configuration>
"@
}

# 把归一化后的 web.config 写入后端目录并准备 stdout 日志目录；ANCM 不会自建日志目录，缺目录会静默不写。
function Write-BackendWebConfig {
    param(
        [Parameter(Mandatory)]
        [bool]$StdoutLogEnabled,
        [Parameter(Mandatory)]
        [ValidateSet('InProcess', 'OutOfProcess')]
        [string]$Model,
        [Parameter(Mandatory)]
        [string]$TargetDirectory
    )

    # 写入前确保目录存在：发布目录可能被运维手动挪动过。
    New-Item -ItemType Directory -Path $TargetDirectory -Force | Out-Null
    $content = New-BackendWebConfigContent -StdoutLogEnabled $StdoutLogEnabled -Model $Model
    Set-Content -LiteralPath (Join-Path $TargetDirectory 'web.config') -Value $content -Encoding UTF8
    New-Item -ItemType Directory -Path (Join-Path $TargetDirectory 'logs') -Force | Out-Null
}

# 可选：把主库连接串写进发布目录的 Configuration\Database.json，只替换 ConnectionString 行以保留注释。
function Update-BackendDatabaseConfig {
    param(
        [Parameter(Mandatory)]
        [string]$TargetDirectory,
        [Parameter(Mandatory)]
        [string]$ConnectionString
    )

    # 定位发布产物中的 Database.json；Furion 配置带注释，不能整体反序列化，只能做行级替换。
    $databaseJson = Join-Path $TargetDirectory 'Configuration\Database.json'
    if (-not (Test-Path -LiteralPath $databaseJson -PathType Leaf)) {
        throw "未找到发布目录下的 Database.json：$databaseJson"
    }

    # JSON 字符串转义：连接串中的反斜杠与引号必须先转义再写入。
    $escaped = $ConnectionString.Replace('\', '\\').Replace('"', '\"')
    $content = [System.IO.File]::ReadAllText($databaseJson)
    $pattern = '("ConnectionString"\s*:\s*")[^"]*(")'
    $updated = [regex]::Replace($content, $pattern, { param($match) $match.Groups[1].Value + $escaped + $match.Groups[2].Value })
    if ($updated -eq $content) {
        throw "Database.json 中未匹配到 ConnectionString 配置行：$databaseJson"
    }

    Write-Warning '已把数据库连接串写入 Configuration\Database.json，密码随之明文落盘；生产环境建议改用环境变量注入并回滚该文件。'
    Set-Content -LiteralPath $databaseJson -Value $updated -Encoding UTF8
}

# 判断目标数据库是否已存在，用于决定是否调用一次性建库脚本，保证 -RunDatabaseInit 可重复执行。
function Test-TargetDatabaseExists {
    param(
        [Parameter(Mandatory)]
        [string]$Server,
        [Parameter(Mandatory)]
        [int]$Port,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$User,
        [Parameter(Mandatory)]
        [string]$Password
    )

    # 连接 master 而不是目标库，目标库可能尚未创建。
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source'] = "$Server,$Port"
    $builder['Initial Catalog'] = 'master'
    $builder['User ID'] = $User
    $builder['Password'] = $Password
    $builder['Connect Timeout'] = 15
    $builder['Encrypt'] = $false
    $builder['TrustServerCertificate'] = $true

    $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = 'SELECT COUNT(*) FROM sys.databases WHERE name = @name;'
        $command.Parameters.AddWithValue('@name', $Name) | Out-Null
        return [int]$command.ExecuteScalar() -gt 0
    }
    finally {
        $connection.Dispose()
    }
}

# 按既有 scripts/database 脚本顺序执行初始化：建库种子 -> 报表配置表 -> 可复用数据资产表 -> 可选 JNPF 报表导入。
function Invoke-DatabaseInitialization {
    # 阶段一：校验密码环境变量与数据库脚本文件齐备，任何缺失都在改动数据库前失败。
    $password = $env:HD_REPORT_CENTER_DB_PASSWORD
    if ([string]::IsNullOrWhiteSpace($password)) {
        throw '未设置 HD_REPORT_CENTER_DB_PASSWORD，数据库初始化拒绝在命令参数中接收明文密码。'
    }

    $databaseScriptDir = Join-Path $repoRoot 'scripts\database'
    $initializeScript = Join-Path $databaseScriptDir 'Initialize-ReportCenterSqlServer.ps1'
    $applyConfigurationScript = Join-Path $databaseScriptDir 'Apply-ReportConfigurationSchema.ps1'
    $applyAssetScript = Join-Path $databaseScriptDir 'Apply-ReusableDataAssetSchema.ps1'
    foreach ($scriptPath in @($initializeScript, $applyConfigurationScript, $applyAssetScript)) {
        if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
            throw "数据库初始化脚本缺失：$scriptPath"
        }
    }

    # 阶段二：首次建库只执行一次；库里已有目标库时跳过，让既有脚本“已存在保护”不被触发。
    # AppDllPath 显式指向本次发布产物，避免依赖 bin\Release 下的历史构建（Debug 发布或干净机器上不存在）。
    $databaseExists = Test-TargetDatabaseExists -Server $DbServer -Port $DbPort -Name $DbName -User $DbUser -Password $password
    if ($databaseExists) {
        Write-Host "==> 数据库 $DbName 已存在，跳过 Initialize-ReportCenterSqlServer。" -ForegroundColor DarkGray
    }
    else {
        Write-Host "==> 初始化数据库 $DbName（建库 + 表结构 + 种子）" -ForegroundColor Cyan
        & $initializeScript -Server $DbServer -Port $DbPort -Database $DbName -User $DbUser `
            -AppDllPath (Join-Path $backendPathFull 'Admin.NET.Web.Entry.dll')
        if ($LASTEXITCODE -ne 0) {
            throw "Initialize-ReportCenterSqlServer.ps1 执行失败，退出码 $LASTEXITCODE。"
        }
    }

    # 阶段三：结构升级脚本自身幂等（全量存在即跳过），可以每次部署重复执行。
    Write-Host '==> 应用报表配置表结构（Apply-ReportConfigurationSchema）' -ForegroundColor Cyan
    & $applyConfigurationScript -Server $DbServer -Port $DbPort -Database $DbName -User $DbUser
    if ($LASTEXITCODE -ne 0) {
        throw 'Apply-ReportConfigurationSchema.ps1 执行失败。'
    }

    Write-Host '==> 应用可复用数据资产表结构（Apply-ReusableDataAssetSchema）' -ForegroundColor Cyan
    & $applyAssetScript -Server $DbServer -Port $DbPort -Database $DbName -User $DbUser
    if ($LASTEXITCODE -ne 0) {
        throw 'Apply-ReusableDataAssetSchema.ps1 执行失败。'
    }

    # 阶段四：可选 JNPF 报表导入预检，默认关闭；目标脚本不带 -WriteTarget 时只做只读预检，不写库。
    if ($ImportJnpfReport) {
        $importScript = Join-Path $databaseScriptDir 'Import-JnpfPatientFeeReport.ps1'
        if (-not (Test-Path -LiteralPath $importScript -PathType Leaf)) {
            throw "JNPF 报表导入脚本缺失：$importScript"
        }

        Write-Host '==> 执行 JNPF 患者费用明细报表导入预检（Import-JnpfPatientFeeReport）' -ForegroundColor Cyan
        & $importScript -TargetServer $DbServer -TargetPort $DbPort -TargetDatabase $DbName -TargetUser $DbUser `
            -TargetDataSourceCode $JnpfTargetDataSourceCode
        if ($LASTEXITCODE -ne 0) {
            throw 'Import-JnpfPatientFeeReport.ps1 执行失败。'
        }
    }
}

# 把当前进程内的数据库环境变量持久化为机器级变量，使 IIS 工作进程无需配置文件即可读到密码。
function Set-MachineDatabaseEnvironment {
    # 连接串优先：设置其一即满足 DbConnectionOptions 的覆盖逻辑，另一个保持不动。
    $connectionString = $env:HD_REPORT_CENTER_DB_CONNECTION_STRING
    if (-not [string]::IsNullOrWhiteSpace($connectionString)) {
        [Environment]::SetEnvironmentVariable('HD_REPORT_CENTER_DB_CONNECTION_STRING', $connectionString, 'Machine')
        Write-Host '==> 已把 HD_REPORT_CENTER_DB_CONNECTION_STRING 持久化为机器级环境变量。' -ForegroundColor DarkGray
    }

    $password = $env:HD_REPORT_CENTER_DB_PASSWORD
    if (-not [string]::IsNullOrWhiteSpace($password)) {
        [Environment]::SetEnvironmentVariable('HD_REPORT_CENTER_DB_PASSWORD', $password, 'Machine')
        Write-Host '==> 已把 HD_REPORT_CENTER_DB_PASSWORD 持久化为机器级环境变量。' -ForegroundColor DarkGray
    }

    if ([string]::IsNullOrWhiteSpace($connectionString) -and [string]::IsNullOrWhiteSpace($password)) {
        throw '当前进程未设置 HD_REPORT_CENTER_DB_CONNECTION_STRING / HD_REPORT_CENTER_DB_PASSWORD，无法持久化。'
    }
}

# 创建或更新报表中心应用池：固定 No Managed Code，已存在时仅校正托管版本，保证幂等。
function Ensure-ReportAppPool {
    param([Parameter(Mandatory)][string]$Name)

    # 已存在时只做属性校正；不重建应用池，避免打断其他共用该池的站点。
    if (Test-Path -LiteralPath "IIS:\AppPools\$Name") {
        Write-Host "==> 应用池 $Name 已存在，校正托管版本为 No Managed Code。" -ForegroundColor DarkGray
        Set-ItemProperty -LiteralPath "IIS:\AppPools\$Name" -Name managedRuntimeVersion -Value ''
        return
    }

    Write-Host "==> 创建应用池 $Name（No Managed Code）" -ForegroundColor Cyan
    New-WebAppPool -Name $Name | Out-Null
    Set-ItemProperty -LiteralPath "IIS:\AppPools\$Name" -Name managedRuntimeVersion -Value ''
}

# 创建或更新站点：已存在时更新物理路径并补写缺失的端口绑定（不删除既有绑定），不存在时按参数新建。
function Ensure-ReportSite {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$SitePhysicalPath,
        [Parameter(Mandatory)][int]$SitePort,
        [Parameter(Mandatory)][string]$AppPoolName
    )

    # 物理路径必须先存在，New-Website 不会自动创建目录。
    New-Item -ItemType Directory -Path $SitePhysicalPath -Force | Out-Null

    # 站点已存在：更新物理路径；目标端口绑定缺失时增量补写，保留站点上其他协议/端口绑定不被破坏。
    if (Test-Path -LiteralPath "IIS:\Sites\$Name") {
        Write-Host "==> 站点 $Name 已存在，更新物理路径与端口 $SitePort。" -ForegroundColor DarkGray
        Set-ItemProperty -LiteralPath "IIS:\Sites\$Name" -Name physicalPath -Value $SitePhysicalPath

        # 只在目标绑定缺失时补写；切换端口部署后旧端口绑定仍然保留，可在 IIS 管理器中按需移除。
        $site = Get-Item -LiteralPath "IIS:\Sites\$Name"
        $desiredBinding = "*:$SitePort`:"
        $hasBinding = @($site.Bindings.Collection | Where-Object { $_.bindingInformation -eq $desiredBinding }).Count -gt 0
        if (-not $hasBinding) {
            New-WebBinding -Name $Name -Protocol http -Port $SitePort | Out-Null
        }
        Set-ItemProperty -LiteralPath "IIS:\Sites\$Name" -Name applicationPool -Value $AppPoolName
        return
    }

    # 新建站点：AppPool 由调用方先建好，这里只做站点级绑定。
    Write-Host "==> 创建站点 $Name（端口 $SitePort -> $SitePhysicalPath）" -ForegroundColor Cyan
    New-Website -Name $Name -PhysicalPath $SitePhysicalPath -Port $SitePort -ApplicationPool $AppPoolName -Force | Out-Null
}

# 启动应用池与站点并回收后端池，让新的 web.config 与环境变量立即生效。
function Start-DeployedSites {
    param(
        [Parameter(Mandatory)][string]$BackendPool,
        [Parameter(Mandatory)][string]$BackendSite,
        [Parameter(Mandatory)][string]$FrontendPool,
        [Parameter(Mandatory)][string]$FrontendSite
    )

    # 阶段一：确保两个应用池处于启动状态。
    foreach ($pool in @($BackendPool, $FrontendPool)) {
        $state = (Get-WebAppPoolState -Name $pool).Value
        if ($state -ne 'Started') {
            Start-WebAppPool -Name $pool
        }
    }

    # 阶段二：后端池做一次 Recycle，确保新写入的 web.config / Database.json 被重新加载。
    if (Test-Path -LiteralPath "IIS:\Sites\$BackendSite") {
        Restart-WebAppPool -Name $BackendPool
        $siteState = (Get-WebsiteState -Name $BackendSite).Value
        if ($siteState -ne 'Started') {
            Start-Website -Name $BackendSite
        }
    }

    # 阶段三：前端纯静态，只需保证站点启动。
    if (Test-Path -LiteralPath "IIS:\Sites\$FrontendSite") {
        $siteState = (Get-WebsiteState -Name $FrontendSite).Value
        if ($siteState -ne 'Started') {
            Start-Website -Name $FrontendSite
        }
    }
}

# 输出部署结果摘要，包括站点地址、物理路径与后续手工核对项。
function Write-DeploySummary {
    Write-Host ''
    Write-Host '================ IIS 部署摘要 ================' -ForegroundColor Green
    Write-Host ("后端站点 {0}  http://*:{1}  ->  {2}" -f $BackendSiteName, $BackendPort, $backendPathFull)
    Write-Host ("前端站点 {0}  http://*:{1}  ->  {2}" -f $FrontendSiteName, $FrontendPort, $frontendPathFull)
    Write-Host '后续核对：浏览器访问前端首页；调用 /api/report-center/features 验证后端连通；检查 HD_REPORT_CENTER_DB_* 环境变量已对应用池生效。'
}

# 主流程：预检 -> 配置文件 -> 可选数据库初始化 -> IIS 站点编排 -> 启动并输出摘要。
function Invoke-DeployReportCenterIis {
    # 阶段零：参数组合校验。JNPF 导入依赖目标数据源编码（目标脚本必填参数），干跑模式也要提前暴露缺失。
    if ($ImportJnpfReport -and [string]::IsNullOrWhiteSpace($JnpfTargetDataSourceCode)) {
        throw '启用 -ImportJnpfReport 时必须同时提供 -JnpfTargetDataSourceCode（Import-JnpfPatientFeeReport.ps1 的必填参数）。'
    }

    # 阶段一：路径与产物校验，全部只读操作，干跑模式也会完整执行以暴露配置问题。
    Assert-PublishedArtifacts

    # 阶段二：配置文件落盘。web.config 归一化覆盖发布产物中的 VS 占位符，Database.json 按需补连接串。
    if ($applyChanges) {
        Write-BackendWebConfig -StdoutLogEnabled ([bool]$EnableStdoutLog) -Model $HostingModel -TargetDirectory $backendPathFull
        Write-Host "==> 已写入 web.config（$HostingModel，stdoutLog=$EnableStdoutLog）。" -ForegroundColor DarkGray
        if (-not [string]::IsNullOrWhiteSpace($DbConnectionString)) {
            Update-BackendDatabaseConfig -TargetDirectory $backendPathFull -ConnectionString $DbConnectionString
        }
    }
    else {
        Write-Host "[WhatIf] 将写入 $backendPathFull\web.config（$HostingModel，stdoutLog=$EnableStdoutLog）" -ForegroundColor Yellow
        if (-not [string]::IsNullOrWhiteSpace($DbConnectionString)) {
            Write-Host "[WhatIf] 将把连接串写入 $backendPathFull\Configuration\Database.json" -ForegroundColor Yellow
        }
    }

    # 阶段三：环境变量与数据库初始化。密码只允许来自环境变量，杜绝命令行与配置文件明文。
    if ($PersistDbEnvironmentToMachine) {
        if ($applyChanges) {
            Set-MachineDatabaseEnvironment
        }
        else {
            Write-Host '[WhatIf] 将把 HD_REPORT_CENTER_DB_* 持久化为机器级环境变量' -ForegroundColor Yellow
        }
    }

    if ($RunDatabaseInit) {
        if ($applyChanges) {
            Invoke-DatabaseInitialization
        }
        else {
            Write-Host "[WhatIf] 将按顺序执行 scripts/database 初始化（目标库 $DbServer,$DbPort/$DbName）" -ForegroundColor Yellow
        }
    }

    # 阶段四：运行时预检放最后一段只读校验，避免 IIS 缺件时前面的文件改动白做。
    if (-not $SkipRuntimeChecks) {
        Test-AspNetCoreRuntimeAvailable
        Test-AspNetCoreModuleInstalled
    }

    # 阶段五：IIS 变更需要管理员与 WebAdministration 模块；干跑模式跳过模块加载也能给出完整计划。
    if (-not $applyChanges) {
        Write-Host '[WhatIf] 将创建/更新应用池与站点：' -ForegroundColor Yellow
        Write-Host "[WhatIf]   AppPool $BackendSiteName + Site $BackendSiteName($BackendPort) + AppPool $FrontendSiteName + Site $FrontendSiteName($FrontendPort)" -ForegroundColor Yellow
        Write-DeploySummary
        return
    }

    Assert-Administrator
    Import-IisManagementModule

    # 阶段六：幂等编排应用池与站点，最后统一启动并回收后端池。
    Ensure-ReportAppPool -Name $BackendSiteName
    Ensure-ReportAppPool -Name $FrontendSiteName
    Ensure-ReportSite -Name $BackendSiteName -SitePhysicalPath $backendPathFull -SitePort $BackendPort -AppPoolName $BackendSiteName
    Ensure-ReportSite -Name $FrontendSiteName -SitePhysicalPath $frontendPathFull -SitePort $FrontendPort -AppPoolName $FrontendSiteName
    Start-DeployedSites -BackendPool $BackendSiteName -BackendSite $BackendSiteName -FrontendPool $FrontendSiteName -FrontendSite $FrontendSiteName
    Write-DeploySummary
}

Invoke-DeployReportCenterIis
