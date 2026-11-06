$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JnpfHisReportMigration.psm1') -Force

$script:passed = 0
# 校验 Test-JnpfHisReportMigration 的 Assert True 条件，并返回可供后续流程判断的结果。
function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "断言失败：$Message" }
    $script:passed++
}

# 校验 Test-JnpfHisReportMigration 的 Assert Equal 条件，并返回可供后续流程判断的结果。
function Assert-Equal {
    param([object]$Actual, [object]$Expected, [string]$Message)
    if ([string]$Actual -cne [string]$Expected) {
        throw "断言失败：$Message；期望 '$Expected'，实际 '$Actual'。"
    }
    $script:passed++
}

$fieldsJson = @(
    [ordered]@{ field = 'PID'; fieldName = '病历号'; dataType = 'string' },
    [ordered]@{ field = '患者姓名'; fieldName = '姓名'; dataType = 'string' },
    [ordered]@{ field = 'AGE'; fieldName = '年龄'; dataType = 'Integer' }
) | ConvertTo-Json -Compress
$fields = @(Convert-JnpfFieldDefinitions $fieldsJson)
Assert-Equal $fields.Count 3 '字段数量'
Assert-Equal $fields[0].Code 'PID' '英文字段保持编码'
Assert-Equal $fields[1].Code 'field_02' '中文字段生成稳定编码'
Assert-Equal $fields[2].DataType 'number' '整数类型转换为 number'

$sql = Convert-JnpfSqlText `
    'SELECT PID, 患者姓名, AGE FROM V_YB_FEE_DETAIL WHERE CREATE_DATE >= {sdate} AND CREATE_DATE < {edate}' `
    $fields `
    'Oracle'
Assert-True ($sql.QueryText -match '@sdate' -and $sql.QueryText -match '@edate') 'SQL 参数转换'
Assert-True ($sql.QueryText -match 'field_02') 'SQL 中文字段别名转换'
Assert-True ($sql.Markers -contains 'DEV_SQL_OBJECT_REVIEW:V_YB_FEE_DETAIL') '开发视图只标记不阻断'
Assert-True ($sql.Markers -contains 'SQL_OUTER_FIELD_ALIAS_APPLIED') '字段别名包裹标记'

$markerSql = Convert-JnpfSqlText `
    "SELECT * FROM t WHERE a = #{startDate} AND b = `${endDate} AND c = :status AND d = {{tenantId}} AND note = '{ignored}' -- #{comment}" `
    $fields `
    'SqlServer'
Assert-True ($markerSql.QueryText -match '@startDate' -and
    $markerSql.QueryText -match '@endDate' -and
    $markerSql.QueryText -match '@status' -and
    $markerSql.QueryText -match '@tenantId') 'JNPF 多种参数标记统一转换'
Assert-True ($markerSql.QueryText -match "note = '\{ignored\}'") 'SQL 字符串中的大括号不应被当作参数'
Assert-True ($markerSql.Markers -contains 'JNPF_PARAMETER_MARKER:hash-brace->startDate') 'hash-brace 参数标记留痕'
$unsupportedSqlThrew = $false
try {
    Convert-JnpfSqlText 'SELECT * FROM t WHERE x = #{bad' $fields 'SqlServer' | Out-Null
}
catch {
    $unsupportedSqlThrew = $true
}
Assert-True $unsupportedSqlThrew '未闭合 JNPF 参数标记必须阻断'

