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
$builder['Application Name'] = 'hd-report-center-reusable-data-assets'

$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
try {
    $connection.Open()
    if ($connection.Database -ne $Database) {
        throw "实际连接数据库为 $($connection.Database)，与目标数据库 $Database 不一致。"
    }

    $preflight = $connection.CreateCommand()
    $preflight.CommandText = @'
IF OBJECT_ID(N'dbo.RcReport', N'U') IS NULL
    OR OBJECT_ID(N'dbo.RcDataSource', N'U') IS NULL
    OR OBJECT_ID(N'dbo.RcDataset', N'U') IS NULL
    OR OBJECT_ID(N'dbo.RcReportParameter', N'U') IS NULL
    THROW 51000, N'缺少旧版报表配置表，请先执行 Apply-ReportConfigurationSchema.ps1。', 1;

IF EXISTS (SELECT Code FROM dbo.RcDataset GROUP BY Code HAVING COUNT_BIG(1) > 1)
    THROW 51000, N'存在重复数据集编码，无法自动改为全局唯一编码。', 1;

IF COL_LENGTH(N'dbo.RcDataset', N'ReportCode') IS NOT NULL
    EXEC sys.sp_executesql N'
        IF EXISTS (SELECT ReportCode FROM dbo.RcDataset WHERE ReportCode IS NOT NULL GROUP BY ReportCode HAVING COUNT_BIG(1) > 1)
            THROW 51000, N''旧模型中一张报表存在多个数据集，无法自动选择主数据集。'', 1;

        IF EXISTS (
            SELECT 1
            FROM dbo.RcDataset d
            LEFT JOIN dbo.RcReport r ON r.Code = d.ReportCode
            WHERE d.ReportCode IS NOT NULL AND r.Id IS NULL)
            THROW 51000, N''存在引用未知报表的数据集，拒绝自动迁移。'', 1;';
'@
    $preflight.ExecuteNonQuery() | Out-Null

    $alreadyUpgraded = $connection.CreateCommand()
    $alreadyUpgraded.CommandText = @'
SELECT CASE WHEN
    COL_LENGTH(N'dbo.RcReport', N'DatasetCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.RcDataSource', N'Revision') IS NOT NULL
    AND COL_LENGTH(N'dbo.RcDataset', N'Revision') IS NOT NULL
    AND EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.RcDataset') AND name = N'uk_RcDataset_Code' AND is_unique = 1)
THEN 1 ELSE 0 END;
'@
    if ([int]$alreadyUpgraded.ExecuteScalar() -eq 1) {
        $validate = $connection.CreateCommand()
        $validate.CommandText = @'
IF EXISTS (SELECT 1 FROM dbo.RcReport WHERE NULLIF(DatasetCode, N'') IS NULL)
    THROW 51000, N'已升级结构中仍有报表未选择数据集。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.RcReport r
    LEFT JOIN dbo.RcDataset d ON d.Code = r.DatasetCode
    WHERE d.Id IS NULL)
    THROW 51000, N'已升级结构中存在无效的数据集引用。', 1;
'@
        $validate.ExecuteNonQuery() | Out-Null
        Write-Host '全局数据资源结构已存在且引用完整，本次未修改数据库。'
        return
    }

    $transaction = $connection.BeginTransaction()
    try {
        $command = $connection.CreateCommand()
        $command.Transaction = $transaction
        $command.CommandTimeout = 120
        $command.CommandText = @'
IF COL_LENGTH(N'dbo.RcReport', N'DatasetCode') IS NULL
    ALTER TABLE dbo.RcReport ADD DatasetCode nvarchar(64) NULL;

IF COL_LENGTH(N'dbo.RcDataSource', N'Revision') IS NULL
    ALTER TABLE dbo.RcDataSource ADD Revision int NOT NULL CONSTRAINT DF_RcDataSource_Revision DEFAULT (1);

IF COL_LENGTH(N'dbo.RcDataset', N'Revision') IS NULL
    ALTER TABLE dbo.RcDataset ADD Revision int NOT NULL CONSTRAINT DF_RcDataset_Revision DEFAULT (1);

IF COL_LENGTH(N'dbo.RcDataset', N'ReportCode') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql N'
        UPDATE r
        SET DatasetCode = d.Code
        FROM dbo.RcReport r
        INNER JOIN dbo.RcDataset d ON d.ReportCode = r.Code
        WHERE NULLIF(r.DatasetCode, N'''') IS NULL;';

    ALTER TABLE dbo.RcDataset ALTER COLUMN ReportCode nvarchar(64) NULL;
END;

EXEC sys.sp_executesql N'
    IF EXISTS (SELECT 1 FROM dbo.RcReport WHERE NULLIF(DatasetCode, N'''') IS NULL)
        THROW 51000, N''至少一张报表没有可迁移的数据集引用。'', 1;

    IF EXISTS (
        SELECT 1 FROM dbo.RcReport r
        LEFT JOIN dbo.RcDataset d ON d.Code = r.DatasetCode
        WHERE d.Id IS NULL)
        THROW 51000, N''迁移后的报表数据集引用无效。'', 1;

    ALTER TABLE dbo.RcReport ALTER COLUMN DatasetCode nvarchar(64) NOT NULL;';

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.RcDataset') AND name = N'uk_RcDataset_ReportCode_Code')
    DROP INDEX uk_RcDataset_ReportCode_Code ON dbo.RcDataset;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.RcDataset') AND name = N'uk_RcDataset_Code')
    CREATE UNIQUE INDEX uk_RcDataset_Code ON dbo.RcDataset(Code);
'@
        $command.ExecuteNonQuery() | Out-Null

        $transaction.Commit()
        Write-Host '已完成全局数据源/数据集结构升级；现有报表已改为通过 DatasetCode 引用数据集。'
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
