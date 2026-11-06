[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourceServer = '188.188.2.5',
    [ValidateRange(1, 65535)]
    [int]$SourcePort = 13306,
    [string]$SourceDatabase = 'jnpf_test_520',
    [string]$SourceUser = 'root',
    [string]$SourceReportName = '患者费用明细',
    [string]$TargetServer = '188.188.2.5',
    [ValidateRange(1, 65535)]
    [int]$TargetPort = 1433,
    [string]$TargetDatabase = 'hd_report_center',
    [string]$TargetUser = 'hdhis',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,63}$')]
    [string]$TargetReportCode = 'jnpf_patient_fee_detail',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]{0,63}$')]
    [Parameter(Mandatory = $true)]
    [string]$TargetDataSourceCode,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]{0,63}$')]
    [string]$TargetDatasetCode = 'jnpf_patient_fee_detail',
    [switch]$WriteTarget
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($TargetDatabase -cne 'hd_report_center') {
    throw '目标数据库必须是 hd_report_center。'
}

$fieldDefinitions = @(
    [ordered]@{ source = '患者姓名'; code = 'patientName'; displayName = '患者姓名'; dataType = 'string' },
    [ordered]@{ source = '患者ID'; code = 'patientId'; displayName = '患者ID'; dataType = 'string' },
    [ordered]@{ source = '费用名称'; code = 'feeName'; displayName = '费用名称'; dataType = 'string' },
    [ordered]@{ source = '费用ID'; code = 'feeId'; displayName = '费用ID'; dataType = 'string' },
    [ordered]@{ source = '明细ID'; code = 'detailId'; displayName = '明细ID'; dataType = 'string' },
    [ordered]@{ source = '费用明细流水号'; code = 'feeDetailSerialNo'; displayName = '费用明细流水号'; dataType = 'string' },
    [ordered]@{ source = '明细项目费用总额'; code = 'detailFeeTotal'; displayName = '明细项目费用总额'; dataType = 'number' },
    [ordered]@{ source = '数量'; code = 'quantity'; displayName = '数量'; dataType = 'number' },
    [ordered]@{ source = '单价'; code = 'unitPrice'; displayName = '单价'; dataType = 'number' },
    [ordered]@{ source = '价格上限金额'; code = 'priceLimitAmount'; displayName = '价格上限金额'; dataType = 'number' },
    [ordered]@{ source = '自付比例'; code = 'selfPayRatio'; displayName = '自付比例'; dataType = 'number' },
    [ordered]@{ source = '全额自付金额'; code = 'fullSelfPayAmount'; displayName = '全额自付金额'; dataType = 'number' },
    [ordered]@{ source = '超限价金额'; code = 'excessPriceAmount'; displayName = '超限价金额'; dataType = 'number' },
    [ordered]@{ source = '先行自付金额'; code = 'advanceSelfPayAmount'; displayName = '先行自付金额'; dataType = 'number' },
    [ordered]@{ source = '符合政策范围金额'; code = 'policyAmount'; displayName = '符合政策范围金额'; dataType = 'number' },
    [ordered]@{ source = '收费项目等级'; code = 'feeItemLevel'; displayName = '收费项目等级'; dataType = 'string' },
    [ordered]@{ source = '医疗收费项目类别'; code = 'medicalFeeCategory'; displayName = '医疗收费项目类别'; dataType = 'string' },
    [ordered]@{ source = '基本药物标志'; code = 'essentialDrugFlag'; displayName = '基本药物标志'; dataType = 'string' },
    [ordered]@{ source = '谈判药品标志'; code = 'negotiatedDrugFlag'; displayName = '谈判药品标志'; dataType = 'string' },
    [ordered]@{ source = '限制使用标志'; code = 'restrictedUseFlag'; displayName = '限制使用标志'; dataType = 'string' },
    [ordered]@{ source = '直接报销标志'; code = 'directReimbursementFlag'; displayName = '直接报销标志'; dataType = 'string' },
    [ordered]@{ source = '申请日期'; code = 'applicationDate'; displayName = '申请日期'; dataType = 'datetime' },
    [ordered]@{ source = '记账日期'; code = 'accountingDate'; displayName = '记账日期'; dataType = 'datetime' },
    [ordered]@{ source = '申请科室'; code = 'applicationDepartment'; displayName = '申请科室'; dataType = 'string' },
    [ordered]@{ source = '医生姓名'; code = 'doctorName'; displayName = '医生姓名'; dataType = 'string' },
    [ordered]@{ source = '机构ID'; code = 'organizationId'; displayName = '机构ID'; dataType = 'string' },
    [ordered]@{ source = '费用总额'; code = 'feeTotal'; displayName = '费用总额'; dataType = 'number' },
    [ordered]@{ source = '销售数量'; code = 'saleQuantity'; displayName = '销售数量'; dataType = 'number' },
    [ordered]@{ source = '销售单位'; code = 'saleUnit'; displayName = '销售单位'; dataType = 'string' }
)

