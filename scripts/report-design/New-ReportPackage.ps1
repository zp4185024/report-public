#Requires -Version 7.0
<#
.SYNOPSIS
把报表设计 spec（JSON）编译成 hd-report-center 报表包 JSON，供报表导入向导校验/试跑/导入。

.DESCRIPTION
AI 报表设计流水线的关键一环：AI 只产出意图（spec），本脚本负责产出合法的模板快照、
规范绑定和包结构，使"包被后端 PocReportPackageService.ValidateAsync 接受"成为硬约束。
spec 校验失败、模板构造越界都会以非零退出码结束并给出中文原因；绝不猜测或静默修正。

用法示例：
  ./New-ReportPackage.ps1 -SpecPath design/report-specs/report_asset_inventory.spec.json
  ./New-ReportPackage.ps1 -SpecPath a.spec.json -OutputPath out/my.report-package.json

.NOTES
需 PowerShell 7+。输出为 UTF-8 无 BOM、两空格缩进的 JSON；字段命名与后端
PocReportPackageFormat.SerializeCanonical（camelCase）及前端 types.ts 保持一致。
#>
[CmdletBinding()]
param(
    # 报表设计 spec 文件路径（UTF-8 JSON）。
    [Parameter(Mandatory = $true)]
    [string]$SpecPath,

    # 报表包输出路径；缺省写入仓库 output/report-design/<报表编码>.report-package.json。
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 与后端 PocDatabaseProvider.SupportedTypes 对齐的受支持数据源类型（规范大小写）。
$script:SupportedDataSourceTypes = @('SqlServer', 'Oracle', 'MySql', 'PostgreSQL')
# 与后端 PocDataAssetService 校验白名单对齐的变量/字段数据类型。
$script:VariableDataTypes = @('string', 'int', 'long', 'decimal', 'date', 'datetime', 'bool')
$script:FieldDataTypes = @('string', 'number', 'date', 'datetime', 'bool')
# 与后端 PocReportConfigurationService 校验白名单对齐的参数类型/来源/控件。
$script:ParameterDataTypes = @('string', 'text', 'int', 'long', 'decimal', 'date', 'datetime', 'bool', 'boolean')
$script:ParameterSourceTypes = @('Form', 'Url', 'System', 'Fixed', 'External')
$script:ParameterControlTypes = @('text', 'number', 'date', 'datetime', 'month', 'year', 'checkbox', 'select', 'multiSelect', 'hidden')
# 与后端 PocReadOnlySqlValidator.ForbiddenTokens 对齐的禁用 SQL 关键字（大小写不敏感匹配）。
$script:ForbiddenSqlTokens = [System.Collections.Generic.HashSet[string]]::new([string[]]@(
    'ALTER', 'BACKUP', 'BULK', 'CREATE', 'DBCC', 'DELETE', 'DENY', 'DROP',
    'EXEC', 'EXECUTE', 'GRANT', 'INSERT', 'INTO', 'MERGE', 'OPENQUERY',
    'OPENROWSET', 'OPENDATASOURCE', 'RECONFIGURE', 'RESTORE', 'REVOKE',
    'TRUNCATE', 'UPDATE', 'USE'), [StringComparer]::OrdinalIgnoreCase)

<#
.SYNOPSIS
抛出携带中文原因的 spec 校验异常，由主流程统一转为非零退出。
#>
function Assert-Design {
    param(
        # 校验条件；为假即终止。
        [bool]$Condition,

        # 面向报表设计者的中文失败原因。
        [string]$Message
    )
    if (-not $Condition) {
        throw "spec 校验失败：$Message"
    }
}

<#
.SYNOPSIS
安全读取 PSCustomObject 的属性值；属性缺失或为 null 时返回 $null，配合严格模式使用。
#>
function Get-SpecValue {
    param(
        # spec 节点对象。
        [object]$Node,

        # 属性名。
        [string]$Name
    )
    if ($null -eq $Node) {
        return $null
    }
    $property = $Node.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

<#
.SYNOPSIS
空安全读取 spec 的数组属性；属性缺失、为 null 或为空数组时统一返回空数组，
避免 PowerShell 的 @($null) 变成单元素数组导致误判“存在缺少 field 的项”。
#>
function Get-SpecArray {
    param(
        # spec 节点对象。
        [object]$Node,

        # 属性名。
        [string]$Name
    )
    $value = Get-SpecValue -Node $Node -Name $Name
    return ConvertTo-SafeArray -Value $value
}

<#
.SYNOPSIS
把任意值规整为数组对象并以“防展开”方式返回：null 回空数组、标量包一层数组、
数组原样返回。PowerShell 的 return @() 会被展开成空管线（调用方拿到 null），
必须用逗号操作符 ,@() 保证调用方拿到的永远是真正的数组。
#>
function ConvertTo-SafeArray {
    param(
        # 待规整的任意值。
        [object]$Value
    )
    if ($null -eq $Value) {
        return ,@()
    }
    return ,@($Value)
}

<#
.SYNOPSIS
按后端 ValidateCode 同一正则校验编码（字母开头，仅字母数字下划线，最长 64）。
#>
function Assert-SpecCode {
    param(
        # 待校验的编码文本。
        [string]$Code,

        # 出错时用于定位的节点中文名（如“报表编码”）。
        [string]$Label
    )
    Assert-Design (
        -not [string]::IsNullOrWhiteSpace($Code) -and $Code -match '^[A-Za-z][A-Za-z0-9_]{0,63}$'
    ) "$Label '$Code' 不符合编码规则：字母开头，仅允许字母/数字/下划线，最长 64 字符。"
}

<#
.SYNOPSIS
剥除 SQL 中的行注释、块注释、字符串与括号标识符，保留字符位置与空白结构。
与后端 PocReadOnlySqlValidator.Sanitize 同一状态机语义，供变量提取与 ORDER BY 判定复用。
#>
function Convert-SqlToSanitized {
    param(
        # 原始 SQL 文本。
        [string]$QueryText
    )

    # 阶段一：逐字符扫描，按 Normal/String/Quoted/Bracket 四态剥除不可见参与解析的片段。
    $output = [System.Text.StringBuilder]::new($QueryText.Length)
    $state = 'Normal'
    for ($i = 0; $i -lt $QueryText.Length; $i++) {
        $current = $QueryText[$i]
        $next = if ($i + 1 -lt $QueryText.Length) { $QueryText[$i + 1] } else { [char]0 }

        switch ($state) {
            'Normal' {
                if ($current -eq '-' -and $next -eq '-') {
                    # 行注释：替换为空白直到行尾，保持后续 token 的相对位置。
                    $i += 2
                    while ($i -lt $QueryText.Length -and $QueryText[$i] -ne "`r" -and $QueryText[$i] -ne "`n") { $i++ }
                    $null = $output.Append(' ')
                    continue
                }
                if ($current -eq '/' -and $next -eq '*') {
                    # 块注释：整体替换为空白；未闭合直接判失败，避免吞掉后半段 SQL。
                    $i += 2
                    $closed = $false
                    while ($i -lt $QueryText.Length) {
                        if ($QueryText[$i] -eq '*' -and $i + 1 -lt $QueryText.Length -and $QueryText[$i + 1] -eq '/') {
                            $i++
                            $closed = $true
                            break
                        }
                        $i++
                    }
                    Assert-Design $closed "SQL 包含未闭合的块注释。"
                    $null = $output.Append(' ')
                    continue
                }
                if ($current -eq ';' -or $current -eq '#') {
                    Assert-Design $false "SQL 不允许多语句分号或 # 临时表语法，只允许单条只读查询。"
                }
                if ($current -eq "'" -or $current -eq '"' -or $current -eq '[') {
                    $state = switch ($current) { "'" { 'String' } '"' { 'Quoted' } default { 'Bracket' } }
                    $null = $output.Append(' ')
                    continue
                }
                $null = $output.Append($current)
            }
            'String' {
                $null = $output.Append(' ')
                if ($current -eq "'" -and $next -eq "'") { $null = $output.Append(' '); $i++ }
                elseif ($current -eq "'") { $state = 'Normal' }
            }
            'Quoted' {
                $null = $output.Append(' ')
                if ($current -eq '"' -and $next -eq '"') { $null = $output.Append(' '); $i++ }
                elseif ($current -eq '"') { $state = 'Normal' }
            }
            'Bracket' {
                $null = $output.Append(' ')
                if ($current -eq ']' -and $next -eq ']') { $null = $output.Append(' '); $i++ }
                elseif ($current -eq ']') { $state = 'Normal' }
            }
        }
    }
    Assert-Design ($state -eq 'Normal') "SQL 包含未闭合的字符串或标识符。"
    return $output.ToString()
}

<#
.SYNOPSIS
先按后端规则静态校验 SQL 只读性，再从剥除注释/字符串后的文本提取全部 @变量编码。
#>
function Get-SqlParameterReferences {
    param(
        # 原始 SQL 文本。
        [string]$QueryText,

        # 预览模式；Paged 时要求包含 ORDER BY。
        [string]$PreviewMode
    )

    # 阶段一：静态只读校验——首 token 必须是 SELECT/WITH，且不含任何禁用关键字。
    $sanitized = Convert-SqlToSanitized -QueryText $QueryText
    $tokens = [regex]::Matches($sanitized, '[A-Za-z_][A-Za-z0-9_]*') | ForEach-Object { $_.Value }
    Assert-Design ($tokens.Count -gt 0) "SQL 为空或不含有效 token。"
    $first = [string]$tokens[0]
    Assert-Design (
        $first -ieq 'SELECT' -or $first -ieq 'WITH'
    ) "数据集只允许单条只读 SELECT 或 WITH 查询，当前以 '$first' 开头。"
    foreach ($token in $tokens) {
        Assert-Design (-not $script:ForbiddenSqlTokens.Contains([string]$token)) `
            "SQL 含禁用关键字 '$token'，只读数据集不允许写操作或服务器级命令。"
    }

    # 阶段二：分页数据集必须有稳定 ORDER BY（与后端 PAGING_ORDER_REQUIRED 同规则）。
    if ($PreviewMode -ieq 'Paged') {
        $hasOrderBy = $false
        for ($i = 0; $i + 1 -lt $tokens.Count; $i++) {
            if ([string]$tokens[$i] -ieq 'ORDER' -and [string]$tokens[$i + 1] -ieq 'BY') {
                $hasOrderBy = $true
                break
            }
        }
        Assert-Design $hasOrderBy "预览模式为 Paged 时，数据集 SQL 必须包含稳定的 ORDER BY。"
    }

    # 阶段三：提取 @变量（负向环视排除 @@error 等服务器变量），与后端 ParameterRegex 一致。
    $codes = [System.Collections.Generic.List[string]]::new()
    foreach ($match in [regex]::Matches($sanitized, '(?<!@)@([A-Za-z][A-Za-z0-9_]{0,63})')) {
        $codes.Add($match.Groups[1].Value)
    }
    return @($codes)
}

<#
.SYNOPSIS
从模板文本（标题/参数行）提取 params/detail 两类 token，返回匹配对象列表。
正则与后端 PocTemplateValidator.TokenRegex 一致（允许 token 两侧空白）。
#>
function Get-TemplateTokens {
    param(
        # 模板文本，例如 "开始日期：{{params.startDate}}"。
        [string]$Text
    )
    if ([string]::IsNullOrEmpty($Text)) {
        return @()
    }
    return @([regex]::Matches($Text, '\{\{[ \t\r\n\f\v]*(params|detail)\.([A-Za-z][A-Za-z0-9_]*)[ \t\r\n\f\v]*\}\}'))
}

<#
.SYNOPSIS
把一基列序号转换为 Excel A1 列字母（1→A，27→AA），供合计行 SUM 公式使用。
#>
function Convert-ColumnIndexToLetter {
    param(
        # 一基列序号。
        [int]$Index
    )
    $letters = ''
    $value = $Index
    while ($value -gt 0) {
        $remainder = ($value - 1) % 26
        $letters = [char](65 + $remainder) + $letters
        $value = [int](($value - 1) / 26)
    }
    return $letters
}

<#
.SYNOPSIS
构造模板行合并区内的单元格对象（值 + 命名样式引用），对齐 Univer cellData 形态。
#>
function New-Cell {
    param(
        # 单元格显示文本；公式单元格传 $null。
        [object]$Value,

        # 单元格引用的命名样式键。
        [string]$Style,

        # 单元格公式（含前导 =）；无公式传 $null。参数用 object 承载，避免 [string] 把 null 归一成空串。
        [object]$Formula
    )
    $cell = [ordered]@{}
    if ($null -ne $Formula -and [string]$Formula -ne '') {
        $cell['f'] = $Formula
    }
    if ($null -ne $Value) {
        $cell['v'] = $Value
    }
    if (-not [string]::IsNullOrEmpty($Style)) {
        $cell['s'] = $Style
    }
    return $cell
}

<#
.SYNOPSIS
按固定布局构造 Univer IWorkbookData 快照：行0标题、行1参数行、行2表头、行3明细模板行、行4合计行。
#>
function New-TemplateSnapshot {
    param(
        # 已校验的完整设计上下文（见主流程装配说明）。
        [hashtable]$Design
    )

    # 阶段一：装配 cellData 稀疏结构。参数行允许整格混排文字与 params token（后端仅要求 detail 独占单元格）。
    $cellData = [ordered]@{
        '0' = [ordered]@{ '0' = New-Cell -Value $Design.title -Style 'title' -Formula $null }
    }
    if (-not [string]::IsNullOrEmpty($Design.parameterLine)) {
        $cellData['1'] = [ordered]@{ '0' = New-Cell -Value $Design.parameterLine -Style 'meta' -Formula $null }
    }
    $headerRow = [ordered]@{}
    $detailRow = [ordered]@{}
    for ($index = 0; $index -lt $Design.columns.Count; $index++) {
        $column = $Design.columns[$index]
        $headerRow["$index"] = New-Cell -Value $column.header -Style 'header' -Formula $null
        $detailStyle = if ($column.fieldDataType -ieq 'number') { 'number' } else { 'center' }
        $detailRow["$index"] = New-Cell -Value "{{detail.$($column.field)}}" -Style $detailStyle -Formula $null
    }
    $cellData['2'] = $headerRow
    $cellData['3'] = $detailRow
    if ($Design.totals.Count -gt 0) {
        # 阶段二：合计行。SUM 显式范围两端点都落在明细行（一基第 4 行），导出展开时由
        # PocFormulaTranslator.InsertRowsVisitor 自动扩展为明细实际行区间（同 JNPF 迁移写法）。
        $totalsRow = [ordered]@{
            '0' = New-Cell -Value $Design.totals[0].label -Style 'totalsLabel' -Formula $null
        }
        foreach ($total in $Design.totals) {
            $letter = Convert-ColumnIndexToLetter -Index ($total.columnIndex + 1)
            $totalsRow["$($total.columnIndex)"] = New-Cell -Value $null -Style 'totals' -Formula "=SUM($letter$($Design.detailRowOneBased):$letter$($Design.detailRowOneBased))"
        }
        $cellData['4'] = $totalsRow
    }

    # 阶段三：装配工作表元数据。列宽/行高/合并区对齐 PocTemplateService.CreateInitialTemplateJson；
    # 合并区只覆盖标题行，不与明细模板行相交（后端硬约束）。
    $usedColumnCount = [Math]::Max(6, $Design.columns.Count)
    $columnData = [ordered]@{}
    for ($index = 0; $index -lt $usedColumnCount; $index++) {
        $columnData["$index"] = [ordered]@{ 'w' = $(if ($index -eq 0) { 150 } else { 120 }) }
    }
    $rowData = [ordered]@{
        '0' = [ordered]@{ 'h' = 42 }
        '1' = [ordered]@{ 'h' = 30 }
        '2' = [ordered]@{ 'h' = 34 }
    }
    if ($Design.totals.Count -gt 0) {
        $rowData['4'] = [ordered]@{ 'h' = 30 }
    }

    # 阶段四：装配工作簿外壳。样式键自由命名，cells 通过 s 引用；明细行样式与初始模板同语义。
    $snapshot = [ordered]@{
        'id'         = "report-$($Design.reportCode)"
        'rev'        = 1
        'name'       = $Design.reportName
        'appVersion' = '1.0.0'
        'locale'     = 'zhCN'
        'styles'     = [ordered]@{
            'title'       = [ordered]@{ 'ff' = 'Microsoft YaHei'; 'fs' = 16; 'bl' = 1; 'vt' = 2 }
            'meta'        = [ordered]@{ 'ff' = 'Microsoft YaHei'; 'fs' = 10; 'cl' = [ordered]@{ 'rgb' = '#344054' }; 'vt' = 2 }
            'header'      = [ordered]@{ 'ff' = 'Microsoft YaHei'; 'fs' = 10; 'bl' = 1; 'bg' = [ordered]@{ 'rgb' = '#e9eef5' }; 'cl' = [ordered]@{ 'rgb' = '#18212b' }; 'ht' = 2; 'vt' = 2 }
            'center'      = [ordered]@{ 'ht' = 2; 'vt' = 2 }
            'number'      = [ordered]@{ 'ht' = 3; 'vt' = 2; 'n' = [ordered]@{ 'pattern' = '#,##0.00' } }
            'totalsLabel' = [ordered]@{ 'ff' = 'Microsoft YaHei'; 'fs' = 10; 'bl' = 1; 'ht' = 2; 'vt' = 2 }
            'totals'      = [ordered]@{ 'ff' = 'Microsoft YaHei'; 'fs' = 10; 'bl' = 1; 'ht' = 3; 'vt' = 2; 'n' = [ordered]@{ 'pattern' = '#,##0.00' } }
        }
        'sheetOrder' = @('report-sheet')
        'sheets'     = [ordered]@{
            'report-sheet' = [ordered]@{
                'id'                 = 'report-sheet'
                'name'               = $Design.sheetName
                'hidden'             = 0
                'rowCount'           = 50
                'columnCount'        = [Math]::Max(12, $usedColumnCount)
                'zoomRatio'          = 1
                'defaultColumnWidth' = 100
                'defaultRowHeight'   = 26
                'mergeData'          = @(
                    [ordered]@{ 'startRow' = 0; 'endRow' = 0; 'startColumn' = 0; 'endColumn' = ($usedColumnCount - 1) }
                )
                'cellData'           = $cellData
                'rowData'            = $rowData
                'columnData'         = $columnData
                'rowHeader'          = [ordered]@{ 'width' = 46; 'hidden' = 0 }
                'columnHeader'       = [ordered]@{ 'height' = 24; 'hidden' = 0 }
                'showGridlines'      = 1
                'rightToLeft'        = 0
            }
        }
    }
    return $snapshot
}

<#
.SYNOPSIS
构造与 PocTemplateValidator.Scan 输出完全一致的规范绑定（validate 阶段硬比对项）。
#>
function New-TemplateBinding {
    param(
        # 已校验的完整设计上下文。
        [hashtable]$Design
    )

    # 阶段一：收集标题格（行0列0）与参数行格（行1列0）内全部 params token，逐次出现各记一条，
    # 再按 (工作表, 行, 列, 编码) 排序——与后端 Scan 的规范排序完全一致。
    $parameterBindings = [System.Collections.Generic.List[object]]::new()
    $textCells = @(
        @{ row = 0; text = $Design.title },
        @{ row = 1; text = $Design.parameterLine }
    )
    foreach ($textCell in $textCells) {
        if ([string]::IsNullOrEmpty($textCell.text)) {
            continue
        }
        foreach ($match in (Get-TemplateTokens -Text $textCell.text)) {
            if ($match.Groups[1].Value -ieq 'detail') {
                continue
            }
            $parameterBindings.Add([ordered]@{
                'code'    = $match.Groups[2].Value
                'sheetId' = 'report-sheet'
                'row'     = $textCell.row
                'column'  = 0
            })
        }
    }
    $orderedParameterBindings = @($parameterBindings | Sort-Object -Property `
        { $_['sheetId'] }, { $_['row'] }, { $_['column'] }, { $_['code'] })

    # 阶段二：明细绑定。datasetCode 固定为字面量 'detail'（与后端 Scan 产出一致，非真实数据集编码）。
    $detailFields = @()
    if ($Design.columns.Count -gt 0) {
        $detailFields = @($Design.columns | ForEach-Object {
            [ordered]@{ 'code' = $_.field; 'column' = $_.columnIndex }
        } | Sort-Object -Property { $_['column'] })
    }
    return [ordered]@{
        'version'           = 1
        'parameterBindings' = $orderedParameterBindings
        'detailBinding'     = [ordered]@{
            'datasetCode' = 'detail'
            'sheetId'     = 'report-sheet'
            'templateRow' = $Design.detailRowZeroBased
            'fields'      = $detailFields
        }
    }
}

<#
.SYNOPSIS
把哈希/数组/标量递归序列化为两空格缩进 JSON；字符串转义与属性命名完全手动控制，
保证 camelCase 字段名、非 ASCII 原样输出（UTF-8 无 BOM），且同一 spec 产出字节级稳定。
#>
function ConvertTo-PackageJson {
    param(
        # 待序列化值：有序哈希、数组或标量。
        [object]$Value,

        # 当前缩进层级，从 0 开始。
        [int]$Level = 0
    )

    # 阶段一：标量与空值分支——数字用固定文化格式，布尔小写，字符串走统一转义。
    $indent = '  ' * $Level
    $childIndent = '  ' * ($Level + 1)
    if ($null -eq $Value) {
        return 'null'
    }
    if ($Value -is [bool]) {
        if ($Value) { return 'true' }
        return 'false'
    }
    if ($Value -is [sbyte] -or $Value -is [byte] -or $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int] -or $Value -is [uint32] -or $Value -is [long] -or $Value -is [uint64]) {
        return $Value.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [double] -or $Value -is [single] -or $Value -is [decimal]) {
        return $Value.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [string]) {
        return Convert-JsonString -Text $Value
    }

    # 阶段二：数组分支——空数组输出 []，否则逐元素递归换行缩进。
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [System.Collections.IDictionary] -and $Value -isnot [pscustomobject]) {
        $items = @($Value)
        if ($items.Count -eq 0) {
            return '[]'
        }
        $lines = foreach ($item in $items) {
            "$childIndent$(ConvertTo-PackageJson -Value $item -Level ($Level + 1))"
        }
        return "[`n" + ($lines -join ",`n") + "`n$indent]"
    }

    # 阶段三：对象分支——有序哈希按插入序输出键；PSCustomObject 按属性序兜底。
    # 注意不能把键值对先转成 string[] 再序列化，否则嵌套对象会被退化为类型名字符串。
    $isDictionary = $Value -is [System.Collections.IDictionary]
    $entryCount = if ($isDictionary) { $Value.Count } else { $Value.PSObject.Properties.Count }
    if ($entryCount -eq 0) {
        return '{}'
    }
    $lines = if ($isDictionary) {
        foreach ($key in $Value.Keys) {
            "$childIndent$(Convert-JsonString -Text ([string]$key)): $(ConvertTo-PackageJson -Value $Value[$key] -Level ($Level + 1))"
        }
    }
    else {
        foreach ($property in $Value.PSObject.Properties) {
            "$childIndent$(Convert-JsonString -Text $property.Name): $(ConvertTo-PackageJson -Value $property.Value -Level ($Level + 1))"
        }
    }
    return "{`n" + ($lines -join ",`n") + "`n$indent}"
}

