Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 查询或读取 JnpfHisReportMigration 所需的 Get Map Value 数据，并按调用约定返回结果。
function Get-MapValue {
    param(
        [object]$InputObject,
        [string[]]$Names,
        [object]$DefaultValue = $null
    )

    if ($null -eq $InputObject) {
        return $DefaultValue
    }

    foreach ($name in $Names) {
        if ($InputObject -is [System.Collections.IDictionary]) {
            foreach ($key in $InputObject.Keys) {
                if ([string]$key -ieq $name) {
                    return $InputObject[$key]
                }
            }
        }
        else {
            $property = $InputObject.PSObject.Properties |
                Where-Object { $_.Name -ieq $name } |
                Select-Object -First 1
            if ($null -ne $property) {
                return $property.Value
            }
        }
    }

    return $DefaultValue
}

# 将 JnpfHisReportMigration 中的 Convert From Optional Json 输入转换为调用方需要的稳定格式。
function ConvertFrom-OptionalJson {
    param(
        [AllowNull()]
        [string]$Json,
        [object]$DefaultValue = $null
    )

    if ([string]::IsNullOrWhiteSpace($Json)) {
        return $DefaultValue
    }

    try {
        return $Json | ConvertFrom-Json -AsHashtable
    }
    catch {
        throw "JNPF 元数据 JSON 无法解析：$($_.Exception.Message)"
    }
}

# 将 JnpfHisReportMigration 中的 Convert To Target Code 输入转换为调用方需要的稳定格式。
function ConvertTo-TargetCode {
    param(
        [AllowNull()]
        [string]$Value,
        [Parameter(Mandatory)]
        [string]$Fallback,
        [int]$MaximumLength = 64
    )

    $candidate = if ([string]::IsNullOrWhiteSpace($Value)) { $Fallback } else { $Value.Trim() }
    $candidate = [regex]::Replace($candidate, '[^A-Za-z0-9_-]+', '_').Trim('_', '-')
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = $Fallback
    }
    if ($candidate -notmatch '^[A-Za-z]') {
        $candidate = "jnpf_$candidate"
    }
    if ($candidate.Length -le $MaximumLength) {
        return $candidate.ToLowerInvariant()
    }

    $sha = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($candidate))
    $suffix = [Convert]::ToHexString($sha).Substring(0, 8).ToLowerInvariant()
    return ($candidate.Substring(0, $MaximumLength - $suffix.Length - 1).TrimEnd('_', '-') + '_' + $suffix).ToLowerInvariant()
}

# 将 JnpfHisReportMigration 中的 Convert To Target Data Type 输入转换为调用方需要的稳定格式。
function ConvertTo-TargetDataType {
    param([AllowNull()][string]$Value)

    $normalized = if ($null -eq $Value) { '' } else { $Value.Trim().ToLowerInvariant() }
    switch -Regex ($normalized) {
        '^(int|integer|long|short|decimal|double|float|number|numeric|money)$' { return 'number' }
        '^(date|datetime|timestamp|time)$' { return 'datetime' }
        '^(bool|boolean|bit)$' { return 'boolean' }
        default { return 'string' }
    }
}

# 将 JnpfHisReportMigration 中的 Convert Jnpf Field Definitions 输入转换为调用方需要的稳定格式。
function Convert-JnpfFieldDefinitions {
    param([Parameter(Mandatory)][string]$FieldJson)

    $sourceFields = @(ConvertFrom-OptionalJson $FieldJson @())
    if ($sourceFields.Count -eq 0) {
        # 第一阶段：根据 Convert-JnpfFieldDefinitions 当前分支转换输入数据，并保存该阶段产生的中间结果。
        throw 'JNPF 数据接口没有返回字段定义。'
    }

    $usedCodes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $result = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $sourceFields.Count; $index++) {
        $sourceField = $sourceFields[$index]
        $sourceName = [string](Get-MapValue $sourceField @('field', 'columnName', 'column', 'name'))
        if ([string]::IsNullOrWhiteSpace($sourceName)) {
            throw "JNPF 第 $($index + 1) 个返回字段缺少字段名。"
        }

        $explicitCode = [string](Get-MapValue $sourceField @('fieldCode', 'enCode', 'code'))
        $codeCandidate = if ($explicitCode -match '^[A-Za-z][A-Za-z0-9_]*$') {
            $explicitCode
        }
        elseif ($sourceName -match '^[A-Za-z][A-Za-z0-9_]*$') {
            $sourceName
        }
        else {
            'field_{0:d2}' -f ($index + 1)
        }
        $code = $codeCandidate
        # 第2阶段：构造 Convert-JnpfFieldDefinitions 当前步骤需要的中间数据和执行参数。
        $sequence = 2
        while (-not $usedCodes.Add($code)) {
            $code = "${codeCandidate}_$sequence"
            $sequence++
        }

        $displayName = [string](Get-MapValue $sourceField @('fieldName', 'fullName', 'label', 'title') $sourceName)
        $dataType = ConvertTo-TargetDataType ([string](Get-MapValue $sourceField @('dataType', 'type', 'fieldType')))
        $result.Add([pscustomobject]@{
            Source = $sourceName
            Code = $code
            DisplayName = if ([string]::IsNullOrWhiteSpace($displayName)) { $sourceName } else { $displayName }
            DataType = $dataType
        })
    }

    return $result.ToArray()
}

