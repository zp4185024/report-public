using System.Diagnostics;
using System.Text;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

/// <summary>
/// PocAiSpecCompiler 单测：钉住与 PowerShell 生成器（New-ReportPackage.ps1）产物的结构一致性，
/// 并覆盖非法 spec 的错误路径（命名、SQL 变量对齐、未知字段严格拒绝等有意分歧项）。
/// </summary>
public sealed class PocAiSpecCompilerTests
{
    private static readonly PocAiSpecCompiler Compiler = new();

    /// <summary>
    /// 结构钉测（离线层）：C# 编译仓库样例 spec 的产物，必须与 ps1 生成并提交入库的
    /// 样板包（report-asset-inventory.report-package.json）在绑定坐标、快照 cellData
    /// 和四节字段上完全一致；ExportedAt 时间戳允许差异。
    /// 绑定与数据集节走“契约反序列化→规范序列化”归一后比对：样板包 JSON 在字段缺省
    /// （如 dataset.fields 无 description 键）与显式 null（参数 sourceCode 等）两种形态并存，
    /// 原树比对会被这类非语义差异误判，契约归一后才是真正需要钉住的结构。
    /// </summary>
    [Fact]
    public void Compile_SampleSpec_MatchesCommittedPowerShellOutput()
    {
        var specJson = File.ReadAllText(RequireRepositoryFile(
            "scripts", "report-design", "samples", "report-asset-inventory.spec.json"));
        var samplePackageJson = File.ReadAllText(RequireRepositoryFile(
            "scripts", "report-design", "samples", "report-asset-inventory.report-package.json"));

        var result = Compiler.Compile(specJson);
        Assert.True(result.Ok, string.Join("; ", result.Issues.Select(item => $"{item.Path}: {item.Message}")));

        var actual = JObject.Parse(PocReportPackageFormat.SerializeCanonical(result.Package));
        var expected = JObject.Parse(samplePackageJson);

        // 绑定坐标：归一化后逐字段比对（参数 token 坐标、明细行与字段列序）。
        Assert.Equal(CanonicalJson(Binding(expected)), CanonicalJson(Binding(actual)));

        // 快照结构：cellData/样式/行高/列宽/合并区/工作表元数据整体无 null 键，直接整树比对。
        Assert.True(
            JToken.DeepEquals(expected["template"]!["snapshot"], actual["template"]!["snapshot"]),
            "snapshot 与 ps1 产物不一致：\n" + expected["template"]!["snapshot"]!.ToString(Formatting.Indented) +
            "\n---实际---\n" + actual["template"]!["snapshot"]!.ToString(Formatting.Indented));

        // report / parameters 两节原树比对（两侧 null 键形态一致）。
        Assert.True(JToken.DeepEquals(expected["report"], actual["report"]),
            "report 节与 ps1 产物不一致：\n" + expected["report"]!.ToString(Formatting.Indented) +
            "\n---实际---\n" + actual["report"]!.ToString(Formatting.Indented));
        Assert.True(JToken.DeepEquals(expected["parameters"], actual["parameters"]));

        // dataset 节：契约归一后比对（code/name/queryText/字段与变量契约/运行参数）。
        Assert.Equal(CanonicalJson(Dataset(expected)), CanonicalJson(Dataset(actual)));

        // 根级固定字段：包类型与版本。
        Assert.Equal("hd-report-center.report-package", actual.Value<string>("packageKind"));
        Assert.Equal(1, actual.Value<int?>("packageVersion"));
    }

    /// <summary>把报表包 JSON 的绑定节归一为契约形态（反序列化→规范序列化）。</summary>
    /// <param name="package">报表包 JSON。</param>
    /// <returns>归一后的绑定对象。</returns>
    private static JObject Binding(JObject package) => JObject.Parse(CanonicalJson(
        JsonConvert.DeserializeObject<HD.ReportCenter.ReportEngine.CoreValidation.PocTemplateBinding>(
            package["template"]!["binding"]!.ToString())));

    /// <summary>把报表包 JSON 的数据集节归一为契约形态（反序列化→规范序列化）。</summary>
    /// <param name="package">报表包 JSON。</param>
    /// <returns>归一后的数据集对象。</returns>
    private static JObject Dataset(JObject package) => JObject.Parse(CanonicalJson(
        JsonConvert.DeserializeObject<HD.ReportCenter.Application.CoreValidation.PocReportPackageDataset>(
            package["dataset"]!.ToString())));

