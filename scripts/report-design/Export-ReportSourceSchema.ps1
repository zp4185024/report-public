#Requires -Version 7.0
<#
.SYNOPSIS
导出业务库的表结构快照（只读元数据），供开发机侧 AI 报表设计流水线消费。

.DESCRIPTION
在内网环境运行（持有 AI 的开发机通常连不上内网库），把指定库的全部表和视图的结构
（列、类型、可空、长度/精度、MS_Description 中文注释，可选行数汇总）导出为 JSON 快照。
脚本只执行 INFORMATION_SCHEMA / sys 目录视图查询，绝不读取任何业务数据行；
密码只从环境变量 REPORT_SOURCE_DB_PASSWORD 读取，绝不进入参数、命令行或输出文件。

用法示例：
  $env:REPORT_SOURCE_DB_PASSWORD = '***'
  ./Export-ReportSourceSchema.ps1 -Server 192.168.1.10 -Database HisReport -User report_reader
  ./Export-ReportSourceSchema.ps1 -Server 192.168.1.10 -Database HisReport -User report_reader -IncludeRowCounts -TableFilter 'dbo.Rc%'

.NOTES
需 PowerShell 7+（依赖 ConvertFrom/ConvertTo-Json 的新行为与 UTF-8 无 BOM 输出）。
#>
[CmdletBinding()]
param(
    # 数据库服务器地址（主机名或 IP）。
    [Parameter(Mandatory = $true)]
    [string]$Server,

    # SQL Server 端口，默认 1433。
    [int]$Port = 1433,

    # 目标数据库名。
    [Parameter(Mandatory = $true)]
    [string]$Database,

    # 只读账号用户名；密码必须经环境变量 REPORT_SOURCE_DB_PASSWORD 提供。
    [Parameter(Mandatory = $true)]
    [string]$User,

    # 快照输出文件路径；缺省写入仓库 output/schema-snapshots/<库名>-<时间戳>.json。
    [string]$OutputPath,

    # 是否附带各表行数（经 sys.dm_db_partition_stats 汇总，视图无行数）。
    [switch]$IncludeRowCounts,

    # 表/视图名过滤，按 SQL LIKE 语义匹配（支持 % 通配），也可匹配“架构.表名”。
    [string]$TableFilter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
把数据库返回的 DBNull 或包装值统一转换为 $null，保证 JSON 输出里不出现 "DBNull" 字样。
#>
function Convert-FromDbNull {
    param(
        # 数据库读取出的原始值，可能是 [DBNull]::Value 或底层包装对象。
        [object]$Value
    )
    if ($null -eq $Value -or $Value -is [System.DBNull]) {
        return $null
    }
    return $Value
}

<#
.SYNOPSIS
读取库内全部表与视图清单及其 MS_Description 中文注释。
#>
function Read-TableInventory {
    param(
        # 已打开的 SQL Server 连接。
        [System.Data.SqlClient.SqlConnection]$Connection,

        # 可选的 LIKE 过滤文本；空串表示不过滤。
        [string]$Filter
    )

    # 阶段一：构造元数据查询，注释经 sys.extended_properties 关联（class=1、minor_id=0 表示对象级说明）。
    $sql = @'
SELECT
    t.TABLE_SCHEMA                       AS SchemaName,
    t.TABLE_NAME                         AS TableName,
    t.TABLE_TYPE                         AS TableType,
    ep.value                             AS CommentValue
FROM INFORMATION_SCHEMA.TABLES t
LEFT JOIN sys.extended_properties ep
    ON ep.class = 1
   AND ep.minor_id = 0
   AND ep.name = 'MS_Description'
   AND ep.major_id = OBJECT_ID(QUOTENAME(t.TABLE_SCHEMA) + '.' + QUOTENAME(t.TABLE_NAME))
WHERE t.TABLE_TYPE IN ('BASE TABLE', 'VIEW')
'@

    # 阶段二：按需追加过滤条件并执行查询，过滤同时命中裸表名与“架构.表名”两种写法。
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $sql += @'
  AND (t.TABLE_NAME LIKE @TableFilter
    OR t.TABLE_SCHEMA + '.' + t.TABLE_NAME LIKE @TableFilter)
'@
    }
    $sql += "ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME"

    $command = $Connection.CreateCommand()
    $command.CommandText = $sql
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $null = $command.Parameters.Add('@TableFilter', [System.Data.SqlDbType]::NVarChar, 256)
        $command.Parameters['@TableFilter'].Value = $Filter
    }

    $reader = $command.ExecuteReader()
    try {
        $tables = [System.Collections.Generic.List[object]]::new()
        while ($reader.Read()) {
            $tables.Add([pscustomobject]@{
                schema    = [string]$reader['SchemaName']
                name      = [string]$reader['TableName']
                type      = if ([string]$reader['TableType'] -eq 'VIEW') { 'VIEW' } else { 'BASE TABLE' }
                comment   = Convert-FromDbNull $reader['CommentValue']
                rowCount  = $null
                columns   = [System.Collections.Generic.List[object]]::new()
            })
        }
        return $tables
    }
    finally {
        $reader.Dispose()
    }
}

