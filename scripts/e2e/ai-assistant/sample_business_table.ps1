<#
.SYNOPSIS
AI 助手 E2E 专用的脱敏业务样例表生命周期脚本（创建/填充/删除 dbo.E2E_AI_SAMPLE_OUTPATIENT）。

.DESCRIPTION
为 S2「模板 AI 全链路」提供一张全合成、零敏感信息的门诊费用样例表：
姓名/科室/费用类别全部为虚构枚举值，不含真实患者姓名、身份证号或联系方式，
AI 的 describe_table / sample_rows / run_readonly_sql 打到该表不会外发任何真实个人数据。
创建幂等（存在即跳过），测试结束必须用 -Drop 清理。
连接参数从环境变量 HD_REPORT_CENTER_DB_CONNECTION_STRING / HD_REPORT_CENTER_DB_PASSWORD
解析，口令绝不进命令行参数。

.PARAMETER Create
创建样例表并播种 12 行合成数据（幂等：表已存在时不重复填充）。

.PARAMETER Drop
删除样例表。

.EXAMPLE
pwsh -File sample_business_table.ps1 -Create
pwsh -File sample_business_table.ps1 -Drop
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
    if ($Drop) {
        $command = $connection.CreateCommand()
        $command.CommandText = "IF OBJECT_ID(N'dbo.E2E_AI_SAMPLE_OUTPATIENT', N'U') IS NOT NULL DROP TABLE dbo.E2E_AI_SAMPLE_OUTPATIENT;"
        $command.ExecuteNonQuery() | Out-Null
        Write-Host '样例业务表 dbo.E2E_AI_SAMPLE_OUTPATIENT 已删除。'
    }
    if ($Create) {
        # 建表幂等；行数据只在空表时播种，避免重复运行时翻倍。
        $command = $connection.CreateCommand()
        $command.CommandText = @'
IF OBJECT_ID(N'dbo.E2E_AI_SAMPLE_OUTPATIENT', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.E2E_AI_SAMPLE_OUTPATIENT
    (
        Id bigint NOT NULL IDENTITY(1,1),
        DEPT_NAME nvarchar(50) NOT NULL,
        FEE_CATEGORY nvarchar(50) NOT NULL,
        FEE_NAME nvarchar(100) NOT NULL,
        AMOUNT decimal(12,2) NOT NULL,
        QUANTITY int NOT NULL,
        SETTLE_DATE datetime NOT NULL,
        IS_REFUND bit NOT NULL CONSTRAINT DF_E2E_AI_SAMPLE_IS_REFUND DEFAULT (0),
        CONSTRAINT PK_E2E_AI_SAMPLE_OUTPATIENT PRIMARY KEY (Id)
    );
END;
'@
        $command.ExecuteNonQuery() | Out-Null

        $countCommand = $connection.CreateCommand()
        $countCommand.CommandText = 'SELECT COUNT_BIG(1) FROM dbo.E2E_AI_SAMPLE_OUTPATIENT'
        $existing = [int64]$countCommand.ExecuteScalar()
        if ($existing -eq 0) {
            # 全合成数据：科室/费用类别/项目名均为虚构枚举，姓名不出现，日期取 2025 年合成区间。
            $seed = $connection.CreateCommand()
            $seed.CommandText = @'
INSERT INTO dbo.E2E_AI_SAMPLE_OUTPATIENT (DEPT_NAME, FEE_CATEGORY, FEE_NAME, AMOUNT, QUANTITY, SETTLE_DATE, IS_REFUND) VALUES
(N'内科', N'西药费', N'合成样例药品A', 16.90, 2, '2025-01-10 09:15:00', 0),
(N'内科', N'西药费', N'合成样例药品B', 45.60, 1, '2025-01-10 09:20:00', 0),
(N'内科', N'检查费', N'合成样例检查X', 120.00, 1, '2025-01-12 10:00:00', 0),
(N'外科', N'治疗费', N'合成样例治疗Y', 260.50, 1, '2025-01-15 14:30:00', 0),
(N'外科', N'西药费', N'合成样例药品C', 88.20, 3, '2025-01-16 11:05:00', 1),
(N'外科', N'检查费', N'合成样例检查Z', 350.00, 1, '2025-02-03 15:45:00', 0),
(N'儿科', N'西药费', N'合成样例药品D', 32.40, 2, '2025-02-05 08:50:00', 0),
(N'儿科', N'治疗费', N'合成样例治疗W', 95.00, 1, '2025-02-08 13:10:00', 0),
(N'儿科', N'检查费', N'合成样例检查X', 120.00, 1, '2025-03-01 09:00:00', 1),
(N'妇科', N'西药费', N'合成样例药品E', 64.80, 1, '2025-03-05 16:20:00', 0),
(N'妇科', N'治疗费', N'合成样例治疗V', 180.00, 1, '2025-03-12 10:40:00', 0),
(N'内科', N'治疗费', N'合成样例治疗U', 145.30, 1, '2025-03-18 11:55:00', 0);
'@
            $seed.ExecuteNonQuery() | Out-Null
        }
        Write-Host '样例业务表 dbo.E2E_AI_SAMPLE_OUTPATIENT 已就绪（全合成脱敏数据）。'
    }
}
finally {
    $connection.Dispose()
}
