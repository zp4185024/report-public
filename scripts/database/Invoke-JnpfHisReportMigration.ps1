[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceServer,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 65535)]
    [int]$SourcePort,
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceDatabase,
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceUser,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')]
    [string]$ReportDataSourceCode,
    [Parameter(Mandatory = $true)]
    [ValidateSet('SqlServer', 'Oracle', 'MySql', 'PostgreSQL')]
    [string]$ReportDataSourceType,
    [string[]]$SourceReportId = @(),
    [string[]]$SourceReportName = @(),
    [ValidateRange(0, 5000)]
    [int]$MaxReports = 0,
    [string]$TargetServer,
    [ValidateRange(1, 65535)]
    [int]$TargetPort,
    [string]$TargetDatabase,
    [string]$TargetUser,
    [ValidateSet('Skip', 'Fail')]
    [string]$ExistingReportAction = 'Skip',
    [AllowNull()]
    [string]$OutputDirectory,
    [switch]$InventoryOnly,
    [switch]$WriteTarget
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'JnpfHisReportMigration.psm1') -Force

$sourcePassword = $env:JNPF_MYSQL_PASSWORD
if ([string]::IsNullOrWhiteSpace($sourcePassword)) {
    throw '未设置 JNPF_MYSQL_PASSWORD。密码只能通过当前 PowerShell 进程环境变量传入。'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $repositoryRoot "output/jnpf-migration/$timestamp"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Write-Host '正在只读扫描 JNPF 启用报表配置……'
$sourceConnection = New-JnpfSourceConnection $SourceServer $SourcePort $SourceDatabase $SourceUser $sourcePassword
try {
    $packages = @(Get-JnpfSourcePackages $sourceConnection)
}
finally {
    $sourceConnection.Dispose()
}

$candidates = @($packages | ForEach-Object {
    New-JnpfMigrationCandidate $_ $ReportDataSourceCode $ReportDataSourceType
})
$inventory = @($candidates | ForEach-Object { ConvertTo-JnpfInventoryRecord $_ })
$inventoryCsvPath = Join-Path $OutputDirectory 'inventory.csv'
$inventoryJsonPath = Join-Path $OutputDirectory 'inventory.json'
$inventory | Export-Csv -LiteralPath $inventoryCsvPath -NoTypeInformation -Encoding utf8BOM
$inventory | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $inventoryJsonPath -Encoding utf8

$readyInventory = @($inventory | Where-Object Status -eq 'Ready')
$blockedInventory = @($inventory | Where-Object Status -eq 'Blocked')
Write-Host "盘点完成：启用报表 $($inventory.Count) 个，可迁移 $($readyInventory.Count) 个，阻断 $($blockedInventory.Count) 个。"
Write-Host "盘点清单：$inventoryCsvPath"
if ($inventory.Count -gt 0) {
    $inventory |
        Sort-Object Status, FieldCount, ReportName |
        Select-Object ReportName, SourceReportId, DataSourceNames, Status, FieldCount, ParameterCount, Markers, Blockers |
        Format-Table -Wrap -AutoSize
}

$readyCandidates = @($candidates | Where-Object {
    $_.Migratable -and $_.Status -eq 'Ready' -and
    ($SourceReportId.Count -eq 0 -or $SourceReportId -contains $_.Package.ReportId) -and
    ($SourceReportName.Count -eq 0 -or $SourceReportName -contains $_.Package.ReportName)
} | Sort-Object { $_.Migration.FieldCount }, { $_.Migration.ParameterCount }, { $_.Package.ReportName } |
    ForEach-Object { $_ })
$selected = if ($MaxReports -eq 0) { $readyCandidates } else { @($readyCandidates | Select-Object -First $MaxReports) }

$selection = @($selected | ForEach-Object {
    [pscustomobject]@{
        SourceReportId = $_.Package.ReportId
        SourceVersionId = $_.Package.VersionId
        ReportName = $_.Package.ReportName
        TargetReportCode = $_.Migration.ReportCode
        ReportDataSourceCode = $_.Migration.DataSourceCode
        FieldCount = $_.Migration.FieldCount
        ParameterCount = $_.Migration.ParameterCount
        DetailFieldCount = $_.Migration.DetailFieldCount
        Markers = $_.Markers
    }
})
$selectionPath = Join-Path $OutputDirectory 'selection.json'
$selection | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $selectionPath -Encoding utf8

if ($InventoryOnly) {
    Write-Host '当前为只读盘点模式，未准备或写入目标库。'
    return
}
if ($selected.Count -eq 0) {
    Write-Warning '没有符合筛选条件且可自动迁移的报表，未写入目标库。'
    return
}

Write-Host "本批选择 $($selected.Count) 个报表，选择清单：$selectionPath"
foreach ($item in $selection) {
    Write-Host "  $($item.ReportName) -> $($item.TargetReportCode)，字段 $($item.FieldCount)，参数 $($item.ParameterCount)"
    foreach ($marker in @($item.Markers)) {
        Write-Warning "  [$($item.TargetReportCode)] $marker"
    }
}
if (-not $WriteTarget) {
    Write-Host '当前为迁移预检，未写入目标库。确认选择清单后增加 -WriteTarget。'
    return
}

$targetConnectionString = $env:HD_REPORT_CENTER_DB_CONNECTION_STRING
$targetDatabaseForWrite = $TargetDatabase
if ([string]::IsNullOrWhiteSpace($targetConnectionString)) {
    if ([string]::IsNullOrWhiteSpace($TargetServer) -or
        $TargetPort -lt 1 -or $TargetPort -gt 65535 -or
        [string]::IsNullOrWhiteSpace($TargetDatabase) -or
        [string]::IsNullOrWhiteSpace($TargetUser)) {
        throw '未提供目标报表中心数据源。请显式传入 TargetServer、TargetPort、TargetDatabase 和 TargetUser，或设置 HD_REPORT_CENTER_DB_CONNECTION_STRING。'
    }
    if ($TargetDatabase -cne 'hd_report_center') {
        throw '目标数据库必须是 hd_report_center。'
    }
    $targetPassword = $env:HD_REPORT_CENTER_DB_PASSWORD
    if ([string]::IsNullOrWhiteSpace($targetPassword)) {
        throw '写目标库前必须设置 HD_REPORT_CENTER_DB_PASSWORD 或 HD_REPORT_CENTER_DB_CONNECTION_STRING。'
    }
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source'] = "$TargetServer,$TargetPort"
    $builder['Initial Catalog'] = $TargetDatabase
    $builder['User ID'] = $TargetUser
    $builder['Password'] = $targetPassword
    $builder['Encrypt'] = $false
    $builder['TrustServerCertificate'] = $true
    $builder['Connect Timeout'] = 15
    $builder['Application Name'] = 'hd-report-center-jnpf-report-migration'
    $targetConnectionString = $builder.ConnectionString
}
else {
    try {
        $legacyConnectionString = ConvertTo-JnpfLegacySqlConnectionString $targetConnectionString
        $targetDatabaseForWrite = [System.Data.SqlClient.SqlConnectionStringBuilder]::new(
            $legacyConnectionString).InitialCatalog
        $targetConnectionString = $legacyConnectionString
    }
    catch {
        throw 'HD_REPORT_CENTER_DB_CONNECTION_STRING 不是有效的 SQL Server 连接配置。'
    }
    if ($targetDatabaseForWrite -cne 'hd_report_center') {
        throw '目标数据库必须是 hd_report_center。'
    }
}

$results = [System.Collections.Generic.List[object]]::new()
$targetConnection = New-JnpfTargetConnection $targetConnectionString $targetDatabaseForWrite
try {
    Test-JnpfTargetSchema $targetConnection
    foreach ($candidate in $selected) {
        $migration = $candidate.Migration
        if (-not $PSCmdlet.ShouldProcess(
                "${targetDatabaseForWrite}/$($migration.ReportCode)",
                "迁移 JNPF 报表 '$($migration.ReportName)'")) {
            $results.Add([pscustomobject]@{
                ReportCode = $migration.ReportCode
                ReportName = $migration.ReportName
                Status = 'WhatIf'
                Message = 'ShouldProcess 未授权写入。'
            })
            continue
        }
        try {
            $result = Invoke-JnpfTargetInsert $targetConnection $migration $ExistingReportAction
            $results.Add($result)
            Write-Host "[$($result.Status)] $($result.ReportCode)：$($result.Message)"
        }
        catch {
            $results.Add([pscustomobject]@{
                ReportCode = $migration.ReportCode
                ReportName = $migration.ReportName
                Status = 'Failed'
                Message = $_.Exception.Message
            })
            Write-Error "迁移 '$($migration.ReportCode)' 失败：$($_.Exception.Message)" -ErrorAction Continue
        }
    }
}
finally {
    $targetConnection.Dispose()
}

$resultsPath = Join-Path $OutputDirectory 'migration-results.json'
$results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultsPath -Encoding utf8
Write-Host "迁移结果：$resultsPath"
if (@($results | Where-Object Status -eq 'Failed').Count -gt 0) {
    throw '本批存在迁移失败项，详情见 migration-results.json。'
}