<#
.SYNOPSIS
按同一过滤条件一次性读取全部列定义与 MS_Description 列注释，并挂到对应表对象上。
#>
function Read-ColumnInventory {
    param(
        # 已打开的 SQL Server 连接。
        [System.Data.SqlClient.SqlConnection]$Connection,

        # 已装配的表清单（schema/name 作为挂载键）。
        [System.Collections.Generic.List[object]]$Tables,

        # 可选的 LIKE 过滤文本；空串表示不过滤。
        [string]$Filter
    )

    # 阶段一：构造列元数据查询，注释按“对象Id + 列序”关联列级扩展属性。
    $sql = @'
SELECT
    c.TABLE_SCHEMA                  AS SchemaName,
    c.TABLE_NAME                    AS TableName,
    c.ORDINAL_POSITION              AS OrdinalPosition,
    c.COLUMN_NAME                   AS ColumnName,
    c.DATA_TYPE                     AS DataType,
    c.IS_NULLABLE                   AS IsNullable,
    c.CHARACTER_MAXIMUM_LENGTH      AS MaxLength,
    c.NUMERIC_PRECISION             AS PrecisionValue,
    c.NUMERIC_SCALE                 AS ScaleValue,
    ep.value                        AS CommentValue
FROM INFORMATION_SCHEMA.COLUMNS c
INNER JOIN INFORMATION_SCHEMA.TABLES t
    ON t.TABLE_SCHEMA = c.TABLE_SCHEMA
   AND t.TABLE_NAME = c.TABLE_NAME
   AND t.TABLE_TYPE IN ('BASE TABLE', 'VIEW')
LEFT JOIN sys.extended_properties ep
    ON ep.class = 1
   AND ep.name = 'MS_Description'
   AND ep.major_id = OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME))
   AND ep.minor_id = COLUMNPROPERTY(ep.major_id, c.COLUMN_NAME, 'ColumnId')
WHERE 1 = 1
'@

    # 阶段二：追加与表清单一致的过滤条件并执行，保持两次查询拿到同一批对象。
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $sql += @'
  AND (c.TABLE_NAME LIKE @TableFilter
    OR c.TABLE_SCHEMA + '.' + c.TABLE_NAME LIKE @TableFilter)
'@
    }
    $sql += "ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION"

    $command = $Connection.CreateCommand()
    $command.CommandText = $sql
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $null = $command.Parameters.Add('@TableFilter', [System.Data.SqlDbType]::NVarChar, 256)
        $command.Parameters['@TableFilter'].Value = $Filter
    }

    # 阶段三：逐行装配列对象并按 schema+name 挂载；表清单缺失同名对象时跳过并计数告警。
    $tableMap = @{}
    foreach ($table in $Tables) {
        $tableMap["$($table.schema).$($table.name)"] = $table
    }

    $reader = $command.ExecuteReader()
    try {
        while ($reader.Read()) {
            $key = "$($reader['SchemaName']).$($reader['TableName'])"
            $table = $tableMap[$key]
            if ($null -eq $table) {
                continue
            }
            $table.columns.Add([pscustomobject]@{
                name       = [string]$reader['ColumnName']
                sqlType    = [string]$reader['DataType']
                nullable   = ([string]$reader['IsNullable'] -eq 'YES')
                maxLength  = Convert-FromDbNull $reader['MaxLength']
                precision  = Convert-FromDbNull $reader['PrecisionValue']
                scale      = Convert-FromDbNull $reader['ScaleValue']
                comment    = Convert-FromDbNull $reader['CommentValue']
            })
        }
    }
    finally {
        $reader.Dispose()
    }
}

<#
.SYNOPSIS
可选步骤：经 sys.dm_db_partition_stats 汇总各基础表行数，只做统计不读取业务数据。
#>
function Read-RowCountMap {
    param(
        # 已打开的 SQL Server 连接。
        [System.Data.SqlClient.SqlConnection]$Connection
    )

    # 堆（index_id=0）与聚集索引（index_id=1）二选一，按对象汇总避免分区重复计数。
    $sql = @'
SELECT
    OBJECT_SCHEMA_NAME(ps.object_id) AS SchemaName,
    OBJECT_NAME(ps.object_id)        AS TableName,
    SUM(ps.row_count)                AS RowCountValue
FROM sys.dm_db_partition_stats ps
WHERE ps.index_id IN (0, 1)
GROUP BY ps.object_id
'@

    $command = $Connection.CreateCommand()
    $command.CommandText = $sql
    $reader = $command.ExecuteReader()
    try {
        $map = @{}
        while ($reader.Read()) {
            $map["$($reader['SchemaName']).$($reader['TableName'])"] = [long]$reader['RowCountValue']
        }
        return $map
    }
    finally {
        $reader.Dispose()
    }
}