$parameterResult = Convert-JnpfParameters @('sdate', 'edate', 'med_type', 'PID') `
    '{"queryList":[{"field":"sdate","label":"开始日期","type":"date","required":true},{"field":"edate","label":"结束日期","type":"date"}]}' `
    '[{"field":"med_type","dataType":"varchar","relationField":"390","sourceType":2}]' `
    '[]'
Assert-Equal $parameterResult.Parameters.Count 4 'SQL 参数数量'
Assert-Equal $parameterResult.Parameters[0].ControlType 'date' '日期控件识别'
Assert-Equal $parameterResult.Parameters[2].SourceType 'Fixed' '关联参数迁移为固定值'
Assert-Equal $parameterResult.Parameters[2].FixedValue '390' '关联参数固定值保留'
Assert-True (-not $parameterResult.Parameters[2].IsVisible) '固定参数默认隐藏'
Assert-True ($parameterResult.Markers -contains 'PARAMETER_FIXED_VALUE_MIGRATED:med_type') '固定参数迁移留痕'
Assert-True ($parameterResult.Markers -contains 'PARAMETER_METADATA_DEFAULTED:PID') '缺失参数元数据有标记'

$snapshot = [ordered]@{
    sheets = [ordered]@{
        sheet1 = [ordered]@{
            name = '门诊明细'
            cellData = [ordered]@{
                '0' = [ordered]@{
                    '0' = [ordered]@{ v = '${门诊数据集.PID}' }
                    '1' = [ordered]@{ v = '${门诊数据集.患者姓名}' }
                }
                '1' = [ordered]@{
                    '0' = [ordered]@{ f = '=SUM(A2)' }
                }
            }
            mergeData = @()
            floatImages = @('legacy-image')
        }
    }
    sheetOrder = @('sheet1')
    resources = @('legacy-resource')
} | ConvertTo-Json -Depth 20 -Compress
$convertedTemplate = Convert-JnpfSnapshot $snapshot '门诊数据集' $fields
Assert-True ($convertedTemplate.SnapshotJson -match '\{\{detail\.PID\}\}') '模板 PID token 转换'
Assert-True ($convertedTemplate.SnapshotJson -match '\{\{detail\.field_02\}\}') '模板中文字段 token 转换'
Assert-True ($convertedTemplate.Markers -contains 'LEGACY_WORKBOOK_RESOURCES_REMOVED') '旧资源有标记'
Assert-True ($convertedTemplate.Markers -contains 'UNSUPPORTED_TEMPLATE_ASSET_REMOVED:floatImages') '旧图片有标记'
Assert-Equal $convertedTemplate.FormulaRangeFixCount 1 '单格 SUM 修正'
Assert-Equal $convertedTemplate.TemplateRow 0 '明细模板行坐标'

$invalidSnapshot = [ordered]@{
    sheets = [ordered]@{
        sheet1 = [ordered]@{
            name = '非法'
            cellData = [ordered]@{
                '0' = [ordered]@{ '0' = [ordered]@{ v = '${门诊数据集.PID}' } }
                '2' = [ordered]@{ '0' = [ordered]@{ v = '${门诊数据集.PID}' } }
            }
        }
    }
    sheetOrder = @('sheet1')
} | ConvertTo-Json -Depth 20 -Compress
$threw = $false
try { Convert-JnpfSnapshot $invalidSnapshot '门诊数据集' $fields | Out-Null } catch { $threw = $true }
Assert-True $threw '多明细模板行必须阻断'

Assert-Equal (ConvertTo-TargetCode '门诊患者统计表-赤峰' 'jnpf_report') 'jnpf_report' '非法中文编码回退稳定'
Assert-Equal (ConvertTo-TargetCode 'jnpf_outpatient_patient_stats_chifeng' 'jnpf_report') 'jnpf_outpatient_patient_stats_chifeng' '报表编码生成'

$entryScript = Join-Path $PSScriptRoot 'Invoke-JnpfHisReportMigration.ps1'
$parseTokens = $null
$parseErrors = $null
$entryAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $entryScript,
    [ref]$parseTokens,
    [ref]$parseErrors)
Assert-Equal $parseErrors.Count 0 '迁移入口脚本语法'
foreach ($parameterName in @(
        'SourceServer', 'SourcePort', 'SourceDatabase', 'SourceUser',
        'ReportDataSourceCode', 'ReportDataSourceType',
        'TargetServer', 'TargetPort', 'TargetDatabase', 'TargetUser')) {
    $parameter = @($entryAst.ParamBlock.Parameters | Where-Object {
        $_.Name.VariablePath.UserPath -eq $parameterName
    }) | Select-Object -First 1
    Assert-True ($null -ne $parameter -and $null -eq $parameter.DefaultValue) `
        "连接参数 $parameterName 不得带固定默认值"
}

$applicationConnection = 'Data Source=188.188.2.5,1433;Initial Catalog=hd_report_center;User ID=hdhis;Password=secret;Encrypt=False;Trust Server Certificate=True;Application Name=hd-report-center-runtime'
$normalizedConnection = ConvertTo-JnpfLegacySqlConnectionString $applicationConnection
$connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($normalizedConnection)
Assert-Equal $connectionBuilder.InitialCatalog 'hd_report_center' '兼容应用连接串中的 Trust Server Certificate 键'

Write-Host "JNPF 报表迁移纯函数测试通过：$script:passed 项。"
