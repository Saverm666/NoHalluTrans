using System.Text.RegularExpressions;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

public sealed class MainFileAmbiguousException(IReadOnlyList<string> candidates)
    : Exception("找到多个主 TeX 文件，请在任务详情中选择主文件。")
{
    public IReadOnlyList<string> Candidates { get; } = candidates;
}

public static class ProjectPreparer
{
    private static readonly Regex Class = new(@"\\documentclass\s*(?:\[[^\]]*\])?\s*\{([^}]+)\}");
    public static PreparedProject Prepare(string acquired, string work, PipelineOptions options, CancellationToken token)
    {
        FileStore.CopyTree(acquired, work, token);
        var files = FileStore.TexFiles(work).ToArray();
        if (files.Length == 0) throw new InvalidDataException("no .tex files found in the input directory or its subdirectories: " + acquired);
        var texts = files.ToDictionary(p => p, p => TexSyntax.WithoutComments(FileStore.ReadTex(p)));
        var candidates = files.Where(p => Class.IsMatch(texts[p]) && !Regex.IsMatch(texts[p], @"\\documentclass\s*\[[^\]]*\]\{subfiles\}")).ToList();
        string? main = null;
        if (!string.IsNullOrWhiteSpace(options.MainHint))
        {
            string hint = options.MainHint.Replace('\\', '/');
            main = candidates.FirstOrDefault(p => Path.GetRelativePath(work, p).Replace('\\', '/').Equals(hint, StringComparison.OrdinalIgnoreCase));
            if (main is null) throw new ArgumentException("指定的主文件不存在或没有顶层 documentclass：" + hint);
        }
        if (candidates.Count == 0)
        {
            if (texts.Values.Any(t => t.Contains("\\documentstyle")))
                throw new InvalidDataException("legacy \\documentstyle sources cannot be safely converted to LaTeX2e automatically");
            throw new InvalidDataException("could not find a top-level \\documentclass command");
        }
        if (main is null)
        {
            var preferred = candidates.Where(p => Path.GetFileNameWithoutExtension(p).Equals("main", StringComparison.OrdinalIgnoreCase)).ToList();
            if (preferred.Count == 1) main = preferred[0];
            else if (candidates.Count == 1) main = candidates[0];
            else throw new MainFileAmbiguousException(candidates.Select(p => Path.GetRelativePath(work, p)).ToArray());
        }
        string text = texts[main]; string documentClass = Class.Match(text).Groups[1].Value;
        string engine = DetectEngine(text, options.TargetLanguage, options.Engine);
        HashSet<string> body = files.Where(p => Regex.IsMatch(texts[p], @"\\begin\s*\{document\}|\\(?:part|chapter|section|subsection|paragraph)\b|\\begin\{(?:abstract|quote|itemize|enumerate|table|figure|theorem|proof)\}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        body.Add(main); Queue<string> queue = new(body);
        while (queue.TryDequeue(out string? current))
        {
            foreach (Match m in Regex.Matches(texts[current], @"\\(?:input|include|subfile)\s*\{([^}]+)\}"))
            {
                string name = m.Groups[1].Value; if (Path.GetExtension(name) == "") name += ".tex";
                foreach (string baseDir in new[] { Path.GetDirectoryName(current)!, work })
                {
                    string included = Path.GetFullPath(Path.Combine(baseDir, name));
                    if (texts.ContainsKey(included) && body.Add(included)) queue.Enqueue(included);
                }
            }
        }
        var project = new PreparedProject(work, main, engine, documentClass, body.Order(StringComparer.Ordinal).ToArray(), options.AllowShellEscape);
        PrepareEngineCompatibility(project, options.TargetLanguage);
        return project;
    }
    public static string DetectEngine(string text, string target, string requested = "auto")
    {
        bool cjk = target.StartsWith("zh") || target.StartsWith("ja") || target.StartsWith("ko");
        if (requested is not ("auto" or ""))
        {
            if (requested is not ("pdflatex" or "xelatex" or "lualatex")) throw new ArgumentException("不支持的 TeX 引擎。");
            if (cjk && requested == "pdflatex") throw new ArgumentException("中文翻译请选择 XeLaTeX 或 LuaLaTeX。");
            return requested;
        }
        var magic = Regex.Match(text, @"(?im)^%\s*!\s*TeX\s+(?:program|TS-program)\s*=\s*(xelatex|lualatex|pdflatex)");
        if (magic.Success && (!cjk || magic.Groups[1].Value.ToLowerInvariant() != "pdflatex")) return magic.Groups[1].Value.ToLowerInvariant();
        if (Regex.IsMatch(text, @"\\directlua|luatexja|luacode")) return "lualatex";
        return cjk || Regex.IsMatch(text, @"ctex|xeCJK|fontspec|\\setmainfont") ? "xelatex" : "pdflatex";
    }
    public static void InjectUnicode(PreparedProject project, string target)
    {
        if (!(target.StartsWith("zh") || target.StartsWith("ja") || target.StartsWith("ko"))) return;
        string text = FileStore.ReadTex(project.MainTex);
        string snippet = "";
        string active = RepairRules.ExecutableText(text);
        string recorder = Path.ChangeExtension(project.MainTex, ".fls");
        bool loadedCjk = File.Exists(recorder) && Regex.IsMatch(File.ReadAllText(recorder), @"(?im)^INPUT [^\r\n]*[/\\](?:ctex|xeCJK|luatexja|polyglossia)\.sty\r?$");
        bool hasCjkPackage = loadedCjk || Regex.IsMatch(active, @"ctex|xeCJK|polyglossia|luatexja", RegexOptions.IgnoreCase);
        bool legacyCjkOnly = Regex.IsMatch(active, @"\\(?:usepackage|RequirePackage)\s*(?:\[[^\]]*\]\s*)?\{[^}]*CJKutf8", RegexOptions.IgnoreCase)
            && !hasCjkPackage;
        if (!text.Contains("% hjfy: prepare_unicode", StringComparison.Ordinal) && legacyCjkOnly && project.Engine == "xelatex")
            snippet += "% hjfy: prepare_unicode -- Unicode CJK fallback for legacy CJKutf8 templates\n" +
                "\\usepackage{xeCJK}\n\\setCJKmainfont{FandolSong-Regular.otf}\n";
        else if (!text.Contains("% hjfy: prepare_unicode", StringComparison.Ordinal) && !hasCjkPackage)
        {
            Match document = Regex.Match(active, @"\\begin\s*\{document\}");
            if (!document.Success) return;
            text = text.Insert(document.Index, UnicodeFontPreparation.Loader + UnicodeFontPreparation.Selection(project) + "\n");
        }
        if (!text.Contains("% hjfy: translated labels", StringComparison.Ordinal))
            snippet += "% hjfy: translated labels\n\\providecommand{\\abstractname}{摘要}\\renewcommand{\\abstractname}{摘要}\n" +
                "\\providecommand{\\refname}{参考文献}\\renewcommand{\\refname}{参考文献}\n" +
                "\\providecommand{\\bibname}{参考文献}\\renewcommand{\\bibname}{参考文献}\n" +
                "\\renewcommand{\\figurename}{图}\\renewcommand{\\tablename}{表}\n";
        if (snippet.Length == 0) return;
        var m = Class.Match(RepairRules.ExecutableText(text));
        if (!m.Success) return;
        // Inject immediately after documentclass, also for single-line documents.
        FileStore.WriteTex(project.MainTex, text.Insert(m.Index + m.Length, "\n" + snippet + "\n"));
    }

    private static void PrepareEngineCompatibility(PreparedProject project, string targetLanguage)
    {
        foreach (string file in FileStore.TexFiles(project.Root))
        {
            string source = FileStore.ReadTex(file);
            string rewritten = Regex.Replace(source, @"\\newcommand\s*\*?\s*\{\\theHalgorithm\}", @"\providecommand{\theHalgorithm}");
            if (rewritten != source) FileStore.WriteTex(file, rewritten);
        }
        if (targetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string file in FileStore.TexFiles(project.Root).Concat(Directory.EnumerateFiles(project.Root, "*.cls", SearchOption.AllDirectories)))
            {
                string source = FileStore.ReadTex(file);
                string rewritten = Regex.Replace(source, @"(\\(?:usepackage|RequirePackage)\s*\[)([^\]]*)(\]\s*\{babel\})", match =>
                {
                    string[] options = match.Groups[2].Value.Split(',').Select(option => option.Trim())
                        .Where(option => option.Length > 0 && !option.Equals("latin", StringComparison.OrdinalIgnoreCase)).ToArray();
                    return options.Length == 0 ? "" : match.Groups[1].Value + string.Join(", ", options) + match.Groups[3].Value;
                }, RegexOptions.IgnoreCase);
                if (rewritten != source) FileStore.WriteTex(file, rewritten);
            }
        }
        if (project.Engine == "pdflatex") return;

        string className = project.DocumentClass.Replace('\\', '/').Split('/').Last();
        if (!className.EndsWith(".cls", StringComparison.OrdinalIgnoreCase)) className += ".cls";
        foreach (string file in Directory.EnumerateFiles(project.Root, "*.cls", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path).Equals(className, StringComparison.OrdinalIgnoreCase)))
        {
            string source = FileStore.ReadTex(file);
            Match assignment = Regex.Match(source, @"\\pdfoutput\s*=\s*(\d+)");
            string rewritten = source;
            if (assignment.Success)
            {
                string guarded = "\\RequirePackage{iftex}\n\\ifPDFTeX\\pdfoutput=" + assignment.Groups[1].Value + "\\fi";
                rewritten = rewritten[..assignment.Index] + guarded + rewritten[(assignment.Index + assignment.Length)..];
            }
            rewritten = Regex.Replace(rewritten, @"\\DisableLigatures[^\r\n]*", match => "\\RequirePackage{iftex}\n\\ifPDFTeX " + match.Value + "\\fi");
            if (rewritten != source) FileStore.WriteTex(file, rewritten);
        }

        string main = FileStore.ReadTex(project.MainTex);
        if (main.Contains("% hjfy: engine_compatibility", StringComparison.Ordinal)) return;
        Match documentClass = Class.Match(RepairRules.ExecutableText(main));
        if (!documentClass.Success) return;
        string compatibility = "% hjfy: engine_compatibility -- harmless pdfTeX registers for XeTeX packages\n" +
            "\\RequirePackage{iftex}\n\\ifXeTeX\n" +
            "\\ifdefined\\pdfcompresslevel\\else\\newcount\\pdfcompresslevel\\fi\n" +
            "\\ifdefined\\pdfoptionpdfminorversion\\else\\newcount\\pdfoptionpdfminorversion\\fi\n" +
            "\\ifdefined\\pdfgentounicode\\else\\newcount\\pdfgentounicode\\fi\n\\fi\n";
        // Class dependencies may use these registers while the class loads.
        // Keep all preceding template comments intact and run the shim first.
        FileStore.WriteTex(project.MainTex, main.Insert(documentClass.Index, compatibility));
    }
}