# 将 JnpfHisReportMigration 中的 Convert Jnpf Sql Parameter Markers 输入转换为调用方需要的稳定格式。
function Convert-JnpfSqlParameterMarkers {
    param([Parameter(Mandatory)][string]$QueryText)

    $builder = [System.Text.StringBuilder]::new()
    $names = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $markers = [System.Collections.Generic.List[string]]::new()
    $unsupported = [System.Collections.Generic.List[string]]::new()
    $length = $QueryText.Length
    # 第一阶段：构造 Convert-JnpfSqlParameterMarkers 当前步骤需要的中间数据和执行参数。
    $index = 0

    # 构建 JnpfHisReportMigration 所需的 Add Parameter 内容，并保证生成结果符合调用约定。
    function Add-Parameter {
        param([string]$Name, [string]$Syntax)
        [void]$builder.Append('@').Append($Name)
        if ($seen.Add($Name)) {
            $names.Add($Name)
        }
        $markers.Add("JNPF_PARAMETER_MARKER:$Syntax->$Name")
    }

    while ($index -lt $length) {
        $current = $QueryText[$index]

        # Keep literals, quoted identifiers and comments byte-for-byte. A marker in a
        # string is data, not a report parameter.
        if ($current -eq "'") {
            [void]$builder.Append($current)
            $index++
            while ($index -lt $length) {
                [void]$builder.Append($QueryText[$index])
                if ($QueryText[$index] -eq "'") {
                    if ($index + 1 -lt $length -and $QueryText[$index + 1] -eq "'") {
                        [void]$builder.Append($QueryText[$index + 1])
                        $index += 2
                        continue
                    }
                    $index++
                    break
                }
                $index++
            }
            continue
        }
        if ($current -eq '"' -or $current -eq '[') {
            $closing = if ($current -eq '[') { ']' } else { '"' }
            # 第2阶段：根据 Convert-JnpfSqlParameterMarkers 当前分支转换输入数据，并保存该阶段产生的中间结果。
            [void]$builder.Append($current)
            $index++
            while ($index -lt $length) {
                [void]$builder.Append($QueryText[$index])
                if ($QueryText[$index] -eq $closing) {
                    if ($index + 1 -lt $length -and $QueryText[$index + 1] -eq $closing) {
                        [void]$builder.Append($QueryText[$index + 1])
                        $index += 2
                        continue
                    }
                    $index++
                    break
                }
                $index++
            }
            continue
        }
        if ($current -eq '-' -and $index + 1 -lt $length -and $QueryText[$index + 1] -eq '-') {
            while ($index -lt $length) {
                [void]$builder.Append($QueryText[$index])
                $index++
                if ($QueryText[$index - 1] -eq "`n") { break }
            }
            continue
        }
        if ($current -eq '/' -and $index + 1 -lt $length -and $QueryText[$index + 1] -eq '*') {
            [void]$builder.Append('/*')
            $index += 2
            while ($index -lt $length) {
                [void]$builder.Append($QueryText[$index])
                if ($QueryText[$index] -eq '*' -and $index + 1 -lt $length -and $QueryText[$index + 1] -eq '/') {
                    [void]$builder.Append('/')
                    $index += 2
                    break
                }
                # 第3阶段：构造 Convert-JnpfSqlParameterMarkers 当前步骤需要的中间数据和执行参数。
                $index++
            }
            continue
        }

        $remaining = $QueryText.Substring($index)
        $match = $null
        $syntax = $null
        if ($remaining -match '^\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}') {
            $match = $Matches
            $syntax = 'double-brace'
        }
        elseif ($remaining -match '^#\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}#?') {
            $match = $Matches
            $syntax = 'hash-brace'
        }
        elseif ($remaining -match '^\$\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}') {
            $match = $Matches
            $syntax = 'dollar-brace'
        }
        elseif ($remaining -match '^\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}') {
            $match = $Matches
            $syntax = 'brace'
        }
        elseif ($remaining -match '^#([A-Za-z][A-Za-z0-9_]*)#') {
            $match = $Matches
            $syntax = 'hash'
        }
        elseif ($remaining -match '^\$([A-Za-z][A-Za-z0-9_]*)\$') {
            $match = $Matches
            $syntax = 'dollar'
        }
        elseif ($remaining -match '^:([A-Za-z][A-Za-z0-9_]*)' -and
            ($index -eq 0 -or $QueryText[$index - 1] -notmatch '[A-Za-z0-9_:$]')) {
            $match = $Matches
            # 第4阶段：构造 Convert-JnpfSqlParameterMarkers 当前步骤需要的中间数据和执行参数。
            $syntax = 'colon'
        }
        elseif ($remaining -match '^@([A-Za-z][A-Za-z0-9_]*)' -and
            ($index -eq 0 -or $QueryText[$index - 1] -notmatch '[A-Za-z0-9_]')) {
            $match = $Matches
            $syntax = 'at'
        }

        if ($null -ne $match) {
            Add-Parameter $match[1] $syntax
            $index += $match[0].Length
            continue
        }

        if ($current -eq '{' -or $current -eq '}') {
            $unsupported.Add("UNSUPPORTED_SQL_PARAMETER_MARKER:$current@$index")
        }

        [void]$builder.Append($current)
        $index++
    }

    return [pscustomobject]@{
        QueryText = $builder.ToString()
        ParameterNames = $names.ToArray()
        Markers = $markers.ToArray()
        UnsupportedMarkers = $unsupported.ToArray()
    }
}

# 查询或读取 JnpfHisReportMigration 所需的 Get Jnpf Sql Parameter Names 数据，并按调用约定返回结果。
function Get-JnpfSqlParameterNames {
    param([AllowNull()][string]$QueryText)

    if ([string]::IsNullOrWhiteSpace($QueryText)) {
        return @()
    }
    return @((Convert-JnpfSqlParameterMarkers $QueryText).ParameterNames)
}

# 将 JnpfHisReportMigration 中的 Quote Sql Identifier 输入转换为调用方需要的稳定格式。
function Quote-SqlIdentifier {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$DbType
    )

    switch ($DbType.ToLowerInvariant()) {
        'sqlserver' { return '[' + $Value.Replace(']', ']]') + ']' }
        'mysql' { return [char]96 + $Value.Replace([string][char]96, ([string][char]96 + [char]96)) + [char]96 }
        default { return '"' + $Value.Replace('"', '""') + '"' }
    }
}

# 将 JnpfHisReportMigration 中的 Convert Jnpf Sql Text 输入转换为调用方需要的稳定格式。
function Convert-JnpfSqlText {
    param(
        [Parameter(Mandatory)][string]$QueryText,
        [Parameter(Mandatory)][object[]]$Fields,
        [Parameter(Mandatory)][string]$DbType
    )

    # 第一阶段：检查 Convert-JnpfSqlText 当前条件，并按匹配分支执行对应处理。
    if ([string]::IsNullOrWhiteSpace($QueryText)) {
        throw 'JNPF 数据接口没有 SQL。'
    }

    $markers = [System.Collections.Generic.List[string]]::new()
    $converted = $QueryText.Trim().TrimEnd(';').TrimEnd()
    $parameterConversion = Convert-JnpfSqlParameterMarkers $converted
    $converted = $parameterConversion.QueryText
    foreach ($marker in $parameterConversion.Markers) { $markers.Add($marker) }

    if ($parameterConversion.UnsupportedMarkers.Count -gt 0) {
        throw "SQL 中存在未识别的 JNPF 参数标记，需要人工转换：$($parameterConversion.UnsupportedMarkers -join ', ')"
    }
    if ($converted -notmatch '(?is)^\s*(SELECT|WITH)\b') {
        throw '数据接口不是 SELECT/WITH 只读查询。'
    }
    if ($converted -match '(?i)\bV_YB_FEE_DETAIL\b') {
        $markers.Add('DEV_SQL_OBJECT_REVIEW:V_YB_FEE_DETAIL')
    }

    $requiresAlias = @($Fields | Where-Object { $_.Source -cne $_.Code }).Count -gt 0
    # 第2阶段：检查 Convert-JnpfSqlText 当前条件，并按匹配分支执行对应处理。
    if ($requiresAlias) {
        $selectList = @($Fields | ForEach-Object {
            $sourceIdentifier = Quote-SqlIdentifier $_.Source $DbType
            "    jnpf_source.$sourceIdentifier AS $($_.Code)"
        }) -join ",`r`n"
        $converted = "SELECT`r`n$selectList`r`nFROM (`r`n$converted`r`n) jnpf_source"
        $markers.Add('SQL_OUTER_FIELD_ALIAS_APPLIED')
        if ($QueryText -match '(?is)\bORDER\s+BY\b') {
            $markers.Add('SQL_WRAPPED_ORDER_BY_REVIEW')
        }
    }

    return [pscustomobject]@{
        QueryText = $converted
        ParameterNames = @(Get-JnpfSqlParameterNames $converted)
        Markers = $markers.ToArray()
    }
}

# 查询或读取 JnpfHisReportMigration 所需的 Get All Maps 数据，并按调用约定返回结果。
function Get-AllMaps {
    param([AllowNull()][object]$Value)

    $result = [System.Collections.Generic.List[object]]::new()
    # 执行 JnpfHisReportMigration 的 Visit Value 核心流程，并保持错误和结果语义稳定。
    function Visit-Value {
        param([AllowNull()][object]$Current)
        if ($null -eq $Current -or $Current -is [string]) {
            return
        }
        if ($Current -is [System.Collections.IDictionary]) {
            $result.Add($Current)
            foreach ($child in $Current.Values) {
                Visit-Value $child
            }
            return
        }
        if ($Current -is [System.Collections.IEnumerable]) {
            foreach ($child in $Current) {
                Visit-Value $child
            }
        }
    }
    Visit-Value $Value
    return $result.ToArray()
}

# 查询或读取 JnpfHisReportMigration 所需的 Find Parameter Metadata 数据，并按调用约定返回结果。
function Find-ParameterMetadata {
    param(
        [Parameter(Mandatory)][string]$Code,
        [object[]]$Documents
    )

    foreach ($document in $Documents) {
        foreach ($map in @(Get-AllMaps $document)) {
            $candidate = [string](Get-MapValue $map @('field', 'code', 'variable', 'parameter', 'vModel', 'id', 'name'))
            if ($candidate.Contains('.')) {
                $candidate = $candidate.Split('.')[-1]
            }
            if ($candidate -ieq $Code) {
                return $map
            }
        }
    }
    return $null
}

