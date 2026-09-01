[CmdletBinding(DefaultParameterSetName = 'Changed')]
param(
    [Parameter(ParameterSetName = 'All')]
    [switch]$All,

    [Parameter(ParameterSetName = 'Base', Mandatory = $true)]
    [string]$BaseRef,

    [Parameter(ParameterSetName = 'Explicit', Mandatory = $true)]
    [string[]]$Path,

    [ValidateRange(10, 500)]
    [int]$LongMethodLines = 40,

    [ValidateRange(1, 100)]
    [int]$MinDescriptionLength = 8,

    [ValidateRange(1, 20)]
    [int]$MinStageComments = 2,

    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$supportedExtensions = @('.cs', '.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.vue', '.ps1', '.psm1')
$excludedPathPattern = '(^|/)(\.git|\.worktrees|node_modules|dist|bin|obj|coverage|references)(/|$)'
$excludedFilePattern = '(?i)(\.d\.ts|\.min\.js|\.(g|generated|designer)\.cs)$'
$diagnostics = [System.Collections.Generic.List[object]]::new()
$script:commentCheckExitCode = 0

# 把绝对路径转换为稳定的仓库相对路径，便于本地和持续集成显示同一结果。
function Convert-ToDisplayPath {
    param([Parameter(Mandatory = $true)][string]$AbsolutePath)

    return [System.IO.Path]::GetRelativePath($repoRoot, $AbsolutePath).Replace('\', '/')
}

# 判断文件是否属于检查器支持且未被生成物、依赖或第三方目录排除的源代码。
function Test-IsInspectableSource {
    param([Parameter(Mandatory = $true)][string]$CandidatePath)

    $normalized = $CandidatePath.Replace('\', '/')
    $extension = [System.IO.Path]::GetExtension($normalized).ToLowerInvariant()

    return $supportedExtensions.Contains($extension) -and
        $normalized -notmatch $excludedPathPattern -and
        $normalized -notmatch $excludedFilePattern
}

# 展开显式路径、工作区改动或目标分支差异，返回去重后的现存源文件。
function Resolve-SourceFiles {
    $candidatePaths = [System.Collections.Generic.List[string]]::new()

    # 显式路径既支持单文件，也支持目录递归审计。
    if ($PSCmdlet.ParameterSetName -eq 'Explicit') {
        foreach ($requestedPath in $Path) {
            $absoluteRequestedPath = if ([System.IO.Path]::IsPathRooted($requestedPath)) {
                [System.IO.Path]::GetFullPath($requestedPath)
            }
            else {
                [System.IO.Path]::GetFullPath((Join-Path $repoRoot $requestedPath))
            }

            if (-not (Test-Path -LiteralPath $absoluteRequestedPath)) {
                throw "检查路径不存在：$requestedPath"
            }

            if (Test-Path -LiteralPath $absoluteRequestedPath -PathType Container) {
                Get-ChildItem -LiteralPath $absoluteRequestedPath -Recurse -File | ForEach-Object {
                    $candidatePaths.Add($_.FullName)
                }
            }
            else {
                $candidatePaths.Add($absoluteRequestedPath)
            }
        }
    }
    elseif ($PSCmdlet.ParameterSetName -eq 'All') {
        # 全量模式覆盖 Git 跟踪文件和未忽略的新文件，避免新脚本绕过质量门禁。
        @(
            git -C $repoRoot ls-files
            git -C $repoRoot ls-files --others --exclude-standard
        ) | ForEach-Object {
            $candidatePaths.Add([System.IO.Path]::GetFullPath((Join-Path $repoRoot $_)))
        }
    }
    elseif ($PSCmdlet.ParameterSetName -eq 'Base') {
        # 分支模式用于持续集成，只检查相对目标分支真正新增或修改的文件。
        git -C $repoRoot rev-parse --verify $BaseRef *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "无法解析目标分支或提交：$BaseRef"
        }

        git -C $repoRoot diff --name-only --diff-filter=ACMR "$BaseRef...HEAD" | ForEach-Object {
            $candidatePaths.Add([System.IO.Path]::GetFullPath((Join-Path $repoRoot $_)))
        }
    }
    else {
        # 默认同时覆盖已暂存、未暂存和未跟踪文件，适合作为提交前检查。
        @(
            git -C $repoRoot diff --name-only --diff-filter=ACMR --cached
            git -C $repoRoot diff --name-only --diff-filter=ACMR
            git -C $repoRoot ls-files --others --exclude-standard
        ) | ForEach-Object {
            if (-not [string]::IsNullOrWhiteSpace($_)) {
                $candidatePaths.Add([System.IO.Path]::GetFullPath((Join-Path $repoRoot $_)))
            }
        }
    }

    return $candidatePaths |
        Where-Object { (Test-Path -LiteralPath $_ -PathType Leaf) -and (Test-IsInspectableSource $_) } |
        Sort-Object -Unique
}

# 统计文字和数字字符，用作方法说明及阶段说明的最低结构化质量门槛。
function Get-MeaningfulLength {
    param([AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return 0
    }

    return [regex]::Matches($Text, '[\p{L}\p{Nd}]').Count
}

# 生成跨语言一致的诊断对象，供控制台和 JSON 两种输出方式共用。
function Add-CommentDiagnostic {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][int]$Line,
        [Parameter(Mandatory = $true)][string]$Language,
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Rule,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $diagnostics.Add([pscustomobject]@{
            path     = Convert-ToDisplayPath $FilePath
            line     = $Line
            language = $Language
            method   = $Method
            rule     = $Rule
            message  = $Message
        })
}

# 定位当前 .NET SDK 自带的 Roslyn 程序集，不增加 NuGet 包或全局工具依赖。
function Import-RoslynAssemblies {
    if ('Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree' -as [type]) {
        return
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
        throw '无法读取 .NET SDK 版本，C# 注释检查需要仓库 global.json 指定的 SDK。'
    }

    $dotnetRoot = Split-Path (Get-Command dotnet -ErrorAction Stop).Source
    $roslynDirectory = Join-Path $dotnetRoot "sdk\$sdkVersion\Roslyn\bincore"
    $codeAnalysisPath = Join-Path $roslynDirectory 'Microsoft.CodeAnalysis.dll'
    $csharpPath = Join-Path $roslynDirectory 'Microsoft.CodeAnalysis.CSharp.dll'

    if (-not (Test-Path -LiteralPath $codeAnalysisPath) -or -not (Test-Path -LiteralPath $csharpPath)) {
        throw "当前 .NET SDK 中未找到 Roslyn：$roslynDirectory"
    }

    Add-Type -Path $codeAnalysisPath
    Add-Type -Path $csharpPath
}

# 把 Roslyn 的不同方法声明节点转换为便于定位的可读名称。
function Get-CSharpMethodName {
    param([Parameter(Mandatory = $true)]$Node)

    if ($Node -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]) {
        return $Node.Identifier.Text
    }
    if ($Node -is [Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax]) {
        return $Node.Identifier.Text
    }
    if ($Node -is [Microsoft.CodeAnalysis.CSharp.Syntax.DestructorDeclarationSyntax]) {
        return "~$($Node.Identifier.Text)"
    }
    if ($Node -is [Microsoft.CodeAnalysis.CSharp.Syntax.OperatorDeclarationSyntax]) {
        return "operator $($Node.OperatorToken.Text)"
    }
    if ($Node -is [Microsoft.CodeAnalysis.CSharp.Syntax.ConversionOperatorDeclarationSyntax]) {
        return "$($Node.ImplicitOrExplicitKeyword.Text) operator $($Node.Type)"
    }

    return $Node.GetType().Name
}

# 从 C# XML 文档中提取 summary，允许无语义变化的继承实现使用 inheritdoc。
function Get-CSharpDocumentation {
    param([Parameter(Mandatory = $true)]$Node)

    $leadingTrivia = $Node.GetLeadingTrivia().ToFullString()
    if ($leadingTrivia -match '(?is)<inheritdoc(?:\s[^>]*)?\s*/>') {
        return [pscustomobject]@{ Found = $true; Inherited = $true; Description = '' }
    }

    $summaryMatches = [regex]::Matches($leadingTrivia, '(?is)<summary\b[^>]*>(.*?)</summary>')
    if ($summaryMatches.Count -eq 0) {
        return [pscustomobject]@{ Found = $false; Inherited = $false; Description = '' }
    }

    # 被整段注释掉的旧方法可能也包含 XML 标签，必须以最靠近当前声明的 summary 为准。
    $summary = $summaryMatches[$summaryMatches.Count - 1]
    $withoutTags = [regex]::Replace($summary.Groups[1].Value, '<[^>]+>', ' ')
    $description = [System.Net.WebUtility]::HtmlDecode($withoutTags).Trim()
    return [pscustomobject]@{ Found = $true; Inherited = $false; Description = $description }
}

# 检查 C# 方法的 XML 文档覆盖率，以及长方法内部的功能阶段注释数量。
function Test-CSharpFile {
    param([Parameter(Mandatory = $true)][string]$FilePath)

    $source = [System.IO.File]::ReadAllText($FilePath)
    $sourceText = [Microsoft.CodeAnalysis.Text.SourceText]::From($source)
    $parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new(
        [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Latest,
        [Microsoft.CodeAnalysis.DocumentationMode]::Parse
    )
    $syntaxTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($sourceText, $parseOptions, $FilePath)
    $root = $syntaxTree.GetRoot()

    # 解析错误单独报告，防止不完整语法树产生误导性的注释缺失结果。
    $syntaxTree.GetDiagnostics() |
        Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error } |
        ForEach-Object {
            $line = $_.Location.GetLineSpan().StartLinePosition.Line + 1
            Add-CommentDiagnostic $FilePath $line 'C#' '<parse>' 'CQ004' $_.GetMessage()
        }

    $methodNodes = $root.DescendantNodes() | Where-Object {
        $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax]
    }

    foreach ($node in $methodNodes) {
        $line = $node.GetLocation().GetLineSpan().StartLinePosition.Line + 1
        $methodName = Get-CSharpMethodName $node
        $documentation = Get-CSharpDocumentation $node

        if (-not $documentation.Found) {
            Add-CommentDiagnostic $FilePath $line 'C#' $methodName 'CQ001' '缺少包含 <summary> 的 XML 方法注释。'
        }
        elseif (-not $documentation.Inherited -and (Get-MeaningfulLength $documentation.Description) -lt $MinDescriptionLength) {
            Add-CommentDiagnostic $FilePath $line 'C#' $methodName 'CQ002' "方法注释有效文字不足 $MinDescriptionLength 个，必须说明职责和业务意图。"
        }

        $body = $node.Body
        if ($null -eq $body) {
            continue
        }

        $bodyLineSpan = $body.GetLocation().GetLineSpan()
        $bodyLines = $bodyLineSpan.EndLinePosition.Line - $bodyLineSpan.StartLinePosition.Line + 1
        if ($bodyLines -le $LongMethodLines) {
            continue
        }

        # 只把独占一行且内容达到最低长度的普通注释计为功能阶段说明。
        $stageCommentCount = 0
        foreach ($trivia in $body.DescendantTrivia()) {
            $isLineComment = $trivia.RawKind -eq [int][Microsoft.CodeAnalysis.CSharp.SyntaxKind]::SingleLineCommentTrivia
            $isBlockComment = $trivia.RawKind -eq [int][Microsoft.CodeAnalysis.CSharp.SyntaxKind]::MultiLineCommentTrivia
            if (-not $isLineComment -and -not $isBlockComment) {
                continue
            }

            $commentLine = $trivia.GetLocation().GetLineSpan().StartLinePosition.Line
            $lineStart = $sourceText.Lines[$commentLine].Start
            $prefixLength = $trivia.SpanStart - $lineStart
            $linePrefix = if ($prefixLength -gt 0) { $source.Substring($lineStart, $prefixLength) } else { '' }
            $commentText = $trivia.ToString() -replace '^\s*//', '' -replace '^\s*/\*', '' -replace '\*/\s*$', '' -replace '(?m)^\s*\*\s?', ''

            if ([string]::IsNullOrWhiteSpace($linePrefix) -and (Get-MeaningfulLength $commentText) -ge 6) {
                $stageCommentCount++
            }
        }

        $requiredStageComments = [Math]::Max($MinStageComments, [Math]::Ceiling($bodyLines / $LongMethodLines))
        if ($stageCommentCount -lt $requiredStageComments) {
            Add-CommentDiagnostic $FilePath $line 'C#' $methodName 'CQ003' "方法体 $bodyLines 行，至少需要 $requiredStageComments 段功能阶段注释，当前只有 $stageCommentCount 段。"
        }
    }
}

