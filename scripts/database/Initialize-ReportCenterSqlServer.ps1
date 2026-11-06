[CmdletBinding()]
param(
    [string]$Server = '188.188.2.5',
    [ValidateRange(1, 65535)]
    [int]$Port = 1433,
    [string]$Database = 'hd_report_center',
    [string]$User = 'hdhis',
    [string]$Collation = 'Chinese_PRC_CI_AS',
    [string]$AppDllPath = (Join-Path $PSScriptRoot '..\..\Admin.NET\Admin.NET.Web.Entry\bin\Release\net8.0\Admin.NET.Web.Entry.dll'),
    [switch]$ResumeEmptyDatabase,
    [ValidateRange(30, 1800)]
    [int]$InitializationTimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 校验 Initialize-ReportCenterSqlServer 的 Assert Safe Sql Identifier 条件，并返回可供后续流程判断的结果。
function Assert-SafeSqlIdentifier {
    param(
        [Parameter(Mandatory)]
        [string]$Value,
        [Parameter(Mandatory)]
        [string]$DisplayName
    )

    # 建库语句不支持将库名和排序规则作为 SQL 参数，因此先限制为安全标识符字符集。
    if ($Value -notmatch '^[A-Za-z0-9_]+$') {
        throw "$DisplayName 只能包含字母、数字和下划线。"
    }
}

# 构建 Initialize-ReportCenterSqlServer 所需的 New Report Center Connection String 内容，并保证生成结果符合调用约定。
function New-ReportCenterConnectionString {
    param(
        [Parameter(Mandatory)]
        [string]$InitialCatalog,
        [Parameter(Mandatory)]
        [string]$Password
    )

    # 使用结构化构造器处理密码中可能存在的分号等特殊字符。
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source'] = "$Server,$Port"
    $builder['Initial Catalog'] = $InitialCatalog
    $builder['User ID'] = $User
    $builder['Password'] = $Password
    $builder['Encrypt'] = $false
    $builder['TrustServerCertificate'] = $true
    $builder['Connect Timeout'] = 15
    $builder['Application Name'] = 'hd-report-center-bootstrap'
    return $builder.ConnectionString
}

# 执行 Initialize-ReportCenterSqlServer 的 Invoke Sql Scalar 核心流程，并保持错误和结果语义稳定。
function Invoke-SqlScalar {
    param(
        [Parameter(Mandatory)]
        [string]$ConnectionString,
        [Parameter(Mandatory)]
        [string]$CommandText,
        [hashtable]$Parameters = @{}
    )

    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $CommandText
        $command.CommandTimeout = 60

        foreach ($entry in $Parameters.GetEnumerator()) {
            $parameter = $command.Parameters.AddWithValue($entry.Key, $entry.Value)
            $parameter | Out-Null
        }

        $result = $command.ExecuteScalar()
        if ($null -eq $result -or $result -is [DBNull]) {
            return $null
        }

        return $result
    }
    finally {
        $connection.Dispose()
    }
}

# 执行 Initialize-ReportCenterSqlServer 的 Invoke Sql Non Query 核心流程，并保持错误和结果语义稳定。
function Invoke-SqlNonQuery {
    param(
        [Parameter(Mandatory)]
        [string]$ConnectionString,
        [Parameter(Mandatory)]
        [string]$CommandText
    )

    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $CommandText
        $command.CommandTimeout = 120
        $command.ExecuteNonQuery() | Out-Null
    }
    finally {
        $connection.Dispose()
    }
}

# 查询或读取 Initialize-ReportCenterSqlServer 所需的 Get Sanitized Log Tail 数据，并按调用约定返回结果。
function Get-SanitizedLogTail {
    param(
        [Parameter(Mandatory)]
        [string[]]$Paths,
        [Parameter(Mandatory)]
        [string]$Password
    )

    # 失败时只返回少量日志，并将运行时密码替换掉，防止凭据进入终端记录。
    $escapedPassword = [Regex]::Escape($Password)
    return $Paths |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-Content -LiteralPath $_ -Tail 20 -ErrorAction SilentlyContinue } |
        ForEach-Object { $_ -replace $escapedPassword, '***' }
}

Assert-SafeSqlIdentifier -Value $Database -DisplayName '数据库名'
Assert-SafeSqlIdentifier -Value $Collation -DisplayName '排序规则'

$password = $env:HD_REPORT_CENTER_DB_PASSWORD
if ([string]::IsNullOrWhiteSpace($password)) {
    throw '未设置 HD_REPORT_CENTER_DB_PASSWORD，拒绝在命令参数或脚本中接收明文密码。'
}

$resolvedAppDllPath = (Resolve-Path -LiteralPath $AppDllPath).Path
$masterConnectionString = New-ReportCenterConnectionString -InitialCatalog 'master' -Password $password
$targetConnectionString = New-ReportCenterConnectionString -InitialCatalog $Database -Password $password

