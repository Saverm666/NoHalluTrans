using System.Text.RegularExpressions;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

public sealed class TexCompiler(TexRuntime runtime, IProcessRunner runner)
{
    public async Task<CompileResult> CompileAsync(PreparedProject project, int timeoutSeconds, CancellationToken token)
    {
        string directory = project.Root;
        string stem = Path.GetFileNameWithoutExtension(project.MainTex);
        string pdf = Path.Combine(directory, stem + ".pdf");
        string logPath = Path.Combine(directory, stem + ".log");
        string aux = Path.Combine(directory, stem + ".aux");
        List<string> args = ["-halt-on-error", "-file-line-error", "-interaction=nonstopmode", "-recorder", project.ShellEscape ? "-shell-escape" : "-no-shell-escape", Path.GetRelativePath(directory, project.MainTex).Replace('\\', '/')];
        // The default XeTeX driver command uses -q and hides the failing font's name.
        if (project.Engine == "xelatex") args.Insert(0, "-output-driver=xdvipdfmx -E");
        int runs = 0; string log = ""; int code = -1; bool bibDone = false;
        string engine;
        try { engine = runtime.Executable(project.Engine); }
        catch (FileNotFoundException ex) { return new(false, null, "! " + ex.Message, 127, 0, []); }
        var compileEnvironment = runtime.EnvironmentVariables();
        // The PDF driver looks up a TFM by basename even when TeX loaded it by a
        // relative path. Keep Kpathsea's default paths and also search this project.
        compileEnvironment["TFMFONTS"] = Path.PathSeparator + ".//";
        for (int pass = 0; pass < (bibDone ? 4 : 3); pass++)
        {
            token.ThrowIfCancellationRequested();
            // Remove stale output: success must refer to this compilation.
            if (File.Exists(pdf)) File.Delete(pdf);
            if (File.Exists(logPath)) File.Delete(logPath);
            string recorder = Path.Combine(directory, stem + ".fls");
            if (File.Exists(recorder)) File.Delete(recorder);
            var result = await runner.RunAsync(engine, args, directory, compileEnvironment, TimeSpan.FromSeconds(timeoutSeconds), token);
            runs++; code = result.ExitCode;
            log = File.Exists(logPath) ? FileStore.ReadTex(logPath) : result.StandardOutput + "\n" + result.StandardError;
            // XeTeX's PDF driver reports missing fonts and duplicate PDF names
            // on stderr after the TeX log has been closed.
            if (File.Exists(logPath) && !string.IsNullOrWhiteSpace(result.StandardError)) log += "\n" + result.StandardError;
            if (result.TimedOut) log += "\n编译超时。";
            if (code != 0) break;
            string auxText = File.Exists(aux) ? FileStore.ReadTex(aux) : "";
            string? bibliography = BibliographyTool(directory, stem, log, auxText);
            if (!bibDone && bibliography is not null)
            {
                bibDone = true;
                var bib = await runner.RunAsync(runtime.Executable(bibliography), [stem], directory, runtime.EnvironmentVariables(), TimeSpan.FromSeconds(timeoutSeconds), token);
                if (bib.ExitCode != 0)
                {
                    log += "\n" + bib.StandardOutput + "\n" + bib.StandardError;
                    return new(false, null, log, bib.ExitCode, runs, Missing(log));
                }
                continue;
            }
            if (!Regex.IsMatch(log, @"Rerun to get cross-references right|Rerun LaTeX|Label\(s\) may have changed|Please \(re\)run Biber")) break;
        }
        bool ok = code == 0 && File.Exists(pdf) && new FileInfo(pdf).Length > 0;
        return new(ok, ok ? pdf : null, log, code, runs, Missing(log));
    }
    internal static string[] MissingPhysicalFonts(string log) =>
        log.Contains("xdvipdfmx:fatal: Cannot proceed without .vf or \"physical\" font", StringComparison.Ordinal)
            ? Regex.Matches(log, @"(?m)^xdvipdfmx:warning: Could not locate a virtual/physical font for TFM ""([A-Za-z0-9_.-]+)""")
                .Select(m => m.Groups[1].Value + ".tfm").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    public static string[] Missing(string log)
    {
        var files = Regex.Matches(log, "(?:File|file) [`']([^`'\\r\\n]+\\.(?:sty|cls|def|fd|cfg|bst|bbx|cbx|lbx|ldf|tex|ltx|enc|map|tfm|vf|pfb|otf|ttf|ttc|clo|ist|code\\.tex|mps))[`'] (?:not found|not found\\.)")
            .Select(m => TexTemplateLibrary.Normalize(m.Groups[1].Value));
        var bibtexStyles = Regex.Matches(log, @"(?m)^I couldn't open style file ([^\r\n]+\.bst)[ \t]*\r?$")
            .Select(m => TexTemplateLibrary.Normalize(m.Groups[1].Value));
        var inputFiles = Regex.Matches(log, @"I can't find file [`']([^`'\r\n]+\.(?:tex|ltx|sty|cls|def|cfg))[`']")
            .Select(m => TexTemplateLibrary.Normalize(m.Groups[1].Value));
        var extensionlessImages = Regex.Matches(log, @"LaTeX Error: File [`']([^`'\r\n./\\]+)[`'] not found\.[\s\S]{0,600}?I could not locate the file with any of these extensions:")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(name => Regex.IsMatch(name, @"^[A-Za-z0-9_.-]+$"))
            .Select(name => "img:" + name);
        var fontDefinitions = Regex.Matches(log, @"(?m)^No file ([^\s]+\.(?:fd|sty|def|cfg|ldf|tex|enc|clo))\.")
            .Select(m => Path.GetFileName(m.Groups[1].Value));
        var tikzLibraries = Regex.Matches(log, @"I did not find the (tikz|pgf|pgfplots) library [`']([^`']+)[`']")
            .Select(m => m.Groups[1].Value + "library" + m.Groups[2].Value + ".code.tex");
        var tcbLibraries = Regex.Matches(log, @"Package tcolorbox Error: Library [`']([^`']+)[`'] not found")
            .Select(m => "tcb" + m.Groups[1].Value + ".code.tex");
        var biblatexStyles = Regex.Matches(log, @"Package biblatex Error: Style [`']([^`']+)[`'] not found")
            .SelectMany(m => new[] { m.Groups[1].Value.Trim() + ".bbx", m.Groups[1].Value.Trim() + ".cbx" });
        var babelLanguages = Regex.Matches(log, @"Package babel Error: Unknown option [`']([^`']+)[`']")
            .Select(m => m.Groups[1].Value.Trim() + ".ldf");
        var tfmFonts = Regex.Matches(log, @"Font (?:\\?[^\s=]+)=([^\s:/\\]+)(?:\s+at\b|\s*not loadable:)[^\r\n]*not loadable: Metric \(TFM\) file (?:or installed font )?not found")
            .Select(m => m.Groups[1].Value.Trim() + ".tfm");
        var driverMetrics = Regex.Matches(log, @"(?m)^xdvipdfmx:fatal: Unable to find TFM file ""([A-Za-z0-9_.-]+)""")
            .Select(m => m.Groups[1].Value.EndsWith(".tfm", StringComparison.OrdinalIgnoreCase)
                ? m.Groups[1].Value : m.Groups[1].Value + ".tfm");
        var pkFonts = Regex.Matches(log, @"mktexpk: don't know how to create bitmap font for ([^\s.]+)")
            .Select(m => m.Groups[1].Value.Trim() + ".tfm");
        var fonts = Regex.Matches(log, @"The font ""([^""\r\n]+)"" cannot be found")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.Contains('/') && !name.Contains('\\') && !name.Contains(':'))
            .Select(name => Regex.IsMatch(name, @"\.(?:otf|ttf|ttc)$", RegexOptions.IgnoreCase) ? name : "font:" + name);
        return files
            .Concat(bibtexStyles)
            .Concat(inputFiles)
            .Concat(extensionlessImages)
            .Concat(fontDefinitions)
            .Concat(tikzLibraries)
            .Concat(tcbLibraries)
            .Concat(biblatexStyles)
            .Concat(babelLanguages)
            .Concat(tfmFonts)
            .Concat(driverMetrics)
            .Concat(pkFonts)
            .Concat(fonts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    public async Task<bool> RunBibliographyAsync(PreparedProject project, int timeout, CancellationToken token)
    {
        string stem = Path.GetFileNameWithoutExtension(project.MainTex);
        string auxPath = Path.Combine(project.Root, stem + ".aux");
        string? tool = BibliographyTool(project.Root, stem, "", File.Exists(auxPath) ? FileStore.ReadTex(auxPath) : "");
        if (tool is null) return false;
        var result = await runner.RunAsync(runtime.Executable(tool), [stem], project.Root, runtime.EnvironmentVariables(), TimeSpan.FromSeconds(timeout), token);
        return result.ExitCode == 0;
    }
    public static string? BibliographyTool(string directory, string stem, string log, string aux)
    {
        bool biber = File.Exists(Path.Combine(directory, stem + ".bcf")) || log.Contains("backend=biber") || aux.Contains("backend=biber") || log.Contains("Please (re)run Biber");
        if (biber) return Directory.EnumerateFiles(directory, "*.bib", SearchOption.AllDirectories).Any() ? "biber" : null;
        var match = Regex.Match(aux, @"\\bibdata\{([^}]*)\}");
        if (!match.Success) return null;
        return match.Groups[1].Value.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0)
            .Any(n => { string path = Path.Combine(directory, Path.GetExtension(n) == ".bib" ? n : n + ".bib"); return File.Exists(path) && new FileInfo(path).Length > 0; }) ? "bibtex" : null;
    }
}