    /// <summary>
    /// 与 PocReportPackageFormat.SerializeCanonical 同规则的通用契约序列化：
    /// camelCase 属性名、保留 null、紧凑格式，用于对报表包分节做归一比对。
    /// </summary>
    /// <param name="value">待序列化的契约对象。</param>
    /// <returns>规范化 JSON 文本。</returns>
    private static string CanonicalJson(object? value) => JsonConvert.SerializeObject(
        value,
        Formatting.None,
        new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include
        });

    /// <summary>
    /// 需要真实 PowerShell 7 的钉测特性：发现期定位 pwsh，找不到时把用例标记为 Skip，
    /// 并把原因写进跳过说明（如实报告“没跑”，绝不假装通过）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class PowerShellRequiredFactAttribute : FactAttribute
    {
        /// <summary>初始化 PowerShellRequiredFactAttribute 特性：按 pwsh 探测结果决定是否跳过。</summary>
        public PowerShellRequiredFactAttribute()
        {
            if (LocatePowerShell() is null)
            {
                Skip = "本机未找到可用的 pwsh（PowerShell 7），ps1 现场编译钉测未执行。";
            }
        }
    }

    /// <summary>
    /// 结构钉测（在线层）：运行 pwsh 现场编译样例 spec，把产物与 C# 编译结果逐结构比对。
    /// 本地找不到可用的 PowerShell 7 时整用例跳过并明确说明原因（不假装通过）。
    /// </summary>
    [PowerShellRequiredFact]
    public async Task Compile_SampleSpec_MatchesLivePowerShellRun()
    {
        var pwshPath = LocatePowerShell()
            ?? throw new InvalidOperationException("pwsh 探测在发现期通过，运行期却找不到，环境可能已变化。");
        var specPath = RequireRepositoryFile(
            "scripts", "report-design", "samples", "report-asset-inventory.spec.json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "hd-report-pin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "live.report-package.json");
        try
        {
            // New-ReportPackage.ps1 与 samples 目录平级：spec 在 scripts/report-design/samples 下，脚本在上一级。
            var scriptPath = Path.Combine(
                Directory.GetParent(Path.GetDirectoryName(specPath)!)!.FullName,
                "New-ReportPackage.ps1");
            // 商店版 PowerShell 的应用执行别名（WindowsApps pwsh.exe）经 .NET Process 直调时
            // 会吞掉带引号的参数列表（exit 64 用法提示）；改用 -EncodedCommand 传递单个
            // base64 参数，转发层无法再对参数做二次解析，是官方推荐的无引号调用方式。
            var invocation =
                "& " + EncodeSingleQuoted(scriptPath) +
                " -SpecPath " + EncodeSingleQuoted(specPath) +
                " -OutputPath " + EncodeSingleQuoted(outputPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = pwshPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(invocation)));

            using var process = Process.Start(startInfo)!;
            // 立即关闭子进程 stdin；商店版 pwsh 在重定向输入未关闭时可能一直等待。
            process.StandardInput.Close();
            // 商店版 pwsh 完成脚本后偶发卡在退出流程（别名转发遗留问题），因此不依赖退出码：
            // 以“stdout 出现 [OK] + 产物文件生成”为成功判据，超时后强杀进程树再读取输出。
            // 不带取消令牌的 ReadToEnd：任务在进程死亡（自然退出或被杀）时随管道关闭而完成。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAny(
                Task.WhenAll(stdoutTask, stderrTask),
                Task.Delay(TimeSpan.FromSeconds(90)));
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 进程已自行退出时忽略 */ }
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(
                stdout.Contains("[OK]", StringComparison.Ordinal) && File.Exists(outputPath),
                $"ps1 现场编译未成功。stdout={stdout}stderr={stderr}");

            // 现场产物与 C# 产物逐结构比对（导出时间戳允许差异）。
            var live = JObject.Parse(await File.ReadAllTextAsync(outputPath));
            var result = Compiler.Compile(await File.ReadAllTextAsync(specPath));
            Assert.True(result.Ok, string.Join("; ", result.Issues.Select(item => item.Message)));
            var actual = JObject.Parse(PocReportPackageFormat.SerializeCanonical(result.Package));

            Assert.True(JToken.DeepEquals(live["template"]!["binding"], actual["template"]!["binding"]));
            Assert.True(JToken.DeepEquals(
                live["template"]!["snapshot"]!["sheets"]!["report-sheet"]!["cellData"],
                actual["template"]!["snapshot"]!["sheets"]!["report-sheet"]!["cellData"]));
            Assert.True(JToken.DeepEquals(live["report"], actual["report"]));
            Assert.True(JToken.DeepEquals(live["parameters"], actual["parameters"]));
            // dataset 节走契约归一比对：C# 契约含 null description 而包 JSON 缺省该键，属非语义差异。
            Assert.Equal(CanonicalJson(Dataset(live)), CanonicalJson(Dataset(actual)));
            Assert.Equal(live.Value<int?>("packageVersion"), actual.Value<int?>("packageVersion"));
            Assert.Equal(live.Value<string>("packageKind"), actual.Value<string>("packageKind"));
        }
        finally
        {
            try { Directory.Delete(outputDirectory, recursive: true); } catch { /* 临时目录清理失败不影响结论 */ }
        }
    }

    /// <summary>验证未知字段严格拒绝：ps1 宽松忽略，C# 编译器必须显式报错并给出节点路径。</summary>
    [Fact]
    public void Compile_RejectsUnknownFields()
    {
        var spec = JObject.Parse(LoadSampleSpec());
        spec["futureExtension"] = new JObject { ["anything"] = "新配置" };

        var result = Compiler.Compile(spec.ToString(Formatting.None));

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "SPEC_FIELD_UNKNOWN" &&
            issue.Path == "$" &&
            issue.Message.Contains("futureExtension"));
    }

    /// <summary>验证非法编码命名被拒绝并带字段路径（report.code 不符合命名规则）。</summary>
    [Fact]
    public void Compile_RejectsInvalidCodeNamingWithPath()
    {
        var spec = JObject.Parse(LoadSampleSpec());
        spec["report"]!["code"] = "1bad-code";

        var result = Compiler.Compile(spec.ToString(Formatting.None));

        Assert.False(result.Ok);
        var issue = Assert.Single(result.Issues.Where(item => item.Code == "SPEC_CODE_INVALID"));
        Assert.Equal("report.code", issue.Path);
    }

    /// <summary>验证 SQL 中未声明的 @变量被拦截：错误落到 dataset.queryText 路径且沿用校验器错误码。</summary>
    [Fact]
    public void Compile_RejectsUndeclaredSqlParameter()
    {
        var spec = JObject.Parse(LoadSampleSpec());
        var query = spec["dataset"]!.Value<string>("queryText")!;
        spec["dataset"]!["queryText"] = query + " AND r.IsEnabled = @enableOnly";

        var result = Compiler.Compile(spec.ToString(Formatting.None));

        Assert.False(result.Ok);
        var issue = Assert.Single(result.Issues.Where(item => item.Code == "DATASET_PARAMETER_UNDEFINED"));
        Assert.Equal("dataset.queryText", issue.Path);
    }

    /// <summary>验证数据集变量缺少同名同类型参数承接时被拒绝（SPEC_VARIABLE_BINDING_MISSING）。</summary>
    [Fact]
    public void Compile_RejectsVariableWithoutMatchingParameter()
    {
        var spec = JObject.Parse(LoadSampleSpec());
        spec["dataset"]!["variables"]![0]!["dataType"] = "datetime";

        var result = Compiler.Compile(spec.ToString(Formatting.None));

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "SPEC_VARIABLE_BINDING_MISSING" &&
            issue.Message.Contains("startDate"));
    }

    /// <summary>验证 template.columns 引用不存在字段（区分大小写）被拒绝。</summary>
    [Fact]
    public void Compile_RejectsColumnFieldNotInDataset()
    {
        var spec = JObject.Parse(LoadSampleSpec());
        spec["template"]!["columns"]![0]!["field"] = "ReportCode";

        var result = Compiler.Compile(spec.ToString(Formatting.None));

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "SPEC_VALUE_INVALID" &&
            issue.Path == "template.columns[0]" &&
            issue.Message.Contains("区分大小写"));
    }

    /// <summary>验证合计项占用首列被拒绝（首列留给合计标签）。</summary>
    [Fact]
    public void Compile_RejectsTotalOnFirstColumn()
    {
        var spec = JObject.Parse(LoadSampleSpec());
        spec["template"]!["totals"] = new JArray
        {
            new JObject { ["field"] = "reportCode", ["label"] = "合计" }
        };

        var result = Compiler.Compile(spec.ToString(Formatting.None));

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "SPEC_VALUE_INVALID" &&
            issue.Path == "template.totals[0]" &&
            issue.Message.Contains("首列"));
    }

    /// <summary>验证非法 JSON、缺失 specVersion 与缺失 report 节各自给出稳定错误码。</summary>
    [Theory]
    [InlineData("{ not-json", "SPEC_JSON_INVALID")]
    [InlineData("{\"report\":{}}", "SPEC_VERSION_INVALID")]
    [InlineData("{\"specVersion\":1}", "SPEC_SECTION_MISSING")]
    public void Compile_RejectsStructurallyInvalidSpecs(string specJson, string expectedCode)
    {
        var result = Compiler.Compile(specJson);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    /// <summary>加载仓库样例 spec 文本。</summary>
    /// <returns>spec JSON 文本。</returns>
    private static string LoadSampleSpec() =>
        File.ReadAllText(RequireRepositoryFile(
            "scripts", "report-design", "samples", "report-asset-inventory.spec.json"));

    /// <summary>从测试程序集目录逐级向上查找仓库根下的相对文件。</summary>
    /// <param name="segments">相对仓库根的路径段。</param>
    /// <returns>命中文件路径。</returns>
    private static string RequireRepositoryFile(params string[] segments)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException(
            $"未找到仓库文件 {string.Join('/', segments)}；请在仓库工作区内运行测试。");
    }

    /// <summary>把路径编码为 PowerShell 单引号字面量（单引号翻倍转义），避免路径进入命令文本被二次解析。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>可直接内嵌 -EncodedCommand 命令文本的路径字面量。</returns>
    private static string EncodeSingleQuoted(string path) =>
        "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>按 PATH 与常见安装位置定位 PowerShell 7 可执行文件；找不到返回 null。</summary>
    /// <returns>pwsh 完整路径或 null。</returns>
    private static string? LocatePowerShell()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "pwsh.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