# 将 JnpfHisReportMigration 中的 Convert To Bool Value 输入转换为调用方需要的稳定格式。
function ConvertTo-BoolValue {
    param([AllowNull()][object]$Value)
    if ($Value -is [bool]) {
        return $Value
    }
    if ($null -eq $Value) {
        return $false
    }
    return ([string]$Value) -match '^(1|true|yes)$'
}

# 将 JnpfHisReportMigration 中的 Convert To Control Type 输入转换为调用方需要的稳定格式。
function ConvertTo-ControlType {
    param(
        [AllowNull()][string]$Value,
        [Parameter(Mandatory)][string]$Code
    )

    $normalized = if ($null -eq $Value) { '' } else { $Value.ToLowerInvariant() }
    if ($normalized -match 'multi') { return 'multiSelect' }
    if ($normalized -match 'select|radio') { return 'select' }
    if ($normalized -match 'date|time' -or $Code -match '(?i)(date|sdate|edate|bdate|time)$') { return 'date' }
    if ($normalized -match 'number|inputnumber') { return 'number' }
    if ($normalized -match 'check|switch|bool') { return 'checkbox' }
    return 'text'
}

# 将 JnpfHisReportMigration 中的 Convert Jnpf Parameters 输入转换为调用方需要的稳定格式。
function Convert-JnpfParameters {
    param(
        [Parameter(Mandatory)][string[]]$ParameterNames,
        [AllowNull()][string]$QueryListJson,
        [AllowNull()][string]$DatasetParameterJson,
        [AllowNull()][string]$InterfaceParameterJson
    )

    # 第一阶段：构造 Convert-JnpfParameters 当前步骤需要的中间数据和执行参数。
    $documents = @(
        (ConvertFrom-OptionalJson $QueryListJson @{}),
        (ConvertFrom-OptionalJson $DatasetParameterJson @()),
        (ConvertFrom-OptionalJson $InterfaceParameterJson @())
    )
    $parameters = [System.Collections.Generic.List[object]]::new()
    $markers = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $ParameterNames.Count; $index++) {
        $code = $ParameterNames[$index]
        $metadata = Find-ParameterMetadata $code $documents
        $config = Get-MapValue $metadata @('config') @{}
        $displayName = [string](Get-MapValue $metadata @('label', 'fullName', 'fieldName', 'title', 'name') $code)
        $rawType = [string](Get-MapValue $metadata @('controlType', 'jnpfKey', 'type', 'dataType'))
        if ([string]::IsNullOrWhiteSpace($rawType)) {
            $rawType = [string](Get-MapValue $config @('controlType', 'jnpfKey', 'type', 'dataType'))
        }
        $required = ConvertTo-BoolValue (Get-MapValue $metadata @('required', 'isRequired') (
            Get-MapValue $config @('required', 'isRequired') $false))
        $legacySourceType = [string](Get-MapValue $metadata @('sourceType'))
        $relationField = [string](Get-MapValue $metadata @('relationField'))
        $isFixedParameter = $legacySourceType -eq '2' -and -not [string]::IsNullOrWhiteSpace($relationField)
        if ($null -eq $metadata) {
            $markers.Add("PARAMETER_METADATA_DEFAULTED:$code")
        }
        if ($isFixedParameter) {
            $markers.Add("PARAMETER_FIXED_VALUE_MIGRATED:$code")
        }
        $controlType = ConvertTo-ControlType $rawType $code
        $dataType = if ($controlType -eq 'number') { 'number' } else { 'string' }
        # 第2阶段：构造 Convert-JnpfParameters 当前步骤需要的中间数据和执行参数。
        $parameters.Add([pscustomobject]@{
            Code = $code
            DisplayName = if ([string]::IsNullOrWhiteSpace($displayName)) { $code } else { $displayName }
            DataType = $dataType
            ControlType = $controlType
            SourceType = if ($isFixedParameter) { 'Fixed' } else { 'Form' }
            SourceCode = $null
            SourceFieldCode = $null
            UrlKey = if ($isFixedParameter) { $null } else { $code }
            DefaultExpression = $null
            FixedValue = if ($isFixedParameter) { $relationField } else { $null }
            OptionsJson = $null
            IsVisible = -not $isFixedParameter
            IsReadOnly = $isFixedParameter
            IsRequired = $required
            AllowUrlPrefill = -not $isFixedParameter
            DisplayOrder = $index
        })
    }

    return [pscustomobject]@{
        Parameters = $parameters.ToArray()
        Markers = $markers.ToArray()
    }
}

# 将 JnpfHisReportMigration 中的 Convert Jnpf Field Presentations 输入转换为调用方需要的稳定格式。
function Convert-JnpfFieldPresentations {
    param(
        [AllowNull()][string]$ConvertConfigJson,
        [Parameter(Mandatory)][object[]]$Fields
    )

    if ([string]::IsNullOrWhiteSpace($ConvertConfigJson)) {
        return '[]'
    }
    $fieldBySource = @{}
    foreach ($field in $Fields) {
        $fieldBySource[$field.Source] = $field
    }
    $presentations = @()
    foreach ($conversion in @(ConvertFrom-OptionalJson $ConvertConfigJson @())) {
        if ([string](Get-MapValue $conversion @('type')) -ne 'select') {
            continue
        }
        $fieldReference = [string](Get-MapValue $conversion @('field'))
        $sourceName = $fieldReference.Split('.')[-1]
        if (-not $fieldBySource.ContainsKey($sourceName)) {
            continue
        }
        $config = Get-MapValue $conversion @('config') @{}
        $options = @((Get-MapValue $config @('options') @()) | ForEach-Object {
            [ordered]@{
                value = [string](Get-MapValue $_ @('id', 'value', 'code'))
                label = [string](Get-MapValue $_ @('fullName', 'label', 'name'))
            }
        } | Where-Object { -not [string]::IsNullOrWhiteSpace($_.value) })
        if ($options.Count -gt 0) {
            $presentations += [ordered]@{
                code = $fieldBySource[$sourceName].Code
                valueMappings = $options
            }
        }
    }
    return $presentations | ConvertTo-Json -Depth 20 -Compress
}

