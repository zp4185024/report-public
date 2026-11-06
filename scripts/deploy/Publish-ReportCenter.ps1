[CmdletBinding()]
param(
    # 编译配置，默认 Release；与 dotnet publish -c 参数一致。
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 统一发布根目录，默认仓库根下 dist\report-center；backend 与 frontend 子目录在其中生成。
    [string]$OutputRoot,

    # 后端入口项目路径，默认 Admin.NET.Web.Entry（报表中心唯一 ASP.NET Core 宿主）。
    [string]$BackendProject,

    # 前端工程目录，默认仓库 ReportWeb；构建脚本来自其 package.json。
    [string]$FrontendDirectory,

    # 包管理器选择：Auto 优先 pnpm（与仓库开发约定一致），缺失时回退 npm。
    [ValidateSet('Auto', 'Npm', 'Pnpm')]
    [string]$PackageManager = 'Auto',

    # 跳过后端发布，仅构建前端；用于前端频繁改版的迭代场景。
    [switch]$SkipBackend,

    # 跳过前端构建，仅发布后端；用于纯服务端变更的部署。
    [switch]$SkipFrontend,

    # 跳过前端依赖安装；构建机已装好 node_modules 时可节省时间。
    [switch]$SkipDependencyInstall,

    # 发布前清空整个输出根目录；默认只清理前端产物（防止旧 hash 资源残留）。
    [switch]$CleanOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'dist\report-center'
}
if ([string]::IsNullOrWhiteSpace($BackendProject)) {
    $BackendProject = Join-Path $repoRoot 'Admin.NET\Admin.NET.Web.Entry\Admin.NET.Web.Entry.csproj'
}
if ([string]::IsNullOrWhiteSpace($FrontendDirectory)) {
    $FrontendDirectory = Join-Path $repoRoot 'ReportWeb'
}

$outputRootFull = [System.IO.Path]::GetFullPath($OutputRoot)
$backendOutput = Join-Path $outputRootFull 'backend'
$frontendOutput = Join-Path $outputRootFull 'frontend'

# 解析可用的包管理器可执行文件，Auto 模式按 pnpm、npm 顺序探测并返回其真实命令对象。
function Resolve-PackageManager {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Auto', 'Npm', 'Pnpm')]
        [string]$Mode
    )

    # 显式指定时只探测所选包管理器，避免开发机差异导致悄悄换工具。
    $orderedNames = if ($Mode -eq 'Pnpm') { @('pnpm') }
    elseif ($Mode -eq 'Npm') { @('npm') }
    else { @('pnpm', 'npm') }

    foreach ($name in $orderedNames) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }

    throw "未找到可用的包管理器（尝试顺序：$($orderedNames -join ' -> ')），请安装 Node.js 与 npm/pnpm。"
}

# 输出后端 dotnet publish 日志，并校验发布产物中入口程序集存在，防止把半成品交给部署脚本。
function Invoke-BackendPublish {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,
        [Parameter(Mandatory)]
        [string]$OutputDirectory,
        [Parameter(Mandatory)]
        [ValidateSet('Debug', 'Release')]
        [string]$BuildConfiguration
    )

    # 校验输入：入口项目文件必须真实存在，路径错误要第一时间报出。
    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
        throw "后端入口项目不存在：$ProjectPath"
    }

    Write-Host "==> dotnet publish $ProjectPath -c $BuildConfiguration" -ForegroundColor Cyan
    & dotnet publish $ProjectPath -c $BuildConfiguration -f net8.0 -o $OutputDirectory --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码 $LASTEXITCODE。"
    }

    # 产物校验：入口 dll 缺失说明发布中断或路径异常，直接失败而不是让 IIS 部署去踩坑。
    $entryDll = Join-Path $OutputDirectory 'Admin.NET.Web.Entry.dll'
    if (-not (Test-Path -LiteralPath $entryDll -PathType Leaf)) {
        throw "发布目录中未找到入口程序集：$entryDll"
    }
}

# 若前端依赖未安装则按所选包管理器执行安装；已存在 node_modules 时不再重复安装。
function Install-FrontendDependencies {
    param(
        [Parameter(Mandatory)]
        [string]$FrontendPath,
        [Parameter(Mandatory)]
        [string]$PackageManagerExe
    )

    # 依赖目录已就绪时直接返回，保持构建可重复且不产生额外网络请求。
    if (Test-Path -LiteralPath (Join-Path $FrontendPath 'node_modules') -PathType Container) {
        Write-Host '==> 前端依赖已安装，跳过 install。' -ForegroundColor DarkGray
        return
    }

    Write-Host "==> $PackageManagerExe install（前端依赖）" -ForegroundColor Cyan
    & $PackageManagerExe install
    if ($LASTEXITCODE -ne 0) {
        throw "前端依赖安装失败，退出码 $LASTEXITCODE。"
    }
}

