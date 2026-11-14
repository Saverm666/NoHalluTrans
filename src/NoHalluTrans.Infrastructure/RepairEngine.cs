using System.Text;
using System.Text.RegularExpressions;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

public sealed class RepairEngine(TexCompiler compiler, TexRuntime runtime)
{
    public RepairRules Catalog { get; } = new();
    public async Task<(CompileResult Compile, List<RepairRecord> Records)> CompileWithRepairAsync(PreparedProject project,
        PipelineOptions options, IProgress<PipelineEvent>? progress, CancellationToken token)
    {
        List<RepairRecord> records = []; HashSet<string> applied = [], packageAttempts = [];
        CompileResult result = await compiler.CompileAsync(project, options.CompileTimeoutSeconds, token);
        int repairRound = 0, packageRound = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string[] missing = result.Ok ? [] : result.MissingFiles.Where(packageAttempts.Add).ToArray();
            if (missing.Length > 0 && packageRound < 32 && await runtime.InstallMissingAsync(missing, progress, token))
            {
                packageRound++;
                result = await compiler.CompileAsync(project, options.CompileTimeoutSeconds, token);
                continue;
            }
            Match unknownBabelOption = Regex.Match(result.Log, @"Package babel Error: Unknown option '([^']+)'");
            if (options.TargetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && unknownBabelOption.Success)
            {
                string option = unknownBabelOption.Groups[1].Value;
                string languageFile = option + ".ldf";
                if (repairRound < options.MaxRepairRounds && packageAttempts.Add(languageFile))
                {
                    try
                    {
                        if (await runtime.InstallMissingAsync([languageFile], progress, token))
                        {
                            packageRound++;
                            result = await compiler.CompileAsync(project, options.CompileTimeoutSeconds, token);
                            continue;
                        }
                    }
                    catch (IOException ex)
                    {
                        progress?.Report(new("issue", "compile", "自动安装 Babel 语言包失败，保留原有语言配置：" + ex.Message, Code: "babel-package-install-failed"));
                    }
                }
            }
            if (repairRound >= options.MaxRepairRounds) break;
            int round = repairRound + 1;
            bool changed = false;
            foreach (var rule in Catalog.Rules)
            {
                if (result.Ok && !RepairRules.AllowsSuccessfulCompile(rule) || !result.Ok && RepairRules.RequiresSuccessfulCompile(rule)) continue;
                if (applied.Contains(rule.Name) && !RepairRules.CanRepeat(rule)) continue;
                string main = FileStore.ReadTex(project.MainTex);
                string[] files = FileStore.CompilationSourceFiles(project.Root, project.MainTex, RepairRules.RepairsLocalStyles(rule)).ToArray();
                string? projectSource = RepairRules.RequiresProjectSourceEvaluation(rule) ? string.Join("\n", files.Select(FileStore.ReadTex)) : null;
                if (rule.Handler == "bbl_language")
                    projectSource += "\n" + string.Join("\n", Directory.EnumerateFiles(project.Root, "*.bbl", SearchOption.AllDirectories).Select(FileStore.ReadTex));
                bool applies = RepairRules.RequiresPerFileEvaluation(rule)
                    ? files.Any(f => Catalog.Applies(rule, FileStore.ReadTex(f), result.Log, project.Engine, options.TargetLanguage, f, project.Root, projectSource))
                    : Catalog.Applies(rule, projectSource ?? main, result.Log, project.Engine, options.TargetLanguage, project.MainTex, project.Root, projectSource);
                if (!applies) continue;
                var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var previousSources = result.Ok ? files.ToDictionary(f => f, File.ReadAllBytes, pathComparer) : null;
                string[] cacheNames = [".aux", ".out", ".toc", ".lof", ".lot", ".bbl", ".blg", ".bcf", ".run.xml", ".pdf", ".log", ".fls"];
                string outputStem = Path.Combine(project.Root, Path.GetFileNameWithoutExtension(project.MainTex));
                var previousOutputs = result.Ok ? cacheNames.Select(ext => outputStem + ext).Concat(FileStore.CompilationOutputFiles(project.Root, project.MainTex))
                    .Distinct(pathComparer).Where(File.Exists).ToDictionary(f => f, File.ReadAllBytes, pathComparer) : null;
                var previousResult = result;
                bool previouslyOk = result.Ok;
                bool didChange = false;
                if (rule.Name == "missing-bbl")
                    didChange = await compiler.RunBibliographyAsync(project, options.CompileTimeoutSeconds, token);
                else if (rule.Name == "utf8-bibliography")
                {
                    var plan = PlanBibliographyRepair(project.Root, project.MainTex, result.Log);
                    if (plan is { } repair)
                    {
                        FileStore.WriteTex(repair.Path, repair.Text); didChange = true;
                    }
                }
                else
                {
                    bool allFiles = RepairRules.RequiresPerFileEvaluation(rule);
                    foreach (string file in allFiles ? files : [project.MainTex])
                    {
                        string old = FileStore.ReadTex(file);
                        if (allFiles && !Catalog.Applies(rule, old, result.Log, project.Engine, options.TargetLanguage, file, project.Root, projectSource)) continue;
                        string next = Catalog.Rewrite(rule, old, new(project.Engine, result.Log, options.TargetLanguage, projectSource, file, project.Root));
                        if (next == old) continue;
                        FileStore.WriteTex(file, next); didChange = true;
                    }
                    if (didChange && rule.Name == "natbib-final-aux")
                    {
                        string auxPath = Path.Combine(project.Root, Path.GetFileNameWithoutExtension(project.MainTex) + ".aux");
                        string aux = File.Exists(auxPath) ? FileStore.ReadTex(auxPath) : "";
                        if (aux.Contains(@"\citation{") && !aux.Contains(@"\bibcite{"))
                            await compiler.RunBibliographyAsync(project, options.CompileTimeoutSeconds, token);
                    }
                }
                if (!didChange) continue;
                applied.Add(rule.Name); records.Add(new(rule.Name, rule.Description, round)); changed = true;
                progress?.Report(new("issue", "compile", "已应用修复：" + rule.Name, Code: "repair-applied"));
                // Recompile after each change: following rules must see fresh diagnostics.
                // Warning-only repairs must preserve a working document.
                if (previouslyOk)
                {
                    var checkedResult = await compiler.CompileAsync(project, options.CompileTimeoutSeconds, token);
                    if (!checkedResult.Ok || !RepairRules.WarningDiagnosticResolved(rule, previousResult.Log, checkedResult.Log,
                        FileStore.ReadTex(project.MainTex), project.Root, project.MainTex))
                    {
                        foreach (var (path, source) in previousSources!) File.WriteAllBytes(path, source);
                        foreach (string path in cacheNames.Select(ext => outputStem + ext).Concat(FileStore.CompilationOutputFiles(project.Root, project.MainTex)).Concat(previousOutputs!.Keys).Distinct(pathComparer))
                        {
                            if (previousOutputs!.TryGetValue(path, out byte[]? bytes)) File.WriteAllBytes(path, bytes);
                            else if (File.Exists(path)) File.Delete(path);
                        }
                        records.RemoveAt(records.Count - 1);
                        result = previousResult;
                        progress?.Report(new("issue", "compile", "告警修复未通过复编译，已恢复原始源码。", Code: "repair-reverted"));
                    }
                    else result = checkedResult;
                }
                break;
            }
            if (!changed) break;
            repairRound++;
            if (!result.Ok) result = await compiler.CompileAsync(project, options.CompileTimeoutSeconds, token);
        }
        return (result, records);
    }
    // Read-only planning keeps validation and execution on the same file/encoding boundary.
    public static (string Path, string Text)? PlanBibliographyRepair(string root, string main, string log)
    {
        Match diagnostic = Regex.Match(log, @"(?m)^([^\r\n]+\.bbl):(\d+): Undefined control sequence\.");
        if (!diagnostic.Success) return null;
        string path = Path.GetFullPath(Path.Combine(root, diagnostic.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
        string? bbl = Directory.EnumerateFiles(root, "*.bbl", SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFullPath(p).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (bbl is null) return null;
        try
        {
            string old = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(bbl)).TrimStart('\ufeff');
            string[] lines = old.Split('\n');
            if (!int.TryParse(diagnostic.Groups[2].Value, out int number)) return null;
            int line = number - 1;
            string active = RepairRules.ExecutableText(old + "\n" + string.Join("\n", FileStore.TexFiles(root).Select(FileStore.ReadTex)));
            if (line < 0 || line >= lines.Length || RepairBibliography(lines[line]) == lines[line]
                || Regex.Matches(RepairRules.ExecutableText(lines[line]), @"\\IeC\b").Count != 1
                || Regex.IsMatch(active, @"\\(?:[gex]?def|(?:re)?newcommand|providecommand|let)[ \t]*\{?[ \t]*\\IeC\b")) return null;
            // Preserve generated bibliographies: BibTeX/Biber may write them again
            // during this or a later compilation. IeC is a legacy input wrapper;
            // its confirmed Latin accent argument remains native TeX under Unicode engines.
            string original = FileStore.ReadTex(main);
            string changed = RepairRules.Inject(original, "% hjfy: utf8-bibliography repair\n\\providecommand{\\IeC}[1]{#1}\n", afterClass: true);
            return changed == original ? null : (main, changed);
        }
        catch (DecoderFallbackException) { return null; }
    }

    public static string RepairBibliography(string text)
    {
        Dictionary<char, char> accents = new() { ['\''] = '\u0301', ['`'] = '\u0300', ['^'] = '\u0302', ['"'] = '\u0308', ['~'] = '\u0303', ['='] = '\u0304', ['.'] = '\u0307', ['u'] = '\u0306', ['v'] = '\u030c', ['H'] = '\u030b', ['r'] = '\u030a', ['c'] = '\u0327', ['k'] = '\u0328' };
        var matches = Regex.Matches(RepairRules.ExecutableText(text), @"\\IeC[ \t]*\{\\([`'^""~=.]|[uvHrck])[ \t]*(?:\{([A-Za-z])\}|([A-Za-z]))\}");
        foreach (Match m in matches.Reverse())
        {
            string letter = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            text = text.Remove(m.Index, m.Length).Insert(m.Index, (letter + accents[m.Groups[1].Value[0]]).Normalize());
        }
        return text;
    }
}