# 将 JnpfHisReportMigration 中的 Convert Jnpf Snapshot 输入转换为调用方需要的稳定格式。
function Convert-JnpfSnapshot {
    param(
        [Parameter(Mandatory)][string]$SnapshotJson,
        [Parameter(Mandatory)][string]$DatasetName,
        [Parameter(Mandatory)][object[]]$Fields
    )

    $snapshot = ConvertFrom-OptionalJson $SnapshotJson @{}
    # 第一阶段：检查 Convert-JnpfSnapshot 当前条件，并按匹配分支执行对应处理。
    if (-not $snapshot.Contains('sheets') -or -not $snapshot.Contains('sheetOrder')) {
        throw 'JNPF 模板快照缺少 sheets 或 sheetOrder。'
    }

    $markers = [System.Collections.Generic.List[string]]::new()
    if ($snapshot.Contains('resources')) {
        $snapshot.Remove('resources')
        $markers.Add('LEGACY_WORKBOOK_RESOURCES_REMOVED')
    }
    $fieldBySource = @{}
    foreach ($field in $Fields) {
        $fieldBySource[$field.Source] = $field
    }

    $coordinates = [System.Collections.Generic.List[object]]::new()
    $tokenPattern = '^\$\{\s*' + [regex]::Escape($DatasetName) + '\.([^}]+)\s*\}$'
    $formulaRangeFixCount = 0
    $unconvertedTokenCount = 0
    foreach ($sheetIdValue in @($snapshot.sheetOrder)) {
        $sheetId = [string]$sheetIdValue
        if (-not $snapshot.sheets.Contains($sheetId)) {
            throw "模板 sheetOrder 引用了不存在的工作表 '$sheetId'。"
        }
        $sheet = $snapshot.sheets[$sheetId]
        foreach ($legacyKey in @('floatEcharts', 'cellEcharts', 'floatImages')) {
            if ($sheet.Contains($legacyKey)) {
                if (@($sheet[$legacyKey]).Count -gt 0) {
                    $markers.Add("UNSUPPORTED_TEMPLATE_ASSET_REMOVED:$legacyKey")
                }
                $sheet.Remove($legacyKey)
            }
        }
        if (-not $sheet.Contains('cellData')) {
            # 第2阶段：根据 Convert-JnpfSnapshot 当前分支转换输入数据，并保存该阶段产生的中间结果。
            continue
        }

        foreach ($rowKey in @($sheet.cellData.Keys)) {
            foreach ($columnKey in @($sheet.cellData[$rowKey].Keys)) {
                $cell = $sheet.cellData[$rowKey][$columnKey]
                if ($cell.Contains('custom')) {
                    $cell.Remove('custom')
                }
                if ($cell.Contains('v') -and $cell.v -is [string]) {
                    $match = [regex]::Match($cell.v, $tokenPattern)
                    if ($match.Success) {
                        $sourceName = $match.Groups[1].Value.Trim()
                        if (-not $fieldBySource.ContainsKey($sourceName)) {
                            throw "模板使用了数据接口未声明的字段 '$sourceName'。"
                        }
                        $field = $fieldBySource[$sourceName]
                        $cell.v = "{{detail.$($field.Code)}}"
                        $coordinates.Add([pscustomobject]@{
                            SheetId = $sheetId
                            Row = [int]$rowKey
                            Column = [int]$columnKey
                            Code = $field.Code
                        })
                    }
                    elseif ($cell.v -match '\$\{[^}]+\}') {
                        $unconvertedTokenCount++
                    }
                }
                if ($cell.Contains('f') -and $cell.f -is [string]) {
                    $sumMatch = [regex]::Match($cell.f, '^=SUM\(([A-Za-z]+[0-9]+)\)$')
                    if ($sumMatch.Success) {
                        # 第3阶段：构造 Convert-JnpfSnapshot 当前步骤需要的中间数据和执行参数。
                        $reference = $sumMatch.Groups[1].Value
                        $cell.f = "=SUM($reference`:$reference)"
                        $formulaRangeFixCount++
                    }
                }
            }
        }
    }

    if ($coordinates.Count -eq 0) {
        throw '模板中没有找到当前数据集的明细占位符。'
    }
    $locations = @($coordinates | Group-Object SheetId, Row)
    if ($locations.Count -ne 1) {
        throw '当前报表中心只支持一个工作表中的一条明细模板行。'
    }
    if ($unconvertedTokenCount -gt 0) {
        $markers.Add("UNCONVERTED_TEMPLATE_TOKEN:$unconvertedTokenCount")
    }
    if (@($snapshot.sheetOrder).Count -gt 1) {
        $markers.Add("MULTI_SHEET_REVIEW:$(@($snapshot.sheetOrder).Count)")
    }

    $detail = @($coordinates | Sort-Object Column, Code)
    $detailSheet = $snapshot.sheets[[string]$detail[0].SheetId]
    foreach ($merge in @(Get-MapValue $detailSheet @('mergeData') @())) {
        $startRow = Get-MapValue $merge @('startRow') -1
        $endRow = Get-MapValue $merge @('endRow') -1
        if ([int]$startRow -le $detail[0].Row -and $detail[0].Row -le [int]$endRow) {
            throw '明细模板行包含合并单元格，当前报表中心无法安全展开。'
        }
    }

    # 第4阶段：构造 Convert-JnpfSnapshot 当前步骤需要的中间数据和执行参数。
    $binding = [ordered]@{
        version = 1
        parameterBindings = @()
        detailBinding = [ordered]@{
            datasetCode = 'detail'
            sheetId = [string]$detail[0].SheetId
            templateRow = [int]$detail[0].Row
            fields = @($detail | ForEach-Object {
                [ordered]@{ code = $_.Code; column = $_.Column }
            })
        }
    }
    $sheetName = [string](Get-MapValue $detailSheet @('name') '')

    return [pscustomobject]@{
        SnapshotJson = $snapshot | ConvertTo-Json -Depth 100 -Compress
        BindingJson = $binding | ConvertTo-Json -Depth 20 -Compress
        SheetName = $sheetName
        SheetId = [string]$detail[0].SheetId
        TemplateRow = [int]$detail[0].Row
        DetailFieldCount = $detail.Count
        FormulaRangeFixCount = $formulaRangeFixCount
        Markers = $markers.ToArray()
    }
}

# 查询或读取 JnpfHisReportMigration 所需的 Find My Sql Connector Assembly 数据，并按调用约定返回结果。
function Find-MySqlConnectorAssembly {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
    $candidates = @(
        (Join-Path $repositoryRoot 'Admin.NET/HD.ReportCenter.Tests/bin/Debug/net8.0/MySqlConnector.dll'),
        (Join-Path $repositoryRoot 'Admin.NET/Admin.NET.Web.Entry/bin/Debug/net8.0/MySqlConnector.dll'),
        (Join-Path $repositoryRoot 'Admin.NET/Admin.NET.Web.Entry/bin/Release/net8.0/MySqlConnector.dll'),
        (Join-Path $repositoryRoot '.codex-run/outpatient-stats-validation/MySqlConnector.dll'),
        (Join-Path $env:USERPROFILE '.nuget/packages/mysqlconnector/2.2.5/lib/net6.0/MySqlConnector.dll')
    )
    $assembly = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($assembly)) {
        throw '找不到 MySqlConnector.dll，请先执行 dotnet restore/build。'
    }
    return $assembly
}

# 执行 JnpfHisReportMigration 的 Invoke Reader Rows 核心流程，并保持错误和结果语义稳定。
function Invoke-ReaderRows {
    param(
        [Parameter(Mandatory)][System.Data.Common.DbConnection]$Connection,
        [Parameter(Mandatory)][string]$CommandText,
        [hashtable]$Parameters = @{}
    )

    # 第一阶段：构造 Invoke-ReaderRows 当前步骤需要的中间数据和执行参数。
    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $CommandText
        $command.CommandTimeout = 60
        foreach ($entry in $Parameters.GetEnumerator()) {
            $parameter = $command.CreateParameter()
            $parameter.ParameterName = "@$($entry.Key)"
            $parameter.Value = if ($null -eq $entry.Value) { [DBNull]::Value } else { $entry.Value }
            $command.Parameters.Add($parameter) | Out-Null
        }
        $reader = $command.ExecuteReader()
        try {
            $rows = [System.Collections.Generic.List[object]]::new()
            while ($reader.Read()) {
                $values = [ordered]@{}
                for ($index = 0; $index -lt $reader.FieldCount; $index++) {
                    $values[$reader.GetName($index)] = if ($reader.IsDBNull($index)) {
                        $null
                    }
                    else {
                        # 第2阶段：构造 Invoke-ReaderRows 当前步骤需要的中间数据和执行参数。
                        $reader.GetValue($index)
                    }
                }
                $rows.Add([pscustomobject]$values)
            }
            return $rows.ToArray()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $command.Dispose()
    }
}