<#
.SYNOPSIS
转义 JSON 字符串：控制字符转 \uXXXX，常用控制符用短转义，引号与反斜杠加反斜杠。
#>
function Convert-JsonString {
    param(
        # 原始文本。
        [string]$Text
    )
    $builder = [System.Text.StringBuilder]::new($Text.Length + 2)
    $null = $builder.Append('"')
    foreach ($character in $Text.ToCharArray()) {
        switch ($character) {
            '"' { $null = $builder.Append('\"') }
            '\' { $null = $builder.Append('\\') }
            "`n" { $null = $builder.Append('\n') }
            "`r" { $null = $builder.Append('\r') }
            "`t" { $null = $builder.Append('\t') }
            "`b" { $null = $builder.Append('\b') }
            "`f" { $null = $builder.Append('\f') }
            default {
                if ([int]$character -lt 0x20) {
                    $null = $builder.Append('\u{0:x4}' -f [int]$character)
                }
                else {
                    $null = $builder.Append($character)
                }
            }
        }
    }
    $null = $builder.Append('"')
    return $builder.ToString()
}

<#
.SYNOPSIS
校验 spec 的数据集节：编码、类型白名单、变量/字段契约与数量上限，返回规范化后的上下文数据。
#>
function Read-DatasetDesign {
    param(
        # spec 的 dataset 节点。
        [object]$Dataset
    )
    Assert-Design ($null -ne $Dataset) "缺少 dataset 节。"

    # 阶段一：编码、名称与数据源指向校验；数据源类型必须落在后端支持的四类方言内。
    $code = [string](Get-SpecValue $Dataset 'code')
    $name = [string](Get-SpecValue $Dataset 'name')
    Assert-SpecCode -Code $code -Label '数据集编码'
    Assert-Design (-not [string]::IsNullOrWhiteSpace($name) -and $name.Length -le 128) "数据集名称必填且不超过 128 字符。"
    $dataSourceCode = [string](Get-SpecValue $Dataset 'dataSourceCode')
    Assert-SpecCode -Code $dataSourceCode -Label '数据源编码'
    $dataSourceType = [string](Get-SpecValue $Dataset 'dataSourceType')
    $normalizedType = $script:SupportedDataSourceTypes | Where-Object { $_ -ieq $dataSourceType } | Select-Object -First 1
    Assert-Design ($null -ne $normalizedType) "数据源类型 '$dataSourceType' 不受支持，仅允许 $($script:SupportedDataSourceTypes -join ' / ')。"

    # 阶段二：SQL 与运行参数校验；超时/行数上限与后端保存链路一致。
    $queryText = [string](Get-SpecValue $Dataset 'queryText')
    Assert-Design (-not [string]::IsNullOrWhiteSpace($queryText)) "数据集 queryText 必填。"
    Assert-Design ($queryText.Length -le 200000) "数据集 queryText 超过 200000 字符上限。"
    $timeoutSeconds = [int]($(if ($null -ne (Get-SpecValue $Dataset 'timeoutSeconds')) { (Get-SpecValue $Dataset 'timeoutSeconds') } else { 60 }))
    $maxRows = [int]($(if ($null -ne (Get-SpecValue $Dataset 'maxRows')) { (Get-SpecValue $Dataset 'maxRows') } else { 20000 }))
    Assert-Design ($timeoutSeconds -ge 1 -and $timeoutSeconds -le 600) "数据集 timeoutSeconds 必须在 1-600 之间。"
    Assert-Design ($maxRows -ge 1 -and $maxRows -le 1000000) "数据集 maxRows 必须在 1-1000000 之间。"

    # 阶段三：变量契约校验——编码唯一、类型白名单、必填默认 false。
    $variables = Get-SpecArray $Dataset 'variables'
    Assert-Design ($variables.Count -le 100) "数据集变量数量不能超过 100。"
    $variableCodes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $variableDesigns = foreach ($variable in $variables) {
        $variableCode = [string](Get-SpecValue $variable 'code')
        $variableDataType = [string](Get-SpecValue $variable 'dataType')
        Assert-SpecCode -Code $variableCode -Label '变量编码'
        Assert-Design $variableCodes.Add($variableCode) "数据集变量编码 '$variableCode' 重复。"
        $matchedVariableType = $script:VariableDataTypes | Where-Object { $_ -ieq $variableDataType } | Select-Object -First 1
        Assert-Design ($null -ne $matchedVariableType) "变量 '$variableCode' 的 dataType '$variableDataType' 不受支持，仅允许 $($script:VariableDataTypes -join ' / ')。"
        [ordered]@{
            'code'        = $variableCode
            'displayName' = [string](Get-SpecValue $variable 'displayName')
            'dataType'    = $matchedVariableType
            'required'    = [bool](Get-SpecValue $variable 'required')
        }
    }

    # 阶段四：字段契约校验——编码唯一、类型白名单、非空且不超过 256 个。
    $fields = Get-SpecArray $Dataset 'fields'
    Assert-Design ($fields.Count -gt 0 -and $fields.Count -le 256) "数据集 fields 必填且不超过 256 个。"
    $fieldCodes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $fieldDesigns = foreach ($field in $fields) {
        $fieldCode = [string](Get-SpecValue $field 'code')
        $fieldDataType = [string](Get-SpecValue $field 'dataType')
        Assert-SpecCode -Code $fieldCode -Label '字段编码'
        Assert-Design $fieldCodes.Add($fieldCode) "数据集字段编码 '$fieldCode' 重复。"
        $matchedFieldType = $script:FieldDataTypes | Where-Object { $_ -ieq $fieldDataType } | Select-Object -First 1
        Assert-Design ($null -ne $matchedFieldType) "字段 '$fieldCode' 的 dataType '$fieldDataType' 不受支持，仅允许 $($script:FieldDataTypes -join ' / ')。"
        [ordered]@{
            'code'        = $fieldCode
            'displayName' = [string](Get-SpecValue $field 'displayName')
            'dataType'    = $matchedFieldType
        }
    }

    return @{
        code           = $code
        name           = $name
        dataSourceCode = $dataSourceCode
        dataSourceType = $normalizedType
        queryText      = $queryText
        timeoutSeconds = $timeoutSeconds
        maxRows        = $maxRows
        variables      = (ConvertTo-SafeArray $variableDesigns)
        fields         = (ConvertTo-SafeArray $fieldDesigns)
    }
}