# 先在 master 库中做存在性保护。该脚本只用于首次初始化，不会覆盖或改造已存在的库。
$existingDatabaseId = Invoke-SqlScalar `
    -ConnectionString $masterConnectionString `
    -CommandText 'SELECT DB_ID(@DatabaseName);' `
    -Parameters @{ '@DatabaseName' = $Database }
if ($null -ne $existingDatabaseId) {
    if (-not $ResumeEmptyDatabase) {
        throw "目标数据库 $Database 已存在，为避免覆盖数据已拒绝继续。"
    }

    # 续跑只用于“建库成功、建表未开始”的恢复场景；任何用户表都会使续跑失败。
    $existingTableCount = Invoke-SqlScalar `
        -ConnectionString $targetConnectionString `
        -CommandText 'SELECT COUNT_BIG(1) FROM sys.tables;'
    if ([int64]$existingTableCount -ne 0) {
        throw "目标数据库 $Database 已有 $existingTableCount 张表，拒绝使用 ResumeEmptyDatabase。"
    }

    Write-Host "目标库已存在且为空库，将续跑 Admin.NET 建表和种子初始化。"
}
else {
    Write-Host "正在创建数据库 $Database（$Collation）..."
    Invoke-SqlNonQuery `
        -ConnectionString $masterConnectionString `
        -CommandText "CREATE DATABASE [$Database] COLLATE $Collation;"
}

$actualCollation = Invoke-SqlScalar `
    -ConnectionString $masterConnectionString `
    -CommandText 'SELECT collation_name FROM sys.databases WHERE name = @DatabaseName;' `
    -Parameters @{ '@DatabaseName' = $Database }
if ($actualCollation -ne $Collation) {
    throw "数据库已创建，但排序规则为 $actualCollation，与要求的 $Collation 不一致。"
}

$environmentKeys = @(
    'HD_REPORT_CENTER_DB_CONNECTION_STRING',
    'HD_REPORT_CENTER_DB_INITIALIZE'
)
$previousEnvironment = @{}
foreach ($key in $environmentKeys) {
    $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}

$stdoutPath = [System.IO.Path]::GetTempFileName()
$stderrPath = [System.IO.Path]::GetTempFileName()
$initializationProcess = $null

try {
    # 仅临时子进程开启建表和种子。正常运行配置始终保持三个开关为 false。
    $env:HD_REPORT_CENTER_DB_CONNECTION_STRING = $targetConnectionString
    $env:HD_REPORT_CENTER_DB_INITIALIZE = 'true'

    $workingDirectory = Split-Path -Parent $resolvedAppDllPath
    $arguments = "`"$resolvedAppDllPath`" --urls http://127.0.0.1:0"
    $initializationProcess = Start-Process `
        -FilePath 'dotnet' `
        -ArgumentList $arguments `
        -WorkingDirectory $workingDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    Write-Host '正在使用 Admin.NET CodeFirst 初始化系统表和种子数据...'
    $deadline = [DateTime]::UtcNow.AddSeconds($InitializationTimeoutSeconds)
    $isReady = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $initializationProcess.Refresh()
        if ($initializationProcess.HasExited) {
            $tail = Get-SanitizedLogTail -Paths @($stdoutPath, $stderrPath) -Password $password
            throw "Admin.NET 初始化进程提前退出（退出码 $($initializationProcess.ExitCode)）。`n$($tail -join [Environment]::NewLine)"
        }

        $requiredTableCount = Invoke-SqlScalar `
            -ConnectionString $targetConnectionString `
            -CommandText "SELECT COUNT_BIG(1) FROM sys.tables WHERE name IN (N'SysUser', N'RcReportTemplate', N'RcExchangeMapping');"

        $sysUserCount = 0
        if ([int64]$requiredTableCount -eq 3) {
            $sysUserCount = Invoke-SqlScalar `
                -ConnectionString $targetConnectionString `
                -CommandText 'SELECT COUNT_BIG(1) FROM dbo.SysUser;'
        }

        # 项目会过滤 Microsoft.Hosting 日志，不能依赖“Now listening”文本。
        # 直接检查子进程的 TCP 监听端口，可以确认 Kestrel 启动管线已完成。
        $applicationStarted = [bool](Get-NetTCPConnection `
            -State Listen `
            -OwningProcess $initializationProcess.Id `
            -ErrorAction SilentlyContinue)
        if ([int64]$requiredTableCount -eq 3 -and [int64]$sysUserCount -gt 0 -and $applicationStarted) {
            $isReady = $true
            break
        }

        Start-Sleep -Seconds 1
    }

    if (-not $isReady) {
        throw "Admin.NET 在 $InitializationTimeoutSeconds 秒内未完成初始化。数据库保留供排查，未执行任何模板迁移。"
    }

    $tableCount = Invoke-SqlScalar `
        -ConnectionString $targetConnectionString `
        -CommandText 'SELECT COUNT_BIG(1) FROM sys.tables;'
    Write-Host "数据库初始化完成：用户表已有种子数据，共创建 $tableCount 张表。"
}
finally {
    if ($null -ne $initializationProcess) {
        $initializationProcess.Refresh()
        if (-not $initializationProcess.HasExited) {
            Stop-Process -Id $initializationProcess.Id -Force -ErrorAction SilentlyContinue
            $initializationProcess.WaitForExit(5000) | Out-Null
        }
        $initializationProcess.Dispose()
    }

    foreach ($key in $environmentKeys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }

    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
}