# 构建 JnpfHisReportMigration 所需的 New Jnpf Source Connection 内容，并保证生成结果符合调用约定。
function New-JnpfSourceConnection {
    param(
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$Password
    )

    if (-not ('MySqlConnector.MySqlConnection' -as [type])) {
        Add-Type -Path (Find-MySqlConnectorAssembly)
    }
    $builder = [MySqlConnector.MySqlConnectionStringBuilder]::new()
    $builder.Server = $Server
    $builder.Port = [uint32]$Port
    $builder.Database = $Database
    $builder.UserID = $User
    $builder.Password = $Password
    $builder.ConnectionTimeout = 10
    $builder.DefaultCommandTimeout = 60
    $builder.SslMode = [MySqlConnector.MySqlSslMode]::Preferred
    $connection = [MySqlConnector.MySqlConnection]::new($builder.ConnectionString)
    $connection.Open()
    return $connection
}

# 查询或读取 JnpfHisReportMigration 所需的 Get Single Source Row 数据，并按调用约定返回结果。
function Get-SingleSourceRow {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Rows,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Rows.Count -gt 1) {
        throw "$Description 返回了 $($Rows.Count) 条，无法确定唯一记录。"
    }
    if ($Rows.Count -eq 1) {
        return $Rows[0]
    }
    return $null
}