# 查找紧邻 PowerShell 函数声明的普通注释，并提取其说明文字。
function Get-PowerShellDocumentation {
    param(
        [Parameter(Mandatory = $true)]$FunctionAst,
        [Parameter(Mandatory = $true)][object[]]$Tokens,
        [Parameter(Mandatory = $true)][string]$Source
    )

    $startOffset = $FunctionAst.Extent.StartOffset
    $commentToken = $Tokens |
        Where-Object { $_.Kind -eq [System.Management.Automation.Language.TokenKind]::Comment -and $_.Extent.EndOffset -le $startOffset } |
        Sort-Object { $_.Extent.EndOffset } -Descending |
        Select-Object -First 1

    if ($null -eq $commentToken) {
        return $null
    }

    $gapLength = $startOffset - $commentToken.Extent.EndOffset
    $gap = if ($gapLength -gt 0) { $Source.Substring($commentToken.Extent.EndOffset, $gapLength) } else { '' }
    if (-not [string]::IsNullOrWhiteSpace($gap)) {
        return $null
    }

    return $commentToken.Text -replace '^\s*#', '' -replace '^\s*<#', '' -replace '#>\s*$', '' -replace '(?m)^\s*#\s?', ''
}

# 检查 PowerShell 函数和类方法的声明注释及长函数阶段注释。
function Test-PowerShellFile {
    param([Parameter(Mandatory = $true)][string]$FilePath)

    # 第一阶段解析语法树和注释标记，语法错误优先形成独立诊断。
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($FilePath, [ref]$tokens, [ref]$parseErrors)
    $source = [System.IO.File]::ReadAllText($FilePath)

    foreach ($parseError in $parseErrors) {
        Add-CommentDiagnostic $FilePath $parseError.Extent.StartLineNumber 'PowerShell' '<parse>' 'CQ004' $parseError.Message
    }

    $functions = $ast.FindAll({
            param($candidate)
            $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
            $candidate -is [System.Management.Automation.Language.FunctionMemberAst]
        }, $true)

    foreach ($function in $functions) {
        $functionName = $function.Name
        $line = $function.Extent.StartLineNumber
        $documentation = Get-PowerShellDocumentation $function $tokens $source

        if ($null -eq $documentation) {
            Add-CommentDiagnostic $FilePath $line 'PowerShell' $functionName 'CQ001' '缺少紧邻声明的函数注释。'
        }
        elseif ((Get-MeaningfulLength $documentation) -lt $MinDescriptionLength) {
            Add-CommentDiagnostic $FilePath $line 'PowerShell' $functionName 'CQ002' "函数注释有效文字不足 $MinDescriptionLength 个，必须说明职责和业务意图。"
        }

        $body = $function.Body
        $bodyLines = $body.Extent.EndLineNumber - $body.Extent.StartLineNumber + 1
        if ($bodyLines -le $LongMethodLines) {
            continue
        }

        # PowerShell 阶段注释必须位于函数体内、独占一行且包含足够的说明文字。
        $stageCommentCount = 0
        foreach ($token in $tokens) {
            if ($token.Kind -ne [System.Management.Automation.Language.TokenKind]::Comment -or
                $token.Extent.StartOffset -le $body.Extent.StartOffset -or
                $token.Extent.EndOffset -ge $body.Extent.EndOffset) {
                continue
            }

            $lineStart = $source.LastIndexOf("`n", [Math]::Max(0, $token.Extent.StartOffset - 1)) + 1
            $prefixLength = $token.Extent.StartOffset - $lineStart
            $linePrefix = if ($prefixLength -gt 0) { $source.Substring($lineStart, $prefixLength) } else { '' }
            $commentText = $token.Text -replace '^\s*#', '' -replace '^\s*<#', '' -replace '#>\s*$', '' -replace '(?m)^\s*#\s?', ''

            if ([string]::IsNullOrWhiteSpace($linePrefix) -and (Get-MeaningfulLength $commentText) -ge 6) {
                $stageCommentCount++
            }
        }

        $requiredStageComments = [Math]::Max($MinStageComments, [Math]::Ceiling($bodyLines / $LongMethodLines))
        if ($stageCommentCount -lt $requiredStageComments) {
            Add-CommentDiagnostic $FilePath $line 'PowerShell' $functionName 'CQ003' "函数体 $bodyLines 行，至少需要 $requiredStageComments 段功能阶段注释，当前只有 $stageCommentCount 段。"
        }
    }
}