$fieldBySource = @{}
foreach ($field in $fieldDefinitions) {
    $fieldBySource[$field.source] = $field
}

# 查询或读取 Import-JnpfPatientFeeReport 所需的 Find My Sql Connector Assembly 数据，并按调用约定返回结果。
function Find-MySqlConnectorAssembly {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
    $candidates = @(
        (Join-Path $repositoryRoot 'Admin.NET/HD.ReportCenter.Tests/bin/Debug/net8.0/MySqlConnector.dll'),
        (Join-Path $repositoryRoot 'Admin.NET/Admin.NET.Web.Entry/bin/Debug/net8.0/MySqlConnector.dll'),
        (Join-Path $repositoryRoot 'Admin.NET/Admin.NET.Web.Entry/bin/Release/net8.0/MySqlConnector.dll'),
        (Join-Path $env:USERPROFILE '.nuget/packages/mysqlconnector/2.2.5/lib/net6.0/MySqlConnector.dll')
    )
    $assembly = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($assembly)) {
        throw '找不到 MySqlConnector.dll，请先执行 dotnet restore/build。'
    }
    return $assembly
}

# 执行 Import-JnpfPatientFeeReport 的 Invoke Reader Rows 核心流程，并保持错误和结果语义稳定。
function Invoke-ReaderRows {
    param(
        [System.Data.Common.DbConnection]$Connection,
        [string]$CommandText,
        [hashtable]$Parameters = @{}
    )

    # 第一阶段：构造 Invoke-ReaderRows 当前步骤需要的中间数据和执行参数。
    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $CommandText
        $command.CommandTimeout = 30
        foreach ($entry in $Parameters.GetEnumerator()) {
            $parameter = $command.CreateParameter()
            $parameter.ParameterName = "@$($entry.Key)"
            $parameter.Value = if ($null -eq $entry.Value) { [DBNull]::Value } else { $entry.Value }
            $command.Parameters.Add($parameter) | Out-Null
        }

        $reader = $command.ExecuteReader()
        try {
            $rows = @()
            while ($reader.Read()) {
                $values = [ordered]@{}
                for ($index = 0; $index -lt $reader.FieldCount; $index++) {
                    $values[$reader.GetName($index)] = if ($reader.IsDBNull($index)) {
                        # 第2阶段：构造 Invoke-ReaderRows 当前步骤需要的中间数据和执行参数。
                        $null
                    }
                    else {
                        $reader.GetValue($index)
                    }
                }
                $rows += [pscustomobject]$values
            }
            return ,$rows
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $command.Dispose()
    }
}

# 执行 Import-JnpfPatientFeeReport 的 Require Single Row 核心流程，并保持错误和结果语义稳定。
function Require-SingleRow {
    param(
        [object[]]$Rows,
        [string]$Description
    )

    if ($Rows.Count -ne 1) {
        throw "$Description 必须恰好一条，实际读到 $($Rows.Count) 条。"
    }
    return $Rows[0]
}

# 将 Import-JnpfPatientFeeReport 中的 Convert Jnpf Query 输入转换为调用方需要的稳定格式。
function Convert-JnpfQuery {
    param([string]$QueryText)

    if ([string]::IsNullOrWhiteSpace($QueryText)) {
        throw 'JNPF 数据接口没有 SQL。'
    }

    $selectList = ($fieldDefinitions | ForEach-Object {
        "    $($_.source) AS $($_.code)"
    }) -join ",`r`n"
    $replacement = "SELECT`r`n$selectList`r`nFROM V_YB_FEE_DETAIL"
    $converted = [regex]::Replace(
        $QueryText.Trim(),
        '(?is)^\s*SELECT\s+\*\s+FROM\s+V_YB_FEE_DETAIL',
        $replacement,
        1)
    if ($converted -eq $QueryText.Trim()) {
        throw '费用明细 SQL 不是预期的 V_YB_FEE_DETAIL 查询，拒绝猜测转换。'
    }

    $converted = [regex]::Replace(
        $converted,
        '\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}',
        '@$1')
    if ($converted -notmatch '(?is)\bORDER\s+BY\b') {
        $converted = "$converted`r`nORDER BY feeDetailSerialNo"
    }
    return $converted
}

