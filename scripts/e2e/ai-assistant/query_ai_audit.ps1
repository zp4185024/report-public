<#
.SYNOPSIS
查询 AI 审计表 RcAiAuditLog 的行数（总量或按会话号过滤），供 E2E 断言审计落库。

.DESCRIPTION
连接参数从环境变量 HD_REPORT_CENTER_DB_CONNECTION_STRING / HD_REPORT_CENTER_DB_PASSWORD
解析，口令绝不进命令行。输出格式为机器可读的单行 JSON：
{"total":N,"filtered":M,"rows":[{"tool":"...","success":true,"rowCount":1}]}
rows 仅在传 -SessionId 时返回该会话的逐条工具明细（最多 50 条），便于测试文档引用。

.PARAMETER SessionId
可选会话号；提供时额外输出 filtered（该会话行数）与逐条工具明细。

.EXAMPLE
pwsh -File query_ai_audit.ps1
pwsh -File query_ai_audit.ps1 -SessionId ai-0123abcd
#>
[CmdletBinding()]
param(
    [string]$SessionId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
$builder['Application Name'] = 'hd-rc-ai-e2e-audit'

$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
try {
    $connection.Open()
    $totalCommand = $connection.CreateCommand()
    $totalCommand.CommandText = 'SELECT COUNT_BIG(1) FROM dbo.RcAiAuditLog'
    $total = [int64]$totalCommand.ExecuteScalar()

    $result = [ordered]@{ total = $total; filtered = $null; rows = $null }
    if ($SessionId) {
        # 会话维度：行数 + 逐条工具明细（工具/成功/行数/耗时），供测试文档核对审计与 UI 工具条一致。
        $countCommand = $connection.CreateCommand()
        $countCommand.CommandText = 'SELECT COUNT_BIG(1) FROM dbo.RcAiAuditLog WHERE SessionId = @sid'
        $null = $countCommand.Parameters.Add('@sid', [System.Data.SqlDbType]::NVarChar, 64).Value = $SessionId
        $result.filtered = [int64]$countCommand.ExecuteScalar()

        $detailCommand = $connection.CreateCommand()
        $detailCommand.CommandText = 'SELECT TOP 50 ToolName, Success, [RowCount], DurationMs, ErrorSummary FROM dbo.RcAiAuditLog WHERE SessionId = @sid ORDER BY Id'
        $null = $detailCommand.Parameters.Add('@sid', [System.Data.SqlDbType]::NVarChar, 64).Value = $SessionId
        $reader = $detailCommand.ExecuteReader()
        $rows = @()
        while ($reader.Read()) {
            $rows += [ordered]@{
                tool = $reader.GetString(0)
                success = $reader.GetBoolean(1)
                rowCount = if ($reader.IsDBNull(2)) { $null } else { $reader.GetInt32(2) }
                durationMs = $reader.GetInt32(3)
                errorSummary = if ($reader.IsDBNull(4)) { $null } else { $reader.GetString(4) }
            }
        }
        $reader.Dispose()
        $result.rows = $rows
    }
    $result | ConvertTo-Json -Compress -Depth 4
    # ConvertTo-Json 单对象直接输出；这里包一层数组保证管道输出恒为一行。
}
finally {
    $connection.Dispose()
}