# 在前端目录执行构建脚本（tsc -b && vite build），并把默认 dist 产物拷贝到统一发布目录。
function Invoke-FrontendBuild {
    param(
        [Parameter(Mandatory)]
        [string]$FrontendPath,
        [Parameter(Mandatory)]
        [string]$PackageManagerExe,
        [Parameter(Mandatory)]
        [string]$TargetDirectory
    )

    # 构建前先清空目标目录：Vite 产物文件名带内容 hash，残留旧文件会让增量发布越来越臃肿。
    if (Test-Path -LiteralPath $TargetDirectory) {
        Remove-Item -LiteralPath $TargetDirectory -Recurse -Force
    }

    Push-Location $FrontendPath
    try {
        Write-Host "==> $PackageManagerExe run build" -ForegroundColor Cyan
        & $PackageManagerExe run build
        if ($LASTEXITCODE -ne 0) {
            throw "前端构建失败，退出码 $LASTEXITCODE。"
        }
    }
    finally {
        Pop-Location
    }

    # 拷贝产物：前端入口 index.html 必须存在，否则说明构建输出位置发生变化。
    $viteDist = Join-Path $FrontendPath 'dist'
    if (-not (Test-Path -LiteralPath (Join-Path $viteDist 'index.html') -PathType Leaf)) {
        throw "未在前端 dist 中找到 index.html：$viteDist"
    }

    Copy-Item -Path (Join-Path $viteDist '*') -Destination $TargetDirectory -Recurse -Force
}

# 统计目录文件数与体积，供发布完成后输出摘要，便于运维核对产物规模。
function Get-DirectorySummary {
    param([Parameter(Mandatory)][string]$DirectoryPath)

    $files = @(Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File)
    $sizeBytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sizeBytes) {
        $sizeBytes = 0
    }

    return [pscustomobject]@{
        Path      = $DirectoryPath
        FileCount = $files.Count
        SizeMB    = [Math]::Round($sizeBytes / 1MB, 2)
    }
}

# 输出统一发布目录清单摘要，作为发布脚本的最终交付凭证。
function Write-PublishSummary {
    param([AllowEmptyCollection()][object[]]$Summaries)

    Write-Host ''
    Write-Host '================ 发布产物摘要 ================' -ForegroundColor Green
    foreach ($summary in $Summaries) {
        Write-Host ("{0}  文件数 {1}  大小 {2} MB" -f $summary.Path, $summary.FileCount, $summary.SizeMB)
    }
    Write-Host '提示：发布产物中的 web.config 含 VS 占位符，IIS 部署请用 Deploy-ReportCenterIis.ps1 归一化。'
}

# 主流程：解析路径、按序发布后端与前端，并输出目录清单摘要。
function Invoke-PublishReportCenter {
    if (-not (Test-Path -LiteralPath $BackendProject -PathType Leaf) -and -not $SkipBackend) {
        throw "后端入口项目不存在：$BackendProject"
    }
    if (-not (Test-Path -LiteralPath $FrontendDirectory -PathType Container) -and -not $SkipFrontend) {
        throw "前端工程目录不存在：$FrontendDirectory"
    }

    # 可选清理：整根删除由 -CleanOutput 显式开启，避免误删其他部署归档。
    if ($CleanOutput -and (Test-Path -LiteralPath $outputRootFull)) {
        Write-Host "==> 清空发布根目录 $outputRootFull" -ForegroundColor Cyan
        Remove-Item -LiteralPath $outputRootFull -Recurse -Force
    }
    New-Item -ItemType Directory -Path $outputRootFull -Force | Out-Null

    # 按序执行发布：先服务端后前端，任一步失败立即中断，不让坏产物进入部署环节。
    $summaries = [System.Collections.Generic.List[object]]::new()
    if (-not $SkipBackend) {
        Invoke-BackendPublish -ProjectPath $BackendProject -OutputDirectory $backendOutput -BuildConfiguration $Configuration
        $summaries.Add((Get-DirectorySummary $backendOutput))
    }

    # 前端构建依赖 Node 工具链；仅在前端参与发布时解析包管理器。
    if (-not $SkipFrontend) {
        $packageManagerExe = Resolve-PackageManager -Mode $PackageManager
        if (-not $SkipDependencyInstall) {
            Install-FrontendDependencies -FrontendPath $FrontendDirectory -PackageManagerExe $packageManagerExe
        }
        New-Item -ItemType Directory -Path $frontendOutput -Force | Out-Null
        Invoke-FrontendBuild -FrontendPath $FrontendDirectory -PackageManagerExe $packageManagerExe -TargetDirectory $frontendOutput
        $summaries.Add((Get-DirectorySummary $frontendOutput))
    }

    if ($summaries.Count -eq 0) {
        throw '后端与前端均被跳过，没有产出任何发布内容；请至少保留一个发布目标。'
    }

    Write-PublishSummary -Summaries $summaries.ToArray()
}

Invoke-PublishReportCenter