# 将 Import-JnpfPatientFeeReport 中的 Convert Jnpf Snapshot 输入转换为调用方需要的稳定格式。
function Convert-JnpfSnapshot {
    param(
        [string]$SnapshotJson,
        [string]$DatasetName
    )

    $snapshot = $SnapshotJson | ConvertFrom-Json -AsHashtable
    if (-not $snapshot.ContainsKey('sheets') -or -not $snapshot.ContainsKey('sheetOrder')) {
        # 第一阶段：根据 Convert-JnpfSnapshot 当前分支转换输入数据，并保存该阶段产生的中间结果。
        throw 'JNPF 模板快照缺少 sheets 或 sheetOrder。'
    }

    $snapshot.Remove('resources')
    $tokenCount = 0
    $formulaRangeFixCount = 0
    $detailCoordinates = @()
    $tokenPattern = '^\$\{' + [regex]::Escape($DatasetName) + '\.([^}]+)\}$'

    foreach ($sheetId in @($snapshot.sheetOrder)) {
        if (-not $snapshot.sheets.ContainsKey([string]$sheetId)) {
            throw "模板 sheetOrder 引用了不存在的工作表 '$sheetId'。"
        }
        $sheet = $snapshot.sheets[[string]$sheetId]
        foreach ($legacyKey in @('floatEcharts', 'cellEcharts', 'floatImages')) {
            $sheet.Remove($legacyKey)
        }
        if (-not $sheet.ContainsKey('cellData')) {
            continue
        }

        foreach ($rowKey in @($sheet.cellData.Keys)) {
            foreach ($columnKey in @($sheet.cellData[$rowKey].Keys)) {
                $cell = $sheet.cellData[$rowKey][$columnKey]
                $cell.Remove('custom')

                if ($cell.ContainsKey('v') -and $cell.v -is [string]) {
                    $match = [regex]::Match($cell.v, $tokenPattern)
                    if ($match.Success) {
                        $sourceField = $match.Groups[1].Value
                        if (-not $fieldBySource.ContainsKey($sourceField)) {
                            throw "模板使用了尚未映射的字段 '$sourceField'。"
                        }
                        # 第2阶段：构造 Convert-JnpfSnapshot 当前步骤需要的中间数据和执行参数。
                        $field = $fieldBySource[$sourceField]
                        $cell.v = "{{detail.$($field.code)}}"
                        $detailCoordinates += [pscustomobject]@{
                            sheetId = [string]$sheetId
                            row = [int]$rowKey
                            column = [int]$columnKey
                            code = [string]$field.code
                        }
                        $tokenCount++
                    }
                }

                if ($cell.ContainsKey('f') -and $cell.f -is [string]) {
                    $sumMatch = [regex]::Match($cell.f, '^=SUM\(([A-Za-z]+[0-9]+)\)$')
                    if ($sumMatch.Success) {
                        $reference = $sumMatch.Groups[1].Value
                        $cell.f = "=SUM($reference`:$reference)"
                        $formulaRangeFixCount++
                    }
                }
            }
        }
    }

    if ($tokenCount -eq 0) {
        throw '模板中没有找到 JNPF 数据集占位符。'
    }
    $locations = @($detailCoordinates | Group-Object sheetId, row)
    if ($locations.Count -ne 1) {
        # 第3阶段：根据 Convert-JnpfSnapshot 当前分支转换输入数据，并保存该阶段产生的中间结果。
        throw '当前报表中心只支持单工作表、单明细模板行；源模板不符合该约束。'
    }

    $detail = $detailCoordinates | Sort-Object column, code
    $binding = [ordered]@{
        version = 1
        parameterBindings = @()
        detailBinding = [ordered]@{
            datasetCode = 'detail'
            sheetId = [string]$detail[0].sheetId
            templateRow = [int]$detail[0].row
            fields = @($detail | ForEach-Object {
                [ordered]@{ code = $_.code; column = $_.column }
            })
        }
    }

    return [pscustomobject]@{
        SnapshotJson = $snapshot | ConvertTo-Json -Depth 100 -Compress
        BindingJson = $binding | ConvertTo-Json -Depth 20 -Compress
        SheetId = [string]$detail[0].sheetId
        TemplateRow = [int]$detail[0].row
        DetailFieldCount = $tokenCount
        FormulaRangeFixCount = $formulaRangeFixCount
    }
}

# 将 Import-JnpfPatientFeeReport 中的 Convert Field Presentations 输入转换为调用方需要的稳定格式。
function Convert-FieldPresentations {
    param([string]$ConvertConfigJson)

    if ([string]::IsNullOrWhiteSpace($ConvertConfigJson)) {
        return '[]'
    }
    $presentations = @()
    foreach ($conversion in @($ConvertConfigJson | ConvertFrom-Json -AsHashtable)) {
        if ($conversion.type -ne 'select' -or -not $conversion.ContainsKey('field')) {
            continue
        }
        $parts = ([string]$conversion.field).Split('.', 2)
        if ($parts.Count -ne 2 -or -not $fieldBySource.ContainsKey($parts[1])) {
            continue
        }
        $options = @($conversion.config.options | ForEach-Object {
            [ordered]@{ value = [string]$_.id; label = [string]$_.fullName }
        })
        if ($options.Count -eq 0) {
            continue
        }
        $presentations += [ordered]@{
            code = [string]$fieldBySource[$parts[1]].code
            valueMappings = $options
        }
    }
    return $presentations | ConvertTo-Json -Depth 20 -Compress
}

