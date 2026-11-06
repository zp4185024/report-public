<#
.SYNOPSIS
AI 助手 E2E 专用的同步目标样例表生命周期脚本（创建/删除 dbo.E2E_AI_TARGET_REPORT）。

.DESCRIPTION
为 S5 场景提供一张列名与源字段完全不同名的目标表，驱动“同名基线零命中 → LLM 语义补齐 →
需人工处理列显式标注”的完整链路。表结构无业务数据，仅结构；创建幂等（存在即跳过），
测试结束必须用 -Drop 清理。密码一律从环境变量 HD_REPORT_CENTER_DB_CONNECTION_STRING /
HD_REPORT_CENTER_DB_PASSWORD 读取，绝不出现在命令行参数。

.PARAMETER Create
创建样例表（幂等）。

.PARAMETER Drop
删除样例表（存在才删）。

.EXAMPLE
pwsh -File scratch_target_table.ps1 -Create
pwsh -File scratch_target_table.ps1 -Drop
#>
[CmdletBinding()]
param(
    [switch]$Create,
    [switch]$Drop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ($Create -or $Drop)) {
    throw '必须指定 -Create 或 -Drop 之一。'
}

# 解析运行环境连接串（部分键带空格需手工拆分），重建规范串；口令不打印不落盘。
$raw = $env:HD_REPORT_CENTER_DB_CONNECTION_STRING
$map = @{}
foreach ($part in $raw -split ';') {
    if ($part.Trim()) {
        $key, $value = $part -split '=', 2
        $map[$key.Trim().Replace(' ', '')] = $value
    }
}
if (-not $map['Password']) {
    $map['Password'] = $env:HD_REPORT_CENTER_DB_PASSWORD
}

$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
$builder['Data Source'] = $map['DataSource']
$builder['Initial Catalog'] = $map['InitialCatalog']
$builder['User ID'] = $map['UserID']
$builder['Password'] = $map['Password']
$builder['Encrypt'] = $false
$builder['TrustServerCertificate'] = $true
$builder['Connect Timeout'] = 15
$builder['Application Name'] = 'hd-rc-ai-e2e-scratch'

$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
try {
    $connection.Open()
    if ($Create) {
        # 三列语义等价但命名不同（驱动 LLM 语义匹配），CREATED_BY 无源对应（驱动“需人工处理”标注）。
        $command = $connection.CreateCommand()
        $command.CommandText = @'
IF OBJECT_ID(N'dbo.E2E_AI_TARGET_REPORT', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.E2E_AI_TARGET_REPORT
    (
        REPORT_CODE nvarchar(64) NOT NULL,
        REPORT_TITLE nvarchar(200) NULL,
        ENABLED_FLAG bit NOT NULL DEFAULT(0),
        CREATED_BY nvarchar(64) NULL,
        CONSTRAINT PK_E2E_AI_TARGET_REPORT PRIMARY KEY (REPORT_CODE)
    );
END;
'@
        $command.ExecuteNonQuery() | Out-Null
        Write-Host '样例目标表 dbo.E2E_AI_TARGET_REPORT 已就绪。'
    }
    if ($Drop) {
        $command = $connection.CreateCommand()
        $command.CommandText = "IF OBJECT_ID(N'dbo.E2E_AI_TARGET_REPORT', N'U') IS NOT NULL DROP TABLE dbo.E2E_AI_TARGET_REPORT;"
        $command.ExecuteNonQuery() | Out-Null
        Write-Host '样例目标表 dbo.E2E_AI_TARGET_REPORT 已删除。'
    }
}
finally {
    $connection.Dispose()
}
