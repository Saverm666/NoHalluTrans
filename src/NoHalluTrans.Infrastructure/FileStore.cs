using System.Text;

namespace NoHalluTrans.Infrastructure;

public static class FileStore
{
    static FileStore() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static string ReadTex(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return DetectEncoding(bytes).GetString(bytes).TrimStart('\ufeff');
    }
    public static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return Encoding.BigEndianUnicode;
        foreach (Encoding encoding in new[] { new UTF8Encoding(false, true), Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), Encoding.Latin1 })
        {
            try { _ = encoding.GetString(bytes); return encoding; }
            catch (DecoderFallbackException) { }
        }
        return Encoding.Latin1;
    }
    public static void WriteTex(string path, string text)
    {
        Encoding encoding = File.Exists(path) ? DetectEncoding(File.ReadAllBytes(path)) : new UTF8Encoding(false);
        try { _ = encoding.GetBytes(text); } catch (EncoderFallbackException) { encoding = new UTF8Encoding(false); }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, encoding); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void CopyTree(string source, string destination, CancellationToken token = default)
    {
        string fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (name is ".git" or "__pycache__" or "node_modules" or "_repro" or ".svn" or ".hg" or ".idea" or ".vscode" or "_download" or ".hjfy-acquired.json") continue;
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
            string absolute = Path.GetFullPath(path);
            if (absolute.Equals(fullDestination, StringComparison.OrdinalIgnoreCase) || fullDestination.StartsWith(absolute + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(destination, name);
            if (Directory.Exists(path)) CopyTree(path, target, token); else File.Copy(path, target, true);
        }
    }
    public static IEnumerable<string> TexFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetExtension(p).Equals(".tex", StringComparison.OrdinalIgnoreCase) && !p.Split(Path.DirectorySeparatorChar).Contains("_repro"))
        .OrderBy(p => p, StringComparer.Ordinal);
    public static IReadOnlyList<string> CompilationTexFiles(string root, string main)
        => CompilationSourceFiles(root, main, false);
    public static IReadOnlyList<string> CompilationSourceFiles(string root, string main, bool includeLocalStyles)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var files = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { Path.GetFullPath(main) };
        string recorder = Path.Combine(root, Path.GetFileNameWithoutExtension(main) + ".fls");
        if (File.Exists(recorder))
            foreach (string line in File.ReadLines(recorder).Where(s => s.StartsWith("INPUT ", StringComparison.Ordinal)))
            {
                string path = Path.GetFullPath(Path.Combine(root, line[6..].Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar)));
                if (path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)
                    && (Path.GetExtension(path).Equals(".tex", StringComparison.OrdinalIgnoreCase)
                        || includeLocalStyles && Path.GetExtension(path).ToLowerInvariant() is ".cls" or ".sty") && File.Exists(path)) files.Add(path);
            }
        return files.Order(StringComparer.Ordinal).ToArray();
    }
    public static IReadOnlyList<string> CompilationOutputFiles(string root, string main)
    {
        string recorder = Path.Combine(root, Path.GetFileNameWithoutExtension(main) + ".fls");
        if (!File.Exists(recorder)) return [];
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return File.ReadLines(recorder).Where(s => s.StartsWith("OUTPUT ", StringComparison.Ordinal))
            .Select(s => Path.GetFullPath(Path.Combine(root, s[7..].Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar))))
            .Where(p => p.StartsWith(prefix, comparison)).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }
    public static bool Ascii(string path) => path.All(c => c <= 127);
    public static string SafeChild(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        if (relative.StartsWith('/') || relative.Split('/').Any(p => p == ".." || p.Contains(':')))
            throw new InvalidDataException("压缩包包含不安全路径。");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("压缩包试图写入目标目录之外。");
        return full;
    }
}
