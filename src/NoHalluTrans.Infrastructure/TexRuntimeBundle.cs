using System.IO.Compression;
using System.Security.Cryptography;

namespace NoHalluTrans.Infrastructure;

/// <summary>Expands the offline compiler bundle once, keeping incomplete extractions out of use.</summary>
public static class TexRuntimeBundle
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static bool IsAvailable(string root) => IsReady(root) || File.Exists(root + ".zip");
    public static bool IsReady(string root) => File.Exists(Path.Combine(root, "hjfy-runtime.json")) && File.Exists(Path.Combine(root, "bin", "windows", "xelatex.exe"));

    public static async Task EnsureAsync(string root, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady(root)) return;
            string bundle = root + ".zip";
            if (!File.Exists(bundle)) throw new FileNotFoundException("未找到内置编译环境包，请完整解压软件。", bundle);
            if (Directory.Exists(root)) throw new IOException("编译环境目录不完整，请将 runtime 中的 texlive 文件夹移走后重试。");
            string staging = root + ".extract-" + Guid.NewGuid().ToString("N");
            try
            {
                await Task.Run(async () =>
                {
                    string checksumFile = bundle + ".sha256";
                    if (!File.Exists(checksumFile)) throw new FileNotFoundException("编译环境校验文件缺失，请重新解压软件。", checksumFile);
                    string expected = (await File.ReadAllTextAsync(checksumFile, cancellationToken)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                    await using var stream = File.OpenRead(bundle);
                    string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("编译环境包已损坏，请重新解压软件。");
                    stream.Position = 0;
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    Directory.CreateDirectory(staging);
                    string prefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                    int count = 0, lastPercent = -1;
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string relative = entry.FullName.Replace('\\', '/');
                        string target = Path.GetFullPath(Path.Combine(staging, relative));
                        if (Path.IsPathRooted(relative) || !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("编译环境包含无效路径。");
                        if (relative.EndsWith('/')) Directory.CreateDirectory(target);
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            await using var input = entry.Open();
                            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            await input.CopyToAsync(output, cancellationToken);
                        }
                        int percent = ++count * 100 / archive.Entries.Count;
                        if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                    }
                    if (!IsReady(staging)) throw new InvalidDataException("编译环境包缺少必要组件。");
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Move(staging, root);
                }, cancellationToken);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        finally { Gate.Release(); }
    }
}
