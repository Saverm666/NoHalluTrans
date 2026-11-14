using System.Text.RegularExpressions;
using System.Security;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

public sealed class TexRuntime
{
    public string Root { get; }
    public string Bin { get; }
    public bool AutoInstallPackages { get; set; }
    private readonly IProcessRunner runner;
    private static readonly SemaphoreSlim PackageGate = new(1, 1);
    public TexRuntime(string root, IProcessRunner runner)
    {
        Root = Path.GetFullPath(root); this.runner = runner;
        Bin = Path.Combine(Root, "bin", OperatingSystem.IsWindows() ? "windows" : "x86_64-linux");
    }
    public string Executable(string name)
    {
        if (!Regex.IsMatch(name, "^[a-z0-9]+$")) throw new ArgumentException("无效的编译工具名称。");
        string path = Path.Combine(Bin, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(path)) throw new FileNotFoundException("编译环境缺少 " + name + "，请在“编译环境”页下载或修复。", path);
        return path;
    }
    public Dictionary<string, string> EnvironmentVariables()
    {
        string state = Path.Combine(Root, "hjfy-state");
        string temporary = Path.Combine(state, "temp");
        Directory.CreateDirectory(temporary);
        string fontConfig = EnsureFontConfig(state);
        return new()
        {
            ["PATH"] = Bin + Path.PathSeparator + Path.Combine(Root, "tlpkg", "tlperl", "bin") + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH"),
            ["TEMP"] = temporary, ["TMP"] = temporary, ["TMPDIR"] = temporary,
            ["TEXMFHOME"] = Path.Combine(state, "home"),
            ["TEXMFVAR"] = Path.Combine(state, "var"),
            ["TEXMFCONFIG"] = Path.Combine(state, "config"),
            ["OSFONTDIR"] = Path.Combine(Root, "texmf-dist", "fonts", "opentype") + "//",
            ["FONTCONFIG_FILE"] = fontConfig,
            ["FONTCONFIG_PATH"] = Path.GetDirectoryName(fontConfig) ?? state,
            ["FC_CACHEDIR"] = Path.Combine(state, "fonts-cache"),
            ["max_print_line"] = "1000", ["error_line"] = "254", ["half_error_line"] = "238",
            ["TEXLIVE_PREFER_OWN"] = "1",
            ["PATHEXT"] = OperatingSystem.IsWindows() ? ".COM;.EXE;.BAT;.CMD" : System.Environment.GetEnvironmentVariable("PATHEXT") ?? ""
        };
    }

