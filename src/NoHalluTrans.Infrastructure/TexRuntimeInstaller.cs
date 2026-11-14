using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

public sealed record TexSetupProgress(string Stage, string Message, int? Percent = null);
public sealed record TexInstallOptions(string Repository = "https://mirrors.tuna.tsinghua.edu.cn/CTAN/systems/texlive/tlnet", int ExpectedYear = 2026);

/// <summary>Installs a portable TeX tree in isolation, validates it, then swaps it into use.</summary>
public sealed class TexRuntimeInstaller(HttpClient http, Func<IProgress<string>, IProcessRunner>? createRunner = null)
{
    private static readonly string[] Packages = [
        "biber", "biblatex", "ctex", "xecjk", "fandol", "natbib", "booktabs", "microtype", "titlesec", "enumitem",
        "subfiles", "soul", "ulem", "cleveref", "algorithmicx", "algorithms", "algorithm2e", "algpseudocodex",
        "multirow", "adjustbox", "tcolorbox", "fontawesome", "fontawesome5", "beamer", "listings", "mathtools",
        "caption", "wrapfig", "xpatch", "xstring", "environ", "trimspaces", "collectbox", "footmisc", "threeparttable",
        "makecell", "tools", "float", "abstract", "preprint", "etoolbox",
        "acmart", "doclicense", "ccicons", "ieeetran", "siunitx", "pgfplots", "thmtools",
        "libertine", "libertinus-otf", "libertinus-type1", "newtx", "newpx", "inconsolata",
        "xurl", "orcidlink", "csquotes", "tabularray", "ninecolors", "arydshln", "diagbox", "colortbl",
        "placeins", "pdflscape", "changepage", "lineno", "todonotes", "pdfpages", "standalone",
        "svg", "transparent", "eso-pic", "overpic", "forest", "doublestroke", "bbm", "bbm-macros",
        "stmaryrd", "cancel", "units", "physics", "braket", "sttools", "cite", "bookmark", "tablefootnote",
        "koma-script", "paralist", "xifthen", "ifmtarg", "zref", "relsize", "anyfontsize", "silence",
        "mdframed", "framed", "fvextra", "upquote", "grffile", "epstopdf", "totpages", "ncctools",
        "comment", "draftwatermark", "hyperxmp", "cmap", "mmap", "refcount", "setspace"
    ];