# 查询或读取 JnpfHisReportMigration 所需的 Get Jnpf Source Packages 数据，并按调用约定返回结果。
function Get-JnpfSourcePackages {
    param([Parameter(Mandatory)][System.Data.Common.DbConnection]$Connection)

    # 第一阶段：构造 Get-JnpfSourcePackages 当前步骤需要的中间数据和执行参数。
    $reportRows = @(Invoke-ReaderRows $Connection @'
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
WHERE COALESCE(t.f_delete_mark, 0) = 0
  AND COALESCE(v.f_delete_mark, 0) = 0
  AND v.f_state = 1
ORDER BY t.f_full_name, v.f_version DESC;
'@)

    $versionCounts = @{}
    foreach ($group in @($reportRows | Group-Object report_id)) {
        $versionCounts[[string]$group.Name] = $group.Count
    }
    $interfaceCache = @{}
    $databaseLinkCache = @{}
    $packages = [System.Collections.Generic.List[object]]::new()
    foreach ($report in $reportRows) {
        $datasetRows = @(Invoke-ReaderRows $Connection @'
SELECT
    f_id AS dataset_id,
    f_full_name AS dataset_name,
    f_interface_id AS interface_id,
    f_parameter_json AS parameter_json
FROM base_data_set
WHERE f_object_type = 'reportVersion'
  AND f_object_id = @versionId
  AND COALESCE(f_delete_mark, 0) = 0
ORDER BY f_id;
'@ @{ versionId = [string]$report.version_id })

        $datasets = [System.Collections.Generic.List[object]]::new()
        foreach ($dataset in $datasetRows) {
            $interfaceId = [string]$dataset.interface_id
            # 第2阶段：检查 Get-JnpfSourcePackages 当前条件，并按匹配分支执行对应处理。
            if (-not $interfaceCache.ContainsKey($interfaceId)) {
                $interfaceCache[$interfaceId] = Get-SingleSourceRow @(
                    Invoke-ReaderRows $Connection @'
SELECT
    f_id,
    f_full_name,
    f_enabled_mark,
    f_data_config_json,
    f_parameter_json,
    f_field_json
FROM base_data_interface
WHERE f_id = @interfaceId
  AND COALESCE(f_delete_mark, 0) = 0
LIMIT 2;
'@ @{ interfaceId = $interfaceId }) "JNPF 数据接口 '$interfaceId'"
            }
            $interface = $interfaceCache[$interfaceId]
            $dataConfig = if ($null -eq $interface) {
                @{}
            }
            else {
                ConvertFrom-OptionalJson ([string]$interface.f_data_config_json) @{}
            }
            $sqlData = Get-MapValue $dataConfig @('sqlData') @{}
            $databaseLinkId = [string](Get-MapValue $sqlData @('dbLinkId'))
            $databaseLink = $null
            if (-not [string]::IsNullOrWhiteSpace($databaseLinkId)) {
                if (-not $databaseLinkCache.ContainsKey($databaseLinkId)) {
                    $databaseLinkCache[$databaseLinkId] = Get-SingleSourceRow @(
                        Invoke-ReaderRows $Connection @'
SELECT *
FROM base_db_link
WHERE f_id = @databaseLinkId
  AND COALESCE(f_delete_mark, 0) = 0
LIMIT 2;
'@ @{ databaseLinkId = $databaseLinkId }) "JNPF 数据库连接 '$databaseLinkId'"
                }
                # 第3阶段：构造 Get-JnpfSourcePackages 当前步骤需要的中间数据和执行参数。
                $databaseLink = $databaseLinkCache[$databaseLinkId]
            }
            $datasets.Add([pscustomobject]@{
                Id = [string]$dataset.dataset_id
                Name = [string]$dataset.dataset_name
                ParameterJson = [string]$dataset.parameter_json
                Interface = $interface
                DatabaseLink = $databaseLink
                QueryText = [string](Get-MapValue $sqlData @('sql'))
            })
        }

        $packages.Add([pscustomobject]@{
            ReportId = [string]$report.report_id
            ReportName = [string]$report.report_name
            SourceReportCode = [string]$report.source_report_code
            VersionId = [string]$report.version_id
            VersionNumber = [int]$report.version_number
            ActiveVersionCount = [int]$versionCounts[[string]$report.report_id]
            SnapshotJson = [string]$report.snapshot_json
            QueryListJson = [string]$report.query_list_json
            ConvertConfigJson = [string]$report.convert_config_json
            Datasets = $datasets.ToArray()
        })
    }
    return $packages.ToArray()
}

# 构建 JnpfHisReportMigration 所需的 New Jnpf Migration Candidate 内容，并保证生成结果符合调用约定。
function New-JnpfMigrationCandidate {
    param(
        [Parameter(Mandatory)][object]$Package,
        [Parameter(Mandatory)][string]$ReportDataSourceCode,
        [Parameter(Mandatory)][string]$ReportDataSourceType
    )

    $blockers = [System.Collections.Generic.List[string]]::new()
    # 第一阶段：构造 New-JnpfMigrationCandidate 当前步骤需要的中间数据和执行参数。
    $markers = [System.Collections.Generic.List[string]]::new()
    $migration = $null
    if ($Package.ActiveVersionCount -ne 1) {
        $blockers.Add("ACTIVE_VERSION_COUNT:$($Package.ActiveVersionCount)")
    }
    if (@($Package.Datasets).Count -ne 1) {
        $blockers.Add("DATASET_COUNT:$(@($Package.Datasets).Count)")
    }

    $dataset = if (@($Package.Datasets).Count -eq 1) { @($Package.Datasets)[0] } else { $null }
    $databaseLink = if ($null -eq $dataset) { $null } else { $dataset.DatabaseLink }
    if ($null -ne $dataset -and $null -eq $dataset.Interface) {
        $blockers.Add('DATA_INTERFACE_NOT_FOUND')
    }
    if ($null -ne $dataset -and $null -eq $databaseLink) {
        $blockers.Add('DATABASE_LINK_NOT_FOUND')
    }
    $dbType = if ($null -eq $databaseLink) { '' } else { [string](Get-MapValue $databaseLink @('f_db_type')) }
    if ($null -ne $databaseLink -and $dbType -notin @('SqlServer', 'Oracle', 'MySql', 'PostgreSQL')) {
        $blockers.Add("UNSUPPORTED_SOURCE_DB_TYPE:$dbType")
    }

    if ($null -ne $dataset -and $null -ne $dataset.Interface -and $null -ne $databaseLink -and $blockers.Count -eq 0) {
        try {
            $fields = @(Convert-JnpfFieldDefinitions ([string]$dataset.Interface.f_field_json))
            $sql = Convert-JnpfSqlText $dataset.QueryText $fields $ReportDataSourceType
            foreach ($marker in $sql.Markers) { $markers.Add($marker) }
            if ($dbType -ine $ReportDataSourceType) {
                $markers.Add("SQL_DIALECT_REVIEW:$dbType->$ReportDataSourceType")
            }
            $parameters = Convert-JnpfParameters @($sql.ParameterNames) $Package.QueryListJson `
                $dataset.ParameterJson ([string]$dataset.Interface.f_parameter_json)
            foreach ($marker in $parameters.Markers) { $markers.Add($marker) }
            # 第2阶段：构造 New-JnpfMigrationCandidate 当前步骤需要的中间数据和执行参数。
            $template = Convert-JnpfSnapshot $Package.SnapshotJson $dataset.Name $fields
            foreach ($marker in $template.Markers) { $markers.Add($marker) }
            $markers.Add('SOURCE_SQL_NOT_EXECUTED')

            $sourceCode = if ([string]::IsNullOrWhiteSpace($Package.SourceReportCode)) {
                "report_$($Package.ReportId)"
            }
            else {
                $Package.SourceReportCode
            }
            $reportCode = ConvertTo-TargetCode "jnpf_$sourceCode" "jnpf_report_$($Package.ReportId)"
            $databaseLinkId = [string](Get-MapValue $databaseLink @('f_id'))
            $fieldSchema = [ordered]@{
                variables = @($parameters.Parameters | ForEach-Object {
                    [ordered]@{
                        code = $_.Code
                        displayName = $_.DisplayName
                        dataType = $_.DataType
                        required = $_.IsRequired
                        sourceType = $_.SourceType
                        sourceCode = $_.SourceCode
                        sourceFieldCode = $_.SourceFieldCode
                    }
                })
                fields = @($fields | ForEach-Object {
                    [ordered]@{ code = $_.Code; displayName = $_.DisplayName; dataType = $_.DataType }
                })
            }
            $reportName = if ($Package.ReportName -match 'JNPF迁移') {
                $Package.ReportName
            }
            else {
                "$($Package.ReportName)（JNPF迁移）"
            }
            # 第3阶段：构造 New-JnpfMigrationCandidate 当前步骤需要的中间数据和执行参数。
            $migration = [pscustomobject]@{
                SourceReportId = $Package.ReportId
                SourceVersionId = $Package.VersionId
                SourceDatabaseLinkId = $databaseLinkId
                ReportCode = $reportCode
                ReportName = $reportName
                DatasetCode = $reportCode
                DatasetName = $dataset.Name
                DataSourceCode = $ReportDataSourceCode
                UseExistingDataSource = $true
                DataSourceName = $ReportDataSourceCode
                DataSourceDbType = $ReportDataSourceType
                DataSourceConnectionString = $null
                QueryText = $sql.QueryText
                FieldSchemaJson = $fieldSchema | ConvertTo-Json -Depth 30 -Compress
                Parameters = @($parameters.Parameters)
                SheetName = if ([string]::IsNullOrWhiteSpace($template.SheetName)) { $Package.ReportName } else { $template.SheetName }
                SnapshotJson = $template.SnapshotJson
                BindingJson = $template.BindingJson
                TemplateRevision = [Math]::Max(1, $Package.VersionNumber)
                PreviewSettingsJson = ([ordered]@{ mode = 'Paged'; pageSize = 20; showExactTotal = $false } | ConvertTo-Json -Compress)
                ExportSettingsJson = ([ordered]@{
                    defaultMode = 'Normal'
                    normalMaxRows = 20000
                    normalMaxCells = 500000
                    normalTimeoutSeconds = 60
                    largeExportEnabled = $true
                    rowsPerSheet = 1000000
                    fileRetentionHours = 24
                } | ConvertTo-Json -Compress)
                FieldPresentationsJson = Convert-JnpfFieldPresentations $Package.ConvertConfigJson $fields
                FieldCount = $fields.Count
                ParameterCount = @($parameters.Parameters).Count
                DetailFieldCount = $template.DetailFieldCount
                FormulaRangeFixCount = $template.FormulaRangeFixCount
            }
        }
        catch {
            # 第4阶段：构造 New-JnpfMigrationCandidate 当前步骤需要的中间数据和执行参数。
            $blockers.Add("CONVERSION_FAILED:$($_.Exception.Message)")
        }
    }

    $status = if ($blockers.Count -gt 0) {
        'Blocked'
    }
    else {
        'Ready'
    }
    return [pscustomobject]@{
        Package = $Package
        Migratable = $status -eq 'Ready'
        Status = $status
        Blockers = $blockers.ToArray()
        Markers = @($markers | Select-Object -Unique)
        Migration = $migration
    }
}

# 将 JnpfHisReportMigration 中的 Convert To Jnpf Inventory Record 输入转换为调用方需要的稳定格式。
function ConvertTo-JnpfInventoryRecord {
    param([Parameter(Mandatory)][object]$Candidate)

    $package = $Candidate.Package
    $links = @($package.Datasets | ForEach-Object { $_.DatabaseLink } | Where-Object { $null -ne $_ })
    return [pscustomobject]@{
        SourceReportId = $package.ReportId
        SourceVersionId = $package.VersionId
        Version = $package.VersionNumber
        ReportName = $package.ReportName
        SourceReportCode = $package.SourceReportCode
        TargetReportCode = if ($null -eq $Candidate.Migration) { '' } else { $Candidate.Migration.ReportCode }
        DatasetCount = @($package.Datasets).Count
        DataSourceIds = (@($links | ForEach-Object { [string](Get-MapValue $_ @('f_id')) } | Select-Object -Unique) -join ',')
        DataSourceNames = (@($links | ForEach-Object { [string](Get-MapValue $_ @('f_full_name')) } | Select-Object -Unique) -join ',')
        DbTypes = (@($links | ForEach-Object { [string](Get-MapValue $_ @('f_db_type')) } | Select-Object -Unique) -join ',')
        Migratable = $Candidate.Migratable
        Status = $Candidate.Status
        FieldCount = if ($null -eq $Candidate.Migration) { 0 } else { $Candidate.Migration.FieldCount }
        ParameterCount = if ($null -eq $Candidate.Migration) { 0 } else { $Candidate.Migration.ParameterCount }
        DetailFieldCount = if ($null -eq $Candidate.Migration) { 0 } else { $Candidate.Migration.DetailFieldCount }
        Blockers = $Candidate.Blockers -join ' | '
        Markers = $Candidate.Markers -join ' | '
    }
}

# 构建 JnpfHisReportMigration 所需的 New Stable Int64 Id 内容，并保证生成结果符合调用约定。
function New-StableInt64Id {
    param(
        [Parameter(Mandatory)][string]$Namespace,
        [Parameter(Mandatory)][string]$Value
    )

    $bytes = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes("hd-report-center:${Namespace}:$Value"))
    $id = [BitConverter]::ToInt64($bytes, 0) -band [long]::MaxValue
    if ($id -eq 0) { return [long]1 }
    return $id
}

# 构建 JnpfHisReportMigration 所需的 Add Db Parameter 内容，并保证生成结果符合调用约定。
function Add-DbParameter {
    param(
        [Parameter(Mandatory)][System.Data.SqlClient.SqlCommand]$Command,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][System.Data.SqlDbType]$Type,
        [AllowNull()][object]$Value,
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

# 构建 JnpfHisReportMigration 所需的 New Jnpf Target Connection 内容，并保证生成结果符合调用约定。
function New-JnpfTargetConnection {
    param(
        [Parameter(Mandatory)][string]$ConnectionString,
        [string]$RequiredDatabase = 'hd_report_center'
    )

    $legacyConnectionString = ConvertTo-JnpfLegacySqlConnectionString $ConnectionString
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($legacyConnectionString)
    if ($builder.InitialCatalog -cne $RequiredDatabase) {
        throw "目标连接串必须指向 $RequiredDatabase。"
    }
    $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $connection.Open()
    if ($connection.Database -cne $RequiredDatabase) {
        $connection.Dispose()
        throw "实际连接数据库为 '$($connection.Database)'，拒绝写入。"
    }
    return $connection
}

# 将 JnpfHisReportMigration 中的 Convert To Jnpf Legacy Sql Connection String 输入转换为调用方需要的稳定格式。
function ConvertTo-JnpfLegacySqlConnectionString {
    param([Parameter(Mandatory)][string]$ConnectionString)

    # System.Data.SqlClient does not recognize the spaced keyword emitted by
    # the Microsoft.Data.SqlClient connection builder.
    return $ConnectionString -replace '(?i)\bTrust\s+Server\s+Certificate\b', 'TrustServerCertificate'
}

# 检查目标数据库的报表中心表结构是否完整，并在缺少必要对象时阻止迁移写入。
function Test-JnpfTargetSchema {
    param([Parameter(Mandatory)][System.Data.SqlClient.SqlConnection]$Connection)

    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = @'
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
        $command.ExecuteNonQuery() | Out-Null
    }
    finally {
        $command.Dispose()
    }
}

# 执行 JnpfHisReportMigration 的 Invoke Jnpf Target Insert 核心流程，并保持错误和结果语义稳定。
function Invoke-JnpfTargetInsert {
    param(
        [Parameter(Mandatory)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory)][object]$Migration,
        [ValidateSet('Skip', 'Fail')]
        [string]$ExistingReportAction = 'Skip'
    )

    $transaction = $Connection.BeginTransaction()
    try {
        # 第一阶段：构造 Invoke-JnpfTargetInsert 当前步骤需要的中间数据和执行参数。
        $collision = $Connection.CreateCommand()
        $collision.Transaction = $transaction
        $collision.CommandText = @'
SELECT
    (SELECT COUNT_BIG(1) FROM dbo.RcReport WHERE Code = @ReportCode),
    (SELECT COUNT_BIG(1) FROM dbo.RcDataset WHERE Code = @DatasetCode),
    (SELECT COUNT_BIG(1) FROM dbo.RcReportTemplate WHERE ReportCode = @ReportCode);
'@
        Add-DbParameter $collision '@ReportCode' NVarChar $Migration.ReportCode 64
        Add-DbParameter $collision '@DatasetCode' NVarChar $Migration.DatasetCode 64
        $reader = $collision.ExecuteReader()
        try {
            if (-not $reader.Read()) {
                throw '目标编码冲突检查没有返回结果。'
            }
            $reportCount = $reader.GetInt64(0)
            $datasetCount = $reader.GetInt64(1)
            $templateCount = $reader.GetInt64(2)
        }
        finally {
            $reader.Dispose()
            $collision.Dispose()
        }
        if ($reportCount -gt 0) {
            $transaction.Rollback()
            if ($ExistingReportAction -eq 'Fail') {
                throw "目标报表 '$($Migration.ReportCode)' 已存在。"
            }
            return [pscustomobject]@{
                ReportCode = $Migration.ReportCode
                ReportName = $Migration.ReportName
                Status = 'Skipped'
                Message = '目标报表已存在，未覆盖。'
            }
        }
        if ($datasetCount -gt 0 -or $templateCount -gt 0) {
            throw '目标存在孤立的同编码数据集或模板，拒绝自动处理。'
        }

        # 第2阶段：构造 Invoke-JnpfTargetInsert 当前步骤需要的中间数据和执行参数。
        $dataSourceCheck = $Connection.CreateCommand()
        $dataSourceCheck.Transaction = $transaction
        $dataSourceCheck.CommandText = 'SELECT DbType FROM dbo.RcDataSource WHERE Code = @Code;'
        Add-DbParameter $dataSourceCheck '@Code' NVarChar $Migration.DataSourceCode 64
        $existingDbType = $dataSourceCheck.ExecuteScalar()
        $dataSourceCheck.Dispose()
        if ($null -ne $existingDbType -and $existingDbType -isnot [DBNull]) {
            if ([string]$existingDbType -ine $Migration.DataSourceDbType) {
                throw "目标数据源 '$($Migration.DataSourceCode)' 类型不是 $($Migration.DataSourceDbType)。"
            }
        }
        elseif ($Migration.UseExistingDataSource) {
            throw "指定复用的目标数据源 '$($Migration.DataSourceCode)' 不存在。"
        }
        else {
            $dataSource = $Connection.CreateCommand()
            $dataSource.Transaction = $transaction
            $dataSource.CommandText = @'
INSERT INTO dbo.RcDataSource
    (Id, Code, Name, DbType, ConnectionString, DefaultTimeoutSeconds,
     DefaultMaxRows, Revision, IsEnabled, CreateTime, UpdateTime)
VALUES
    (@Id, @Code, @Name, @DbType, @ConnectionString, 30, 20000, 1, 1, @Now, @Now);
'@
            Add-DbParameter $dataSource '@Id' BigInt (New-StableInt64Id 'data-source' $Migration.SourceDatabaseLinkId)
            Add-DbParameter $dataSource '@Code' NVarChar $Migration.DataSourceCode 64
            Add-DbParameter $dataSource '@Name' NVarChar $Migration.DataSourceName 128
            Add-DbParameter $dataSource '@DbType' NVarChar $Migration.DataSourceDbType 32
            Add-DbParameter $dataSource '@ConnectionString' NVarChar $Migration.DataSourceConnectionString -1
            Add-DbParameter $dataSource '@Now' DateTime ([DateTime]::Now)
            $dataSource.ExecuteNonQuery() | Out-Null
            $dataSource.Dispose()
        }

        $now = [DateTime]::Now
        $dataset = $Connection.CreateCommand()
        $dataset.Transaction = $transaction
        # 第3阶段：构造 Invoke-JnpfTargetInsert 当前步骤需要的中间数据和执行参数。
        $dataset.CommandText = @'
INSERT INTO dbo.RcDataset
    (Id, DataSourceCode, Code, Name, QueryText, FieldSchemaJson,
     TimeoutSeconds, MaxRows, Revision, IsEnabled, CreateTime, UpdateTime)
VALUES
    (@Id, @DataSourceCode, @Code, @Name, @QueryText, @FieldSchemaJson,
     60, 20000, 1, 1, @Now, @Now);
'@
        Add-DbParameter $dataset '@Id' BigInt (New-StableInt64Id 'dataset' $Migration.SourceVersionId)
        Add-DbParameter $dataset '@DataSourceCode' NVarChar $Migration.DataSourceCode 64
        Add-DbParameter $dataset '@Code' NVarChar $Migration.DatasetCode 64
        Add-DbParameter $dataset '@Name' NVarChar $Migration.DatasetName 128
        Add-DbParameter $dataset '@QueryText' NVarChar $Migration.QueryText -1
        Add-DbParameter $dataset '@FieldSchemaJson' NVarChar $Migration.FieldSchemaJson -1
        Add-DbParameter $dataset '@Now' DateTime $now
        $dataset.ExecuteNonQuery() | Out-Null
        $dataset.Dispose()

        $report = $Connection.CreateCommand()
        $report.Transaction = $transaction
        $report.CommandText = @'
INSERT INTO dbo.RcReport
    (Id, Code, Name, SheetName, DatasetCode, PreviewSettingsJson,
     ExportSettingsJson, FieldPresentationsJson, Revision, IsEnabled, CreateTime, UpdateTime)
VALUES
    (@Id, @Code, @Name, @SheetName, @DatasetCode, @PreviewSettingsJson,
     @ExportSettingsJson, @FieldPresentationsJson, 1, 1, @Now, @Now);
'@
        Add-DbParameter $report '@Id' BigInt (New-StableInt64Id 'report' $Migration.SourceReportId)
        Add-DbParameter $report '@Code' NVarChar $Migration.ReportCode 64
        Add-DbParameter $report '@Name' NVarChar $Migration.ReportName 128
        Add-DbParameter $report '@SheetName' NVarChar $Migration.SheetName 128
        Add-DbParameter $report '@DatasetCode' NVarChar $Migration.DatasetCode 64
        Add-DbParameter $report '@PreviewSettingsJson' NVarChar $Migration.PreviewSettingsJson -1
        Add-DbParameter $report '@ExportSettingsJson' NVarChar $Migration.ExportSettingsJson -1
        Add-DbParameter $report '@FieldPresentationsJson' NVarChar $Migration.FieldPresentationsJson -1
        Add-DbParameter $report '@Now' DateTime $now
        # 第4阶段：构造 Invoke-JnpfTargetInsert 当前步骤需要的中间数据和执行参数。
        $report.ExecuteNonQuery() | Out-Null
        $report.Dispose()

        for ($index = 0; $index -lt $Migration.Parameters.Count; $index++) {
            $definition = $Migration.Parameters[$index]
            $parameter = $Connection.CreateCommand()
            $parameter.Transaction = $transaction
            $parameter.CommandText = @'
INSERT INTO dbo.RcReportParameter
    (Id, ReportCode, Code, DisplayName, DataType, ControlType, SourceType,
     SourceCode, SourceFieldCode, UrlKey, DefaultExpression, FixedValue,
     OptionsJson, IsVisible, IsReadOnly, IsRequired, AllowUrlPrefill,
     DisplayOrder, CreateTime, UpdateTime)
VALUES
    (@Id, @ReportCode, @Code, @DisplayName, @DataType, @ControlType, @SourceType,
     @SourceCode, @SourceFieldCode, @UrlKey, @DefaultExpression, @FixedValue,
     @OptionsJson, @IsVisible, @IsReadOnly, @IsRequired, @AllowUrlPrefill,
     @DisplayOrder, @Now, @Now);
'@
            Add-DbParameter $parameter '@Id' BigInt (New-StableInt64Id 'parameter' "$($Migration.SourceReportId):$($definition.Code)")
            Add-DbParameter $parameter '@ReportCode' NVarChar $Migration.ReportCode 64
            Add-DbParameter $parameter '@Code' NVarChar $definition.Code 64
            Add-DbParameter $parameter '@DisplayName' NVarChar $definition.DisplayName 128
            Add-DbParameter $parameter '@DataType' NVarChar $definition.DataType 32
            Add-DbParameter $parameter '@ControlType' NVarChar $definition.ControlType 32
            Add-DbParameter $parameter '@SourceType' NVarChar $definition.SourceType 16
            Add-DbParameter $parameter '@SourceCode' NVarChar $definition.SourceCode 64
            Add-DbParameter $parameter '@SourceFieldCode' NVarChar $definition.SourceFieldCode 64
            Add-DbParameter $parameter '@UrlKey' NVarChar $definition.UrlKey 64
            Add-DbParameter $parameter '@DefaultExpression' NVarChar $definition.DefaultExpression 256
            Add-DbParameter $parameter '@FixedValue' NVarChar $definition.FixedValue 1024
            Add-DbParameter $parameter '@OptionsJson' NVarChar $definition.OptionsJson -1
            Add-DbParameter $parameter '@IsVisible' Bit $definition.IsVisible
            Add-DbParameter $parameter '@IsReadOnly' Bit $definition.IsReadOnly
            Add-DbParameter $parameter '@IsRequired' Bit $definition.IsRequired
            Add-DbParameter $parameter '@AllowUrlPrefill' Bit $definition.AllowUrlPrefill
            Add-DbParameter $parameter '@DisplayOrder' Int $definition.DisplayOrder
            Add-DbParameter $parameter '@Now' DateTime $now
            $parameter.ExecuteNonQuery() | Out-Null
            # 第5阶段：构造 Invoke-JnpfTargetInsert 当前步骤需要的中间数据和执行参数。
            $parameter.Dispose()
        }

        $template = $Connection.CreateCommand()
        $template.Transaction = $transaction
        $template.CommandText = @'
INSERT INTO dbo.RcReportTemplate
    (Id, ReportCode, SnapshotJson, BindingJson, Revision, CreateTime, UpdateTime)
VALUES
    (@Id, @ReportCode, @SnapshotJson, @BindingJson, @Revision, @Now, @Now);
'@
        Add-DbParameter $template '@Id' BigInt (New-StableInt64Id 'template' $Migration.SourceVersionId)
        Add-DbParameter $template '@ReportCode' NVarChar $Migration.ReportCode 64
        Add-DbParameter $template '@SnapshotJson' NVarChar $Migration.SnapshotJson -1
        Add-DbParameter $template '@BindingJson' NVarChar $Migration.BindingJson -1
        Add-DbParameter $template '@Revision' Int $Migration.TemplateRevision
        Add-DbParameter $template '@Now' DateTime $now
        $template.ExecuteNonQuery() | Out-Null
        $template.Dispose()

        $verify = $Connection.CreateCommand()
        $verify.Transaction = $transaction
        $verify.CommandText = @'
SELECT
    (SELECT COUNT_BIG(1) FROM dbo.RcDataset WHERE Code = @DatasetCode),
    (SELECT COUNT_BIG(1) FROM dbo.RcReport WHERE Code = @ReportCode),
    (SELECT COUNT_BIG(1) FROM dbo.RcReportParameter WHERE ReportCode = @ReportCode),
    (SELECT COUNT_BIG(1) FROM dbo.RcReportTemplate WHERE ReportCode = @ReportCode);
'@
        Add-DbParameter $verify '@DatasetCode' NVarChar $Migration.DatasetCode 64
        Add-DbParameter $verify '@ReportCode' NVarChar $Migration.ReportCode 64
        $verifyReader = $verify.ExecuteReader()
        try {
            if (-not $verifyReader.Read()) {
                throw '目标写后校验没有返回结果。'
            }
            $actual = @(
                # 第6阶段：构造 Invoke-JnpfTargetInsert 当前步骤需要的中间数据和执行参数。
                $verifyReader.GetInt64(0),
                $verifyReader.GetInt64(1),
                $verifyReader.GetInt64(2),
                $verifyReader.GetInt64(3))
            $expected = @(1, 1, $Migration.Parameters.Count, 1)
            if (($actual -join ',') -ne ($expected -join ',')) {
                throw "目标写后校验失败，期望 $($expected -join ',')，实际 $($actual -join ',')。"
            }
        }
        finally {
            $verifyReader.Dispose()
            $verify.Dispose()
        }

        $transaction.Commit()
        return [pscustomobject]@{
            ReportCode = $Migration.ReportCode
            ReportName = $Migration.ReportName
            Status = 'Migrated'
            Message = "已写入 $($Migration.FieldCount) 个字段、$($Migration.ParameterCount) 个参数。"
        }
    }
    catch {
        try { $transaction.Rollback() } catch { }
        throw
    }
    finally {
        $transaction.Dispose()
    }
}

Export-ModuleMember -Function @(
    'ConvertTo-TargetCode',
    'Convert-JnpfFieldDefinitions',
    'Get-JnpfSqlParameterNames',
    'Convert-JnpfSqlText',
    'Convert-JnpfParameters',
    'Convert-JnpfFieldPresentations',
    'Convert-JnpfSnapshot',
    'ConvertFrom-OptionalJson',
    'New-JnpfSourceConnection',
    'Get-JnpfSourcePackages',
    'New-JnpfMigrationCandidate',
    'ConvertTo-JnpfInventoryRecord',
    'ConvertTo-JnpfLegacySqlConnectionString',
    'New-JnpfTargetConnection',
    'Test-JnpfTargetSchema',
    'Invoke-JnpfTargetInsert'
)