    private string EnsureFontConfig(string state)
    {
        string configDirectory = Path.Combine(state, "fontconfig");
        string configPath = Path.Combine(configDirectory, "fonts.conf");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(Path.Combine(state, "fonts-cache"));
        string root = Path.GetFullPath(Root).Replace('\\', '/');
        static string Xml(string value) => SecurityElement.Escape(value) ?? value;
        string content = $"<?xml version=\"1.0\"?><!DOCTYPE fontconfig SYSTEM \"fonts.dtd\"><fontconfig>" +
            $"<dir>{Xml(root + "/texmf-dist/fonts/opentype")}</dir>" +
            $"<dir>{Xml(root + "/texmf-dist/fonts/truetype")}</dir>" +
            "<dir prefix=\"xdg\">fonts</dir>" +
            $"<cachedir>{Xml(Path.Combine(state, "fonts-cache").Replace('\\', '/'))}</cachedir>" +
            "</fontconfig>";
        if (!File.Exists(configPath) || !string.Equals(File.ReadAllText(configPath), content, StringComparison.Ordinal))
            File.WriteAllText(configPath, content);
        return configPath;
    }
    public async Task<string> CheckAsync(CancellationToken token)
    {
        List<string> lines = [];
        foreach (string tool in new[] { "pdflatex", "xelatex", "lualatex", "bibtex", "biber", "kpsewhich" })
        {
            token.ThrowIfCancellationRequested();
            string executable = Executable(tool);
            var result = await runner.RunAsync(executable, ["--version"], Root, EnvironmentVariables(), TimeSpan.FromSeconds(30), token);
            if (result.ExitCode != 0) throw new IOException(tool + " 检查失败：" + result.StandardError);
            lines.Add(result.StandardOutput.Split('\n')[0].Trim());
        }
        foreach (string file in new[] { "ctex.sty", "xeCJK.sty", "fontspec.sty", "FandolSong-Regular.otf" })
        {
            var result = await runner.RunAsync(Executable("kpsewhich"), [file], Root, EnvironmentVariables(), TimeSpan.FromSeconds(30), token);
            lines.Add(file + ": " + (result.ExitCode == 0 && result.StandardOutput.Trim().Length > 0 ? "就绪" : "缺失"));
            if (result.ExitCode != 0 || result.TimedOut || string.IsNullOrWhiteSpace(result.StandardOutput)) throw new IOException("内置环境缺少 " + file);
        }
        return string.Join('\n', lines);
    }
    public async Task<TexPreflight> PreflightAsync(PreparedProject project, CancellationToken token)
    {
        string? binary;
        try { binary = Executable(project.Engine); }
        catch (FileNotFoundException) { return new(project.Engine, null, false, [project.Engine], new Dictionary<string, bool?>()); }
        string text = TexSyntax.WithoutComments(FileStore.ReadTex(project.MainTex));
        int body = text.IndexOf(@"\begin{document}", StringComparison.Ordinal);
        if (body >= 0) text = text[..body];
        var packages = Regex.Matches(text, @"\\(?:usepackage|RequirePackage)\s*(?:\[[^\]]*\])?\s*\{([^}]+)\}")
            .SelectMany(m => m.Groups[1].Value.Split(',')).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().Take(80).ToArray();
        List<string> missing = [];
        Dictionary<string, bool?> available = [];
        string lookup;
        try { lookup = Executable("kpsewhich"); }
        catch (FileNotFoundException) { return new(project.Engine, binary, true, [], packages.ToDictionary(p => p, _ => (bool?)null)); }
        foreach (string file in packages.Select(p => p.EndsWith(".sty") ? p : p + ".sty").Prepend(project.DocumentClass.EndsWith(".cls") ? project.DocumentClass : project.DocumentClass + ".cls"))
        {
            token.ThrowIfCancellationRequested();
            bool local = File.Exists(Path.Combine(project.Root, file)) || Directory.EnumerateFiles(project.Root, Path.GetFileName(file), SearchOption.AllDirectories).Any();
            if (!local)
            {
                var result = await runner.RunAsync(lookup, [file], project.Root, EnvironmentVariables(), TimeSpan.FromSeconds(30), token);
                local = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
            }
            available[file] = local;
            if (!local) missing.Add(file);
        }
        string? suggested = project.Engine == "pdflatex" && packages.Any(p => p is "ctex" or "xeCJK" or "fontspec" or "unicode-math" or "luatexja") ? "xelatex" : null;
        return new(project.Engine, binary, missing.Count == 0 && suggested is null, missing, available, suggested);
    }
    public async Task<bool> InstallMissingAsync(IEnumerable<string> files, IProgress<PipelineEvent>? progress, CancellationToken token)
    {
        if (!AutoInstallPackages) return false;
        await PackageGate.WaitAsync(token);
        try
        {
            var version = await TlmgrAsync(["--version"], token);
            var year = Regex.Match(version.StandardOutput, @"version\s+(20\d{2})");
            if (!year.Success) throw new IOException("无法确认 TeX Live 年度，已停止补包。");
            if (File.Exists(Path.Combine(Root, "hjfy-runtime.json")))
            {
                var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "hjfy-runtime.json")));
                if (manifest.RootElement.GetProperty("year").GetInt32().ToString() != year.Groups[1].Value)
                    throw new IOException("TeX Live 环境年度与清单不一致。");
            }
            HashSet<string> packages = [];
            List<string> searched = [];
            foreach (string file in files.Distinct().Take(10))
            {
                string? pattern = null;
                string display = file;
                if (file.StartsWith("font:", StringComparison.Ordinal))
                {
                    string fontName = file["font:".Length..];
                    if (!Regex.IsMatch(fontName, @"^[A-Za-z0-9_.-]+$")) continue;
                    pattern = "/(?i)" + Regex.Escape(fontName) + "\\.(otf|ttf|ttc)$";
                    display = fontName;
                }
                else if (file.StartsWith("img:", StringComparison.Ordinal))
                {
                    string stem = file["img:".Length..];
                    if (!Regex.IsMatch(stem, @"^[A-Za-z0-9_.-]+$")) continue;
                    pattern = "/(?i)" + Regex.Escape(stem) + "\\.(pdf|png|jpg|jpeg|eps|mps)$";
                    display = stem;
                }
                else if (Regex.IsMatch(file, @"^[A-Za-z0-9_.-]+\.(sty|cls|def|fd|cfg|bst|bbx|cbx|lbx|ldf|tex|ltx|enc|map|tfm|vf|pfb|clo|ist|code\.tex|otf|ttf|ttc|mps)$", RegexOptions.IgnoreCase))
                {
                    pattern = "/(?i)" + Regex.Escape(file) + "$";
                }
                if (pattern is null) continue;
                progress?.Report(new("stage", "packages", "正在查找依赖：" + display));
                var result = await TlmgrAsync(["search", "--global", "--file", pattern], token);
                if (result.ExitCode != 0) throw new IOException("补包仓库不可用或年度不匹配。请检查网络和编译环境。\n" + result.StandardError);
                int before = packages.Count;
                foreach (Match m in Regex.Matches(result.StandardOutput, @"(?m)^([a-z0-9][a-z0-9.-]*):\s*$")) packages.Add(m.Groups[1].Value);
                if (packages.Count > before) searched.Add(file);
            }
            if (packages.Count == 0) return false;
            progress?.Report(new("stage", "packages", "正在安装依赖：" + string.Join(", ", packages)));
            var install = await TlmgrAsync(["install", .. packages], token);
            if (install.ExitCode != 0)
                throw new IOException("TeX 依赖安装失败。\n" + install.StandardError + install.StandardOutput);
            bool verified = true;
            foreach (string file in searched)
            {
                string[] candidates = file.StartsWith("font:", StringComparison.Ordinal)
                    ? [file["font:".Length..] + ".otf", file["font:".Length..] + ".ttf", file["font:".Length..] + ".ttc"]
                    : file.StartsWith("img:", StringComparison.Ordinal)
                        ? [file["img:".Length..] + ".pdf", file["img:".Length..] + ".png", file["img:".Length..] + ".jpg", file["img:".Length..] + ".jpeg", file["img:".Length..] + ".eps", file["img:".Length..] + ".mps"]
                        : [file];
                bool found = false;
                foreach (string candidate in candidates)
                {
                    var check = await runner.RunAsync(Executable("kpsewhich"), [candidate], Root, EnvironmentVariables(), TimeSpan.FromSeconds(30), token);
                    if (check.ExitCode == 0 && !string.IsNullOrWhiteSpace(check.StandardOutput)) { found = true; break; }
                }
                if (!found)
                {
                    verified = false;
                    progress?.Report(new("issue", "packages", "安装后仍未找到依赖文件：" + file, Code: "package-install-unverified"));
                }
            }
            if (!verified)
                throw new IOException("TeX 依赖安装完成但文件未进入当前便携环境，请检查 TeX Live 仓库或运行时权限。\n" + install.StandardError);
            return true;
        }
        finally { PackageGate.Release(); }
    }
    private Task<ProcessResult> TlmgrAsync(string[] args, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            string perl = Path.Combine(Root, "tlpkg", "tlperl", "bin", "perl.exe");
            string script = Path.Combine(Root, "texmf-dist", "scripts", "texlive", "tlmgr.pl");
            return runner.RunAsync(perl, [script, .. args], Root, EnvironmentVariables(), TimeSpan.FromMinutes(10), token);
        }
        return runner.RunAsync(Path.Combine(Bin, "tlmgr"), args, Root, EnvironmentVariables(), TimeSpan.FromMinutes(10), token);
    }
}
