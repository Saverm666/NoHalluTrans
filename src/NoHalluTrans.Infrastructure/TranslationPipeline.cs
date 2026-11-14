using System.Text.Json;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

public sealed class TranslationPipeline(HttpClient http, TexRuntime runtime, IProcessRunner processes)
{
    public async Task<PipelineResult> RunAsync(PipelineOptions options, ModelSettings model, IReadOnlyList<TermEntry> suppliedTerms,
        string workDirectory, string cachePath, IProgress<PipelineEvent>? progress, CancellationToken token)
    {
        if (!FileStore.Ascii(workDirectory)) throw new ArgumentException("编译工作目录需要使用 ASCII 路径。");
        Directory.CreateDirectory(workDirectory);
        using EventJournal journal = new(Path.Combine(workDirectory, "events.jsonl"), progress);
        journal.Emit("run_started", new Dictionary<string, object?> { ["input"] = options.Source, ["options"] = options });
        var issues = new List<TranslationIssue>(); var records = new List<RepairRecord>();
        void Stage(string id, string message) => journal.Report(new("stage", id, message));
        PipelineResult? terminal = null;
        int exitCode = 1;
        try
        {
            Stage("prepare_cache", "准备翻译缓存与术语");
            using var cache = new TranslationCache(cachePath);
            Stage("prepare_source", "准备隔离的 LaTeX 工程副本");
            string acquired = await new SourceAcquirer(http).AcquireAsync(options.Source, Path.Combine(workDirectory, "acquired"), token);
            PreparedProject project = ProjectPreparer.Prepare(acquired, Path.Combine(workDirectory, "source"), options, token);
            string? incompatible = Directory.EnumerateFileSystemEntries(project.Root, "*", SearchOption.AllDirectories)
                .FirstOrDefault(p => !FileStore.Ascii(Path.GetRelativePath(project.Root, p)));
            if (incompatible is not null)
                journal.Report(new("issue", "prepare_source", "工程内部含非 ASCII 文件名；若编译失败，请将相关文件名及引用改为英文：" + Path.GetRelativePath(project.Root, incompatible), Code: "non-ascii-asset"));
            Stage("compile_original", "编译原文 PDF");
            var preflight = await runtime.PreflightAsync(project, token);
            FileStore.WriteText(Path.Combine(workDirectory, "preflight.json"), JsonSerializer.Serialize(preflight, JsonDefaults.Options));
            if (preflight.Missing.Count > 0)
            {
                journal.Report(new("issue", "compile_original", "检测到缺失的 TeX 依赖：" + string.Join(", ", preflight.Missing), Code: "preflight-missing"));
                await runtime.InstallMissingAsync(preflight.Missing, journal, token);
            }
            var compiler = new TexCompiler(runtime, processes); var repair = new RepairEngine(compiler, runtime);
            var original = await repair.CompileWithRepairAsync(project, options, journal, token);
            records.AddRange(original.Records);
            string? originalPdf = null;
            if (original.Compile.Ok)
            { originalPdf = Path.Combine(workDirectory, "original.pdf"); File.Copy(original.Compile.Pdf!, originalPdf, true); }
            else
            {
                issues.Add(new("original-compile-failed", "原文编译失败，参见 original-compile.log。"));
                journal.Report(new("issue", "compile_original", "原文编译失败，仍尝试翻译。请在诊断日志中检查原因。", Code: "original-compile-failed"));
                FileStore.WriteText(Path.Combine(workDirectory, "original-compile.log"), original.Compile.Log);
            }
            if (originalPdf is null && options.PdfLayout != "target-only")
                throw new IOException("原文 PDF 构建失败，无法生成双语 PDF。请选择仅目标语言，或修复原文后重试。");
            Stage("parse", "提取正文片段并保护公式与命令");
            List<Fragment> fragments = []; Dictionary<string, string> sourceTexts = [];
            int scannedFiles = 0;
            journal.Report(new("progress", "parse", "开始切分正文", 0, project.BodyFiles.Count, Fragments: 0));
            foreach (string file in project.BodyFiles)
            {
                string text = FileStore.ReadTex(file); sourceTexts[file] = text;
                List<string> warnings = [];
                fragments.AddRange(FragmentScanner.Scan(text, file, fragments.Count, warnings));
                journal.Report(new("progress", "parse", $"已扫描 {++scannedFiles}/{project.BodyFiles.Count} 个文件，已切分 {fragments.Count} 块", scannedFiles, project.BodyFiles.Count, Fragments: fragments.Count));
                foreach (string warning in warnings) journal.Report(new("issue", "parse", warning, Code: "scan-warning"));
            }
            if (fragments.Count == 0)
                throw new InvalidDataException("没有提取到可翻译的正文。此来源可能只包含嵌入的 PDF；请提供论文的 TeX 源码压缩包。");
            var terms = suppliedTerms.ToList();
            ITranslationProvider provider = options.TranslationsFile is { Length: > 0 } path ? new FileTranslationProvider(path)
                : options.Mock ? new MockTranslationProvider() : new OpenAiProvider(http, model);
            if (options.AutoTerminology && terms.Count == 0 && provider is OpenAiProvider openAi)
            {
                Stage("terminology", "正在提取术语表");
                string prose = string.Join("\n\n", fragments.Take(40).Select(f => f.Source));
                try { terms = await openAi.ExtractTermsAsync(prose[..Math.Min(12000, prose.Length)], options.SourceLanguage, options.TargetLanguage, token); }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                { journal.Report(new("issue", "parse", "自动术语提取失败，继续翻译。", Code: "terminology-failed")); }
            }
            FileStore.WriteText(Path.Combine(workDirectory, "request.json"), JsonSerializer.Serialize(new
            {
                source_language = options.SourceLanguage, target_language = options.TargetLanguage, terminology = terms,
                fragments = fragments.Select(f => new { id = f.Id, file = Path.GetRelativePath(project.Root, f.File).Replace('\\', '/'), kind = f.Kind, text = f.Template })
            }, JsonDefaults.Options));
            Stage("translate", "正在翻译正文");
            var translations = await new TranslationScheduler().TranslateAsync(fragments, provider, cache, model,
                options.SourceLanguage, options.TargetLanguage, terms, journal, token);
            issues.AddRange(translations.Issues);
            foreach (var issue in issues) journal.Report(new("issue", "translate", issue.Message, Code: issue.Code, Fragment: issue.Fragment));
            FileStore.WriteText(Path.Combine(workDirectory, "translations.json"), JsonSerializer.Serialize(new
            {
                fragments = translations.Translations.OrderBy(p => p.Key).Select(p => new { id = $"f{p.Key:D4}", translation = p.Value })
            }, JsonDefaults.Options));
            Stage("reassemble", "回写译文并准备排版环境");
            foreach (var group in fragments.GroupBy(f => f.File))
            {
                string originalText = sourceTexts[group.Key];
                string backup = Path.Combine(project.Root, "_repro", "orig", Path.GetRelativePath(project.Root, group.Key));
                FileStore.WriteText(backup, originalText);
                string rewritten = FragmentScanner.Reassemble(originalText, group, translations.Translations, options.TargetLanguage);
                if (rewritten != originalText) FileStore.WriteTex(group.Key, rewritten);
            }
            // Insert after reassembly so UTF-16 fragment offsets still refer to the scanned text.
            ProjectPreparer.InjectUnicode(project, options.TargetLanguage);
            Stage("compile_translated", "构建译文 PDF，正在处理排版与引用");
            var translated = await repair.CompileWithRepairAsync(project, options, journal, token);
            records.AddRange(translated.Records);
            FileStore.WriteText(Path.Combine(workDirectory, "translated-compile.log"), translated.Compile.Log);
            FileStore.WriteText(Path.Combine(workDirectory, "compile-diagnostics.json"), JsonSerializer.Serialize(translated.Compile.Diagnostics, JsonDefaults.Options));
            if (!translated.Compile.Ok)
            {
                var first = translated.Compile.Diagnostics.FirstOrDefault(d => d.Kind == "error");
                string detail = first is null ? "" : $"\n{first.File}:{first.Line} {first.Message}";
                throw new IOException("译文编译失败。请查看任务诊断日志；译后源码已保留，可导出检查。" + detail);
            }
            if (translated.Compile.Log.Contains("Missing character:"))
            {
                issues.Add(new("missing-glyph", "译文存在字体缺字，请检查 PDF 与编译日志。"));
                journal.Report(new("issue", "compile_translated", issues[^1].Message, Code: "missing-glyph"));
            }
            if (translated.Compile.Log.Contains("There were undefined references") || translated.Compile.Log.Contains("There were undefined citations"))
            {
                issues.Add(new("unresolved-references", "译文仍有未解析引用，请检查文献资源与编译日志。"));
                journal.Report(new("issue", "compile_translated", issues[^1].Message, Code: "unresolved-references"));
            }
            token.ThrowIfCancellationRequested();
            Stage("compose_pdf", "生成指定格式 PDF");
            string finalPdf = PdfComposer.Compose(translated.Compile.Pdf!, originalPdf, options.PdfLayout, workDirectory, token);
            token.ThrowIfCancellationRequested();
            Stage("write_output", "保存最终 PDF");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPdf))!);
            File.Copy(finalPdf, options.OutputPdf, true);
            journal.Emit("output_published", new Dictionary<string, object?> { ["path"] = options.OutputPdf, ["kind"] = "pdf", ["bytes"] = new FileInfo(options.OutputPdf).Length });
            terminal = new("succeeded", options.OutputPdf, originalPdf, workDirectory, fragments.Count,
                translations.Cached, translations.Translated, translations.Fallback, records, issues);
            exitCode = 0;
            return terminal;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { terminal = new("cancelled", null, null, workDirectory, 0, 0, 0, 0, records, issues); exitCode = 3; throw; }
        catch (Exception ex)
        {
            issues.Add(new("pipeline-failed", ex.Message));
            journal.Report(new("issue", "", ex.Message, Code: "pipeline-failed"));
            terminal = new("failed", null, null, workDirectory, 0, 0, 0, 0, records, issues);
            throw;
        }
        finally
        {
            if (terminal is not null) FileStore.WriteText(Path.Combine(workDirectory, "result.json"), JsonSerializer.Serialize(terminal, JsonDefaults.Options));
            journal.Emit("run_finished", new Dictionary<string, object?> { ["outcome"] = terminal?.Outcome ?? "failed", ["exit_code"] = exitCode,
                ["artifacts"] = terminal?.OutputPdf is { } pdf ? new[] { pdf } : Array.Empty<string>() });
        }
    }
}
