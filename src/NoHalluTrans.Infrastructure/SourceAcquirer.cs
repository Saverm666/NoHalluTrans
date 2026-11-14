using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace NoHalluTrans.Infrastructure;

public sealed class SourceAcquirer(HttpClient client)
{
    public const long MaxBytes = 256L * 1024 * 1024;
    public static string? ArxivId(string source)
    {
        source = source.Trim().Trim('"', '\'');
        string id = @"(?:\d{4}\.\d{4,5}|[a-z][a-z\-]*(?:\.[A-Z]{2})?/\d{7})(?:v\d+)?";
        var url = Regex.Match(source, @"^https?://(?:export\.|www\.)?arxiv\.org/(?:abs|pdf|src|e-print)/(" + id + @")(?:\.pdf)?/?(?:[?#].*)?$", RegexOptions.IgnoreCase);
        if (url.Success) return url.Groups[1].Value;
        var bare = Regex.Match(source, @"^(?:arxiv:\s*)?(" + id + ")$", RegexOptions.IgnoreCase);
        return bare.Success ? bare.Groups[1].Value : null;
    }
    public async Task<string> AcquireAsync(string source, string destination, CancellationToken token, bool force = false)
    {
        source = source.Trim().Trim('"');
        destination = Path.GetFullPath(destination);
        string receipt = Path.Combine(destination, ".hjfy-acquired.json");
        if (!force && File.Exists(receipt))
        {
            using var record = JsonDocument.Parse(File.ReadAllText(receipt));
            if (record.RootElement.GetProperty("source").GetString() == source && FileStore.TexFiles(destination).Any())
                return ProjectRoot(destination);
        }
        Directory.CreateDirectory(destination);
        string Finish()
        {
            FileStore.WriteText(receipt, JsonSerializer.Serialize(new { source }));
            return ProjectRoot(destination);
        }
        if (Directory.Exists(source))
        {
            string fullSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            if (fullSource.Equals(destination, StringComparison.OrdinalIgnoreCase)) return destination;
            if (destination.StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("编译工作目录不能放在输入工程目录内。");
            FileStore.CopyTree(source, destination, token); return Finish();
        }
        if (File.Exists(source))
        {
            if (Path.GetExtension(source).Equals(".tex", StringComparison.OrdinalIgnoreCase))
                FileStore.CopyTree(Path.GetDirectoryName(Path.GetFullPath(source))!, destination, token);
            else await ExtractAsync(source, destination, token);
            return Finish();
        }
        string? arxiv = ArxivId(source);
        string url = arxiv is not null ? "https://export.arxiv.org/e-print/" + arxiv : source;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "file"))
            throw new ArgumentException("请选择存在的文件、工程文件夹，或输入 arXiv 编号及源码链接。");
        if (uri.IsFile) return await AcquireAsync(uri.LocalPath, destination, token);
        string download = Path.Combine(destination, "_download"); Directory.CreateDirectory(download);
        string payload = Path.Combine(download, "source.payload"); string? contentType = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(120));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("HJFY/0.1 (LaTeX-source-translator)");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
                { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token); continue; }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("源码下载超过 256 MB 限制。");
                contentType = response.Content.Headers.ContentType?.MediaType;
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                await using var output = File.Create(payload);
                await CopyLimitedAsync(input, output, MaxBytes, deadline.Token);
                break;
            }
            catch (HttpRequestException ex) when (attempt < 2 && (ex.StatusCode is null || (int)ex.StatusCode >= 500 || ex.StatusCode == HttpStatusCode.TooManyRequests))
            { await Task.Delay(1000 * (attempt + 1), token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < 2)
            { await Task.Delay(1000 * (attempt + 1), token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("源码下载超时，请检查网络后重试。"); }
        }
        if (contentType?.Contains("html") == true) throw new InvalidDataException("下载到的是网页，请提供 TeX 源码或源码压缩包。");
        byte[] prefixBytes = new byte[2048];
        using var prefixStream = File.OpenRead(payload);
        string prefix = System.Text.Encoding.UTF8.GetString(prefixBytes, 0, prefixStream.Read(prefixBytes));
        prefixStream.Dispose();
        if (uri.AbsolutePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) || contentType == "application/x-tex" ||
            prefix.Contains(@"\documentclass") || prefix.Contains(@"\documentstyle"))
            File.Copy(payload, Path.Combine(destination, "main.tex"), true);
        else await ExtractAsync(payload, destination, token);
        return Finish();
    }
    public static string ProjectRoot(string directory)
    {
        while (!Directory.EnumerateFiles(directory).Any(p => Path.GetExtension(p).Equals(".tex", StringComparison.OrdinalIgnoreCase)))
        {
            var children = Directory.EnumerateDirectories(directory).Where(d => Path.GetFileName(d) is not ("_download" or "__MACOSX")).ToArray();
            if (children.Length != 1) break;
            directory = children[0];
        }
        return directory;
    }
    public static async Task CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        byte[] buffer = new byte[65536]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        { total += count; if (total > limit) throw new InvalidDataException("解压或下载超过 256 MB 限制。"); await output.WriteAsync(buffer.AsMemory(0, count), token); }
    }
    public static async Task ExtractAsync(string archive, string destination, CancellationToken token)
    {
        // ReaderFactory handles zip, tar and compressed tar streams without spawning shell tools.
        await using var stream = File.OpenRead(archive);
        using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions { LeaveStreamOpen = true });
        long total = 0; int files = 0;
        while (reader.MoveToNextEntry())
        {
            token.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory) continue;
            if (!string.IsNullOrEmpty(entry.LinkTarget)) throw new InvalidDataException("不接受压缩包中的符号链接。");
            if (++files > 10000) throw new InvalidDataException("压缩包文件数量过多。");
            string name = entry.Key ?? "main.tex";
            if (string.IsNullOrWhiteSpace(name) || name == "source.payload") name = "main.tex";
            if (name.Contains("__MACOSX")) continue;
            string target = FileStore.SafeChild(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = reader.OpenEntryStream();
            await using var output = File.Create(target);
            await CopyLimitedAsync(input, output, MaxBytes - total, token);
            total += output.Length;
        }
        if (files == 0) throw new InvalidDataException("源码压缩包为空或格式不支持。");
        var extracted = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "_download" + Path.DirectorySeparatorChar)).ToArray();
        if (extracted.Length == 1 && !Path.GetExtension(extracted[0]).Equals(".tex", StringComparison.OrdinalIgnoreCase))
        {
            string prefix = FileStore.ReadTex(extracted[0]);
            if (prefix.Contains(@"\documentclass") || prefix.Contains(@"\documentstyle")) File.Move(extracted[0], Path.Combine(destination, "main.tex"), true);
        }
    }
}
