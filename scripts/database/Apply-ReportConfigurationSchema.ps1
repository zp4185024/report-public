[CmdletBinding()]
param(
    [string]$Server = '192.168.2.5',
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
$builder['Application Name'] = 'hd-report-center-configuration-schema'

$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
try {
    $connection.Open()
    if ($connection.Database -ne $Database) {
        throw "实际连接数据库为 $($connection.Database)，与目标数据库 $Database 不一致。"
    }

    $targetTables = @('RcReport', 'RcDataSource', 'RcDataset', 'RcReportParameter')
    $tableCheck = $connection.CreateCommand()
    $tableCheck.CommandText = @'
SELECT name
FROM sys.tables
WHERE schema_id = SCHEMA_ID(N'dbo')
  AND name IN (N'RcReport', N'RcDataSource', N'RcDataset', N'RcReportParameter');
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
        throw "配置表只存在 $($existingTables.Count) 张（$($existingTables -join ', ')），拒绝自动补齐半成品结构。"
    }

    if ($existingTables.Count -eq $targetTables.Count) {
        $schemaCheck = $connection.CreateCommand()
        $schemaCheck.CommandText = @'
SELECT COUNT_BIG(1)
FROM sys.columns c
INNER JOIN sys.tables t ON t.object_id = c.object_id
WHERE t.schema_id = SCHEMA_ID(N'dbo')
  AND (
       (t.name = N'RcReport' AND c.name IN (N'Id', N'Code', N'Name', N'SheetName', N'DatasetCode', N'PreviewSettingsJson', N'ExportSettingsJson', N'Revision', N'IsEnabled', N'CreateTime', N'UpdateTime'))
    OR (t.name = N'RcDataSource' AND c.name IN (N'Id', N'Code', N'Name', N'DbType', N'ConnectionString', N'DefaultTimeoutSeconds', N'DefaultMaxRows', N'Revision', N'IsEnabled', N'CreateTime', N'UpdateTime'))
    OR (t.name = N'RcDataset' AND c.name IN (N'Id', N'DataSourceCode', N'Code', N'Name', N'QueryText', N'FieldSchemaJson', N'TimeoutSeconds', N'MaxRows', N'Revision', N'IsEnabled', N'CreateTime', N'UpdateTime'))
    OR (t.name = N'RcReportParameter' AND c.name IN (N'Id', N'ReportCode', N'Code', N'DisplayName', N'DataType', N'ControlType', N'SourceType', N'UrlKey', N'DefaultExpression', N'FixedValue', N'IsVisible', N'IsReadOnly', N'IsRequired', N'AllowUrlPrefill', N'DisplayOrder', N'CreateTime', N'UpdateTime'))
  );
'@
        $requiredColumnCount = 51
        $actualColumnCount = [int64]$schemaCheck.ExecuteScalar()
        if ($actualColumnCount -ne $requiredColumnCount) {
            throw "4 张配置表已存在，但必需列仅匹配 $actualColumnCount/$requiredColumnCount，拒绝继续。"
        }

        Write-Host '4 张报表配置表已存在且必需列完整，本次未修改数据库。'
        return
    }

    $transaction = $connection.BeginTransaction()
    try {
        $command = $connection.CreateCommand()
        $command.Transaction = $transaction
        $command.CommandTimeout = 120
        $command.CommandText = @'
CREATE TABLE dbo.RcReport
(
    Id bigint NOT NULL,
    Code nvarchar(64) NOT NULL,
    Name nvarchar(128) NOT NULL,
    SheetName nvarchar(128) NOT NULL,
    DatasetCode nvarchar(64) NOT NULL,
    PreviewSettingsJson nvarchar(max) NOT NULL,
    ExportSettingsJson nvarchar(max) NOT NULL,
    Revision int NOT NULL,
    IsEnabled bit NOT NULL,
    CreateTime datetime NULL,
    UpdateTime datetime NULL,
    CreateUserId bigint NULL,
    CreateUserName nvarchar(64) NULL,
    UpdateUserId bigint NULL,
    UpdateUserName nvarchar(64) NULL,
    CONSTRAINT PK_RcReport PRIMARY KEY (Id)
);
CREATE UNIQUE INDEX uk_RcReport_Code ON dbo.RcReport(Code);
CREATE INDEX index_RcReport_CT ON dbo.RcReport(CreateTime);

CREATE TABLE dbo.RcDataSource
(
    Id bigint NOT NULL,
    Code nvarchar(64) NOT NULL,
    Name nvarchar(128) NOT NULL,
    DbType nvarchar(32) NOT NULL,
    ConnectionString nvarchar(max) NOT NULL,
    DefaultTimeoutSeconds int NOT NULL,
    DefaultMaxRows int NOT NULL,
    Revision int NOT NULL,
    IsEnabled bit NOT NULL,
    CreateTime datetime NULL,
    UpdateTime datetime NULL,
    CreateUserId bigint NULL,
    CreateUserName nvarchar(64) NULL,
    UpdateUserId bigint NULL,
    UpdateUserName nvarchar(64) NULL,
    CONSTRAINT PK_RcDataSource PRIMARY KEY (Id)
);
CREATE UNIQUE INDEX uk_RcDataSource_Code ON dbo.RcDataSource(Code);
CREATE INDEX index_RcDataSource_CT ON dbo.RcDataSource(CreateTime);

CREATE TABLE dbo.RcDataset
(
    Id bigint NOT NULL,
    DataSourceCode nvarchar(64) NOT NULL,
    Code nvarchar(64) NOT NULL,
    Name nvarchar(128) NOT NULL,
    QueryText nvarchar(max) NOT NULL,
    FieldSchemaJson nvarchar(max) NOT NULL,
    TimeoutSeconds int NOT NULL,
    MaxRows int NOT NULL,
    Revision int NOT NULL,
    IsEnabled bit NOT NULL,
    CreateTime datetime NULL,
    UpdateTime datetime NULL,
    CreateUserId bigint NULL,
    CreateUserName nvarchar(64) NULL,
    UpdateUserId bigint NULL,
    UpdateUserName nvarchar(64) NULL,
    CONSTRAINT PK_RcDataset PRIMARY KEY (Id)
);
CREATE UNIQUE INDEX uk_RcDataset_Code ON dbo.RcDataset(Code);
CREATE INDEX index_RcDataset_CT ON dbo.RcDataset(CreateTime);

CREATE TABLE dbo.RcReportParameter
(
    Id bigint NOT NULL,
    ReportCode nvarchar(64) NOT NULL,
    Code nvarchar(64) NOT NULL,
    DisplayName nvarchar(128) NOT NULL,
    DataType nvarchar(32) NOT NULL,
    ControlType nvarchar(32) NOT NULL,
    SourceType nvarchar(16) NOT NULL,
    UrlKey nvarchar(64) NULL,
    DefaultExpression nvarchar(256) NULL,
    FixedValue nvarchar(1024) NULL,
    IsVisible bit NOT NULL,
    IsReadOnly bit NOT NULL,
    IsRequired bit NOT NULL,
    AllowUrlPrefill bit NOT NULL,
    DisplayOrder int NOT NULL,
    CreateTime datetime NULL,
    UpdateTime datetime NULL,
    CreateUserId bigint NULL,
    CreateUserName nvarchar(64) NULL,
    UpdateUserId bigint NULL,
    UpdateUserName nvarchar(64) NULL,
    CONSTRAINT PK_RcReportParameter PRIMARY KEY (Id)
);
CREATE UNIQUE INDEX uk_RcReportParameter_ReportCode_Code ON dbo.RcReportParameter(ReportCode, Code);
CREATE INDEX index_RcReportParameter_CT ON dbo.RcReportParameter(CreateTime);
'@
        $command.ExecuteNonQuery() | Out-Null

        $verify = $connection.CreateCommand()
        $verify.Transaction = $transaction
        $verify.CommandText = @'
SELECT COUNT_BIG(1)
FROM sys.tables
WHERE schema_id = SCHEMA_ID(N'dbo')
  AND name IN (N'RcReport', N'RcDataSource', N'RcDataset', N'RcReportParameter');
'@
        if ([int64]$verify.ExecuteScalar() -ne 4) {
            throw '事务内校验未找到全部 4 张配置表。'
        }

        $transaction.Commit()
        Write-Host '已在目标库中事务创建 4 张报表配置表；现有表和数据均未修改。'
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