# 构建 Import-JnpfPatientFeeReport 所需的 New Oracle Connection String 内容，并保证生成结果符合调用约定。
function New-OracleConnectionString {
    param([pscustomobject]$DatabaseLink)

    if ([string]::IsNullOrWhiteSpace([string]$DatabaseLink.f_password)) {
        throw 'JNPF HIS 数据源没有可迁移的密码。'
    }
    $oracleConfig = if ([string]::IsNullOrWhiteSpace([string]$DatabaseLink.f_oracle_param)) {
        @{}
    }
    else {
        $DatabaseLink.f_oracle_param | ConvertFrom-Json -AsHashtable
    }
    $service = [string]$oracleConfig.oracleService
    if ([string]::IsNullOrWhiteSpace($service)) {
        $service = [string]$DatabaseLink.f_service_name
    }
    if ([string]::IsNullOrWhiteSpace($service)) {
        throw 'JNPF Oracle 数据源没有 SID/Service Name。'
    }

    $role = [string]$oracleConfig.oracleRole
    $rolePart = if ($role -eq 'SYSDBA') { 'DBA Privilege=SYSDBA;' } else { '' }
    return "Data Source=$($DatabaseLink.f_host):$($DatabaseLink.f_port)/$service;" +
        "User Id=$($DatabaseLink.f_user_name);Password=$($DatabaseLink.f_password);" +
        "Connection Timeout=30;$rolePart"
}

# 构建 Import-JnpfPatientFeeReport 所需的 Add Sql Parameter 内容，并保证生成结果符合调用约定。
function Add-SqlParameter {
    param(
        [System.Data.SqlClient.SqlCommand]$Command,
        [string]$Name,
        [System.Data.SqlDbType]$Type,
        [object]$Value,
        [int]$Size = 0
    )

    $parameter = if ($Size -ne 0) {
        $Command.Parameters.Add($Name, $Type, $Size)
    }
    else {
        $Command.Parameters.Add($Name, $Type)
    }
    $parameter.Value = if ($null -eq $Value) { [DBNull]::Value } else { $Value }
}

