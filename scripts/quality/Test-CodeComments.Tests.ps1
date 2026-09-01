[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$checker = Join-Path $PSScriptRoot 'Test-CodeComments.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "hd-report-comment-check-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    @'
class GoodSample
{
    /// <summary>根据输入构造稳定的示例结果。</summary>
    public int Build(int input)
    {
        // 第一阶段校验调用方输入。
        if (input < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input));
        }

        // 第二阶段生成并返回结果。
        var result = input + 1;
        result += 1;
        return result;
    }
}
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Good.cs') -Encoding utf8

    @'
class BadSample
{
    public void MissingDocumentation()
    {
    }

    /// <summary>太短</summary>
    public int MissingStages(int input)
    {
        var result = input;
        result += 1;
        result += 1;
        result += 1;
        result += 1;
        result += 1;
        result += 1;
        result += 1;
        return result;
    }
}
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Bad.cs') -Encoding utf8

    @'
/** 根据输入构造稳定的脚本结果。 */
function build(input: number) {
  // 第一阶段校验调用方输入。
  if (input < 0) {
    throw new Error("invalid input");
  }
  // 第二阶段生成并返回结果。
  let result = input + 1;
  result += 1;
  return result;
}

const handlers = {
  /** 根据输入生成对象方法的计算结果。 */
  buildAgain: (input: number) => input + 1,
};

/** 把类型化输入写入全局处理入口并返回计算结果。 */
globalThis.buildGlobal = (input: number) => input + 1;
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'good.ts') -Encoding utf8

    @'
<script setup lang="ts">
/** 返回当前页面元素引用的类型化占位值。 */
function readElement() {
  const target = null as HTMLElement | null;
  return target;
}
</script>
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'good.vue') -Encoding utf8

    @'
const missingDocumentation = () => 1;

globalThis.missingGlobalDocumentation = () => 2;

/** 太短 */
function missingStages(input: number) {
  let result = input;
  result += 1;
  result += 1;
  result += 1;
  result += 1;
  result += 1;
  result += 1;
  return result;
}
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'bad.ts') -Encoding utf8

    $goodOutput = [System.Collections.Generic.List[string]]::new()
    foreach ($goodFile in @((Join-Path $testRoot 'Good.cs'), (Join-Path $testRoot 'good.ts'), (Join-Path $testRoot 'good.vue'))) {
        $currentOutput = & pwsh -NoProfile -File $checker -Path $goodFile -LongMethodLines 10 2>&1
        $goodOutput.AddRange([string[]]$currentOutput)
        if ($LASTEXITCODE -ne 0) {
            throw "合法样例不应失败：$($currentOutput -join [Environment]::NewLine)"
        }
    }

    $badOutput = [System.Collections.Generic.List[string]]::new()
    foreach ($badFile in @((Join-Path $testRoot 'Bad.cs'), (Join-Path $testRoot 'bad.ts'))) {
        $currentOutput = & pwsh -NoProfile -File $checker -Path $badFile -LongMethodLines 10 2>&1
        $badOutput.AddRange([string[]]$currentOutput)
        if ($LASTEXITCODE -ne 1) {
            throw "非法样例应返回退出码 1：$($currentOutput -join [Environment]::NewLine)"
        }
    }

    $badText = $badOutput -join [Environment]::NewLine
    foreach ($rule in @('CQ001', 'CQ002', 'CQ003')) {
        if ($badText -notmatch [regex]::Escape("[$rule]")) {
            throw "非法样例没有产生预期规则 $rule：$badText"
        }
    }

    $badTypeScriptPath = Join-Path $testRoot 'bad.ts'
    $jsonOutput = & pwsh -NoProfile -File $checker `
        -Path $badTypeScriptPath `
        -LongMethodLines 10 `
        -Json
    if ($LASTEXITCODE -ne 1) {
        throw 'JSON 模式必须在发现违规时返回退出码 1。'
    }

    $jsonDiagnostics = @($jsonOutput | ConvertFrom-Json)
    if ($jsonDiagnostics.Count -lt 2 -or $jsonDiagnostics.rule -notcontains 'CQ001' -or $jsonDiagnostics.rule -notcontains 'CQ002') {
        throw 'JSON 模式没有保留完整的结构化诊断。'
    }

    Write-Host '代码注释检查器自测通过。' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