    public async Task InstallAsync(string root, bool repair = false, IProgress<TexSetupProgress>? progress = null,
        CancellationToken token = default, TexInstallOptions? options = null)
    {
        root = Path.GetFullPath(root);
        options ??= new();
        if (!FileStore.Ascii(root)) throw new ArgumentException("请将便携目录放在可写的纯英文路径后安装环境。");
        if (!Uri.TryCreate(options.Repository, UriKind.Absolute, out var repository) || repository.Scheme != "https" || repository.Query.Length > 0 || repository.Fragment.Length > 0)
            throw new ArgumentException("编译环境下载源必须是 HTTPS 地址。");
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("当前环境是外部目录联接。请在独立便携目录内下载或修复环境。");
        if (!repair && TexRuntimeBundle.IsReady(root)) return;
        string parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        FileStream installLock;
        try { installLock = new FileStream(Path.Combine(parent, ".texlive-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("另一个环境安装正在进行，请稍后重试。", ex); }
        using var heldLock = installLock;
        if (!repair && TexRuntimeBundle.IsReady(root)) return;
        string session = Path.Combine(parent, ".texlive-setup-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(session, "texlive");
        string temporary = Path.Combine(session, "temp");
        string logs = Path.Combine(Path.GetDirectoryName(parent)!, "data", "logs");
        Directory.CreateDirectory(temporary); Directory.CreateDirectory(logs);
        string logPath = Path.Combine(logs, "tex-setup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        object logGate = new();
        void Log(string message) { lock (logGate) log.WriteLine(message); }
        void Report(string phase, string message, int? percent = null) { Log(message); progress?.Report(new(phase, message, percent)); }
        string phase = "安装基础环境";
        var output = new SetupOutput(line =>
        {
            Log(line);
            var count = Regex.Match(line, @"(?:Installing\s+)?\[\s*(\d+)/\s*(\d+)", RegexOptions.IgnoreCase);
            if (count.Success && int.TryParse(count.Groups[1].Value, out int done) && int.TryParse(count.Groups[2].Value, out int total) && total > 0)
                progress?.Report(new(phase, line, Math.Clamp(done * 100 / total, 0, 100)));
        });
        var runner = createRunner?.Invoke(output) ?? new ProcessRunner(output);
        var environment = new Dictionary<string, string>
        {
            ["TEMP"] = temporary, ["TMP"] = temporary, ["TMPDIR"] = temporary,
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD", ["TEXLIVE_INSTALL_NO_WELCOME"] = "1"
        };
        async Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, TimeSpan timeout)
        {
            var result = await runner.RunAsync(executable, arguments, session, environment, timeout, token);
            // Also retain output when a test or external runner does not stream it.
            Log(result.StandardOutput); Log(result.StandardError);
            if (result.ExitCode != 0 || result.TimedOut) throw new IOException(result.TimedOut
                ? $"{phase}超时，请检查网络后重试。日志：{logPath}"
                : $"{phase}失败，请检查网络后重试。日志：{logPath}");
            return result;
        }
        try
        {
            string repo = options.Repository.TrimEnd('/');
            Report("下载引导工具", "正在下载 TeX Live 安装工具（约 25 MB）…", 0);
            string checksum = await ReadChecksumAsync(new Uri(repo + "/install-tl.zip.sha512"), token);
            string archive = Path.Combine(session, "install-tl.zip");
            await DownloadAsync(new Uri(repo + "/install-tl.zip"), archive, progress, token);
            Report("校验下载", "正在校验安装工具…");
            await using (var input = File.OpenRead(archive))
                if (!Convert.ToHexString(await SHA512.HashDataAsync(input, token)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装工具校验失败，请重新下载。");
            string installer = Path.Combine(session, "installer");
            await ExtractAsync(archive, installer, token);
            string installScript = Directory.EnumerateFiles(installer, "install-tl", SearchOption.AllDirectories).Single();
            string installerRoot = Path.GetDirectoryName(installScript)!;
            string perl = Path.Combine(installerRoot, "tlpkg", "tlperl", "bin", "perl.exe");
            if (!File.Exists(perl)) throw new InvalidDataException("安装工具缺少 Windows 组件。");
            string profile = Path.Combine(session, "texlive.profile");
            string normalized = stage.Replace('\\', '/');
            await File.WriteAllTextAsync(profile, $"""
                selected_scheme scheme-small
                TEXDIR {normalized}
                TEXMFLOCAL {normalized}/texmf-local
                TEXMFSYSCONFIG {normalized}/texmf-config
                TEXMFSYSVAR {normalized}/texmf-var
                binary_windows 1
                instopt_portable 1
                instopt_adjustpath 0
                tlpdbopt_install_docfiles 0
                tlpdbopt_install_srcfiles 0
                tlpdbopt_desktop_integration 0
                tlpdbopt_file_assocs 0
                collection-latexextra 0
                collection-langchinese 0
                collection-xetex 1
                collection-luatex 1
                collection-fontsrecommended 1
                collection-bibtexextra 0
                """ + "\n", Encoding.ASCII, token);
            Report(phase, "正在安装基础编译器与字体；所需时间取决于网络速度…");
            await Run(perl, [installScript, "-no-gui", "-no-interaction", "-profile", profile, "-repository", repo], TimeSpan.FromHours(2));
            string runtimePerl = Path.Combine(stage, "tlpkg", "tlperl", "bin", "perl.exe");
            string tlmgr = Path.Combine(stage, "texmf-dist", "scripts", "texlive", "tlmgr.pl");
            environment["PATH"] = Path.Combine(stage, "bin", "windows") + ";" + Path.Combine(stage, "tlpkg", "tlperl", "bin") + ";" + Environment.GetEnvironmentVariable("PATH");
            var version = await Run(runtimePerl, [tlmgr, "--version"], TimeSpan.FromSeconds(30));
            if (!Regex.IsMatch(version.StandardOutput, @"version\s+" + options.ExpectedYear + @"\b"))
                throw new IOException($"下载源的 TeX Live 年度不是 {options.ExpectedYear}，请更新软件后重试。");
            phase = "安装论文与中文支持";
            Report(phase, "正在安装中文字体、参考文献工具与常用论文宏包…");
            await Run(runtimePerl, [tlmgr, "option", "repository", repo], TimeSpan.FromSeconds(30));
            await Run(runtimePerl, [tlmgr, "install", .. Packages], TimeSpan.FromHours(1));
            var packages = await Run(runtimePerl, [tlmgr, "info", "--only-installed", "--data", "name,localrev"], TimeSpan.FromMinutes(2));
            await File.WriteAllTextAsync(Path.Combine(stage, "hjfy-packages.txt"), packages.StandardOutput, token);
            phase = "检查编译环境";
            Report(phase, "正在检查编译器、中文字体和包管理工具…");
            await new TexRuntime(stage, runner).CheckAsync(token);
            await File.WriteAllTextAsync(Path.Combine(stage, "hjfy-runtime.json"), JsonSerializer.Serialize(new
            {
                year = options.ExpectedYear, repository = repo, created_utc = DateTime.UtcNow.ToString("o"), installer_sha512 = checksum,
                packages_sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(stage, "hjfy-packages.txt"), token)))
            }, JsonDefaults.Options), token);
            token.ThrowIfCancellationRequested();
            Report("启用环境", "检查通过，正在启用便携编译环境…");
            string backup = root + ".previous-" + Guid.NewGuid().ToString("N");
            bool hadPrevious = Directory.Exists(root);
            if (hadPrevious) Directory.Move(root, backup);
            try { Directory.Move(stage, root); }
            catch { if (hadPrevious) Directory.Move(backup, root); throw; }
            if (hadPrevious)
                try { Directory.Delete(backup, true); } catch (IOException) { Log("旧环境待清理：" + backup); } catch (UnauthorizedAccessException) { Log("旧环境待清理：" + backup); }
            Report("安装完成", "编译环境已就绪，所有文件均保存在便携目录内。", 100);
        }
        catch (OperationCanceledException) { Log("安装已取消，原有环境保持不变。"); throw; }
        catch (Exception ex) { Log(ex.ToString()); throw; }
        finally { try { Directory.Delete(session, true); } catch (IOException) { Log("安装临时目录待清理：" + session); } catch (UnauthorizedAccessException) { Log("安装临时目录待清理：" + session); } }
    }

    private async Task<string> ReadChecksumAsync(Uri address, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = DownloadRequest(address);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        CheckDownloadResponse(response);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using MemoryStream buffer = new();
        await SourceAcquirer.CopyLimitedAsync(stream, buffer, 4096, deadline.Token);
        var match = Regex.Match(Encoding.UTF8.GetString(buffer.ToArray()), @"\A\s*([a-fA-F0-9]{128})\s+\*?install-tl\.zip\s*\z");
        if (!match.Success) throw new InvalidDataException("下载源的安装工具校验信息无效。");
        return match.Groups[1].Value;
    }
    private async Task DownloadAsync(Uri address, string path, IProgress<TexSetupProgress>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var request = DownloadRequest(address);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        CheckDownloadResponse(response);
        long? total = response.Content.Headers.ContentLength;
        if (total > 128 * 1024 * 1024) throw new InvalidDataException("安装工具超过预期大小。");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = new byte[81920]; long received = 0; int previous = -1;
        while (await stream.ReadAsync(bytes, deadline.Token) is int read && read > 0)
        {
            received += read;
            if (received > 128 * 1024 * 1024) throw new InvalidDataException("安装工具超过预期大小。");
            await output.WriteAsync(bytes.AsMemory(0, read), deadline.Token);
            int percent = total is > 0 ? (int)Math.Min(100, received * 100 / total.Value) : -1;
            if (percent != previous) { previous = percent; progress?.Report(new("下载引导工具", $"已下载 {received / 1048576.0:F1} MB" + (total is > 0 ? $" / {total.Value / 1048576.0:F1} MB" : ""), percent >= 0 ? percent : null)); }
        }
        if (total is not null && received != total.Value) throw new IOException("安装工具下载不完整，请重试。");
    }
    private static async Task ExtractAsync(string archivePath, string destination, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        string prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar; long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            string target = Path.GetFullPath(Path.Combine(destination, name));
            if (Path.IsPathRooted(name) || !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Contains(':') || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("安装工具包含无效文件路径。");
            expanded += entry.Length;
            if (expanded > 512 * 1024 * 1024) throw new InvalidDataException("安装工具展开大小超过限制。");
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, token);
        }
    }
    private static HttpRequestMessage DownloadRequest(Uri address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("HJFY/0.2.1");
        return request;
    }
    private static void CheckDownloadResponse(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new IOException("下载源重定向到非 HTTPS 地址，已停止下载。");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"环境下载源返回 HTTP {(int)response.StatusCode}，请稍后重试。", null, response.StatusCode);
    }
    private sealed class SetupOutput(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