# 执行 Import-JnpfPatientFeeReport 的 Invoke Target Insert 核心流程，并保持错误和结果语义稳定。
function Invoke-TargetInsert {
    param(
        [string]$ConnectionString,
        [pscustomobject]$Migration
    )

    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        # 第一阶段：检查 Invoke-TargetInsert 当前条件，并按匹配分支执行对应处理。
        if ($connection.Database -cne 'hd_report_center') {
            throw "实际连接数据库为 '$($connection.Database)'，拒绝写入。"
        }

        $preflight = $connection.CreateCommand()
        $preflight.CommandText = @'
IF OBJECT_ID(N'dbo.RcDataSource', N'U') IS NULL
    OR OBJECT_ID(N'dbo.RcDataset', N'U') IS NULL
    OR OBJECT_ID(N'dbo.RcReport', N'U') IS NULL
    OR OBJECT_ID(N'dbo.RcReportParameter', N'U') IS NULL
    OR OBJECT_ID(N'dbo.RcReportTemplate', N'U') IS NULL
    THROW 51000, N'目标库缺少报表中心配置表。', 1;
IF COL_LENGTH(N'dbo.RcReport', N'FieldPresentationsJson') IS NULL
    OR COL_LENGTH(N'dbo.RcReportParameter', N'OptionsJson') IS NULL
    OR COL_LENGTH(N'dbo.RcReportParameter', N'SourceCode') IS NULL
    OR COL_LENGTH(N'dbo.RcReportParameter', N'SourceFieldCode') IS NULL
    THROW 51000, N'目标库报表结构不是当前版本，请先完成结构升级。', 1;
'@
        $preflight.ExecuteNonQuery() | Out-Null

        $transaction = $connection.BeginTransaction()
        try {
            $collision = $connection.CreateCommand()
            $collision.Transaction = $transaction
            $collision.CommandText = @'
SELECT
    (SELECT COUNT_BIG(1) FROM dbo.RcDataSource WHERE Code = @DataSourceCode) +
    (SELECT COUNT_BIG(1) FROM dbo.RcDataset WHERE Code = @DatasetCode) +
    (SELECT COUNT_BIG(1) FROM dbo.RcReport WHERE Code = @ReportCode) +
    (SELECT COUNT_BIG(1) FROM dbo.RcReportTemplate WHERE ReportCode = @ReportCode);
'@
            Add-SqlParameter $collision '@DataSourceCode' NVarChar $Migration.DataSourceCode 64
            Add-SqlParameter $collision '@DatasetCode' NVarChar $Migration.DatasetCode 64
            Add-SqlParameter $collision '@ReportCode' NVarChar $Migration.ReportCode 64
            if ([int64]$collision.ExecuteScalar() -ne 0) {
                throw '目标库已存在相同的数据源、数据集或报表编码，拒绝覆盖。'
            }

            # 第2阶段：构造 Invoke-TargetInsert 当前步骤需要的中间数据和执行参数。
            $now = [DateTime]::Now
            $dataSource = $connection.CreateCommand()
            $dataSource.Transaction = $transaction
            $dataSource.CommandText = @'
INSERT INTO dbo.RcDataSource
    (Id, Code, Name, DbType, ConnectionString, DefaultTimeoutSeconds,
     DefaultMaxRows, Revision, IsEnabled, CreateTime, UpdateTime)
VALUES
    (@Id, @Code, @Name, N'Oracle', @ConnectionString, 30, 20000, 1, 1, @Now, @Now);
'@
            Add-SqlParameter $dataSource '@Id' BigInt $Migration.DataSourceId
            Add-SqlParameter $dataSource '@Code' NVarChar $Migration.DataSourceCode 64
            Add-SqlParameter $dataSource '@Name' NVarChar $Migration.DataSourceName 128
            Add-SqlParameter $dataSource '@ConnectionString' NVarChar $Migration.DataSourceConnectionString -1
            Add-SqlParameter $dataSource '@Now' DateTime $now
            $dataSource.ExecuteNonQuery() | Out-Null

            $dataset = $connection.CreateCommand()
            $dataset.Transaction = $transaction
            $dataset.CommandText = @'
INSERT INTO dbo.RcDataset
    (Id, DataSourceCode, Code, Name, QueryText, FieldSchemaJson,
     TimeoutSeconds, MaxRows, Revision, IsEnabled, CreateTime, UpdateTime)
VALUES
    (@Id, @DataSourceCode, @Code, @Name, @QueryText, @FieldSchemaJson,
     60, 20000, 1, 1, @Now, @Now);
'@
            Add-SqlParameter $dataset '@Id' BigInt $Migration.DatasetId
            Add-SqlParameter $dataset '@DataSourceCode' NVarChar $Migration.DataSourceCode 64
            Add-SqlParameter $dataset '@Code' NVarChar $Migration.DatasetCode 64
            Add-SqlParameter $dataset '@Name' NVarChar $Migration.DatasetName 128
            Add-SqlParameter $dataset '@QueryText' NVarChar $Migration.QueryText -1
            Add-SqlParameter $dataset '@FieldSchemaJson' NVarChar $Migration.FieldSchemaJson -1
            Add-SqlParameter $dataset '@Now' DateTime $now
            $dataset.ExecuteNonQuery() | Out-Null

            $report = $connection.CreateCommand()
            $report.Transaction = $transaction
            # 第3阶段：构造 Invoke-TargetInsert 当前步骤需要的中间数据和执行参数。
            $report.CommandText = @'
INSERT INTO dbo.RcReport
    (Id, Code, Name, SheetName, DatasetCode, PreviewSettingsJson,
     ExportSettingsJson, FieldPresentationsJson, Revision, IsEnabled, CreateTime, UpdateTime)
VALUES
    (@Id, @Code, @Name, @SheetName, @DatasetCode, @PreviewSettingsJson,
     @ExportSettingsJson, @FieldPresentationsJson, 1, 1, @Now, @Now);
'@
            Add-SqlParameter $report '@Id' BigInt $Migration.ReportId
            Add-SqlParameter $report '@Code' NVarChar $Migration.ReportCode 64
            Add-SqlParameter $report '@Name' NVarChar $Migration.ReportName 128
            Add-SqlParameter $report '@SheetName' NVarChar $Migration.SheetName 128
            Add-SqlParameter $report '@DatasetCode' NVarChar $Migration.DatasetCode 64
            Add-SqlParameter $report '@PreviewSettingsJson' NVarChar $Migration.PreviewSettingsJson -1
            Add-SqlParameter $report '@ExportSettingsJson' NVarChar $Migration.ExportSettingsJson -1
            Add-SqlParameter $report '@FieldPresentationsJson' NVarChar $Migration.FieldPresentationsJson -1
            Add-SqlParameter $report '@Now' DateTime $now
            $report.ExecuteNonQuery() | Out-Null

            for ($index = 0; $index -lt $Migration.Parameters.Count; $index++) {
                $definition = $Migration.Parameters[$index]
                $parameter = $connection.CreateCommand()
                $parameter.Transaction = $transaction
                $parameter.CommandText = @'
INSERT INTO dbo.RcReportParameter
    (Id, ReportCode, Code, DisplayName, DataType, ControlType, SourceType,
     SourceCode, SourceFieldCode, UrlKey, DefaultExpression, FixedValue,
     OptionsJson, IsVisible, IsReadOnly, IsRequired, AllowUrlPrefill,
     DisplayOrder, CreateTime, UpdateTime)
VALUES
    (@Id, @ReportCode, @Code, @DisplayName, @DataType, @ControlType, N'Form',
     NULL, NULL, @UrlKey, NULL, NULL, NULL, 1, 0, @IsRequired, 1,
     @DisplayOrder, @Now, @Now);
'@
                Add-SqlParameter $parameter '@Id' BigInt ([int64]$Migration.TemplateId + $index + 1)
                Add-SqlParameter $parameter '@ReportCode' NVarChar $Migration.ReportCode 64
                Add-SqlParameter $parameter '@Code' NVarChar $definition.code 64
                Add-SqlParameter $parameter '@DisplayName' NVarChar $definition.displayName 128
                # 第4阶段：根据 Invoke-TargetInsert 当前分支转换输入数据，并保存该阶段产生的中间结果。
                Add-SqlParameter $parameter '@DataType' NVarChar $definition.dataType 32
                Add-SqlParameter $parameter '@ControlType' NVarChar $definition.controlType 32
                Add-SqlParameter $parameter '@UrlKey' NVarChar $definition.urlKey 64
                Add-SqlParameter $parameter '@IsRequired' Bit $definition.required
                Add-SqlParameter $parameter '@DisplayOrder' Int $index
                Add-SqlParameter $parameter '@Now' DateTime $now
                $parameter.ExecuteNonQuery() | Out-Null
            }

            $template = $connection.CreateCommand()
            $template.Transaction = $transaction
            $template.CommandText = @'
INSERT INTO dbo.RcReportTemplate
    (Id, ReportCode, SnapshotJson, BindingJson, Revision, CreateTime, UpdateTime)
VALUES
    (@Id, @ReportCode, @SnapshotJson, @BindingJson, @Revision, @Now, @Now);
'@
            Add-SqlParameter $template '@Id' BigInt $Migration.TemplateId
            Add-SqlParameter $template '@ReportCode' NVarChar $Migration.ReportCode 64
            Add-SqlParameter $template '@SnapshotJson' NVarChar $Migration.SnapshotJson -1
            Add-SqlParameter $template '@BindingJson' NVarChar $Migration.BindingJson -1
            Add-SqlParameter $template '@Revision' Int $Migration.TemplateRevision
            Add-SqlParameter $template '@Now' DateTime $now
            $template.ExecuteNonQuery() | Out-Null

            $verify = $connection.CreateCommand()
            $verify.Transaction = $transaction
            $verify.CommandText = @'
SELECT
    (SELECT COUNT_BIG(1) FROM dbo.RcDataSource WHERE Code = @DataSourceCode),
    (SELECT COUNT_BIG(1) FROM dbo.RcDataset WHERE Code = @DatasetCode),
    (SELECT COUNT_BIG(1) FROM dbo.RcReport WHERE Code = @ReportCode),
    (SELECT COUNT_BIG(1) FROM dbo.RcReportParameter WHERE ReportCode = @ReportCode),
    (SELECT COUNT_BIG(1) FROM dbo.RcReportTemplate WHERE ReportCode = @ReportCode);
'@
            Add-SqlParameter $verify '@DataSourceCode' NVarChar $Migration.DataSourceCode 64
            Add-SqlParameter $verify '@DatasetCode' NVarChar $Migration.DatasetCode 64
            Add-SqlParameter $verify '@ReportCode' NVarChar $Migration.ReportCode 64
            # 第5阶段：构造 Invoke-TargetInsert 当前步骤需要的中间数据和执行参数。
            $reader = $verify.ExecuteReader()
            try {
                if (-not $reader.Read()) {
                    throw '目标写后校验没有返回结果。'
                }
                $actual = @($reader.GetInt64(0), $reader.GetInt64(1), $reader.GetInt64(2), $reader.GetInt64(3), $reader.GetInt64(4))
                $expected = @(1, 1, 1, $Migration.Parameters.Count, 1)
                if (($actual -join ',') -ne ($expected -join ',')) {
                    throw "目标写后校验失败，期望 $($expected -join ',')，实际 $($actual -join ',')。"
                }
            }
            finally {
                $reader.Dispose()
            }

            $transaction.Commit()
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
}

$sourcePassword = $env:JNPF_MYSQL_PASSWORD
if ([string]::IsNullOrWhiteSpace($sourcePassword)) {
    throw '未设置 JNPF_MYSQL_PASSWORD，拒绝在命令参数或脚本中接收明文密码。'
}

Add-Type -Path (Find-MySqlConnectorAssembly)
$sourceBuilder = [MySqlConnector.MySqlConnectionStringBuilder]::new()
$sourceBuilder.Server = $SourceServer
$sourceBuilder.Port = [uint32]$SourcePort
$sourceBuilder.Database = $SourceDatabase
$sourceBuilder.UserID = $SourceUser
$sourceBuilder.Password = $sourcePassword
$sourceBuilder.ConnectionTimeout = 10
$sourceBuilder.DefaultCommandTimeout = 30
$sourceBuilder.SslMode = [MySqlConnector.MySqlSslMode]::Preferred

$sourceConnection = [MySqlConnector.MySqlConnection]::new($sourceBuilder.ConnectionString)
try {
    $sourceConnection.Open()
    $reportRows = Invoke-ReaderRows $sourceConnection @'
SELECT
    t.f_id AS report_id,
    t.f_full_name AS report_name,
    t.f_en_code AS source_report_code,
    v.f_id AS version_id,
    v.f_version AS version_number,
    v.f_snapshot AS snapshot_json,
    v.f_query_list AS query_list_json,
    v.f_convert_config AS convert_config_json
FROM report_template t
INNER JOIN report_version v ON v.f_template_id = t.f_id
WHERE t.f_full_name = @reportName
  AND COALESCE(t.f_delete_mark, 0) = 0
  AND COALESCE(v.f_delete_mark, 0) = 0
  AND v.f_state = 1
ORDER BY v.f_version DESC
LIMIT 2;
'@ @{ reportName = $SourceReportName }
    $report = Require-SingleRow $reportRows "启用的 JNPF 报表 '$SourceReportName'"

    $datasetRows = Invoke-ReaderRows $sourceConnection @'
SELECT
    f_id AS dataset_id,
    f_full_name AS dataset_name,
    f_interface_id AS interface_id,
    f_parameter_json AS parameter_json
FROM base_data_set
WHERE f_object_type = 'reportVersion'
  AND f_object_id = @versionId
  AND COALESCE(f_delete_mark, 0) = 0
ORDER BY f_id
LIMIT 2;
'@ @{ versionId = [string]$report.version_id }
    $sourceDataset = Require-SingleRow $datasetRows 'JNPF 报表版本的数据集'

    $interfaceRows = Invoke-ReaderRows $sourceConnection @'
SELECT
    f_id,
    f_full_name,
    f_data_config_json,
    f_parameter_json,
    f_field_json
FROM base_data_interface
WHERE f_id = @interfaceId
  AND COALESCE(f_delete_mark, 0) = 0
  AND f_enabled_mark = 1
LIMIT 2;
'@ @{ interfaceId = [string]$sourceDataset.interface_id }
    $interface = Require-SingleRow $interfaceRows 'JNPF 费用明细数据接口'

    $dataConfig = $interface.f_data_config_json | ConvertFrom-Json -AsHashtable
    if (-not $dataConfig.ContainsKey('sqlData') -or [string]::IsNullOrWhiteSpace([string]$dataConfig.sqlData.dbLinkId)) {
        throw 'JNPF 费用明细接口没有数据库连接引用。'
    }
    $databaseLinkRows = Invoke-ReaderRows $sourceConnection @'
SELECT
    f_id,
    f_full_name,
    f_db_type,
    f_host,
    f_port,
    f_user_name,
    f_password,
    f_service_name,
    f_oracle_param
FROM base_db_link
WHERE f_id = @databaseLinkId
  AND COALESCE(f_delete_mark, 0) = 0
LIMIT 2;
'@ @{ databaseLinkId = [string]$dataConfig.sqlData.dbLinkId }
    $databaseLink = Require-SingleRow $databaseLinkRows 'JNPF HIS 数据源'
}
finally {
    $sourceConnection.Dispose()
}

if ([string]$databaseLink.f_db_type -ne 'Oracle') {
    throw "当前试迁移只支持 JNPF Oracle 数据源，实际为 '$($databaseLink.f_db_type)'。"
}

$sourceFields = @($interface.f_field_json | ConvertFrom-Json -AsHashtable)
$unmappedFields = @($sourceFields | Where-Object { -not $fieldBySource.ContainsKey([string]$_.field) })
if ($unmappedFields.Count -ne 0) {
    throw "JNPF 接口出现未映射字段：$((@($unmappedFields.field) -join '、'))。"
}

$queryText = Convert-JnpfQuery ([string]$dataConfig.sqlData.sql)
$snapshot = Convert-JnpfSnapshot ([string]$report.snapshot_json) ([string]$sourceDataset.dataset_name)
$fieldPresentationsJson = Convert-FieldPresentations ([string]$report.convert_config_json)

$variables = @(
    [ordered]@{ code = 'hisOrganizeId'; displayName = '机构ID'; dataType = 'string'; required = $true; sourceType = 'Form'; sourceCode = $null; sourceFieldCode = $null },
    [ordered]@{ code = 'bdate'; displayName = '申请开始日期'; dataType = 'string'; required = $true; sourceType = 'Form'; sourceCode = $null; sourceFieldCode = $null },
    [ordered]@{ code = 'edate'; displayName = '申请结束日期'; dataType = 'string'; required = $true; sourceType = 'Form'; sourceCode = $null; sourceFieldCode = $null }
)
$parameters = @(
    [ordered]@{ code = 'hisOrganizeId'; displayName = '机构ID'; dataType = 'string'; controlType = 'text'; urlKey = 'orgId'; required = $true },
    [ordered]@{ code = 'bdate'; displayName = '申请开始日期'; dataType = 'string'; controlType = 'date'; urlKey = 'bdate'; required = $true },
    [ordered]@{ code = 'edate'; displayName = '申请结束日期'; dataType = 'string'; controlType = 'date'; urlKey = 'edate'; required = $true }
)
$fieldSchema = [ordered]@{
    variables = $variables
    fields = @($fieldDefinitions | ForEach-Object {
        [ordered]@{ code = $_.code; displayName = $_.displayName; dataType = $_.dataType }
    })
}
$previewSettings = [ordered]@{ mode = 'Paged'; pageSize = 20; showExactTotal = $false }
$exportSettings = [ordered]@{
    defaultMode = 'Normal'
    normalMaxRows = 20000
    normalMaxCells = 500000
    normalTimeoutSeconds = 60
    largeExportEnabled = $true
    rowsPerSheet = 1000000
    fileRetentionHours = 24
}

$sheetName = [string](($report.snapshot_json | ConvertFrom-Json -AsHashtable).sheets[$snapshot.SheetId].name)
$migration = [pscustomobject]@{
    DataSourceId = [int64]$databaseLink.f_id
    DataSourceCode = $TargetDataSourceCode
    DataSourceName = "JNPF-$($databaseLink.f_full_name)"
    DataSourceConnectionString = New-OracleConnectionString $databaseLink
    DatasetId = [int64]$sourceDataset.dataset_id
    DatasetCode = $TargetDatasetCode
    DatasetName = [string]$interface.f_full_name
    QueryText = $queryText
    FieldSchemaJson = $fieldSchema | ConvertTo-Json -Depth 20 -Compress
    ReportId = [int64]$report.report_id
    ReportCode = $TargetReportCode
    ReportName = [string]$report.report_name
    SheetName = if ([string]::IsNullOrWhiteSpace($sheetName)) { [string]$report.report_name } else { $sheetName }
    PreviewSettingsJson = $previewSettings | ConvertTo-Json -Compress
    ExportSettingsJson = $exportSettings | ConvertTo-Json -Compress
    FieldPresentationsJson = $fieldPresentationsJson
    Parameters = $parameters
    TemplateId = [int64]$report.version_id
    TemplateRevision = [int]$report.version_number
    SnapshotJson = $snapshot.SnapshotJson
    BindingJson = $snapshot.BindingJson
}

$queryListDocument = if ([string]::IsNullOrWhiteSpace([string]$report.query_list_json)) {
    $null
}
else {
    $report.query_list_json | ConvertFrom-Json -AsHashtable
}
$queryControlCount = if ($null -eq $queryListDocument) {
    0
}
else {
    @($queryListDocument.queryList).Count
}
$unsupportedQueryControls = [Math]::Max(0, $queryControlCount - 2)

Write-Host 'JNPF 患者费用明细迁移预检通过：'
Write-Host "  源报表：$($migration.ReportName) / $($report.source_report_code) / 版本 $($migration.TemplateRevision)"
Write-Host "  目标编码：$($migration.ReportCode)"
Write-Host "  数据源：Oracle $($databaseLink.f_host):$($databaseLink.f_port)（账号和密码不输出）"
Write-Host "  数据集：$($migration.DatasetCode)，3 个 SQL 参数，$($fieldDefinitions.Count) 个返回字段"
Write-Host "  模板：工作表 '$($migration.SheetName)'，$($snapshot.DetailFieldCount) 个明细绑定，模板行 $($snapshot.TemplateRow + 1)"
Write-Host "  公式修正：$($snapshot.FormulaRangeFixCount) 个单格 SUM 已改为可扩展范围"
Write-Host "  JNPF 查询控件：$queryControlCount 个；本次迁移日期控件 2 个，另增加机构 ID 控件"
if ($unsupportedQueryControls -gt 0) {
    Write-Warning "另有 $unsupportedQueryControls 个结果字段筛选控件不属于当前数据集 SQL 参数，本次未迁移。"
}

if (-not $WriteTarget) {
    Write-Host '当前为只读预检，未写入目标库。需要落库时增加 -WriteTarget。'
    return
}

$targetConnectionString = $env:HD_REPORT_CENTER_DB_CONNECTION_STRING
if ([string]::IsNullOrWhiteSpace($targetConnectionString)) {
    $targetPassword = $env:HD_REPORT_CENTER_DB_PASSWORD
    if ([string]::IsNullOrWhiteSpace($targetPassword)) {
        throw '写目标库前必须设置 HD_REPORT_CENTER_DB_PASSWORD 或 HD_REPORT_CENTER_DB_CONNECTION_STRING。'
    }
    $targetBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $targetBuilder['Data Source'] = "$TargetServer,$TargetPort"
    $targetBuilder['Initial Catalog'] = $TargetDatabase
    $targetBuilder['User ID'] = $TargetUser
    $targetBuilder['Password'] = $targetPassword
    $targetBuilder['Encrypt'] = $false
    $targetBuilder['TrustServerCertificate'] = $true
    $targetBuilder['Connect Timeout'] = 15
    $targetBuilder['Application Name'] = 'hd-report-center-jnpf-import'
    $targetConnectionString = $targetBuilder.ConnectionString
}
else {
    $targetBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($targetConnectionString)
    if ($targetBuilder.InitialCatalog -cne 'hd_report_center') {
        throw 'HD_REPORT_CENTER_DB_CONNECTION_STRING 必须指向 hd_report_center。'
    }
}

if ($PSCmdlet.ShouldProcess('hd_report_center', "迁移报表 '$($migration.ReportCode)'")) {
    Invoke-TargetInsert $targetConnectionString $migration
    Write-Host "迁移完成：$($migration.ReportCode)，模板 revision $($migration.TemplateRevision)。"
}