<#
.SYNOPSIS
校验 spec 的报表节与参数节：名称/工作表、预览与导出设置、参数定义与变量绑定对齐。
#>
function Read-ReportAndParameterDesign {
    param(
        # spec 的 report 节点。
        [object]$Report,

        # spec 的 parameters 数组。
        [object[]]$Parameters,

        # 已规范化的数据集设计数据（变量绑定对齐需要其 variables）。
        [hashtable]$DatasetDesign
    )
    Assert-Design ($null -ne $Report) "缺少 report 节。"

    # 阶段一：报表标识与工作表名校验；工作表规则与后端 ValidateRequest 完全一致。
    $code = [string](Get-SpecValue $Report 'code')
    $name = [string](Get-SpecValue $Report 'name')
    $sheetName = [string](Get-SpecValue $Report 'sheetName')
    Assert-SpecCode -Code $code -Label '报表编码'
    Assert-Design (-not [string]::IsNullOrWhiteSpace($name) -and $name.Length -le 128) "报表名称必填且不超过 128 字符。"
    Assert-Design (-not [string]::IsNullOrWhiteSpace($sheetName) -and $sheetName.Length -le 31) "工作表名 sheetName 必填且不超过 31 字符。"
    Assert-Design (-not ($sheetName.IndexOfAny([char[]]@('[', ']', ':', '*', '?', '/', '\')) -ge 0)) "工作表名 sheetName 不能包含 []:*?/\ 字符。"

    # 阶段二：预览与导出设置校验（缺省值与后端建议一致）；Paged 的 ORDER BY 在 SQL 校验阶段判定。
    $preview = Get-SpecValue $Report 'preview'
    $previewMode = [string]($(if ($null -ne (Get-SpecValue $preview 'mode')) { (Get-SpecValue $preview 'mode') } else { 'Full' }))
    Assert-Design ($previewMode -ieq 'Full' -or $previewMode -ieq 'Paged') "preview.mode 只允许 Full 或 Paged。"
    $previewMode = $(if ($previewMode -ieq 'Paged') { 'Paged' } else { 'Full' })
    $previewPageSize = [int]($(if ($null -ne (Get-SpecValue $preview 'pageSize')) { (Get-SpecValue $preview 'pageSize') } else { 20 }))
    Assert-Design ($previewPageSize -ge 1 -and $previewPageSize -le 10000) "preview.pageSize 必须在 1-10000 之间。"
    $showExactTotal = [bool]($(if ($null -ne (Get-SpecValue $preview 'showExactTotal')) { (Get-SpecValue $preview 'showExactTotal') } else { $false }))

    $export = Get-SpecValue $Report 'export'
    $defaultMode = [string]($(if ($null -ne (Get-SpecValue $export 'defaultMode')) { (Get-SpecValue $export 'defaultMode') } else { 'Normal' }))
    Assert-Design ($defaultMode -ieq 'Normal' -or $defaultMode -ieq 'Large') "export.defaultMode 只允许 Normal 或 Large。"
    $defaultMode = $(if ($defaultMode -ieq 'Large') { 'Large' } else { 'Normal' })
    $normalMaxRows = [int]($(if ($null -ne (Get-SpecValue $export 'normalMaxRows')) { (Get-SpecValue $export 'normalMaxRows') } else { 20000 }))
    $normalMaxCells = [int]($(if ($null -ne (Get-SpecValue $export 'normalMaxCells')) { (Get-SpecValue $export 'normalMaxCells') } else { 500000 }))
    $normalTimeoutSeconds = [int]($(if ($null -ne (Get-SpecValue $export 'normalTimeoutSeconds')) { (Get-SpecValue $export 'normalTimeoutSeconds') } else { 60 }))
    $largeExportEnabled = [bool]($(if ($null -ne (Get-SpecValue $export 'largeExportEnabled')) { (Get-SpecValue $export 'largeExportEnabled') } else { $true }))
    $rowsPerSheet = [int]($(if ($null -ne (Get-SpecValue $export 'rowsPerSheet')) { (Get-SpecValue $export 'rowsPerSheet') } else { 1000000 }))
    $fileRetentionHours = [int]($(if ($null -ne (Get-SpecValue $export 'fileRetentionHours')) { (Get-SpecValue $export 'fileRetentionHours') } else { 24 }))
    Assert-Design ($normalMaxRows -ge 1 -and $normalMaxRows -le 1000000) "export.normalMaxRows 必须在 1-1000000 之间。"
    Assert-Design ($normalMaxCells -ge 1 -and $normalMaxCells -le 20000000) "export.normalMaxCells 必须在 1-20000000 之间。"
    Assert-Design ($normalTimeoutSeconds -ge 1 -and $normalTimeoutSeconds -le 3600) "export.normalTimeoutSeconds 必须在 1-3600 之间。"
    Assert-Design ($rowsPerSheet -ge 1 -and $rowsPerSheet -le 1048576) "export.rowsPerSheet 必须在 1-1048576 之间。"
    Assert-Design ($fileRetentionHours -ge 1 -and $fileRetentionHours -le 720) "export.fileRetentionHours 必须在 1-720 之间。"
    Assert-Design (-not ($defaultMode -ieq 'Large' -and -not $largeExportEnabled)) "export.defaultMode 为 Large 时必须开启 largeExportEnabled。"
    # 阶段三：参数定义校验——编码/白名单/唯一性与后端保存链路同规则；displayOrder 缺省按声明顺序。
    $parameterCodes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $parameterDesigns = for ($index = 0; $index -lt $Parameters.Count; $index++) {
        $parameter = $Parameters[$index]
        $parameterCode = [string](Get-SpecValue $parameter 'code')
        $parameterDataType = [string](Get-SpecValue $parameter 'dataType')
        $parameterControlType = [string]($(if ($null -ne (Get-SpecValue $parameter 'controlType')) { (Get-SpecValue $parameter 'controlType') } else { 'text' }))
        $parameterSourceType = [string]($(if ($null -ne (Get-SpecValue $parameter 'sourceType')) { (Get-SpecValue $parameter 'sourceType') } else { 'Form' }))
        Assert-SpecCode -Code $parameterCode -Label '参数编码'
        Assert-Design $parameterCodes.Add($parameterCode) "参数编码 '$parameterCode' 重复。"
        $matchedParameterType = $script:ParameterDataTypes | Where-Object { $_ -ieq $parameterDataType } | Select-Object -First 1
        Assert-Design ($null -ne $matchedParameterType) "参数 '$parameterCode' 的 dataType '$parameterDataType' 不受支持，仅允许 $($script:ParameterDataTypes -join ' / ')。"
        $matchedControlType = $script:ParameterControlTypes | Where-Object { $_ -ieq $parameterControlType } | Select-Object -First 1
        Assert-Design ($null -ne $matchedControlType) "参数 '$parameterCode' 的 controlType '$parameterControlType' 不受支持，仅允许 $($script:ParameterControlTypes -join ' / ')。"
        $matchedSourceType = $script:ParameterSourceTypes | Where-Object { $_ -ieq $parameterSourceType } | Select-Object -First 1
        Assert-Design ($null -ne $matchedSourceType) "参数 '$parameterCode' 的 sourceType '$parameterSourceType' 不受支持，仅允许 $($script:ParameterSourceTypes -join ' / ')。"
        $defaultExpression = [string](Get-SpecValue $parameter 'defaultExpression')
        Assert-Design ($defaultExpression.Length -le 256) "参数 '$parameterCode' 的 defaultExpression 超过 256 字符。"
        [ordered]@{
            'code'             = $parameterCode
            'displayName'      = [string](Get-SpecValue $parameter 'displayName')
            'dataType'         = $matchedParameterType
            'controlType'      = $matchedControlType
            'sourceType'       = $matchedSourceType
            'urlKey'           = [string](Get-SpecValue $parameter 'urlKey')
            'defaultExpression'= $(if ([string]::IsNullOrWhiteSpace($defaultExpression)) { $null } else { $defaultExpression })
            'fixedValue'       = [string](Get-SpecValue $parameter 'fixedValue')
            'isVisible'        = [bool]($(if ($null -ne (Get-SpecValue $parameter 'isVisible')) { (Get-SpecValue $parameter 'isVisible') } else { $true }))
            'isReadOnly'       = [bool](Get-SpecValue $parameter 'isReadOnly')
            'isRequired'       = [bool](Get-SpecValue $parameter 'isRequired')
            'allowUrlPrefill'  = [bool](Get-SpecValue $parameter 'allowUrlPrefill')
            'displayOrder'     = [int]($(if ($null -ne (Get-SpecValue $parameter 'displayOrder')) { (Get-SpecValue $parameter 'displayOrder') } else { $index + 1 }))
        }
    }

    # 阶段四：变量绑定对齐——每个数据集变量必须有同名同类型（大小写不敏感）参数承接，
    # 与后端 ValidateDatasetVariableBindings 同规则，缺承接在生成期即失败。
    foreach ($variable in $DatasetDesign.variables) {
        $binding = $parameterDesigns | Where-Object { $_.code -ieq $variable.code -and $_.dataType -ieq $variable.dataType } | Select-Object -First 1
        Assert-Design ($null -ne $binding) "数据集变量 '@$($variable.code)'（$($variable.dataType)）缺少同名同类型的报表参数承接，请在 parameters 中补充。"
    }

    return @{
        code            = $code
        name            = $name
        sheetName       = $sheetName
        previewMode     = $previewMode
        previewPageSize = $previewPageSize
        showExactTotal  = $showExactTotal
        export          = [ordered]@{
            'defaultMode'          = $defaultMode
            'normalMaxRows'        = $normalMaxRows
            'normalMaxCells'       = $normalMaxCells
            'normalTimeoutSeconds' = $normalTimeoutSeconds
            'largeExportEnabled'   = $largeExportEnabled
            'rowsPerSheet'         = $rowsPerSheet
            'fileRetentionHours'   = $fileRetentionHours
        }
        parameters       = (ConvertTo-SafeArray $parameterDesigns)
    }
}

<#
.SYNOPSIS
校验 spec 的模板节并解析列/合计与 token 引用，产出模板构造所需的完整设计上下文。
#>
function Read-TemplateDesign {
    param(
        # spec 的 template 节点。
        [object]$Template,

        # 已规范化的数据集与报表设计数据。
        [hashtable]$DatasetDesign,
        [hashtable]$ReportDesign
    )
    Assert-Design ($null -ne $Template) "缺少 template 节。"

    # 阶段一：标题与参数行文本；title 允许混排 params token，但 detail token 一律拒绝
    # （后端要求 detail 独占单元格且同属一条模板行，混入固定文本无法通过校验）。
    $title = [string](Get-SpecValue $Template 'title')
    Assert-Design (-not [string]::IsNullOrWhiteSpace($title)) "template.title 必填。"
    $parameterLine = [string](Get-SpecValue $Template 'parameterLine')
    $parameterCodes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($parameter in $ReportDesign.parameters) {
        $null = $parameterCodes.Add($parameter.code)
    }
    foreach ($text in @($title, $parameterLine)) {
        if ([string]::IsNullOrEmpty($text)) {
            continue
        }
        foreach ($match in (Get-TemplateTokens -Text $text)) {
            Assert-Design ($match.Groups[1].Value -ine 'detail') "标题/参数行不允许出现 detail token，明细列请在 template.columns 中声明。"
            Assert-Design $parameterCodes.Contains($match.Groups[2].Value) "标题/参数行引用了未定义参数 '{{params.$($match.Groups[2].Value)}}'。"
        }
        $residual = [regex]::Replace($text, '\{\{[ \t\r\n\f\v]*(params|detail)\.([A-Za-z][A-Za-z0-9_]*)[ \t\r\n\f\v]*\}\}', '')
        Assert-Design (-not ($residual.Contains('{{') -or $residual.Contains('}}'))) "标题/参数行含无法识别的花括号 token，请检查 {{params.编码}} 写法。"
    }

    # 阶段二：列声明校验——每列 field 必须精确存在于 dataset.fields（后端 token 匹配区分大小写），
    # 列序号即明细行中的物理列坐标。
    $columns = Get-SpecArray $Template 'columns'
    Assert-Design ($columns.Count -gt 0) "template.columns 必填，至少一列。"
    $columnFields = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $columnDesigns = @()
    $columnIndex = 0
    foreach ($column in $columns) {
        $field = [string](Get-SpecValue $column 'field')
        $header = [string](Get-SpecValue $column 'header')
        Assert-Design (-not [string]::IsNullOrEmpty($field)) "template.columns 存在缺少 field 的列。"
        # 后端模板扫描按区分大小写的编码匹配 detail token，这里必须精确命中字段编码。
        $exactField = $DatasetDesign.fields | Where-Object { $_.code -ceq $field } | Select-Object -First 1
        Assert-Design ($null -ne $exactField) "template.columns 的 field '$field' 不在 dataset.fields 中（区分大小写），请先在数据集字段里声明。"
        Assert-Design $columnFields.Add($field) "template.columns 的 field '$field' 重复。"
        $columnDesigns += [ordered]@{
            'field'         = $field
            'header'        = $(if ([string]::IsNullOrEmpty($header)) { $field } else { $header })
            'fieldDataType' = $exactField.dataType
            'columnIndex'   = $columnIndex
        }
        $columnIndex++
    }

    # 阶段三：合计行校验——字段必须出现在列声明中且不得占用首列（首列留给合计标签）。
    $totals = Get-SpecArray $Template 'totals'
    $totalDesigns = @()
    foreach ($total in $totals) {
        $totalField = [string](Get-SpecValue $total 'field')
        $totalLabel = [string](Get-SpecValue $total 'label')
        Assert-Design (-not [string]::IsNullOrEmpty($totalField)) "template.totals 存在缺少 field 的合计项。"
        Assert-Design (-not [string]::IsNullOrEmpty($totalLabel)) "合计项 '$totalField' 缺少 label 文本。"
        $matchedColumn = $columnDesigns | Where-Object { $_.field -ceq $totalField } | Select-Object -First 1
        Assert-Design ($null -ne $matchedColumn) "template.totals 的 field '$totalField' 不在 template.columns 中，合计列必须同为明细列。"
        Assert-Design ($matchedColumn.columnIndex -gt 0) "合计项 '$totalField' 落在首列；首列用于放置合计标签，请调整列顺序。"
        $totalDesigns += [ordered]@{
            'field'       = $totalField
            'label'       = $totalLabel
            'columnIndex' = $matchedColumn.columnIndex
        }
    }

    return @{
        title              = $title
        parameterLine      = $parameterLine
        columns            = $columnDesigns
        totals             = $totalDesigns
        detailRowZeroBased = 3
        detailRowOneBased  = 4
    }
}

<#
.SYNOPSIS
组装完整报表包对象（camelCase 手工命名，与后端序列化契约和前端 types.ts 一致）。
#>
function New-ReportPackageObject {
    param(
        # spec 根节点（读取可选的 fieldPresentations）。
        [object]$Spec,

        # 已校验的报表/数据集/模板设计数据。
        [hashtable]$ReportDesign,
        [hashtable]$DatasetDesign,
        [hashtable]$TemplateDesign
    )

    # 阶段一：快照与绑定构造。绑定必须与后端 PocTemplateValidator.Scan 的重扫结果完全一致。
    $design = @{
        reportCode         = $ReportDesign.code
        reportName         = $ReportDesign.name
        sheetName          = $ReportDesign.sheetName
        title              = $TemplateDesign.title
        parameterLine      = $TemplateDesign.parameterLine
        columns            = $TemplateDesign.columns
        totals             = $TemplateDesign.totals
        detailRowZeroBased = $TemplateDesign.detailRowZeroBased
        detailRowOneBased  = $TemplateDesign.detailRowOneBased
    }
    $snapshot = New-TemplateSnapshot -Design $design
    $binding = New-TemplateBinding -Design $design

    # 阶段二：参数列表按包契约补全全部字段（sourceCode/sourceFieldCode 固定为 null：
    # 外部参数源依赖涉及目标环境配置，走报表中心配置页另行设置，spec 阶段不承载）。
    $packageParameters = @($ReportDesign.parameters | ForEach-Object {
        [ordered]@{
            'code'             = $_.code
            'displayName'      = $_.displayName
            'dataType'         = $_.dataType
            'controlType'      = $_.controlType
            'sourceType'       = $_.sourceType
            'sourceCode'       = $null
            'sourceFieldCode'  = $null
            'urlKey'           = $(if ([string]::IsNullOrEmpty($_.urlKey)) { $null } else { $_.urlKey })
            'defaultExpression'= $_.defaultExpression
            'fixedValue'       = $(if ([string]::IsNullOrEmpty($_.fixedValue)) { $null } else { $_.fixedValue })
            'options'          = $null
            'isVisible'        = $_.isVisible
            'isReadOnly'       = $_.isReadOnly
            'isRequired'       = $_.isRequired
            'allowUrlPrefill'  = $_.allowUrlPrefill
            'displayOrder'     = $_.displayOrder
        }
    })

    # 阶段三：可选字段显示映射（如 0/1 → 停用/启用）原样透传到 report.fieldPresentations。
    $fieldPresentations = $null
    $rawPresentations = Get-SpecArray (Get-SpecValue $Spec 'report') 'fieldPresentations'
    if ($rawPresentations.Count -gt 0) {
        $fieldPresentations = @($rawPresentations | ForEach-Object {
            # Get-SpecArray 以防展开方式返回数组，不能内联进管道（会整体变成单个元素），
            # 必须先用 foreach 展开映射项再逐项转字符串。
            $mappings = @()
            foreach ($mapping in (Get-SpecArray $_ 'valueMappings')) {
                $mappings += [ordered]@{
                    'value' = [string](Get-SpecValue $mapping 'value')
                    'label' = [string](Get-SpecValue $mapping 'label')
                }
            }
            [ordered]@{
                'code'          = [string](Get-SpecValue $_ 'code')
                'valueMappings' = $mappings
            }
        })
    }

    # 阶段四：组装根对象。exportedAt 使用 UTC ISO 8601；isEnabled 固定启用（停用包无法导入）。
    return [ordered]@{
        'packageKind'          = 'hd-report-center.report-package'
        'packageVersion'       = 1
        'exportedAt'           = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        'sourceDataSourceCode' = $DatasetDesign.dataSourceCode
        'sourceDataSourceType' = $DatasetDesign.dataSourceType
        'report'               = [ordered]@{
            'code'              = $ReportDesign.code
            'name'              = $ReportDesign.name
            'sheetName'         = $ReportDesign.sheetName
            'datasetCode'       = $DatasetDesign.code
            'previewSettings'   = [ordered]@{
                'mode'           = $ReportDesign.previewMode
                'pageSize'       = $ReportDesign.previewPageSize
                'showExactTotal' = $ReportDesign.showExactTotal
            }
            'exportSettings'    = $ReportDesign.export
            'fieldPresentations'= $fieldPresentations
            'isEnabled'         = $true
        }
        'parameters'           = $packageParameters
        'template'             = [ordered]@{
            'snapshot' = $snapshot
            'binding'  = $binding
            'revision' = 1
        }
        'dataset'              = [ordered]@{
            'code'           = $DatasetDesign.code
            'name'           = $DatasetDesign.name
            'queryText'      = $DatasetDesign.queryText
            'fieldSchema'    = [ordered]@{
                # 变量按后端 ReadSchema 的规范化形态输出：sourceType 固定 Form，
                # sourceCode/sourceFieldCode 仅 External 源才有值（spec 流水线不承载外部源绑定）。
                'variables' = @($DatasetDesign.variables | ForEach-Object {
                    [ordered]@{
                        'code'            = $_.code
                        'displayName'     = $_.displayName
                        'dataType'        = $_.dataType
                        'required'        = $_.required
                        'sourceType'      = 'Form'
                        'sourceCode'      = $null
                        'sourceFieldCode' = $null
                    }
                })
                'fields'    = @($DatasetDesign.fields)
            }
            'timeoutSeconds' = $DatasetDesign.timeoutSeconds
            'maxRows'        = $DatasetDesign.maxRows
            'isEnabled'      = $true
        }
    }
}

# 主流程：读 spec → 校验三大节 → 构造包对象 → 写出文件 → 打印摘要；任何失败以非零退出。
try {
    # 阶段一：读取并解析 spec。JSON 语法错误在此即失败，给出文件与原因。
    Assert-Design ([string]::IsNullOrWhiteSpace($SpecPath) -eq $false) "必须通过 -SpecPath 指定 spec 文件。"
    Assert-Design (Test-Path -LiteralPath $SpecPath -PathType Leaf) "spec 文件不存在：$SpecPath"
    $rawSpec = Get-Content -LiteralPath $SpecPath -Raw -Encoding UTF8
    try {
        $spec = $rawSpec | ConvertFrom-Json
    }
    catch {
        Assert-Design $false "spec 不是合法 JSON：$($_.Exception.Message)"
    }
    $specVersion = Get-SpecValue $spec 'specVersion'
    Assert-Design ($null -ne $specVersion -and [int]$specVersion -eq 1) "specVersion 必须为 1，当前为 '$specVersion'。"

    # 阶段二：三大节依次校验并规范化；顺序为数据集 → 报表/参数 → 模板（后者依赖前者的契约）。
    $datasetDesign = Read-DatasetDesign -Dataset (Get-SpecValue $spec 'dataset')
    $reportDesign = Read-ReportAndParameterDesign -Report (Get-SpecValue $spec 'report') `
        -Parameters (Get-SpecArray $spec 'parameters') -DatasetDesign $datasetDesign
    $templateDesign = Read-TemplateDesign -Template (Get-SpecValue $spec 'template') `
        -DatasetDesign $datasetDesign -ReportDesign $reportDesign

    # 阶段三：SQL 静态只读校验与变量引用核对；@变量必须全部在数据集变量中声明。
    $referencedSqlVariables = Get-SqlParameterReferences -QueryText $datasetDesign.queryText -PreviewMode $reportDesign.previewMode
    $declaredVariables = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($variable in $datasetDesign.variables) {
        $null = $declaredVariables.Add($variable.code)
    }
    foreach ($referenced in ($referencedSqlVariables | Select-Object -Unique)) {
        Assert-Design $declaredVariables.Contains($referenced) "SQL 中的 @变量 '$referenced' 未在 dataset.variables 中声明。"
    }

    # 阶段四：构造包对象并写出。默认输出到仓库 output/report-design/<报表编码>.report-package.json。
    $package = New-ReportPackageObject -Spec $spec -ReportDesign $reportDesign -DatasetDesign $datasetDesign -TemplateDesign $templateDesign
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $OutputPath = Join-Path $repoRoot "output/report-design/$($reportDesign.code).report-package.json"
    }
    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        $null = New-Item -ItemType Directory -Force -Path $outputDirectory
    }
    [System.IO.File]::WriteAllText($OutputPath, (ConvertTo-PackageJson -Value $package), [System.Text.UTF8Encoding]::new($false))

    # 阶段五：打印生成摘要，便于人工核对包内容后再走导入向导。
    Write-Host "[OK] 报表包已生成: $OutputPath"
    Write-Host ("     报表: {0}（{1}）" -f $reportDesign.code, $reportDesign.name)
    Write-Host ("     数据集: {0}（{1}，超时 {2}s，行数上限 {3}）" -f $datasetDesign.code, $datasetDesign.dataSourceType, $datasetDesign.timeoutSeconds, $datasetDesign.maxRows)
    Write-Host ("     参数: {0} 个；字段: {1} 个" -f $reportDesign.parameters.Count, $datasetDesign.fields.Count)
    Write-Host ("     模板: 标题 + {0}列 表头 + 明细行{1}；预览 {2}" -f @(
        $templateDesign.columns.Count,
        $(if ($templateDesign.totals.Count -gt 0) { " + 合计行（$($templateDesign.totals.Count) 项）" } else { '' }),
        $reportDesign.previewMode))
    Write-Host "     下一步：启动本地后端，在报表中心走『报表导入』向导完成 校验 → 试跑 → 导入。"
    exit 0
}
catch {
    Write-Host "[失败] $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "       spec 文件：$SpecPath" -ForegroundColor Red
    exit 1
}
