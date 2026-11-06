[CmdletBinding()]
param(
    [string]$Server = '188.188.2.5',
    [ValidateRange(1, 65535)]
    [int]$Port = 1433,
    [string]$Database = 'hd_report_center',
    [string]$User = 'hdhis'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Database -notmatch '^[A-Za-z0-9_]+$') {
    throw '数据库名只能包含字母、数字和下划线。'
}

$password = $env:HD_REPORT_CENTER_DB_PASSWORD
if ([string]::IsNullOrWhiteSpace($password)) {
    throw '未设置 HD_REPORT_CENTER_DB_PASSWORD，拒绝在命令参数或脚本中接收明文密码。'
}

$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
$builder['Data Source'] = "$Server,$Port"
$builder['Initial Catalog'] = $Database
$builder['User ID'] = $User
$builder['Password'] = $password
$builder['Encrypt'] = $false
$builder['TrustServerCertificate'] = $true
$builder['Connect Timeout'] = 15
$builder['Application Name'] = 'hd-report-center-ai-assistant-schema'

$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
try {
    $connection.Open()
    if ($connection.Database -ne $Database) {
        throw "实际连接数据库为 $($connection.Database)，与目标数据库 $Database 不一致。"
    }

    # 前置校验：AI 配置表依赖报表中心基础结构已就位；RcDataSource 缺失说明基础脚本尚未执行。
    $preflight = $connection.CreateCommand()
    $preflight.CommandText = @'
IF OBJECT_ID(N'dbo.RcDataSource', N'U') IS NULL
    THROW 51000, N'缺少报表中心基础配置表，请先执行 Apply-ReportConfigurationSchema.ps1。', 1;
'@
    $preflight.ExecuteNonQuery() | Out-Null

    $targetTables = @('RcAiSettings', 'RcAiAuditLog')
    $tableCheck = $connection.CreateCommand()
    $tableCheck.CommandText = @'
SELECT name
FROM sys.tables
WHERE schema_id = SCHEMA_ID(N'dbo')
  AND name IN (N'RcAiSettings', N'RcAiAuditLog');
'@
    $reader = $tableCheck.ExecuteReader()
    $existingTables = [System.Collections.Generic.List[string]]::new()
    try {
        while ($reader.Read()) {
            $existingTables.Add($reader.GetString(0))
        }
    }
    finally {
        $reader.Dispose()
    }

    if ($existingTables.Count -gt 0 -and $existingTables.Count -lt $targetTables.Count) {
        throw "AI 助手表只存在 $($existingTables.Count) 张（$($existingTables -join ', ')），拒绝自动补齐半成品结构。"
    }

    if ($existingTables.Count -eq $targetTables.Count) {
        # 幂等出口前的列升级：阶段四为 RcAiSettings 幂等追加 VisionModel 列（空值回退主模型），老库无痛升级。
        $visionColumnCheck = $connection.CreateCommand()
        $visionColumnCheck.CommandText = @'
IF OBJECT_ID(N'dbo.RcAiSettings', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.RcAiSettings', N'VisionModel') IS NULL
BEGIN
    ALTER TABLE dbo.RcAiSettings ADD VisionModel nvarchar(128) NOT NULL CONSTRAINT DF_RcAiSettings_VisionModel DEFAULT ('');
END;
'@
        $visionColumnCheck.ExecuteNonQuery() | Out-Null

        # 幂等出口：两张表都已存在时只校验必需列，绝不改列、删表或覆盖配置。
        $schemaCheck = $connection.CreateCommand()
        $schemaCheck.CommandText = @'
SELECT COUNT_BIG(1)
FROM sys.columns c
INNER JOIN sys.tables t ON t.object_id = c.object_id
WHERE t.schema_id = SCHEMA_ID(N'dbo')
  AND (
       (t.name = N'RcAiSettings' AND c.name IN (N'Id', N'IsEnabled', N'BaseUrl', N'ApiKey', N'Model', N'VisionModel', N'SampleRowLimit', N'SqlProbeEnabled', N'SqlProbeRowLimit', N'MaxToolRounds', N'CreateTime', N'UpdateTime', N'CreateUserId', N'CreateUserName', N'UpdateUserId', N'UpdateUserName'))
    OR (t.name = N'RcAiAuditLog' AND c.name IN (N'Id', N'Time', N'SessionId', N'ToolName', N'DataSourceCode', N'SqlText', N'RowCount', N'DurationMs', N'Success', N'ErrorSummary', N'CreateTime', N'UpdateTime', N'CreateUserId', N'CreateUserName', N'UpdateUserId', N'UpdateUserName'))
  );
'@
        $requiredColumnCount = 32
        $actualColumnCount = [int64]$schemaCheck.ExecuteScalar()
        if ($actualColumnCount -ne $requiredColumnCount) {
            throw "AI 助手两张表已存在，但必需列仅匹配 $actualColumnCount/$requiredColumnCount，拒绝继续。"
        }

        Write-Host 'AI 助手表已存在且必需列完整，本次未修改数据库。'
        return
    }

    $transaction = $connection.BeginTransaction()
    try {
        $command = $connection.CreateCommand()
        $command.Transaction = $transaction
        $command.CommandTimeout = 120
        # RcAiSettings 固定单行配置：脚本只负责建表和播种一行默认关闭的配置，是否启用由管理页决定。
        # RcAiAuditLog 按时间增长：SessionId/Time/ToolName 上索引便于按会话或时间回溯排查。
        $command.CommandText = @'
IF OBJECT_ID(N'dbo.RcAiSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RcAiSettings
    (
        Id bigint NOT NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_RcAiSettings_IsEnabled DEFAULT (0),
        BaseUrl nvarchar(512) NOT NULL CONSTRAINT DF_RcAiSettings_BaseUrl DEFAULT (''),
        ApiKey nvarchar(512) NULL,
        Model nvarchar(128) NOT NULL CONSTRAINT DF_RcAiSettings_Model DEFAULT (''),
        VisionModel nvarchar(128) NOT NULL CONSTRAINT DF_RcAiSettings_VisionModel DEFAULT (''),
        SampleRowLimit int NOT NULL CONSTRAINT DF_RcAiSettings_SampleRowLimit DEFAULT (20),
        SqlProbeEnabled bit NOT NULL CONSTRAINT DF_RcAiSettings_SqlProbeEnabled DEFAULT (1),
        SqlProbeRowLimit int NOT NULL CONSTRAINT DF_RcAiSettings_SqlProbeRowLimit DEFAULT (100),
        MaxToolRounds int NOT NULL CONSTRAINT DF_RcAiSettings_MaxToolRounds DEFAULT (8),
        CreateTime datetime NULL,
        UpdateTime datetime NULL,
        CreateUserId bigint NULL,
        CreateUserName nvarchar(64) NULL,
        UpdateUserId bigint NULL,
        UpdateUserName nvarchar(64) NULL,
        CONSTRAINT PK_RcAiSettings PRIMARY KEY (Id)
    );

    INSERT INTO dbo.RcAiSettings
        (Id, IsEnabled, BaseUrl, ApiKey, Model, SampleRowLimit, SqlProbeEnabled, SqlProbeRowLimit, MaxToolRounds, CreateTime)
    SELECT 1, 0, N'', NULL, N'', 20, 1, 100, 8, SYSDATETIME()
    WHERE NOT EXISTS (SELECT 1 FROM dbo.RcAiSettings);
END;

IF OBJECT_ID(N'dbo.RcAiAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RcAiAuditLog
    (
        Id bigint NOT NULL,
        Time datetime NOT NULL,
        SessionId nvarchar(64) NOT NULL,
        ToolName nvarchar(64) NOT NULL,
        DataSourceCode nvarchar(64) NULL,
        SqlText nvarchar(2000) NULL,
        [RowCount] int NULL,
        DurationMs int NOT NULL,
        Success bit NOT NULL,
        ErrorSummary nvarchar(512) NULL,
        CreateTime datetime NULL,
        UpdateTime datetime NULL,
        CreateUserId bigint NULL,
        CreateUserName nvarchar(64) NULL,
        UpdateUserId bigint NULL,
        UpdateUserName nvarchar(64) NULL,
        CONSTRAINT PK_RcAiAuditLog PRIMARY KEY (Id)
    );

    CREATE INDEX ix_RcAiAuditLog_Time ON dbo.RcAiAuditLog(Time);
    CREATE INDEX ix_RcAiAuditLog_SessionId ON dbo.RcAiAuditLog(SessionId, ToolName);
END;
'@
        $command.ExecuteNonQuery() | Out-Null

        # 收尾校验：确保单行配置恰好一行；历史上出现多行时拒绝继续，避免读侧随机取行。
        $guard = $connection.CreateCommand()
        $guard.Transaction = $transaction
        $guard.CommandText = @'
IF (SELECT COUNT_BIG(1) FROM dbo.RcAiSettings) > 1
    THROW 51000, N'RcAiSettings 必须保持单行配置，请人工清理多余行后再执行本脚本。', 1;
'@
        $guard.ExecuteNonQuery() | Out-Null

        $transaction.Commit()
        Write-Host '已完成 AI 助手结构升级：RcAiSettings（单行默认关闭）与 RcAiAuditLog 已就绪。'
    }
    catch {
        $transaction.Rollback()
        throw
    }
    finally {
        $transaction.Dispose()
    }
}
finally {
    $connection.Dispose()
}