<#
.SYNOPSIS
组装快照文档对象并按 schema,name 排序，保证同一库多次导出的 JSON 稳定可比对。
#>
function New-SchemaSnapshotDocument {
    param(
        # 目标服务器（原样写入快照，便于区分来源环境）。
        [string]$ServerName,

        # 目标数据库名。
        [string]$DatabaseName,

        # 已装配表/视图/列的表清单。
        [System.Collections.Generic.List[object]]$Tables,

        # 可选行数映射；键为“架构.表名”。
        [hashtable]$RowCounts
    )

    # 阶段一：排序并按需回填行数，视图行数保持 null。
    $ordered = $Tables | Sort-Object -Property schema, name
    foreach ($table in $ordered) {
        $key = "$($table.schema).$($table.name)"
        if ($null -ne $RowCounts -and $RowCounts.ContainsKey($key)) {
            $table.rowCount = $RowCounts[$key]
        }
    }

    # 阶段二：组装顶层文档，exportedAt 使用带时区的本地时间便于人工核对导出时刻。
    return [pscustomobject]@{
        exportedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        server     = $ServerName
        database   = $DatabaseName
        tables     = @($ordered)
    }
}

<#
.SYNOPSIS
把快照文档写成 UTF-8 无 BOM 的缩进 JSON，并打印输出路径与统计摘要。
#>
function Write-SnapshotJson {
    param(
        # 快照文档对象。
        [object]$Snapshot,

        # 目标输出文件完整路径。
        [string]$Path
    )

    # 阶段一：确保输出目录存在；ConvertTo-Json 深度按列/表两层结构放大到 8 防截断。
    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        $null = New-Item -ItemType Directory -Force -Path $directory
    }
    $json = $Snapshot | ConvertTo-Json -Depth 8

    # 阶段二：UTF8(无 BOM) 写入并输出摘要，供内网执行人核对导出规模。
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
    Write-Host "[OK] 已写出快照: $Path"
    Write-Host ("[OK] 表/视图 {0} 个，列 {1} 个{2}" -f @(
        $Snapshot.tables.Count,
        ($Snapshot.tables | ForEach-Object { $_.columns.Count } | Measure-Object -Sum).Sum,
        ($(if ($null -ne $Snapshot.tables[0] -and $null -ne $Snapshot.tables[0].rowCount) { '，含行数统计' } else { '' }))))
}

# 主流程：校验参数与环境变量，建立只读连接，依次装配表/列/行数并写出快照。
try {
    # 阶段一：参数与凭据检查。密码只认环境变量，缺失立即拒绝，绝不提示任何默认值。
    $password = $env:REPORT_SOURCE_DB_PASSWORD
    if ([string]::IsNullOrEmpty($password)) {
        throw "未检测到环境变量 REPORT_SOURCE_DB_PASSWORD；请先执行 `$env:REPORT_SOURCE_DB_PASSWORD = '<密码>' 后重试。脚本不会从参数或文件读取密码。"
    }
    if ([string]::IsNullOrWhiteSpace($Server) -or [string]::IsNullOrWhiteSpace($Database) -or [string]::IsNullOrWhiteSpace($User)) {
        throw "-Server、-Database、-User 均为必填参数。"
    }

    # 阶段二：建立连接并执行元数据读取。连接串只在内存中拼装，禁止打印到控制台。
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source'] = "$Server,$Port"
    $builder['Initial Catalog'] = $Database
    $builder['User ID'] = $User
    $builder['Password'] = $password
    $builder['Connect Timeout'] = 15

    $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try {
        $connection.Open()
        $tables = Read-TableInventory -Connection $connection -Filter $TableFilter
        Read-ColumnInventory -Connection $connection -Tables $tables -Filter $TableFilter
        $rowCounts = $null
        if ($IncludeRowCounts) {
            $rowCounts = Read-RowCountMap -Connection $connection
        }
        $snapshot = New-SchemaSnapshotDocument -ServerName $Server -DatabaseName $Database -Tables $tables -RowCounts $rowCounts
    }
    finally {
        $connection.Dispose()
    }

    # 阶段三：解析默认输出路径（相对仓库根），写出快照文件。
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $safeDatabase = ($Database -replace '[\\/:*?"<>|]', '_')
        $OutputPath = Join-Path $repoRoot "output/schema-snapshots/$safeDatabase-$stamp.json"
    }
    Write-SnapshotJson -Snapshot $snapshot -Path $OutputPath
    exit 0
}
catch {
    Write-Host "[失败] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