# 调用 TypeScript 编译器检查 TS、JS 和 Vue 脚本，并合并为统一诊断结果。
function Test-ScriptFiles {
    param([AllowEmptyCollection()][string[]]$FilePaths)

    if ($FilePaths.Count -eq 0) {
        return
    }

    $nodeChecker = Join-Path $PSScriptRoot 'check-script-comments.mjs'
    $temporaryList = New-TemporaryFile

    try {
        # 第一阶段写入结构化文件清单，避免大量路径触发 Windows 命令行长度限制。
        $payload = [pscustomobject]@{
            repoRoot = $repoRoot
            files    = @($FilePaths | ForEach-Object {
                    [pscustomobject]@{
                        absolutePath = $_
                        relativePath = Convert-ToDisplayPath $_
                    }
                })
        }
        $payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryList.FullName -Encoding utf8

        $nodeOutput = & node $nodeChecker `
            --file-list $temporaryList.FullName `
            --long-method-lines $LongMethodLines `
            --min-description-length $MinDescriptionLength `
            --min-stage-comments $MinStageComments

        if ($LASTEXITCODE -ne 0) {
            throw "TypeScript/Vue 注释检查器执行失败：$nodeOutput"
        }

        # 第二阶段解析并合并 Node 检查结果，最终由统一出口决定退出码。
        if (-not [string]::IsNullOrWhiteSpace($nodeOutput)) {
            foreach ($diagnostic in ($nodeOutput | ConvertFrom-Json)) {
                $diagnostics.Add($diagnostic)
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryList.FullName -Force -ErrorAction SilentlyContinue
    }
}

# 组织跨语言检查、稳定排序输出，并以退出码表达质量门禁结果。
function Invoke-CommentQualityCheck {
    $sourceFiles = @(Resolve-SourceFiles)
    Write-Verbose "选择模式=$($PSCmdlet.ParameterSetName)，All=$All，BaseRef=$BaseRef，Path=$($Path -join ',')，Json=$Json，文件数=$($sourceFiles.Count)"
    if ($sourceFiles.Count -eq 0) {
        if ($Json) {
            '[]'
        }
        else {
            Write-Host '代码注释检查通过：当前范围内没有需要检查的源文件。' -ForegroundColor Green
        }
        $script:commentCheckExitCode = 0
        return
    }

    # 第一阶段按语言分组，确保每类文件交给对应的真实语法分析器。
    $csharpFiles = @($sourceFiles | Where-Object { [System.IO.Path]::GetExtension($_) -eq '.cs' })
    $powerShellFiles = @($sourceFiles | Where-Object { [System.IO.Path]::GetExtension($_) -in @('.ps1', '.psm1') })
    $scriptFiles = @($sourceFiles | Where-Object { [System.IO.Path]::GetExtension($_) -in @('.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.vue') })

    # 各语言使用自身语法树，避免以正则猜测方法边界和注释归属。
    if ($csharpFiles.Count -gt 0) {
        Import-RoslynAssemblies
        foreach ($file in $csharpFiles) {
            Test-CSharpFile $file
        }
    }

    foreach ($file in $powerShellFiles) {
        Test-PowerShellFile $file
    }
    Test-ScriptFiles $scriptFiles

    $sortedDiagnostics = @($diagnostics | Sort-Object path, line, rule, method)
    if ($Json) {
        ConvertTo-Json -InputObject @($sortedDiagnostics) -Depth 5
    }
    elseif ($sortedDiagnostics.Count -eq 0) {
        Write-Host "代码注释检查通过：已检查 $($sourceFiles.Count) 个源文件。" -ForegroundColor Green
    }
    else {
        foreach ($diagnostic in $sortedDiagnostics) {
            Write-Host "$($diagnostic.path):$($diagnostic.line) [$($diagnostic.rule)] $($diagnostic.method) - $($diagnostic.message)"
        }
        Write-Host "代码注释检查失败：$($sourceFiles.Count) 个文件中发现 $($sortedDiagnostics.Count) 个问题。" -ForegroundColor Red
    }

    $script:commentCheckExitCode = if ($sortedDiagnostics.Count -eq 0) { 0 } else { 1 }
}

Invoke-CommentQualityCheck
exit $script:commentCheckExitCode
